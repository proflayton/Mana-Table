package forge.api;

import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import org.testng.annotations.DataProvider;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.util.List;
import java.util.Map;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** A departed defender does not remove its attackers from combat or retarget them. */
public class PlaneswalkerCombatRegressionTest {
    @DataProvider public Object[][] blocks() { return new Object[][] {{false}, {true}}; }

    @Test(groups = "canonical-interaction", dataProvider = "blocks", timeOut = 90000)
    public void lethalCombatDamageLeavesAnAttackWithoutItsDepartedPlaneswalkerDefender(boolean blocked) throws Exception {
        try (var scenario = new RulesScenario("planeswalker-lethal-combat" + (blocked ? "-blocked" : ""), Map.of(
                0, List.of("Charging Monstrosaur", "Reliquary Tower"),
                1, blocked ? List.of("Mountain", "Mountain", "Mountain", "Mountain", "Reliquary Tower", "Darksteel Myr")
                        : List.of("Mountain", "Mountain", "Mountain", "Mountain", "Reliquary Tower"),
                2, List.of("Reliquary Tower"), 3, List.of("Reliquary Tower")),
                "Chandra, the Firebrand", "Progenitus")) {
            var cast = scenario.until(input -> input.seat() == 1 && text(input.state(), "phaseKey").equals("MAIN1")
                    && input.state().get("activePlayerId").getAsInt() == input.state().get("viewerId").getAsInt()
                    && text(input.prompt(), "inputType").equals("InputPassPriority"));
            var source = card(viewer(cast.state()), "Hand", "Chandra, the Firebrand");
            var catalog = new GsonBuilder().create().toJsonTree(scenario.table.seat(1).sourceActions(request(cast,
                    Map.of("key", text(source, "key"))))).getAsJsonObject();
            var action = catalog.getAsJsonArray("actions").asList().stream().map(value -> value.getAsJsonObject())
                    .filter(value -> text(value, "kind").equals("cast")).findFirst().orElseThrow();
            assertTrue(scenario.answer(cast, Map.of("action", "useSourceAction", "key", text(source, "key"), "actionId", text(action, "id"))));
            var payment = scenario.until(input -> input.seat() == 1 && text(input.prompt(), "inputType").startsWith("InputPayMana"));
            assertTrue(payment.prompt().get("okEnabled").getAsBoolean());
            assertTrue(scenario.answer(payment, Map.of("action", "ok")));
            var declare = scenario.until(input -> input.seat() == 0 && text(input.prompt(), "inputType").equals("InputAttack"));
            assertEquals(declare.seat(), 0);
            int owner = declare.state().get("viewerId").getAsInt();
            int defendingPlayer = scenario.state(1).get("viewerId").getAsInt();
            var attacker = card(viewer(declare.state()), "Battlefield", "Charging Monstrosaur");
            var walker = card(player(declare.state(), defendingPlayer), "Battlefield", "Chandra, the Firebrand");
            assertEquals(walker.getAsJsonObject("counters").get("Loyalty").getAsInt(), 3);
            assertTrue(scenario.answer(declare, Map.of("action", "attack", "attackerKey", text(attacker, "key"),
                    "defenderKey", text(walker, "key"))));
            var assigned = scenario.until(input -> input.state().has("combat") && !input.state().get("combat").isJsonNull()
                    && input.state().getAsJsonObject("combat").getAsJsonArray("attackers").size() == 1);
            var liveAttack = assigned.state().getAsJsonObject("combat").getAsJsonArray("attackers").get(0).getAsJsonObject();
            assertTrue(liveAttack.get("hasDefender").getAsBoolean());
            assertEquals(text(liveAttack.getAsJsonObject("defender"), "id"), text(walker, "combatId"));
            String blockerId = "";
            if (blocked) {
                var blocking = scenario.until(input -> input.seat() == 1 && text(input.prompt(), "inputType").equals("InputBlock"));
                var blocker = card(viewer(blocking.state()), "Battlefield", "Darksteel Myr");
                blockerId = text(blocker, "combatId");
                var blockingAttacker = card(player(blocking.state(), owner), "Battlefield", "Charging Monstrosaur");
                assertTrue(scenario.answer(blocking, Map.of("action", "block", "attackerKey", text(blockingAttacker, "key"),
                        "blockerKey", text(blocker, "key"))));
            }
            var after = scenario.until(input -> text(input.prompt(), "kind").equals("allocate")
                    || text(input.state(), "phaseKey").equals("COMBAT_DAMAGE")
                    && count(player(input.state(), defendingPlayer), "Graveyard") == 1);
            if (text(after.prompt(), "kind").equals("allocate")) {
                assertTrue(blocked);
                var choices = after.prompt().getAsJsonArray("choices");
                assertEquals(choices.size(), 2);
                assertTrue(scenario.answer(after, Map.of("values", choices.asList().stream().map(value ->
                        value.getAsJsonObject().get("isDefender").getAsBoolean() ? 4 : 1).toList())));
                after = scenario.until(input -> text(input.state(), "phaseKey").equals("COMBAT_DAMAGE")
                        && count(player(input.state(), defendingPlayer), "Graveyard") == 1);
            }
            card(player(after.state(), defendingPlayer), "Graveyard", "Chandra, the Firebrand");
            assertEquals(player(after.state(), defendingPlayer).get("life").getAsInt(), 40,
                    "Combat damage to a planeswalker must not be redirected to its controller");
            var json = new GsonBuilder().setPrettyPrinting().serializeNulls().create();
            for (int audience = 0; audience < 4; audience++) {
                var state = scenario.state(audience);
                Files.writeString(scenario.evidence.resolve("after-lethal-seat-" + audience + ".json"), json.toJson(state));
                assertDepartedDefender(state, text(attacker, "combatId"), defendingPlayer, blockerId);
            }
            long boardRevision = after.state().get("boardRevision").getAsLong();
            var publicState = json.toJsonTree(scenario.table.game.getAction().readState(() ->
                    SpectatorObservation.project(scenario.table, boardRevision, null))).getAsJsonObject();
            Files.writeString(scenario.evidence.resolve("after-lethal-spectator.json"), json.toJson(publicState));
            assertDepartedDefender(publicState, text(attacker, "combatId"), defendingPlayer, blockerId);
            if (blocked) assertEquals(card(player(after.state(), defendingPlayer), "Battlefield", "Darksteel Myr").get("damage").getAsInt(), 1,
                    "The real indestructible blocker survives its assigned trample damage");
            var main = scenario.until(input -> input.seat() == 0 && text(input.state(), "phaseKey").equals("MAIN2")
                    && text(input.prompt(), "inputType").equals("InputPassPriority"));
            assertNotNull(card(player(main.state(), owner), "Battlefield", "Charging Monstrosaur"));
            assertEquals(player(main.state(), defendingPlayer).get("life").getAsInt(), 40);
        }
    }

