package forge.api;

import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import forge.StaticData;
import forge.deck.Deck;
import forge.game.GameRules;
import forge.game.GameType;
import forge.game.ability.AbilityKey;
import forge.game.card.Card;
import forge.game.card.CardCopyService;
import org.testng.annotations.Test;

import java.nio.file.Path;
import java.util.HashMap;
import java.util.HashSet;
import java.util.List;
import java.util.Map;
import java.util.UUID;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Actual registered commanders and controlled rules operations, not names or tax heuristics. */
public class CommanderCombatProjectionTest {
    private static final Gson JSON = new GsonBuilder().serializeNulls().create();
    private static final Path RESOURCES = Path.of("../forge-gui/res");

    @Test(timeOut = 90000)
    public void partnersAndSameNamedCommandersHaveSharedAudienceButDistinctRulesLineages() throws Exception {
        try (var scenario = new RulesScenario("commander-lineage-partners", Map.of(), "Forest",
                List.of("Pir, Imaginative Rascal", "Toothy, Imaginary Friend"))) {
            main(scenario);
            var first = scenario.state(0);
            assertEquals(first.getAsJsonObject("commanderCombat").get("damageLimit").getAsInt(), 21);
            var expected = rows(first, 0);
            assertEquals(expected.size(), 8, "Four actual two-commander designations, including same names on other seats");
            var ids = new HashSet<String>();
            for (var row : expected.values()) {
                String id = text(row, "commanderId");
                assertEquals(UUID.fromString(id).toString(), id); assertTrue(ids.add(id));
            }
            for (int audience = 0; audience < 4; audience++) {
                var state = scenario.state(audience);
                for (int recipient = 0; recipient < 4; recipient++) assertEquals(rows(state, recipient), expected,
                        "Every audience and recipient uses the same owner/partner lineage table");
                for (int owner = 0; owner < 4; owner++) for (var value : zone(player(state, owner), "Command").getAsJsonArray("cards")) {
                    var face = value.getAsJsonObject();
                    assertEquals(text(face, "commanderId"), text(expected.get(owner + ":" + text(face, "name")), "commanderId"));
                    assertNotEquals(text(face, "commanderId"), text(face, "visualId"), "A rules lineage is not an occurrence/action key");
                }
            }
            var spectator = JSON.toJsonTree(SpectatorObservation.project(scenario.table, 1, null)).getAsJsonObject();
            assertEquals(rows(spectator, 0), expected);
            assertEquals(spectator.get("commanderCombat"), first.get("commanderCombat"));
        }
    }

