package forge.api;

import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import forge.StaticData;
import forge.deck.Deck;
import forge.game.card.Card;
import forge.game.card.CardView;
import forge.game.ability.AbilityFactory;
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

    @Test public void preparedDetailsDescribeTheCurrentVisibleSpellWithoutRevealingAHiddenLinkedFace() throws Exception {
        try (var table = table("public-prepared-designation")) {
            var owner = table.game.getPlayers().get(0);
            var source = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard("Emeritus of Ideation"), owner);
            owner.getZone(ZoneType.Battlefield).add(source, null, null);
            var prepare = AbilityFactory.getAbility("DB$ AlterAttribute | Defined$ Self | Attributes$ Prepared", source);
            prepare.setActivatingPlayer(owner); prepare.resolve();
            assertTrue(source.isPrepared());
            var observer = table.game.getPlayers().get(1).getView();
            var status = source.getView().getPublicStates(observer).stream().filter(state -> state.key().equals("prepared")).findFirst().orElseThrow();
            assertEquals(status.detail(), "Ancestral Recall {U}");

            var spell = source.getPreparedSpell();
            owner.getZone(ZoneType.Exile).remove(spell); owner.getZone(ZoneType.Hand).add(spell, null, null);
            assertEquals(source.getView().getPublicStates(observer).stream().filter(state -> state.key().equals("prepared")).findFirst().orElseThrow().detail(), "");
            assertEquals(source.getView().getPublicStates(owner.getView()).stream().filter(state -> state.key().equals("prepared")).findFirst().orElseThrow().detail(), "Ancestral Recall {U}");
            owner.getZone(ZoneType.Hand).remove(spell); owner.getZone(ZoneType.Exile).add(spell, null, null);
            spell.turnFaceDown(true);
            assertEquals(source.getView().getPublicStates(observer).stream().filter(state -> state.key().equals("prepared")).findFirst().orElseThrow().detail(), "",
                    "Prepared remains public, but its hidden linked face is not described");
            source.turnFaceDown(true);
            assertTrue(source.getView().getPublicStates(observer).stream().noneMatch(state -> state.key().equals("prepared")),
                    "A face-down source does not expose its original card's designation");
        }
    }

    @Test public void blockingCapacityTracksEffectiveFiniteAndUnlimitedFactsWithoutExposingHiddenCards() throws Exception {
        try (var table = table("public-blocking-capacity")) {
            var owner = table.game.getPlayers().get(0);
            var card = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard("Grizzly Bears"), owner);
            owner.getZone(ZoneType.Battlefield).add(card, null, null);
            java.util.function.Consumer<CardView.PublicBlockingCapacity> published = expected -> {
                var views = new ArrayList<>(table.testViews());
                views.add(SpectatorObservation.project(table, 1, null));
                for (var view : views) {
                    var projected = firstBattlefield(JSON.toJsonTree(view).getAsJsonObject(), owner.getId());
                    assertEquals(projected.get("blockingCapacity"), JSON.toJsonTree(expected),
                            "Every seat and the independent public audience see the same effective blocking bound");
                }
            };
            table.publish(); published.accept(new CardView.PublicBlockingCapacity(1, false));
            long first = table.game.getNextTimestamp(), second = table.game.getNextTimestamp(), any = table.game.getNextTimestamp();
            card.addCanBlockAdditional(1, first); card.addCanBlockAdditional(2, second);
            table.publish(); published.accept(new CardView.PublicBlockingCapacity(4, false));
            var saved = table.seat(1).state();
            card.addCanBlockAny(any); table.publish();
            published.accept(new CardView.PublicBlockingCapacity(null, true));
            card.removeCanBlockAny(any); table.publish();
            published.accept(new CardView.PublicBlockingCapacity(4, false));
            card.removeCanBlockAdditional(first); card.removeCanBlockAdditional(second); table.publish();
            published.accept(new CardView.PublicBlockingCapacity(1, false));
            assertEquals(firstBattlefield(JSON.toJsonTree(saved).getAsJsonObject(), owner.getId())
                    .getAsJsonObject("blockingCapacity").get("maximum").getAsInt(), 4,
                    "The earlier publication retains its immutable effective bound after capabilities disappear");

            card.addCanBlockAny(any); assertTrue(card.turnFaceDown(true)); table.publish();
            published.accept(null);
            assertNull(card.getView().getPublicBlockingCapacity(owner.getView()));
            assertNull(MatchSession.choiceCard(card.getView(), owner.getView()).get("blockingCapacity"),
                    "Even its owner's readable concealed original cannot become a public blocking capability");

            var held = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard("Grizzly Bears"), owner);
            owner.getZone(ZoneType.Hand).add(held, null, null); held.addCanBlockAdditional(7, first);
            table.publish();
            var other = table.game.getPlayers().get(1).getView();
            assertNull(held.getView().getPublicBlockingCapacity(owner.getView()));
            assertNull(held.getView().getPublicBlockingCapacity(other));
            assertNull(MatchSession.choiceCard(held.getView(), owner.getView()).get("blockingCapacity"),
                    "A disclosed hand face is not a current battlefield blocker");
            var hiddenChoice = MatchSession.choiceCard(held.getView(), other);
            assertNull(hiddenChoice.get("blockingCapacity"));
            assertEquals(hiddenChoice.get("name"), "Face-down or hidden card");
            assertFalse(JSON.toJson(hiddenChoice).contains("Grizzly Bears"));
            for (int seat = 1; seat < 4; seat++)
                assertTrue(zone(player(JSON.toJsonTree(table.seat(seat).state()).getAsJsonObject(), owner.getId()), "Hand")
                        .getAsJsonArray("cards").isEmpty());
        }
    }

    private static JsonObject firstBattlefield(JsonObject state, int playerId) {
        return zone(player(state, playerId), "Battlefield").getAsJsonArray("cards").get(0).getAsJsonObject();
    }
}
