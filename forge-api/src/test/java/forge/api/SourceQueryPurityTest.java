package forge.api;

import com.google.gson.Gson;
import com.google.gson.JsonObject;
import forge.game.spellability.SpellAbility;
import forge.game.zone.ZoneType;
import org.testng.annotations.DataProvider;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.util.List;
import java.util.Map;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Real source reads preserve live execution context and already offered handles. */
public class SourceQueryPurityTest {
    private static final Gson JSON = new Gson();
    @DataProvider public Object[][] inputs() { return new Object[][] {{false}, {true}}; }

    @Test(dataProvider = "inputs", timeOut = 90000)
    public void readingAnUnavailableForeignSourcePreservesItsLiveAbilityContext(boolean payment) throws Exception {
        try (var game = new RulesScenario("query-purity-foreign-" + payment,
                Map.of(0, List.of("Forest", "Forest", "Forest"), 1, List.of("Forest")), "Manalith", "Rhys the Redeemed")) {
            var input = main(game);
            if (payment) {
                var spell = card(input.state(), 0, "Hand", "Manalith");
                var catalog = query(game, input, spell);
                assertTrue(game.answer(input, command(catalog)));
                input = game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").startsWith("InputPayMana"));
            }
            var current = input;
            HeadlessPlatform.query(game.table, () -> {
                var actor = game.table.game.getRegisteredPlayers().get(0);
                var owner = game.table.game.getRegisteredPlayers().get(1);
                var source = owner.getCardsIn(ZoneType.Battlefield).get(0);
                var original = source.getManaAbilities().get(0);
                var beforeActor = original.getActivatingPlayer();
                var before = game.state(0);
                var contextBefore = facts(original);
                var queried = query(game, current, card(before, 1, "Battlefield", "Forest"));
                assertTrue(queried.getAsJsonArray("actions").isEmpty(), "The actor cannot use the opponent's Forest");
                assertEquals(game.state(0), before, "The entire published board hides the internal context mutation");
                Files.writeString(game.evidence.resolve("live-context-before-after.json"), JSON.toJson(Map.of(
                        "payment", payment, "queryActor", actor.getId(), "sourceOwner", owner.getId(),
                        "before", contextBefore, "after", facts(original), "published", before, "query", queried)));
                assertSame(original.getActivatingPlayer(), beforeActor,
                        "A read-only source query must not overwrite the live original's execution actor");
                return null;
            });
        }
    }

    @Test(timeOut = 90000)
    public void aRetainedActionStillExecutesAfterAnotherActorsEligibilityReadWithoutRefreshingTheHandle() throws Exception {
        try (var game = new RulesScenario("query-purity-retained-handle", Map.of(0, List.of("Forest")))) {
            var input = main(game);
            var prepared = HeadlessPlatform.query(game.table, () -> {
                var actor = game.table.game.getRegisteredPlayers().get(0);
                var other = game.table.game.getRegisteredPlayers().get(1);
                var source = actor.getCardsIn(ZoneType.Battlefield).get(0);
                var original = source.getManaAbilities().get(0);
                var before = game.state(0);
                var catalog = query(game, new Input(0, before, before.getAsJsonObject("prompt")), card(before, 0, "Battlefield", "Forest"));
                assertSame(original.getActivatingPlayer(), actor, "The offered real action begins with its actual actor");
                var contextBefore = facts(original);
                assertTrue(source.getAllPossibleAbilities(other, true).isEmpty());
                assertEquals(game.state(0), before);
                Files.writeString(game.evidence.resolve("retained-handle-context.json"), JSON.toJson(Map.of(
                        "before", contextBefore, "after", facts(original), "catalog", catalog, "state", before)));
                return new Prepared(new Input(0, before, before.getAsJsonObject("prompt")), catalog);
            });
            game.session(0).action(request(prepared.input(), command(prepared.catalog())));
            var result = game.until(next -> next.seat() == 0 && priority(next));
            Files.writeString(game.evidence.resolve("retained-handle-outcome.json"), JSON.toJson(Map.of(
                    "before", prepared.input().state(), "after", result.state(), "catalog", prepared.catalog())));
            assertEquals(viewer(result.state()).getAsJsonObject("mana").get("G").getAsInt(), 1,
                    "An eligibility read must not silently invalidate an already offered physical Forest action");
        }
    }

    private record Prepared(Input input, JsonObject catalog) { }

    @Test(timeOut = 90000)
    public void aPiracyPaymentKeepsItsPlayableAlternativeWhenTheSameIdBaseRouteIsUnavailable() throws Exception {
        try (var game = new RulesScenario("query-purity-piracy-payment", Map.of(
                0, List.of("Island", "Island"), 1, List.of("Island", "Island")), "Piracy", "Rhys the Redeemed")) {
            var input = main(game);
            assertTrue(game.answer(input, command(query(game, input, card(input.state(), 0, "Hand", "Piracy")))));
            for (int source = 0; source < 2; source++) {
                var payment = game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").startsWith("InputPayMana"));
                var island = zone(viewer(payment.state()), "Battlefield").getAsJsonArray("cards").asList().stream()
                        .map(value -> value.getAsJsonObject()).filter(value -> !value.get("tapped").getAsBoolean())
                        .findFirst().orElseThrow();
                assertTrue(game.answer(payment, command(query(game, payment, island))));
            }
            var active = game.until(next -> next.seat() == 0 && priority(next) && next.state().getAsJsonArray("stack").isEmpty()
                    && game.table.game.getRegisteredPlayers().get(0).hasKeyword("Piracy"));
            assertTrue(game.answer(active, command(query(game, active, card(active.state(), 0, "Hand", "Piracy")))));
            var payment = game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").startsWith("InputPayMana"));
            var source = card(payment.state(), 1, "Battlefield", "Island");
            var catalog = query(game, payment, source);
            Files.writeString(game.evidence.resolve("piracy-current-route.json"), JSON.toJson(Map.of(
                    "state", payment.state(), "catalog", catalog)));
            assertEquals(catalog.getAsJsonArray("actions").size(), 1,
                    "The payable Piracy route survives removal of its unavailable same-ID base route");
            assertTrue(game.answer(payment, command(catalog)));
            var next = game.until(value -> value.seat() == 0 && text(value.prompt(), "inputType").startsWith("InputPayMana")
                    && zone(value.state().getAsJsonArray("players").get(1).getAsJsonObject(), "Battlefield")
                        .getAsJsonArray("cards").asList().stream().map(card -> card.getAsJsonObject())
                        .anyMatch(card -> card.get("tapped").getAsBoolean()));
            assertTrue(next.prompt().has("unpaidManaParts"), "One actual opponent Island pays only one part of the current bill");
        }
    }
    private static Map<String, Object> facts(SpellAbility ability) {
        return Map.of("abilityId", ability.getId(), "hostId", ability.getHostCard().getId(),
                "activatingPlayer", ability.getActivatingPlayer() == null ? -1 : ability.getActivatingPlayer().getId(),
                "originalId", ability.getOriginalAbility() == null ? -1 : ability.getOriginalAbility().getId(),
                "manaExpressChoice", ability.getManaPart().getExpressChoice(),
                "subActor", ability.getSubAbility() == null || ability.getSubAbility().getActivatingPlayer() == null
                        ? -1 : ability.getSubAbility().getActivatingPlayer().getId());
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
    private static Map<String, ?> command(JsonObject catalog) {
        return Map.of("action", "useSourceAction", "key", text(catalog, "sourceCardKey"),
                "actionId", text(catalog.getAsJsonArray("actions").get(0).getAsJsonObject(), "id"));
    }
}
