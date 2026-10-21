package forge.api;

import com.google.gson.JsonObject;
import org.testng.annotations.DataProvider;
import org.testng.annotations.Test;

import java.util.List;
import java.util.Map;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

public class LootCombatRegressionTest {
    @DataProvider public Object[][] combats() {
        return new Object[][] {
                {"Loot, Exuberant Explorer", "Grizzly Bears", "", true},
                {"Loot, the Pathfinder", "Grizzly Bears", "", true},
                {"Loot, Exuberant Explorer", "Gifted Aetherborn", "deathtouch", false},
                {"Loot, the Pathfinder", "Gifted Aetherborn", "deathtouch", false},
                {"Loot, Exuberant Explorer", "Hearthfire Hobgoblin", "double strike", false},
                {"Loot, the Pathfinder", "Hearthfire Hobgoblin", "double strike", true}
        };
    }

    private static JsonObject card(JsonObject player, String zone, String name) {
        return zone(player, zone).getAsJsonArray("cards").asList().stream()
                .map(c -> c.getAsJsonObject()).filter(c -> text(c, "name").equals(name))
                .findFirst().orElseThrow(() -> new AssertionError(name + " missing from " + zone));
    }

    @Test(dataProvider = "combats", timeOut = 90000)
    public void fourToughnessLootBlocksTwoPowerAttacker(String loot, String attackerName,
                                                       String keyword, boolean survives) throws Exception {
        String scenario = "loot-block-" + loot.replaceAll("[^A-Za-z]+", "-") + "-" + attackerName.replace(' ', '-');
        // Fervor lets the fixture creature attack on the first turn.
        try (var game = new RulesScenario(scenario, Map.of(0, List.of(attackerName, "Fervor"), 1, List.of(loot)))) {
            var main = game.until(i -> i.seat() == 0 && text(i.state(), "phaseKey").equals("MAIN1"));
            var land = card(viewer(main.state()), "Hand", "Forest");
            assertTrue(game.answer(main, Map.of("action", "card", "key", text(land, "key"))));
            var declare = game.until(i -> text(i.prompt(), "inputType").equals("InputAttack"));
            assertEquals(declare.seat(), 0);
            int attackerOwner = declare.state().get("viewerId").getAsInt();
            int defender = game.state(1).get("viewerId").getAsInt();
            var attacker = card(viewer(declare.state()), "Battlefield", attackerName);
            assertEquals(attacker.get("power").getAsInt(), 2);
            if (!keyword.isEmpty()) assertTrue(attacker.getAsJsonArray("combatKeywords").asList().stream()
                    .anyMatch(k -> k.getAsString().equals(keyword)), "The attacker exposes its effective combat ability");
            assertTrue(game.answer(declare, Map.of("action", "attack", "attackerKey", text(attacker, "key"), "defenderPlayerId", defender)));

            var blocking = game.until(i -> text(i.prompt(), "inputType").equals("InputBlock"));
            assertEquals(blocking.seat(), 1);
            var blocker = card(viewer(blocking.state()), "Battlefield", loot);
            assertEquals(blocker.get("toughness").getAsInt(), 4);
            assertEquals(blocker.get("damage").getAsInt(), 0);
            var visibleAttacker = card(player(blocking.state(), attackerOwner), "Battlefield", attackerName);
            assertTrue(game.answer(blocking, Map.of("action", "block", "attackerKey", text(visibleAttacker, "key"), "blockerKey", text(blocker, "key"))));

            var after = game.until(i -> text(i.state(), "phaseKey").equals("MAIN2"));
            var defendingPlayer = player(after.state(), defender);
            assertEquals(defendingPlayer.get("life").getAsInt(), 40, "Loot's block prevents damage to its controller");
            if (survives) {
                var survivor = card(defendingPlayer, "Battlefield", loot);
                assertEquals(survivor.get("toughness").getAsInt(), 4, "Damage does not reduce toughness");
                assertEquals(survivor.get("damage").getAsInt(), loot.equals("Loot, the Pathfinder") && keyword.isEmpty() ? 0 : 2);
                assertEquals(count(defendingPlayer, "Graveyard"), 0);
                int turn = after.state().get("turn").getAsInt();
                var nextTurn = game.until(i -> i.state().get("turn").getAsInt() > turn);
                assertEquals(card(player(nextTurn.state(), defender), "Battlefield", loot).get("damage").getAsInt(), 0,
                        "Combat damage clears during cleanup before the next turn");
            } else {
                assertEquals(count(defendingPlayer, "Battlefield"), 0);
                card(defendingPlayer, "Graveyard", loot);
            }
        }
    }
}