    @Test(timeOut = 90000)
    public void stealingRenamingAndZoneChangesPreserveOnlyTheActualCommandersPublicBinding() throws Exception {
        try (var scenario = new RulesScenario("commander-lineage-movement", Map.of())) {
            main(scenario);
            var table = scenario.table;
            var owner = table.game.getRegisteredPlayers().get(0);
            var thief = table.game.getRegisteredPlayers().get(1);
            var recipient = table.game.getRegisteredPlayers().get(2);
            Card designated = owner.getCommanders().get(0);
            String lineage = table.commanderCombat.publicId(designated.getView());
            assertNotNull(lineage);
            Card battlefield = HeadlessPlatform.query(table, () -> {
                var moved = table.game.getAction().moveToPlay(designated, owner, null, AbilityKey.newMap());
                table.publish(); return moved;
            });
            assertEquals(table.commanderCombat.publicId(battlefield.getView()), lineage);
            var beforeSteal = scenario.state(0);
            var oldView = battlefield.getView();
            HeadlessPlatform.query(table, () -> {
                battlefield.addChangedName("A different public name", false, table.game.getNextTimestamp(), 0);
                battlefield.setController(thief, table.game.getNextTimestamp());
                table.game.getAction().controllerChangeZoneCorrection(battlefield);
                recipient.addCommanderDamage(battlefield.getRealCommander(), 7);
                table.publish(); return null;
            });
            assertEquals(table.commanderCombat.publicId(battlefield.getView()), lineage);
            var stolen = scenario.state(0);
            var stolenFace = zone(player(stolen, 1), "Battlefield").getAsJsonArray("cards").asList().stream()
                    .map(value -> value.getAsJsonObject()).filter(card -> text(card, "name").equals("A different public name")).findFirst().orElseThrow();
            assertEquals(text(stolenFace, "commanderId"), lineage);
            var damage = rows(stolen, 2).values().stream().filter(row -> text(row, "commanderId").equals(lineage)).findFirst().orElseThrow();
            assertEquals(damage.get("ownerId").getAsInt(), 0, "Stealing the body does not change the registered commander's owner");
            assertEquals(damage.get("damage").getAsInt(), 7);
            assertEquals(rows(beforeSteal, 2).values().stream().filter(row -> text(row, "commanderId").equals(lineage)).findFirst().orElseThrow()
                    .get("damage").getAsInt(), 0, "Later rules changes cannot mutate the previously published damage rows");

            Card copy = HeadlessPlatform.query(table, () -> {
                // Copy the real copiable characteristics into a different rules
                // card. Commander designation is deliberately not copiable.
                var copied = CardCopyService.copyStats(battlefield, recipient, true);
                assertFalse(copied.isCommander());
                copied = table.game.getAction().moveToPlay(copied, recipient, null, AbilityKey.newMap());
                table.publish(); return copied;
            });
            assertNull(table.commanderCombat.publicId(copy.getView()), "A same-named copy is not the registered commander");
            var lki = CardCopyService.getLKICopy(battlefield);
            assertEquals(lki.getId(), battlefield.getId()); assertTrue(lki.isCommander());
            assertNull(table.commanderCombat.publicId(lki.getView()), "Equal physical IDs do not authorize a historical face");

            HeadlessPlatform.query(table, () -> { assertTrue(battlefield.turnFaceDown(true)); table.publish(); return null; });
            assertNull(table.commanderCombat.publicId(battlefield.getView()));
            for (int audience = 0; audience < 4; audience++) {
                var concealed = scenario.state(audience);
                var face = zone(player(concealed, 1), "Battlefield").getAsJsonArray("cards").asList().stream()
                        .map(value -> value.getAsJsonObject()).filter(card -> card.get("faceDown").getAsBoolean()).findFirst().orElseThrow();
                assertFalse(face.has("commanderId"), "Neither owner knowledge nor public battlefield position discloses a hidden lineage binding");
            }
            Card hand = HeadlessPlatform.query(table, () -> {
                battlefield.forceTurnFaceUp();
                owner.setCommanderReplacementSuppressed(true);
                try { var moved = table.game.getAction().moveToHand(battlefield, null); table.publish(); return moved; }
                finally { owner.setCommanderReplacementSuppressed(false); }
            });
            assertNull(table.commanderCombat.publicId(hand.getView()));
            assertNull(table.commanderCombat.publicId(oldView), "A departed prior battlefield portrait cannot retain binding authority");
            for (var value : zone(player(scenario.state(0), 0), "Hand").getAsJsonArray("cards"))
                assertFalse(value.getAsJsonObject().has("commanderId"), "A private hand portrait does not inherit public commander binding");
            Card library = HeadlessPlatform.query(table, () -> {
                owner.setCommanderReplacementSuppressed(true);
                try { var moved = table.game.getAction().moveToLibrary(hand, null); table.publish(); return moved; }
                finally { owner.setCommanderReplacementSuppressed(false); }
            });
            assertNull(table.commanderCombat.publicId(library.getView()));
            Card returned = HeadlessPlatform.query(table, () -> {
                var moved = table.game.getAction().moveToPlay(library, owner, null, AbilityKey.newMap()); table.publish(); return moved;
            });
            assertEquals(table.commanderCombat.publicId(returned.getView()), lineage,
                    "The actual registered commander keeps one rules lineage across hidden zones and a new public occurrence");
            assertEquals(rows(scenario.state(0), 2).values().stream().filter(row -> text(row, "commanderId").equals(lineage))
                    .findFirst().orElseThrow().get("damage").getAsInt(), 7);
        }
    }

    @Test(timeOut = 90000)
    public void thePublishedLimitComesFromTheActualLossRuleRatherThanCommanderPresence() throws Exception {
        for (var variant : List.of(GameType.Brawl, GameType.TinyLeaders, GameType.Oathbreaker)) {
            var rules = new GameRules(GameType.Constructed); rules.addAppliedVariant(variant);
            assertTrue(rules.hasCommander()); assertNull(rules.getCommanderCombatDamageLimit());
        }
        var commander = new GameRules(GameType.Constructed); commander.addAppliedVariant(GameType.Commander);
        assertEquals(commander.getCommanderCombatDamageLimit(), Integer.valueOf(21));
        if (StaticData.instance() == null) EngineResources.load(RESOURCES);
        var deck = new Deck("Disabled commander damage");
        deck.getMain().add(StaticData.instance().getCommonCards().getCard("Forest"), 60);
        var players = List.of(MatchTable.automated(deck, "Constructed", "First"), MatchTable.automated(deck, "Constructed", "Second"));
        try (var table = new MatchTable("Constructed", players, RESOURCES, ApiTestWorkspace.evidence("commander-limit-disabled"), true)) {
            var publicState = JSON.toJsonTree(SpectatorObservation.project(table, 0, null)).getAsJsonObject();
            assertTrue(publicState.has("commanderCombat"));
            assertTrue(publicState.getAsJsonObject("commanderCombat").get("damageLimit").isJsonNull(),
                    "A new provider explicitly reports disabled rules; absence remains reserved for an older provider");
            assertTrue(rows(publicState, 0).isEmpty());
        }
    }

    private static void main(RulesScenario scenario) throws Exception {
        scenario.until(input -> input.seat() == 0 && text(input.prompt(), "inputType").equals("InputPassPriority")
                && text(input.state(), "phaseKey").equals("MAIN1"));
    }
    private static Map<String, JsonObject> rows(JsonObject state, int recipient) {
        var result = new HashMap<String, JsonObject>();
        for (var value : player(state, recipient).getAsJsonArray("commanderDamage")) {
            var row = value.getAsJsonObject();
            assertNull(result.put(row.get("ownerId").getAsInt() + ":" + text(row, "name"), row));
        }
        return result;
    }
}
