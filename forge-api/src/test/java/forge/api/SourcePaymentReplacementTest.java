package forge.api;

import com.google.gson.Gson;
import com.google.gson.JsonObject;
import com.google.common.eventbus.Subscribe;
import forge.game.event.ManaPaymentReceipt;
import forge.game.cost.CostPayment;
import forge.game.spellability.SpellAbility;
import forge.game.zone.ZoneType;
import forge.gamemodes.match.input.InputPayMana;
import forge.gamemodes.match.input.InputConfirm;
import forge.player.PlayerControllerHuman;
import org.testng.annotations.DataProvider;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.util.ArrayList;
import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.TimeUnit;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Payment descendants cannot outlive their actual rules input owner. */
public class SourcePaymentReplacementTest {
    private static final Gson JSON = new Gson();

    @Test(timeOut = 90000)
    public void decliningAProducerSacrificeRestoresItsTapAndReopensOnlyItsUnpaidParent() throws Exception {
        try (var game = new RulesScenario("source-payment-producer-decline", Map.of(0, List.of("Lotus Petal", "Island")),
                "Island", "Balmor, Battlemage Captain")) {
            var main = main(game);
            assertTrue(game.answer(main, use(query(game, main, card(main.state(), "Command", "Balmor, Battlemage Captain")))));
            var payment = game.until(input -> input.seat() == 0 && payment(input));
            var original = game.table.game.getRegisteredPlayers().get(0).getCardsIn(ZoneType.Battlefield).stream()
                    .filter(value -> value.getName().equals("Lotus Petal")).findFirst().orElseThrow().getManaAbilities().get(0);
            assertTrue(game.answer(payment, use(query(game, payment, card(payment.state(), "Battlefield", "Lotus Petal")))));
            var confirmation = game.until(input -> input.seat() == 0 && text(input.prompt(), "inputType").equals("InputConfirm"));
            assertTrue(card(confirmation.state(), "Battlefield", "Lotus Petal").get("tapped").getAsBoolean());
            assertTrue(game.answer(confirmation, Map.of("action", "cancel")));
            var returned = game.until(input -> input.seat() == 0 && payment(input));
            assertFalse(card(returned.state(), "Battlefield", "Lotus Petal").get("tapped").getAsBoolean());
            assertEquals(count(viewer(returned.state()), "Graveyard"), 0);
            assertEquals(viewer(returned.state()).getAsJsonObject("mana").entrySet().stream().mapToInt(value -> value.getValue().getAsInt()).sum(), 0);
            assertEquals(returned.prompt().getAsJsonArray("unpaidManaParts"), payment.prompt().getAsJsonArray("unpaidManaParts"));
            assertEquals(original.getActivationsThisTurn(), 0);
            assertFalse(query(game, returned, card(returned.state(), "Battlefield", "Lotus Petal")).getAsJsonArray("actions").isEmpty(),
                    "A declined producer unlocks its unchanged current parent; it does not leave the source permanently busy");
            assertTrue(game.answer(returned, Map.of("action", "cancel")));
            game.until(input -> input.seat() == 0 && priority(input));
        }
    }

