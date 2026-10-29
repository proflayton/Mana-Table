package forge.api;

import com.google.gson.JsonObject;
import org.testng.annotations.Test;
import java.util.*;
import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Large declarations still use one fresh authoritative command per pair;
 * completing the declaration remains a separate player decision. */
public class FiniteCombatStagingTest {
    @Test(groups = "canonical-interaction", timeOut = 180000)
    public void oneHundredRealBlockTogglesRefreshCurrentKeysWithoutCompletingTheDeclaration() throws Exception {
        var field = new ArrayList<>(Collections.nCopies(100, "Memnite")); field.add("Reliquary Tower");
        try (var scenario = new RulesScenario("finite-combat-100", Map.of(0, field, 1, field,
                2, List.of("Reliquary Tower"), 3, List.of("Reliquary Tower")), RulesScenario.TraceMode.SCALE)) {
            try {
            var attacking = scenario.until(input -> input.seat() == 0 && type(input, "InputAttack"));
            int defender = scenario.state(1).get("viewerId").getAsInt();
            for (int index = 0; index < 100; index++) {
                var card = cards(attacking.state(), attacking.state().get("viewerId").getAsInt()).stream()
                        .filter(value -> text(value, "name").equals("Memnite") && !value.get("attacking").getAsBoolean()).findFirst().orElseThrow();
                assertTrue(scenario.answer(attacking, Map.of("action", "attack", "attackerKey", text(card, "key"), "defenderPlayerId", defender)));
                attacking = scenario.until(input -> input.seat() == 0 && type(input, "InputAttack"));
                assertEquals(attacking.state().getAsJsonObject("combat").getAsJsonArray("attackers").size(), index + 1);
            }
            assertTrue(scenario.answer(attacking, Map.of("action", "ok")));
            var blocking = scenario.until(input -> input.seat() == 1 && type(input, "InputBlock"));
            String declaration = text(blocking.state().getAsJsonObject("combat"), "declarationId"); assertFalse(declaration.isEmpty());
            var attackers = blocking.state().getAsJsonObject("combat").getAsJsonArray("attackers").asList().stream()
                    .map(value -> text(value.getAsJsonObject(), "cardId")).toList();
            var blockers = cards(blocking.state(), defender).stream().filter(value -> text(value, "name").equals("Memnite"))
                    .map(value -> text(value, "combatId")).toList();
            assertEquals(attackers.size(), 100); assertEquals(blockers.size(), 100);
            var promptIds = new HashSet<String>();
            for (int index = 0; index < 100; index++) {
                var combat = blocking.state().getAsJsonObject("combat");
                assertEquals(text(combat, "declarationId"), declaration);
                assertTrue(promptIds.add(text(blocking.prompt(), "id")), "Every pair is staged through newly published current authority");
                final int at = index;
                var offered = combat.getAsJsonArray("attackers").asList().stream().map(value -> value.getAsJsonObject())
                        .filter(value -> text(value, "cardId").equals(attackers.get(at))).findFirst().orElseThrow();
                assertTrue(offered.getAsJsonArray("eligibleBlockerIds").asList().stream().anyMatch(value -> value.getAsString().equals(blockers.get(at))));
                var attack = cards(blocking.state(), attacking.state().get("viewerId").getAsInt()).stream()
                        .filter(value -> text(value, "combatId").equals(attackers.get(at))).findFirst().orElseThrow();
                var block = cards(blocking.state(), defender).stream().filter(value -> text(value, "combatId").equals(blockers.get(at))).findFirst().orElseThrow();
                assertTrue(scenario.answer(blocking, Map.of("action", "block", "attackerKey", text(attack, "key"), "blockerKey", text(block, "key"))));
                blocking = scenario.until(input -> input.seat() == 1 && type(input, "InputBlock"));
                assertEquals(blocking.state().getAsJsonObject("combat").getAsJsonArray("attackers").asList().stream()
                        .map(value -> value.getAsJsonObject().getAsJsonArray("blockerIds").size()).mapToInt(Integer::intValue).sum(), index + 1);
            }
            assertEquals(text(blocking.state(), "phaseKey"), "COMBAT_DECLARE_BLOCKERS");
            assertTrue(blocking.prompt().get("okEnabled").getAsBoolean());
            assertEquals(text(blocking.state().getAsJsonObject("combat"), "declarationId"), declaration);
            assertTrue(blocking.state().getAsJsonObject("combat").getAsJsonArray("attackers").asList().stream()
                    .allMatch(value -> value.getAsJsonObject().getAsJsonArray("blockerIds").size() == 1));
            assertTrue(scenario.answer(blocking, Map.of("action", "ok")), "Only the separate final command completes the100 staged pairs");
            scenario.until(input -> type(input, "InputPassPriority") && text(input.state(), "phaseKey").equals("COMBAT_DECLARE_BLOCKERS"));
            } catch (Exception | AssertionError failure) { scenario.failed(failure); throw failure; }
        }
    }
    private static boolean type(RulesScenario.Input input, String type) { return text(input.prompt(), "inputType").equals(type); }
    private static List<JsonObject> cards(JsonObject state, int owner) {
        return zone(player(state, owner), "Battlefield").getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject()).toList();
    }
}
