package forge.api;

import com.google.gson.Gson;
import com.google.gson.JsonObject;
import forge.game.combat.CombatInputState;
import forge.player.PlayerControllerHuman;
import org.testng.annotations.Test;
import java.util.*;
import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

public class CombatChoicePublicationTest {
    @Test(timeOut = 90000) public void unknownCombatFactsWaitThenPublishExactlyTheActualEngineDomain() throws Exception {
        try (var scenario = new RulesScenario("combat-choice-publication", Map.of(0, List.of("Ripscale Predator", "Reliquary Tower"),
                1, List.of("Grizzly Bears", "Grizzly Bears", "Reliquary Tower"), 2, List.of("Reliquary Tower"), 3, List.of("Reliquary Tower")))) {
            var attack = scenario.until(input -> input.seat() == 0 && type(input, "InputAttack"));
            var attacker = cards(attack.state(), attack.state().get("viewerId").getAsInt()).stream()
                    .filter(card -> text(card, "name").equals("Ripscale Predator")).findFirst().orElseThrow();
            int defender = scenario.state(1).get("viewerId").getAsInt();
            assertTrue(scenario.answer(attack, Map.of("action", "attack", "attackerKey", text(attacker, "key"), "defenderPlayerId", defender)));
            var block = scenario.until(input -> input.seat() == 1 && type(input, "InputBlock"));
            var human = (PlayerControllerHuman)scenario.table.game.getPlayers().get(1).getController();
            String[] beforeRulesRefresh = new String[1];
            var missing = HeadlessPlatform.query(scenario.table, () -> {
                var proxy = human.getInputProxy(); long sequence = proxy.getInputSequence(); var input = proxy.getInput();
                var session = scenario.session(1); var owner = human.getPlayer().getView();
                session.gui().setInputState(owner, "InputBlock", sequence, true, false);
                session.publishInput();
                var unknown = session.state();
                assertNull(unknown.get("prompt"), "Unknown offers must withhold the executable decision, not masquerade as an impossible choice");
                var actual = CombatInputState.blockers(scenario.table.game.getCombat(), human.getPlayer());
                session.gui().setCombatChoices(scenario.table.game.getPlayers().get(0).getView(), sequence, actual);
                session.gui().setCombatChoices(owner, sequence - 1, actual);
                session.publishInput(); assertNull(session.state().get("prompt"), "Foreign and stale callbacks cannot authorize the waiting host");
                proxy.publishCombatChoices(input, actual); session.publishInput();
                var ready = new Gson().toJsonTree(session.state()).getAsJsonObject();
                assertNotNull(ready.getAsJsonObject("prompt"));
                assertDomain(ready, actual, human.getPlayer().getId());
                beforeRulesRefresh[0] = text(ready.getAsJsonObject("prompt"), "id");
                // Emission-only invalidation control: no running permanent is
                // modified and the real input/sequence remain exactly the same.
                scenario.table.game.fireEvent(new forge.game.event.GameEventCardStatsChanged(human.getPlayer().getCreaturesInPlay()));
                assertSame(proxy.getInput(), input); assertEquals(proxy.getInputSequence(), sequence);
                session.publishInput(); assertNull(session.state().get("prompt"), "A same-input rules change retires the entire stale executable scope");
                var stale = new RulesScenario.Input(1, ready, ready.getAsJsonObject("prompt"));
                assertThrows(IllegalArgumentException.class, () -> session.action(request(stale, Map.of("action", "ok"))));
                return unknown;
            });
            assertNull(missing.get("prompt"));
            block = scenario.until(input -> input.seat() == 1 && type(input, "InputBlock"));
            assertNotEquals(text(block.prompt(), "id"), beforeRulesRefresh[0],
                    "The queued real input refresh restores current offers under a fresh decision instead of reviving stale keys");
            String declaration = text(block.state().getAsJsonObject("combat"), "declarationId");
            for (int count = 0; count < 2; count++) {
                var nextBlocker = cards(block.state(), defender).stream().filter(card -> text(card, "name").equals("Grizzly Bears")
                        && !card.get("blocking").getAsBoolean()).findFirst().orElseThrow();
                var currentAttacker = cards(block.state(), 0).stream().filter(card -> text(card, "name").equals("Ripscale Predator")).findFirst().orElseThrow();
                assertTrue(scenario.answer(block, Map.of("action", "block", "attackerKey", text(currentAttacker, "key"), "blockerKey", text(nextBlocker, "key"))));
                block = scenario.until(input -> input.seat() == 1 && type(input, "InputBlock"));
                assertEquals(text(block.state().getAsJsonObject("combat"), "declarationId"), declaration);
                assertEquals(text(block.state().getAsJsonObject("combat"), "blockProblem").isEmpty(), count == 1,
                        "The actual menace requirement remains current after each staged pair");
            }
        }
    }
    private static void assertDomain(JsonObject state, CombatInputState actual, int owner) {
        var identities = new HashMap<Integer, String>();
        // Physical IDs never leave the provider. Match exact current visible
        // creatures by their deterministic engine/public ordering in this fixture.
        var publicIds = cards(state, owner).stream().filter(card -> text(card, "name").equals("Grizzly Bears"))
                .map(card -> text(card, "combatId")).toList();
        for (int index = 0; index < actual.blockerCandidates().size(); index++) identities.put(actual.blockerCandidates().get(index), publicIds.get(index));
        var expected = actual.blockOptions().values().stream().flatMap(Collection::stream).map(identities::get).collect(java.util.stream.Collectors.toSet());
        var observed = state.getAsJsonObject("combat").getAsJsonArray("attackers").asList().stream().map(value -> value.getAsJsonObject())
                .flatMap(value -> value.getAsJsonArray("eligibleBlockerIds").asList().stream()).map(value -> value.getAsString()).collect(java.util.stream.Collectors.toSet());
        assertEquals(observed, expected);
    }
    private static boolean type(RulesScenario.Input input, String type) { return text(input.prompt(), "inputType").equals(type); }
    private static List<JsonObject> cards(JsonObject state, int owner) {
        return zone(player(state, owner), "Battlefield").getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject()).toList();
    }
}