    @Test(timeOut = 90000)
    public void finishingAProducerCostConfirmationCannotReopenItsStillRunningParentPayment() throws Exception {
        try (var game = new RulesScenario("source-payment-producer-confirm", Map.of(0, List.of("Lotus Petal", "Island")),
                "Island", "Balmor, Battlemage Captain")) {
            var main = main(game);
            assertTrue(game.answer(main, use(query(game, main, card(main.state(), "Command", "Balmor, Battlemage Captain")))));
            var payment = game.until(input -> input.seat() == 0 && payment(input));
            var actual = HeadlessPlatform.query(game.table, () -> (InputPayMana)((PlayerControllerHuman)
                    game.table.game.getRegisteredPlayers().get(0).getController()).getInputQueue().getInput());
            assertTrue(game.answer(payment, use(query(game, payment, card(payment.state(), "Battlefield", "Lotus Petal")))));
            var confirmation = game.until(input -> input.seat() == 0 && text(input.prompt(), "inputType").equals("InputConfirm"));
            HeadlessPlatform.query(game.table, () -> {
                var controller = (PlayerControllerHuman)game.table.game.getRegisteredPlayers().get(0).getController();
                assertFalse(actual.isPaymentActive());
                controller.selectButtonOk();
                actual.showMessageInitial();
                // The real rules continuation waits for this state boundary.
                // Restoring the input beneath a cost question cannot complete
                // its still-running mana producer or reopen another gesture.
                var facts = Map.of("paymentActive", actual.isPaymentActive(), "confirmation", confirmation.state(),
                        "current", game.state(0), "sameParent", controller.getInputQueue().getInput() == actual);
                Files.writeString(game.evidence.resolve("producer-confirmation-before-continuation.json"), JSON.toJson(facts));
                assertFalse(actual.isPaymentActive(), "A child cost confirmation is not producer completion");
                return null;
            });
            var child = game.until(input -> input.seat() == 0 && text(input.prompt(), "context").equals("colorChoice"));
            assertTrue(game.answer(child, Map.of("action", "choice", "choices", List.of(red(child)))));
            var remaining = game.until(input -> input.seat() == 0 && payment(input));
            assertEquals(remaining.prompt().getAsJsonArray("unpaidManaParts"), JSON.toJsonTree(List.of(Map.of("symbol", "{U}", "count", 1))));
            assertTrue(game.answer(remaining, Map.of("action", "cancel")));
            game.until(input -> input.seat() == 0 && priority(input));
        }
    }

    @DataProvider public Object[][] producers() { return new Object[][] {{false, true}, {true, true}, {true, false}}; }

