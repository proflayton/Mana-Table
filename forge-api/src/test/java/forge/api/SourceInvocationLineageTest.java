package forge.api;

import com.google.gson.Gson;
import com.google.gson.JsonObject;
import forge.game.cost.CostPayLife;
import forge.game.zone.ZoneType;
import forge.player.PlayerControllerHuman;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.util.List;
import java.util.Map;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Fresh invocation copies must retain real costs and the engine's activation ledger. */
public class SourceInvocationLineageTest {
    private static final Gson JSON = new Gson();

    @Test(timeOut = 90000)
    public void repeatedSourceReadsCannotRenewARealOncePerTurnActivation() throws Exception {
        try (var game = new RulesScenario("source-query-rootwalla-ledger", Map.of(0,
                List.of("Basking Rootwalla", "Forest", "Forest", "Forest", "Forest")))) {
            var input = main(game);
            for (int amount = 1; amount <= 2; amount++) {
                var forest = zone(viewer(input.state()), "Battlefield").getAsJsonArray("cards").asList().stream()
                        .map(value -> value.getAsJsonObject()).filter(value -> text(value, "name").equals("Forest")
                                && !value.get("tapped").getAsBoolean()).findFirst().orElseThrow();
                assertTrue(game.answer(input, command(query(game, input, forest), 0)));
                final int expected = amount;
                input = game.until(next -> next.seat() == 0 && priority(next)
                        && viewer(next.state()).getAsJsonObject("mana").get("G").getAsInt() == expected);
            }
            var catalog = query(game, input, card(input.state(), 0, "Battlefield", "Basking Rootwalla"));
            assertEquals(catalog.getAsJsonArray("actions").size(), 1);
            assertTrue(game.answer(input, command(catalog, 0)));
            var settled = game.until(next -> next.seat() == 0 && (priority(next)
                    || text(next.prompt(), "inputType").startsWith("InputPayMana")));
            if (!priority(settled)) assertTrue(game.answer(settled, Map.of("action", "ok")));
            var pumped = game.until(next -> next.seat() == 0 && priority(next)
                    && text(card(next.state(), 0, "Battlefield", "Basking Rootwalla"), "power").equals("3"));
            HeadlessPlatform.query(game.table, () -> {
                var actor = game.table.game.getRegisteredPlayers().get(0);
                var source = actor.getCardsIn(ZoneType.Battlefield).stream()
                        .filter(value -> value.getName().equals("Basking Rootwalla")).findFirst().orElseThrow();
                var original = source.getNonManaAbilities().stream()
                        .filter(ability -> !ability.isSpell() && ability.getApi() == forge.game.ability.ApiType.Pump)
                        .findFirst().orElseThrow();
                assertEquals(original.getActivationsThisTurn(), 1, "The actual original ability owns the activation, not a query/invocation copy");
                assertEquals(actor.getCardsIn(ZoneType.Battlefield).stream()
                        .filter(value -> value.getName().equals("Forest") && !value.isTapped()).count(), 2L,
                        "Enough real resources remain for another pump; the activation limit is decisive");
                var before = game.state(0);
                for (int read = 0; read < 3; read++) assertTrue(query(game, pumped,
                        card(before, 0, "Battlefield", "Basking Rootwalla")).getAsJsonArray("actions").isEmpty());
                assertEquals(original.getActivationsThisTurn(), 1);
                assertEquals(game.state(0), before);
                Files.writeString(game.evidence.resolve("original-activation-ledger.json"), JSON.toJson(Map.of(
                        "originalAbilityId", original.getId(), "activations", original.getActivationsThisTurn(), "state", before)));
                return null;
            });
        }
    }

    @Test(timeOut = 90000)
    public void anAlternativeCostKeepsItsRouteThroughQueryAndActualInvocation() throws Exception {
        try (var game = new RulesScenario("source-query-snuff-out-alternative",
                Map.of(0, List.of("Swamp"), 1, List.of("Grizzly Bears")), "Snuff Out", "Rhys the Redeemed")) {
            var input = main(game);
            var catalog = query(game, input, card(input.state(), 0, "Hand", "Snuff Out"));
            int alternative = HeadlessPlatform.query(game.table, () -> {
                var actor = game.table.game.getRegisteredPlayers().get(0);
                var controller = (PlayerControllerHuman)actor.getController();
                var offered = controller.getInputQueue().getInput().getSourceActions(actor.getCardsIn(ZoneType.Hand).get(0));
                for (int index = 0; index < offered.size(); index++)
                    if (offered.get(index).getPayCosts().getCostParts().stream().anyMatch(CostPayLife.class::isInstance)) return index;
                throw new AssertionError("The real Snuff Out script must offer its four-life alternative");
            });
            int life = viewer(input.state()).get("life").getAsInt();
            assertTrue(game.answer(input, command(catalog, alternative)));
            var target = game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").equals("InputSelectTargets"));
            var bear = card(target.state(), 1, "Battlefield", "Grizzly Bears");
            assertTrue(game.answer(target, Map.of("action", "card", "key", text(bear, "key"))));
            var result = game.until(next -> next.seat() == 0 && priority(next)
                    && zone(next.state().getAsJsonArray("players").get(1).getAsJsonObject(), "Graveyard")
                        .getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                        .anyMatch(value -> text(value, "name").equals("Grizzly Bears")));
            assertEquals(viewer(result.state()).get("life").getAsInt(), life - 4);
            assertFalse(card(result.state(), 0, "Battlefield", "Swamp").get("tapped").getAsBoolean(),
                    "The alternative paid life; it did not turn back into the printed mana route");
            Files.writeString(game.evidence.resolve("alternative-cost-outcome.json"), JSON.toJson(Map.of(
                    "before", input.state(), "catalog", catalog, "after", result.state())));
        }
    }

    private static Input main(RulesScenario game) throws Exception {
        return game.until(input -> input.seat() == 0 && priority(input) && text(input.state(), "phaseKey").equals("MAIN1"));
    }
    private static boolean priority(Input input) { return text(input.prompt(), "inputType").equals("InputPassPriority"); }
    private static JsonObject card(JsonObject state, int seat, String zone, String name) {
        return zone(state.getAsJsonArray("players").get(seat).getAsJsonObject(), zone).getAsJsonArray("cards").asList().stream()
                .map(value -> value.getAsJsonObject()).filter(value -> text(value, "name").equals(name)).findFirst().orElseThrow();
    }
    private static JsonObject query(RulesScenario game, Input input, JsonObject source) {
        return JSON.toJsonTree(game.session(input.seat()).sourceActions(request(input, Map.of("key", text(source, "key"))))).getAsJsonObject();
    }
    private static Map<String, ?> command(JsonObject catalog, int index) {
        return Map.of("action", "useSourceAction", "key", text(catalog, "sourceCardKey"),
                "actionId", text(catalog.getAsJsonArray("actions").get(index).getAsJsonObject(), "id"));
    }
}