    private static void assertDepartedDefender(JsonObject state, String attackerId, int playerId, String blockerId) {
        var combat = state.getAsJsonObject("combat");
        var attack = combat.getAsJsonArray("attackers").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "cardId").equals(attackerId)).findFirst().orElseThrow();
        assertFalse(attack.get("hasDefender").getAsBoolean());
        assertTrue(attack.get("defender").isJsonNull(), "The engine's absent-defender placeholder is not a real card occurrence: " + attack);
        assertEquals(attack.get("defendingPlayerId").getAsInt(), playerId);
        assertEquals(attack.get("blocked").getAsBoolean(), !blockerId.isEmpty());
        assertEquals(attack.getAsJsonArray("blockerIds").asList().stream().map(value -> value.getAsString()).toList(),
                blockerId.isEmpty() ? List.of() : List.of(blockerId));
        assertTrue(attack.getAsJsonArray("eligibleBlockerIds").isEmpty());
        assertTrue(combat.getAsJsonArray("defenders").asList().stream().map(value -> value.getAsJsonObject())
                .noneMatch(value -> text(value, "kind").equals("card")), "The departed planeswalker is not a current attack choice");
        assertFalse(state.toString().contains("<Nothing>"), "Internal combat helpers have no public face, name or identity");
    }

    private static JsonObject card(JsonObject player, String zone, String name) {
        return zone(player, zone).getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "name").equals(name)).findFirst().orElseThrow();
    }
}