    @Test(dataProvider = "producers", timeOut = 90000)
    public void cancellingARealParentThenCastingAgainKeepsOnlyTheEngineDefinedResources(boolean sacrifice, boolean offeredSource) throws Exception {
        String producer = sacrifice ? "Lotus Petal" : "Temple of Epiphany";
        try (var game = new RulesScenario("source-payment-replacement-" + sacrifice + "-" + offeredSource,
                Map.of(0, List.of(producer, "Island", "Forest", "Forest", "Forest")), "Manalith", "Balmor, Battlemage Captain")) {
            var receipts = new Receipts();
            game.table.game.subscribeToEvents(receipts);
            var main = main(game);
            SpellAbility original = HeadlessPlatform.query(game.table, () -> game.table.game.getRegisteredPlayers().get(0)
                    .getCardsIn(ZoneType.Battlefield).stream().filter(value -> value.getName().equals(producer))
                    .findFirst().orElseThrow().getManaAbilities().get(0));
            assertTrue(game.answer(main, use(query(game, main, card(main.state(), "Command", "Balmor, Battlemage Captain")))));
            var firstBill = game.until(input -> input.seat() == 0 && payment(input));
            var oldCatalog = query(game, firstBill, card(firstBill.state(), "Battlefield", producer));
            assertTrue(game.answer(firstBill, offeredSource ? use(oldCatalog)
                    : Map.of("action", "card", "key", text(card(firstBill.state(), "Battlefield", producer), "key"))));
            Input child;
            try { child = colorChoice(game, receipts); }
            catch (Exception | AssertionError failure) {
                Files.writeString(game.evidence.resolve("producer-before-missing-child.json"), JSON.toJson(Map.of(
                        "offeredSource", offeredSource, "originalActivations", original.getActivationsThisTurn(),
                        "state", game.state(0), "receipts", receipts.copy(), "costs", receipts.costs())));
                throw failure;
            }
            var red = red(child);
            assertTrue(game.answer(child, Map.of("action", "choice", "choices", List.of(red))));
            var remaining = game.until(input -> input.seat() == 0 && payment(input)
                    && input.prompt().getAsJsonArray("unpaidManaParts").equals(JSON.toJsonTree(List.of(Map.of("symbol", "{U}", "count", 1)))));
            assertEquals(original.getActivationsThisTurn(), 1);
            assertTrue(remaining.prompt().get("cancelEnabled").getAsBoolean());
            assertTrue(game.answer(remaining, Map.of("action", "cancel")));
            var returned = game.until(input -> input.seat() == 0 && priority(input));
            assertEquals(count(viewer(returned.state()), "Command"), 1, "The cancelled commander never reached the stack");
            assertTrue(returned.state().getAsJsonArray("stack").isEmpty());
            assertEquals(original.getActivationsThisTurn(), 1, "Cancellation does not manufacture a second producer activation");
            assertEquals(viewer(returned.state()).getAsJsonObject("mana").get("R").getAsInt(), sacrifice ? 1 : 0,
                    "Forge restores non-undoable mana to the pool and reverses a renewable paying source");
            if (sacrifice) assertEquals(text(card(returned.state(), "Graveyard", producer), "name"), producer,
                    "Cancelling the parent cannot undo its already committed producer sacrifice");
            else assertFalse(card(returned.state(), "Battlefield", producer).get("tapped").getAsBoolean(),
                    "The rules-owned cancellation reverses this renewable producer tap");
            assertTrue(game.answer(returned, use(query(game, returned, card(returned.state(), "Hand", "Manalith")))));
            var replacement = game.until(input -> input.seat() == 0 && payment(input));
            assertBill(replacement, 3);
            var replacementBill = replacement;
            var actualReplacement = HeadlessPlatform.query(game.table, () -> {
                var controller = (PlayerControllerHuman)game.table.game.getRegisteredPlayers().get(0).getController();
                return (InputPayMana)controller.getInputQueue().getInput();
            });
            var delayedChild = new CompletableFuture<Boolean>();
            HeadlessPlatform.query(game.table, () -> {
                var before = game.state(0);
                assertThrows(IllegalArgumentException.class, () -> game.session(0).action(request(firstBill, use(oldCatalog))));
                assertThrows(IllegalArgumentException.class, () -> game.session(0).action(request(replacementBill, use(oldCatalog))));
                assertThrows(IllegalArgumentException.class, () -> game.session(0).action(request(remaining, Map.of("action", "cancel"))));
                assertEquals(game.state(0), before);
                HeadlessPlatform.later(() -> {
                    try {
                        var current = game.state(0);
                        assertThrows(IllegalArgumentException.class, () -> game.session(0).action(request(child,
                                Map.of("action", "choice", "choices", List.of(red)))));
                        assertEquals(game.state(0), current);
                        assertEquals(actualReplacement.getRemainingManaCost().getConvertedManaCost(), 3);
                        delayedChild.complete(true);
                    } catch (Throwable failure) { delayedChild.completeExceptionally(failure); }
                });
                return null;
            });
            assertTrue(delayedChild.get(5, TimeUnit.SECONDS));
            var current = game.state(0);
            replacement = new Input(0, current, current.getAsJsonObject("prompt"));
            assertBill(replacement, 3);
            if (sacrifice) assertTrue(game.answer(replacement, Map.of("action", "spendPoolMana", "manaColor", "R")));
            else {
                assertTrue(game.answer(replacement, use(query(game, replacement, card(replacement.state(), "Battlefield", producer)))));
                var nextChild = colorChoice(game, receipts);
                assertTrue(game.answer(nextChild, Map.of("action", "choice", "choices", List.of(red(nextChild)))));
            }
            for (int bill = 2; bill >= 1; bill--) {
                final int expected = bill;
                var payment = game.until(input -> input.seat() == 0 && payment(input) && genericBill(input) == expected);
                var forest = zone(viewer(payment.state()), "Battlefield").getAsJsonArray("cards").asList().stream()
                        .map(value -> value.getAsJsonObject()).filter(value -> text(value, "name").equals("Forest")
                                && !value.get("tapped").getAsBoolean()).findFirst().orElseThrow();
                assertTrue(game.answer(payment, use(query(game, payment, forest))));
            }
            var committed = game.until(input -> input.seat() == 0 && priority(input) && !input.state().getAsJsonArray("stack").isEmpty());
            assertEquals(committed.state().getAsJsonArray("stack").size(), 1, "Only the replacement spell is cast");
            var settled = game.until(input -> input.seat() == 0 && priority(input) && input.state().getAsJsonArray("stack").isEmpty());
            assertEquals(zone(viewer(settled.state()), "Battlefield").getAsJsonArray("cards").asList().stream()
                    .map(value -> value.getAsJsonObject()).filter(value -> text(value, "name").equals("Manalith")).count(), 1L);
            assertEquals(zone(viewer(settled.state()), "Battlefield").getAsJsonArray("cards").asList().stream()
                    .map(value -> value.getAsJsonObject()).filter(value -> text(value, "name").equals("Forest") && value.get("tapped").getAsBoolean()).count(), 2L);
            assertEquals(original.getActivationsThisTurn(), sacrifice ? 1 : 2);
            assertEquals(viewer(settled.state()).getAsJsonObject("mana").entrySet().stream().mapToInt(value -> value.getValue().getAsInt()).sum(), 0);
            assertEquals(receipts.count("produced", producer), sacrifice ? 1L : 2L, "Stale requests never replay a producer");
            assertEquals(receipts.count("produced", "Forest"), 2L);
            assertEquals(receipts.count("cancelled", "Balmor, Battlemage Captain"), 1L);
            assertEquals(receipts.count("restored", "Balmor, Battlemage Captain"), 1L);
            assertEquals(receipts.count("completed", "Manalith"), 1L);
            Files.writeString(game.evidence.resolve("payment-replacement-receipts.json"), JSON.toJson(Map.of(
                    "sacrifice", sacrifice, "firstBill", firstBill.state(), "child", child.state(), "cancelled", returned.state(),
                    "replacement", current, "settled", settled.state(), "receipts", receipts.copy())));
        }
    }

