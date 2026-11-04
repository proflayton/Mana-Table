package forge.api;

import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

public class StackTargetProjectionTest {
    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void aMovedPathTargetCannotRebindToItsNewExileOccurrenceWhileTheRealSearchIsPending() throws Exception {
        try (var scenario = RulesScenario.allied("public-in-flight-target-movement", Map.of(
                0, List.of("Plains", "Reliquary Tower"), 1, List.of("Birds of Paradise", "Reliquary Tower")), "Path to Exile")) {
            var main = main(scenario);
            int owner = scenario.table.game.getRegisteredPlayers().get(1).getId();
            var spell = zone(viewer(main.state()), "Hand").getAsJsonArray("cards").asList().stream()
                    .map(value -> value.getAsJsonObject()).filter(value -> text(value, "name").equals("Path to Exile")).findFirst().orElseThrow();
            assertTrue(scenario.answer(main, Map.of("action", "card", "key", text(spell, "key"))));
            var targeting = scenario.until(input -> text(input.prompt(), "inputType").equals("InputSelectTargets"));
            var recipient = card(targeting.state(), owner, "Birds of Paradise");
            assertTrue(scenario.answer(targeting, Map.of("action", "card", "key", text(recipient, "key"))));
            payAndStack(scenario);
            assertTargets(scenario, "Path to Exile", text(recipient, "combatId"), owner);
            var oldTargetView = HeadlessPlatform.query(scenario.table, () -> scenario.table.game.getRegisteredPlayers().get(1)
                    .getCardsIn(forge.game.zone.ZoneType.Battlefield).stream().filter(value -> value.getName().equals("Birds of Paradise"))
                    .findFirst().orElseThrow().getView());
            var json = new GsonBuilder().serializeNulls().create();
            Object retained = HeadlessPlatform.query(scenario.table, () -> {
                var state = SpectatorObservation.project(scenario.table,
                        ((Number)scenario.session(0).state().get("boardRevision")).longValue(), null);
                return ((Map<?, ?>)((List<?>)state.get("stack")).get(0)).get("targets");
            });
            var before = json.toJsonTree(retained);
            var search = scenario.until(input -> input.seat() == 1 && text(input.prompt(), "inputType").equals("InputConfirm"));
            assertEquals(search.prompt().getAsJsonObject("librarySearch").get("libraryOwnerId").getAsInt(), owner);
            assertEquals(zone(player(search.state(), owner), "Exile").getAsJsonArray("cards").size(), 1);
            HeadlessPlatform.query(scenario.table, () -> {
                var states = new ArrayList<JsonObject>();
                for (int seat = 0; seat < 4; seat++) states.add(json.toJsonTree(scenario.table.seat(seat).state()).getAsJsonObject());
                states.add(json.toJsonTree(SpectatorObservation.project(scenario.table,
                        states.get(0).get("boardRevision").getAsLong(), null)).getAsJsonObject());
                var identityCalls = new java.util.concurrent.atomic.AtomicInteger();
                assertNull(StackTargetProjection.card(scenario.table.game, oldTargetView, ignored -> {
                    identityCalls.incrementAndGet(); return "retired-reference-must-not-allocate";
                }));
                assertEquals(identityCalls.get(), 0, "Retired target admission fails before any audience identity lookup or allocation");
                var exileView = scenario.table.game.getRegisteredPlayers().get(1).getCardsIn(forge.game.zone.ZoneType.Exile).get(0).getView();
                assertNotSame(exileView, oldTargetView); assertEquals(exileView.getId(), oldTargetView.getId());
                assertNotNull(StackTargetProjection.card(scenario.table.game, exileView, current -> {
                    identityCalls.incrementAndGet(); return scenario.table.identities.id(current);
                }), "A genuinely current public exile view still supports a newly admitted reference");
                assertEquals(identityCalls.get(), 1);
                for (var state : states) {
                    var item = state.getAsJsonArray("stack").get(0).getAsJsonObject();
                    Files.writeString(scenario.evidence.resolve("after-movement-seat-" + state.get("viewerId").getAsInt() + ".json"), json.toJson(state));
                    assertEquals(text(item, "name"), "Path to Exile");
                    assertTrue(item.getAsJsonArray("targets").isEmpty(),
                            "The old battlefield target is historical, not the new public exile occurrence with the same physical numeric ID");
                }
                return null;
            });
            assertEquals(json.toJsonTree(retained), before, "Earlier target descriptions remain detached historical values");
            assertTrue(scenario.answer(search, Map.of("action", "cancel")), "The actual controller declines its optional library search");
            var settled = scenario.until(input -> text(input.prompt(), "inputType").equals("InputPassPriority")
                    && input.state().getAsJsonArray("stack").isEmpty());
            assertEquals(zone(player(settled.state(), owner), "Exile").getAsJsonArray("cards").size(), 1);
            assertTrue(zone(viewer(settled.state()), "Graveyard").getAsJsonArray("cards").asList().stream()
                    .anyMatch(value -> text(value.getAsJsonObject(), "name").equals("Path to Exile")));
        }
    }

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void anActualDragonBloodActivationKeepsItsAllyTargetControllerForEveryAudience() throws Exception {
        try (var scenario = RulesScenario.allied("public-ally-stack-target", Map.of(
                0, List.of("Dragon Blood", "Forest", "Forest", "Forest", "Reliquary Tower"),
                1, List.of("Grizzly Bears", "Reliquary Tower")), "Forest")) {
            var main = main(scenario);
            int owner = scenario.table.game.getRegisteredPlayers().get(1).getId();
            assertFalse(scenario.table.game.getRegisteredPlayers().get(0).isOpponentOf(scenario.table.game.getRegisteredPlayers().get(1)),
                    "The target is an actual allied player, not a renamed opponent");
            var source = card(main.state(), 0, "Dragon Blood");
            assertTrue(scenario.answer(main, Map.of("action", "card", "key", text(source, "key"))));
            var targeting = scenario.until(input -> text(input.prompt(), "inputType").equals("InputSelectTargets"));
            var recipient = card(targeting.state(), owner, "Grizzly Bears");
            assertTrue(scenario.answer(targeting, Map.of("action", "card", "key", text(recipient, "key"))));
            var stacked = payAndStack(scenario);
            assertTargets(scenario, "Dragon Blood", text(recipient, "combatId"), owner);
            assertTrue(card(stacked.state(), 0, "Dragon Blood").get("tapped").getAsBoolean());
            var resolved = scenario.until(input -> card(input.state(), owner, "Grizzly Bears").get("power").getAsInt() == 3);
            assertEquals(card(resolved.state(), owner, "Grizzly Bears").get("toughness").getAsInt(), 3,
                    "The real activation resolves its counter onto the ally instead of merely projecting a target");
        }
    }

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void repeatedTargetedSpellPartsDisplayOneReferenceWhileAllThreeEffectsStillResolve() throws Exception {
        try (var scenario = RulesScenario.allied("public-repeated-stack-target", Map.of(
                0, List.of("Forest", "Plains", "Reliquary Tower"), 1, List.of("Grizzly Bears", "Reliquary Tower")), "Seeds of Strength")) {
            var main = main(scenario);
            int owner = scenario.table.game.getRegisteredPlayers().get(1).getId();
            var spell = zone(viewer(main.state()), "Hand").getAsJsonArray("cards").asList().stream()
                    .map(value -> value.getAsJsonObject()).filter(value -> text(value, "name").equals("Seeds of Strength")).findFirst().orElseThrow();
            assertTrue(scenario.answer(main, Map.of("action", "card", "key", text(spell, "key"))));
            String recipientId = "";
            for (int part = 0; part < 3; part++) {
                var targeting = scenario.until(input -> text(input.prompt(), "inputType").equals("InputSelectTargets"));
                var recipient = card(targeting.state(), owner, "Grizzly Bears"); recipientId = text(recipient, "combatId");
                assertTrue(scenario.answer(targeting, Map.of("action", "card", "key", text(recipient, "key"))));
            }
            payAndStack(scenario);
            assertTargets(scenario, "Seeds of Strength", recipientId, owner);
            var resolved = scenario.until(input -> card(input.state(), owner, "Grizzly Bears").get("power").getAsInt() == 5);
            assertEquals(card(resolved.state(), owner, "Grizzly Bears").get("toughness").getAsInt(), 5,
                    "Deduplicating display references cannot collapse the three actual targeted pump effects");
        }
    }

