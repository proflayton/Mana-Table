package forge.api;

import com.google.gson.Gson;
import com.google.gson.JsonObject;
import org.testng.annotations.DataProvider;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.util.Collections;
import java.util.List;
import java.util.Map;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Actual casts, target choices, payment and counter resolution through the
 * production rules boundary; these are not hypothetical source target domains. */
public class PendingOperationProjectionTest {
    private static final Gson JSON = new Gson();

    @DataProvider public Object[][] effects() {
        return new Object[][] {{"Lightning Bolt", "Mountain", "Damage", 3},
                {"Sign in Blood", "Swamp", "Draw", 2}, {"Doom Blade", "Swamp", "Destroy", null}};
    }

    @Test(groups = "canonical-interaction", dataProvider = "effects", timeOut = 90000)
    public void pendingEffectsKeepTheCommittedRecipientAndCounterTargetsKeepExactStackIdentity(
            String spell, String land, String effect, Integer amount) throws Exception {
        try (var game = new RulesScenario("pending-operation-" + spell.replace(' ', '-'), Map.of(
                0, List.of(land, land), 1, List.of("Island", "Island"),
                2, List.of("Colossal Dreadmaw"), 3, List.of("Grizzly Bears")), spell, "Rhys the Redeemed",
                Map.of(1, Collections.nCopies(99, "Counterspell")))) {
            var main = game.until(input -> input.seat() == 0 && priority(input) && text(input.state(), "phaseKey").equals("MAIN1"));
            start(game, main, spell);
            var pending = finish(game, 0, 1, effect.equals("Destroy") ? "Colossal Dreadmaw" : null,
                    game.state(1).get("viewerId").getAsInt());
            var stacked = pending.state().getAsJsonArray("stack").get(0).getAsJsonObject();
            var graph = stacked.getAsJsonObject("operationGraph");
            assertEquals(text(graph, "knowledge"), "Known");
            var first = graph.getAsJsonArray("operations").get(0).getAsJsonObject();
            assertEquals(text(first, "effect"), effect);
            if (amount == null) assertEquals(text(first, "amountKnowledge"), "Unknown");
            else assertEquals(first.get("amount").getAsInt(), amount.intValue());
            var selected = first.getAsJsonObject("targets");
            assertEquals(selected.get("min").getAsInt(), 1); assertEquals(selected.get("max").getAsInt(), 1);
            assertEquals(selected.getAsJsonArray("candidates").size(), 1,
                    "The stack cannot substitute every still-legal recipient for the one actually chosen");
            var recipient = selected.getAsJsonArray("candidates").get(0).getAsJsonObject();
            if (effect.equals("Destroy")) {
                var target = permanent(pending.state(), "Colossal Dreadmaw");
                assertEquals(text(recipient, "cardId"), text(target, "visualId"));
                assertEquals(recipient.get("controllerId").getAsInt(), game.state(2).get("viewerId").getAsInt(),
                        "Opponent removal can benefit the responding seat by answering a third player's creature");
            } else assertEquals(recipient.get("playerId").getAsInt(), game.state(1).get("viewerId").getAsInt());
            for (var operation : graph.getAsJsonArray("operations"))
                assertTrue(operation.getAsJsonObject().getAsJsonArray("costs").isEmpty(), "Committed costs have already been paid");

            var response = game.until(input -> input.seat() == 1 && priority(input) && input.state().getAsJsonArray("stack").size() == 1);
            String spellId = text(response.state().getAsJsonArray("stack").get(0).getAsJsonObject(), "id");
            var option = game.table.game.getAction().readState(() -> {
                var actual = game.table.game.getStack().iterator().next();
                return DecisionSemantics.options(List.of(actual.getView()), ignored -> null,
                        game.table.game.getRegisteredPlayers().get(1), game.table.identities::id).get(0);
            });
            var offered = JSON.toJsonTree(option).getAsJsonObject().getAsJsonObject("target");
            assertEquals(text(offered, "stackItemId"), spellId,
                    "The exact counter target option has a structural identity independent of its description");
            int responseHand = count(viewer(response.state()), "Hand");
            start(game, response, "Counterspell");
            var countered = finish(game, 1, 2, null, -1);
            var counter = countered.state().getAsJsonArray("stack").asList().stream().map(value -> value.getAsJsonObject())
                    .filter(value -> text(value, "name").equals("Counterspell")).findFirst().orElseThrow();
            var counterOperation = counter.getAsJsonObject("operationGraph").getAsJsonArray("operations").get(0).getAsJsonObject();
            assertEquals(text(counterOperation, "effect"), "CounterSpell");
            var target = counterOperation.getAsJsonObject("targets").getAsJsonArray("candidates").get(0).getAsJsonObject();
            assertEquals(text(target, "kind"), "Spell"); assertEquals(text(target, "stackItemId"), spellId);
            Files.writeString(game.evidence.resolve("committed-counter.json"), JSON.toJson(countered.state()));
            var resolved = game.until(input -> priority(input) && input.state().getAsJsonArray("stack").isEmpty());
            assertEquals(player(resolved.state(), game.state(1).get("viewerId").getAsInt()).get("life").getAsInt(), 40);
            assertNotNull(permanent(resolved.state(), "Colossal Dreadmaw"));
            assertEquals(count(player(resolved.state(), game.state(1).get("viewerId").getAsInt()), "Hand"), responseHand - 1,
                    "Countering the actual pending draw prevents its cards, as well as its linked life loss");
            assertEquals(count(player(resolved.state(), game.state(0).get("viewerId").getAsInt()), "Graveyard"), 1,
                    "The actual counter removes the spell rather than applying its committed damage/draw/removal");
        }
    }

