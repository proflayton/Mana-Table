package forge.api;

import java.util.*;
import org.testng.annotations.DataProvider;
import org.testng.annotations.Test;
import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

public class CombatAutoRegressionTest {
    @DataProvider public Object[][] pairs() {
        return new Object[][] {{"Raging Goblin", "Grizzly Bears"}, {"Skyknight Legionnaire", "Giant Spider"}};
    }
    @Test(dataProvider = "pairs", timeOut = 90000)
    public void autoPriorityCannotSkipALegalBlock(String attackerName, String blockerName) throws Exception {
        try (var game = new RulesScenario("auto-block-" + attackerName.replace(' ', '-'), Map.of(0, List.of(attackerName), 1, List.of(blockerName)))) {
            var declare = game.until(i -> text(i.prompt(), "inputType").equals("InputAttack"));
            assertEquals(declare.seat(), 0);
            var attacker = zone(viewer(declare.state()), "Battlefield").getAsJsonArray("cards").get(0).getAsJsonObject();
            int defender = game.state(1).get("viewerId").getAsInt();
            assertTrue(game.answer(declare, Map.of("action", "attack", "attackerKey", text(attacker, "key"), "defenderPlayerId", defender)));
            var blocking = game.until(i -> text(i.prompt(), "inputType").equals("InputBlock"));
            assertEquals(blocking.seat(), 1);
            assertFalse(blocking.prompt().get("canAutoPass").getAsBoolean());
            game.rejected(blocking, "passIfNoResponse", "This response window needs your decision");
            var attack = blocking.state().getAsJsonObject("combat").getAsJsonArray("attackers").get(0).getAsJsonObject();
            var blocker = zone(viewer(blocking.state()), "Battlefield").getAsJsonArray("cards").get(0).getAsJsonObject();
            assertTrue(attack.getAsJsonArray("eligibleBlockerIds").asList().stream().anyMatch(c -> c.getAsString().equals(text(blocker, "combatId"))));
            // No input is sent while polling: the declaration must wait indefinitely.
            Thread.sleep(200);
            assertEquals(text(game.state(1).getAsJsonObject("prompt"), "id"), text(blocking.prompt(), "id"));
            var visibleAttacker = zone(player(blocking.state(), declare.state().get("viewerId").getAsInt()), "Battlefield").getAsJsonArray("cards").get(0).getAsJsonObject();
            assertTrue(game.answer(blocking, Map.of("action", "block", "attackerKey", text(visibleAttacker, "key"), "blockerKey", text(blocker, "key"))));
            var damage = game.until(i -> text(i.state(), "phaseKey").equals("COMBAT_DAMAGE"));
            assertEquals(player(damage.state(), defender).get("life").getAsInt(), 40, "The declared block prevents damage to the defender");
            assertEquals(count(player(damage.state(), declare.state().get("viewerId").getAsInt()), "Graveyard"), 1, "The blocker kills the attacker through real combat damage");
        }
    }
}