    public static final class Receipts {
        private final List<Map<String, Object>> values = Collections.synchronizedList(new ArrayList<>());
        private final List<CostPayment> actualCosts = new ArrayList<>();
        @Subscribe public void payment(ManaPaymentReceipt receipt) {
            var value = new LinkedHashMap<String, Object>();
            value.put("kind", receipt.kind()); value.put("execution", receipt.executionId());
            value.put("producerExecution", receipt.producerExecutionId());
            value.put("source", receipt.source() == null ? "" : receipt.source().getCurrentState().getName());
            value.put("produced", receipt.produced()); value.put("spent", receipt.spent());
            value.put("before", receipt.billBefore()); value.put("after", receipt.billAfter()); value.put("pool", receipt.poolAfter());
            values.add(value);
        }
        long count(String kind, String source) { return copy().stream().filter(value -> kind.equals(value.get("kind")) && source.equals(value.get("source"))).count(); }
        List<Map<String, Object>> copy() { synchronized (values) { return List.copyOf(values); } }
        List<Map<String, Object>> costs() {
            return actualCosts.stream().map(cost -> Map.<String, Object>of("fullyPaid", cost.isFullyPaid(),
                    "originalParts", cost.getCost().getCostParts().stream().map(value -> value.getClass().getSimpleName() + ":" + System.identityHashCode(value)).toList(),
                    "unpaid", cost.getUnpaidCostParts().stream().map(value -> value.getClass().getSimpleName() + ":" + System.identityHashCode(value)).toList(),
                    "abilityId", cost.getAbility().getId())).toList();
        }
    }

    private static Input colorChoice(RulesScenario game, Receipts receipts) throws Exception {
        for (int step = 0; step < 4; step++) {
            var next = game.until(input -> input.seat() == 0 && (text(input.prompt(), "context").equals("colorChoice")
                    || text(input.prompt(), "inputType").equals("InputConfirm")
                    || input.prompt().has("ordered") && input.prompt().get("ordered").getAsBoolean()));
            if (text(next.prompt(), "context").equals("colorChoice")) return next;
            if (text(next.prompt(), "inputType").equals("InputConfirm")) {
                HeadlessPlatform.query(game.table, () -> {
                    receipts.actualCosts.add(game.table.game.costPaymentStack.peek().payment()); return null;
                });
                assertTrue(game.answer(next, Map.of("action", "ok")));
                continue;
            }
            var order = next.prompt().getAsJsonArray("choices").asList().stream().map(value -> value.getAsJsonObject().get("index").getAsInt()).toList();
            assertTrue(game.answer(next, Map.of("action", "choice", "choices", order)));
        }
        throw new AssertionError("The producer did not reach its actual required color choice");
    }
    private static int red(Input child) { return child.prompt().getAsJsonArray("choices").asList().stream().map(value -> value.getAsJsonObject())
            .filter(value -> text(value, "mana").equals("{R}")).findFirst().orElseThrow().get("index").getAsInt(); }
    private static boolean priority(Input input) { return text(input.prompt(), "inputType").equals("InputPassPriority"); }
    private static int genericBill(Input input) {
        var bill = input.prompt().getAsJsonArray("unpaidManaParts");
        return bill != null && bill.size() == 1 ? Integer.parseInt(text(bill.get(0).getAsJsonObject(), "symbol").replace("{", "").replace("}", "")) : -1;
    }
    private static void assertBill(Input input, int expected) { assertEquals(genericBill(input), expected); }