    private static void start(RulesScenario game, Input input, String name) {
        var source = zone(viewer(input.state()), "Hand").getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "name").equals(name)).findFirst().orElseThrow();
        String key = text(source, "key");
        var catalog = JSON.toJsonTree(game.table.seat(input.seat()).sourceActions(request(input, Map.of("key", key)))).getAsJsonObject();
        var action = catalog.getAsJsonArray("actions").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "kind").equals("cast")).findFirst().orElseThrow();
        assertTrue(game.answer(input, Map.of("action", "useSourceAction", "key", key, "actionId", text(action, "id"))));
    }

    private static Input finish(RulesScenario game, int actor, int stackSize, String targetCard, int targetPlayer) throws Exception {
        for (int step = 0; step < 12; step++) {
            var input = game.until(next -> next.seat() == actor && (priority(next) && next.state().getAsJsonArray("stack").size() == stackSize
                    || text(next.prompt(), "inputType").equals("InputSelectTargets")
                    || text(next.prompt(), "inputType").startsWith("InputPayMana") || text(next.prompt(), "kind").equals("choice")));
            if (priority(input)) return input;
            if (text(input.prompt(), "inputType").equals("InputSelectTargets")) {
                assertTrue(game.answer(input, targetCard == null ? Map.of("action", "player", "playerId", targetPlayer)
                        : Map.of("action", "card", "key", text(permanent(input.state(), targetCard), "key"))));
            } else if (text(input.prompt(), "kind").equals("choice")) {
                int index = input.prompt().getAsJsonArray("choices").get(0).getAsJsonObject().get("index").getAsInt();
                assertTrue(game.answer(input, Map.of("choices", List.of(index))));
            } else game.answer(input, Map.of("action", "ok")); // Auto may supersede a republished payment.
        }
        throw new AssertionError("The actual cast did not reach its committed stack occurrence");
    }

    private static boolean priority(Input input) { return text(input.prompt(), "inputType").equals("InputPassPriority"); }
    private static JsonObject permanent(JsonObject state, String name) {
        return state.getAsJsonArray("players").asList().stream().map(value -> value.getAsJsonObject())
                .flatMap(player -> zone(player, "Battlefield").getAsJsonArray("cards").asList().stream()).map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "name").equals(name)).findFirst().orElseThrow();
    }
}
