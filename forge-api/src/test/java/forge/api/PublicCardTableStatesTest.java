package forge.api;

import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import forge.StaticData;
import forge.deck.Deck;
import forge.game.card.Card;
import forge.game.keyword.Keyword;
import forge.game.player.RegisteredPlayer;
import forge.game.zone.ZoneType;
import org.testng.annotations.BeforeClass;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Actual effective engine mutations reach copied card/table state in every private seat view. */
public class PublicCardTableStatesTest {
    private static final Path RESOURCES = Path.of("../forge-gui/res");
    private static final com.google.gson.Gson JSON = new GsonBuilder().serializeNulls().create();
    @BeforeClass public void resources() { if (StaticData.instance() == null) EngineResources.load(RESOURCES); }

    private MatchTable table(String name) throws Exception {
        var players = new ArrayList<RegisteredPlayer>();
        for (int seat = 0; seat < 4; seat++) players.add(MatchTable.human(new Deck(), "Constructed", "Seat " + seat));
        var result = new MatchTable("Constructed", players, RESOURCES, Files.createTempDirectory(name));
        HeadlessPlatform.activate(result); return result;
    }

    @Test public void effectiveKeywordsRetainQualifiersCountsAndDisappearanceAcrossSeatSnapshots() throws Exception {
        try (var table = table("public-card-descriptors")) {
            var owner = table.game.getPlayers().get(0);
            var card = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard("Forest"), owner);
            owner.getZone(ZoneType.Battlefield).add(card, null, null);
            long timestamp = table.game.getNextTimestamp();
            card.addChangedCardKeywords(List.of("Shroud", "Shadow", "Hexproof:Red", "Protection:Blue", "Ward:2", "Ward:2"),
                    List.of(), false, timestamp, null);
            table.publish();
            var captured = table.seat(1).state();
            for (var seat : table.testViews()) {
                var projected = firstBattlefield(JSON.toJsonTree(seat).getAsJsonObject(), owner.getId());
                var states = projected.getAsJsonArray("states");
                assertEquals(states.size(), 5);
                assertTrue(states.asList().stream().anyMatch(value -> text(value.getAsJsonObject(), "label").equals("Hexproof from red")));
                assertTrue(states.asList().stream().anyMatch(value -> text(value.getAsJsonObject(), "label").equals("Protection from blue")));
                var ward = states.asList().stream().map(value -> value.getAsJsonObject()).filter(value -> text(value, "key").equals("ward")).findFirst().orElseThrow();
                assertEquals(ward.get("value").getAsInt(), 2); assertTrue(text(ward, "label").contains("2"));
                assertTrue(projected.getAsJsonArray("combatKeywords").asList().stream().anyMatch(value -> value.getAsString().equals("shroud")));
                assertFalse(states.toString().contains("Hexproof:Red")); assertFalse(states.toString().contains("Protection:Blue"));
            }
            assertTrue(card.hasKeyword(Keyword.SHROUD));
            card.removeChangedCardKeywords(timestamp, 0); table.publish();
            assertFalse(card.hasKeyword(Keyword.SHROUD));
            for (var seat : table.testViews()) {
                var projected = firstBattlefield(JSON.toJsonTree(seat).getAsJsonObject(), owner.getId());
                assertTrue(projected.getAsJsonArray("states").isEmpty());
                assertTrue(projected.getAsJsonArray("combatKeywords").isEmpty());
            }
            assertEquals(firstBattlefield(JSON.toJsonTree(captured).getAsJsonObject(), owner.getId()).getAsJsonArray("states").size(), 5,
                    "Published old state remains a copy after the effective abilities disappear");
        }
    }

    @Test public void faceDownOnlyPublishesCurrentPublicCharacteristicsAndTableDayNightIsGeneric() throws Exception {
        try (var table = table("public-hidden-table-states")) {
            var owner = table.game.getPlayers().get(0);
            var card = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard("Invisible Stalker"), owner);
            owner.getZone(ZoneType.Battlefield).add(card, null, null);
            card.turnFaceDown(true);
            card.addChangedCardKeywords(List.of("Ward:3", "Protection:Blue"), List.of(), false, table.game.getNextTimestamp(), null);
            table.game.setDayTime(false); table.publish();
            var captured = table.seat(1).state();
            for (var seat : table.testViews()) {
                var state = JSON.toJsonTree(seat).getAsJsonObject();
                var projected = firstBattlefield(state, owner.getId());
                assertTrue(projected.get("faceDown").getAsBoolean()); assertEquals(text(projected, "name"), "Face-down card");
                assertFalse(projected.toString().contains("Invisible Stalker")); assertEquals(text(projected, "text"), "");
                assertFalse(projected.has("otherFace")); assertFalse(projected.has("artName"));
                var states = projected.getAsJsonArray("states");
                assertTrue(states.asList().stream().anyMatch(value -> text(value.getAsJsonObject(), "label").equals("Protection from blue")));
                assertTrue(states.asList().stream().noneMatch(value -> text(value.getAsJsonObject(), "key").equals("hexproof")),
                        "The original printed hexproof is not a face-down public characteristic");
                var tableState = state.getAsJsonArray("states").get(0).getAsJsonObject();
                assertEquals(text(tableState, "label"), "Day"); assertEquals(text(tableState, "kind"), "status");
                assertEquals(text(tableState, "id"), "day-night");
            }
            var masked = MatchSession.choiceCard(card.getView(), owner.getView());
            assertEquals(masked.get("states"), List.of());
            var allocation = MatchSession.allocationCardDetails(card.getView(), table.game.getPlayers().get(1).getView());
            assertFalse(((List<?>) allocation.get("states")).isEmpty());
            table.game.setDayTime(true); table.publish();
            for (var seat : table.testViews()) assertEquals(text(JSON.toJsonTree(seat).getAsJsonObject().getAsJsonArray("states").get(0).getAsJsonObject(), "label"), "Night");
            assertEquals(text(JSON.toJsonTree(captured).getAsJsonObject().getAsJsonArray("states").get(0).getAsJsonObject(), "label"), "Day");
            owner.getZone(ZoneType.Battlefield).remove(card); owner.getZone(ZoneType.Hand).add(card, null, null);
            table.game.setDayTime(null); table.publish();
            var own = JSON.toJsonTree(table.seat(0).state()).getAsJsonObject();
            var hidden = zone(player(own, owner.getId()), "Hand").getAsJsonArray("cards").get(0).getAsJsonObject();
            assertTrue(hidden.getAsJsonArray("states").isEmpty()); assertTrue(own.getAsJsonArray("states").isEmpty());
            for (int seat = 1; seat < 4; seat++) assertTrue(zone(player(JSON.toJsonTree(table.seat(seat).state()).getAsJsonObject(), owner.getId()), "Hand").getAsJsonArray("cards").isEmpty());
        }
    }

    private static JsonObject firstBattlefield(JsonObject state, int playerId) {
        return zone(player(state, playerId), "Battlefield").getAsJsonArray("cards").get(0).getAsJsonObject();
    }
}