    @Test(timeOut = 90000)
    public void aRequiredColorReplyCannotAnswerWhileItsRealPaymentParentIsDisplaced() throws Exception {
        try (var game = new RulesScenario("source-payment-displaced-child",
                Map.of(0, List.of("Temple of Epiphany", "Island")), "Island", "Balmor, Battlemage Captain")) {
            var main = main(game);
            assertTrue(game.answer(main, use(query(game, main, card(main.state(), "Command", "Balmor, Battlemage Captain")))));
            var payment = game.until(input -> input.seat() == 0 && payment(input));
            var temple = query(game, payment, card(payment.state(), "Battlefield", "Temple of Epiphany"));
            assertTrue(game.answer(payment, use(temple)));
            var child = game.until(input -> input.seat() == 0 && text(input.prompt(), "context").equals("colorChoice"));
            int red = child.prompt().getAsJsonArray("choices").asList().stream().map(value -> value.getAsJsonObject())
                    .filter(value -> text(value, "mana").equals("{R}")).findFirst().orElseThrow().get("index").getAsInt();
            HeadlessPlatform.query(game.table, () -> {
                var actor = game.table.game.getRegisteredPlayers().get(0);
                var controller = (PlayerControllerHuman)actor.getController();
                var parent = (InputPayMana)controller.getInputQueue().getInput();
                var temporary = new InputConfirm(controller, "Temporary input owner", "Yes", "No", true, null, null);
                controller.getInputQueue().setInput(temporary);
                try {
                    assertNotSame(controller.getInputQueue().getInput(), parent);
                    var before = game.state(0);
                    Files.writeString(game.evidence.resolve("displaced-parent-before-child.json"), JSON.toJson(Map.of(
                            "payment", payment.state(), "child", child.state(), "displaced", before,
                            "parentOwnsQueue", controller.getInputQueue().getInput() == parent,
                            "remaining", parent.getRemainingManaCost().toString())));
                    assertThrows(IllegalArgumentException.class, () -> game.session(0).action(request(child,
                            Map.of("action", "choice", "choices", List.of(red)))));
                    assertEquals(game.state(0), before, "A stale child reply cannot consume a prompt or mutate its replacement owner");
                } finally {
                    // Restore only the genuine suspended owner; no original
                    // rules continuation, future or execution scope is stopped.
                    temporary.stop();
                }
                assertSame(controller.getInputQueue().getInput(), parent);
                assertTrue(game.answer(child, Map.of("action", "choice", "choices", List.of(red))),
                        "The still-live parent's required child remains answerable after lawful restoration");
                return null;
            });
            var remaining = game.until(input -> input.seat() == 0 && payment(input)
                    && input.prompt().getAsJsonArray("unpaidManaParts").equals(JSON.toJsonTree(List.of(Map.of("symbol", "{U}", "count", 1)))));
            assertTrue(game.answer(remaining, use(query(game, remaining, card(remaining.state(), "Battlefield", "Island")))));
            var settled = game.until(input -> input.seat() == 0 && priority(input) && input.state().getAsJsonArray("stack").isEmpty()
                    && count(viewer(input.state()), "Command") == 0);
            assertEquals(text(card(settled.state(), "Battlefield", "Balmor, Battlemage Captain"), "power"), "1");
        }
    }

    private static Input main(RulesScenario game) throws Exception {
        return game.until(input -> input.seat() == 0 && text(input.prompt(), "inputType").equals("InputPassPriority")
                && text(input.state(), "phaseKey").equals("MAIN1"));
    }
    private static boolean payment(Input input) { return text(input.prompt(), "inputType").startsWith("InputPayMana"); }
    private static JsonObject card(JsonObject state, String zoneName, String name) {
        return zone(viewer(state), zoneName).getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "name").equals(name)).findFirst().orElseThrow();
    }
    private static JsonObject query(RulesScenario game, Input input, JsonObject source) {
        return JSON.toJsonTree(game.session(0).sourceActions(request(input, Map.of("key", text(source, "key"))))).getAsJsonObject();
    }
    private static Map<String, ?> use(JsonObject catalog) {
        return Map.of("action", "useSourceAction", "key", text(catalog, "sourceCardKey"),
                "actionId", text(catalog.getAsJsonArray("actions").get(0).getAsJsonObject(), "id"));
    }
}