    private static RulesScenario.Input main(RulesScenario scenario) throws Exception {
        return scenario.until(input -> input.seat() == 0 && text(input.state(), "phaseKey").equals("MAIN1")
                && text(input.prompt(), "inputType").equals("InputPassPriority"));
    }

    private static RulesScenario.Input payAndStack(RulesScenario scenario) throws Exception {
        var payment = scenario.until(input -> text(input.prompt(), "inputType").startsWith("InputPayMana"));
        assertTrue(payment.prompt().get("okEnabled").getAsBoolean());
        assertTrue(scenario.answer(payment, Map.of("action", "ok")), "Explicitly use the real human Auto payment control");
        return scenario.until(input -> text(input.prompt(), "inputType").equals("InputPassPriority")
                && !input.state().getAsJsonArray("stack").isEmpty());
    }

    private static JsonObject card(JsonObject state, int owner, String name) {
        return zone(player(state, owner), "Battlefield").getAsJsonArray("cards").asList().stream()
                .map(value -> value.getAsJsonObject()).filter(value -> text(value, "name").equals(name)).findFirst().orElseThrow();
    }

    private static void assertTargets(RulesScenario scenario, String source, String recipientId, int owner) throws Exception {
        HeadlessPlatform.query(scenario.table, () -> {
            var json = new GsonBuilder().serializeNulls().setPrettyPrinting().create();
            var states = new ArrayList<JsonObject>();
            for (int seat = 0; seat < 4; seat++) states.add(json.toJsonTree(scenario.table.seat(seat).state()).getAsJsonObject());
            states.add(json.toJsonTree(SpectatorObservation.project(scenario.table,
                    states.get(0).get("boardRevision").getAsLong(), null)).getAsJsonObject());
            for (var state : states) {
                var stack = state.getAsJsonArray("stack").asList().stream().map(value -> value.getAsJsonObject())
                        .filter(item -> text(item, "name").equals(source)).findFirst().orElseThrow();
                Files.writeString(scenario.evidence.resolve("target-seat-" + state.get("viewerId").getAsInt() + ".json"), json.toJson(state));
                var targets = stack.getAsJsonArray("targets");
                assertEquals(targets.size(), 1, "Committed display targets form one ordered set across all targeted spell parts");
                var target = targets.get(0).getAsJsonObject();
                assertEquals(text(target, "kind"), "card"); assertEquals(text(target, "id"), recipientId);
                assertTrue(target.has("playerId") && !target.get("playerId").isJsonNull(), "A disclosed card target includes its current controller");
                assertEquals(target.get("playerId").getAsInt(), owner);
            }
            return null;
        });
    }
}
