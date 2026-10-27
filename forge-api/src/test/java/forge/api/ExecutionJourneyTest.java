package forge.api;

import com.google.gson.Gson;
import com.google.gson.JsonObject;
import com.google.common.eventbus.Subscribe;
import forge.game.ExecutionTracker;
import org.testng.annotations.DataProvider;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.util.*;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Actual input capabilities and rules events, not an authored lifecycle trace. */
public class ExecutionJourneyTest {
    private static final Gson JSON = new Gson();
    private static final String MARE = "Loch Mare";

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void consumingANestedManaSourceResumesItsActualUnfinishedPayment() throws Exception {
        try (var game = new RulesScenario("execution-consuming-mana", Map.of(0,
                List.of("Wastes", "Wastes", "Crystal Vein")), "Traxos, Scourge of Kroog", "Rhys the Redeemed")) {
            var main = main(game);
            var spell = zone(viewer(main.state()), "Hand").getAsJsonArray("cards").get(0).getAsJsonObject();
            start(game, main, spell, "");
            var payment = game.until(i -> i.seat() == 0 && mana(i));
            String owner = execution(payment);
            String initialScope = text(payment.prompt(), "id");
            start(game, payment, permanent(payment.state(), "Wastes"), "Add {C}");
            payment = awaitOwnedPayment(game, owner, i -> unpaid(i, "{3}")
                    && zone(viewer(i.state()), "Battlefield").getAsJsonArray("cards").asList().stream()
                    .anyMatch(value -> text(value.getAsJsonObject(), "name").equals("Wastes")
                            && value.getAsJsonObject().get("tapped").getAsBoolean()));
            assertEquals(execution(payment), owner);
            assertEquals(text(payment.prompt(), "executionStep"), "cost");
            var sacrificedSource = permanent(payment.state(), "Crystal Vein");
            start(game, payment, sacrificedSource, "Add {C}{C}");
            var resumed = awaitOwnedPayment(game, owner, i -> unpaid(i, "{1}")
                    && zone(viewer(i.state()), "Graveyard").getAsJsonArray("cards").asList().stream()
                    .anyMatch(value -> text(value.getAsJsonObject(), "name").equals("Crystal Vein"))
                    && occurrences(i.state()).stream().anyMatch(value -> text(value, "sourceName").equals("Crystal Vein")
                            && text(value, "parentId").equals(owner) && text(value, "step").equals("complete")), choice -> {
                assertEquals(text(choice.prompt(), "inputType"), "InputConfirm");
                assertEquals(text(choice.prompt().getAsJsonObject("sourceCard"), "visualId"), text(sacrificedSource, "visualId"));
                var mana = occurrence(choice.state(), execution(choice));
                assertEquals(text(mana, "kind"), "mana");
                assertEquals(text(mana, "parentId"), owner);
                assertEquals(text(mana, "budgetRootId"), owner);
                assertEquals(text(mana, "step"), "cost");
                assertTrue(choice.prompt().get("okEnabled").getAsBoolean());
                game.answer(choice, Map.of("action", "ok"));
            });
            assertNotEquals(text(resumed.prompt(), "id"), initialScope, "A source's sacrifice refreshes the current action scope");
            var consumed = occurrences(resumed.state()).stream().filter(value -> text(value, "sourceName").equals("Crystal Vein"))
                    .findFirst().orElseThrow();
            assertEquals(text(consumed, "parentId"), owner);
            assertEquals(text(consumed, "budgetRootId"), owner);
            assertEquals(text(consumed, "step"), "complete");
            assertEquals(text(consumed, "outcome"), "resolved");
            // Reproduce the callback schedule deterministically: the real
            // consumed source's scope is still the caller while the actual
            // payment input regains a fresh answer capability. No rules,
            // payment progress or authored execution records are changed.
            var refreshed = new java.util.concurrent.atomic.AtomicReference<JsonObject>();
            game.table.game.getAction().withState(() -> {
                try (var callback = game.table.game.getExecutions().enterCause(text(consumed, "id"))) {
                    game.table.seat(0).clearDecision();
                    game.table.seat(0).publishInput();
                    refreshed.set(game.state(0));
                }
            });
            var state = refreshed.get();
            var current = new Input(0, state, state.getAsJsonObject("prompt"));
            assertEquals(execution(current), owner, "The restored payment belongs to the paid-for spell, never its completed mana child");
            assertEquals(text(current.prompt(), "executionStep"), "cost");
            assertEquals(text(occurrence(current.state(), owner), "step"), "cost");
            assertNotEquals(text(current.prompt(), "id"), text(resumed.prompt(), "id"));
            var remaining = zone(viewer(current.state()), "Battlefield").getAsJsonArray("cards").asList().stream()
                    .map(value -> value.getAsJsonObject()).filter(value -> text(value, "name").equals("Wastes")
                            && !value.get("tapped").getAsBoolean()).findFirst().orElseThrow();
            start(game, current, remaining, "Add {C}");
            var resolved = game.until(i -> i.seat() == 0 && priority(i) && i.state().getAsJsonArray("stack").isEmpty()
                    && zone(viewer(i.state()), "Battlefield").getAsJsonArray("cards").asList().stream()
                    .anyMatch(value -> text(value.getAsJsonObject(), "name").equals("Traxos, Scourge of Kroog")));
            assertEquals(text(occurrence(resolved.state(), owner), "outcome"), "resolved");
            assertTrue(activities(resolved.state(), owner).stream().anyMatch(value -> text(value, "kind").equals("resolved")));
            assertThrows(IllegalArgumentException.class, () -> game.table.seat(0).action(request(resumed, Map.of("action", "ok"))));
        }
    }

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void decliningAnOptionalTriggerRecordsItsResultButCompletesAfterStackCleanup() throws Exception {
        try (var game = new RulesScenario("execution-optional-decline", Map.of(0,
                List.of("Soul's Attendant")), "Memnite", "Rhys the Redeemed")) {
            var main = main(game);
            int life = viewer(main.state()).get("life").getAsInt();
            var hand = zone(viewer(main.state()), "Hand").getAsJsonArray("cards").get(0).getAsJsonObject();
            start(game, main, hand, "");
            Input choice;
            for (;;) {
                choice = game.until(i -> i.seat() == 0 && (text(i.prompt(), "inputType").equals("InputConfirm")
                        || mana(i) && i.prompt().get("okEnabled").getAsBoolean()));
                if (!mana(choice)) break;
                assertTrue(game.answer(choice, Map.of("action", "ok")));
            }
            String id = execution(choice);
            assertEquals(text(occurrence(choice.state(), id), "sourceName"), "Soul's Attendant");
            assertTrue(choice.prompt().get("cancelEnabled").getAsBoolean());
            var events = new ArrayList<ExecutionTracker.Committed>();
            var listener = new Object() {
                @Subscribe public void committed(ExecutionTracker.Committed event) {
                    if (event.execution() != null && event.execution().id().equals(id)) events.add(event);
                }
            };
            game.table.game.subscribeToEvents(listener);
            var obsolete = choice;
            assertTrue(game.answer(choice, Map.of("action", "cancel")));
            var settled = game.until(i -> i.seat() == 0 && priority(i) && i.state().getAsJsonArray("stack").isEmpty());
            assertEquals(text(occurrence(settled.state(), id), "outcome"), "declined");
            assertEquals(viewer(settled.state()).get("life").getAsInt(), life);
            assertTrue(events.stream().anyMatch(event -> event.event() instanceof forge.game.event.GameEventSpellResolved));
            assertTrue(events.stream().noneMatch(event -> event.execution().step().equals("complete")),
                    "Declining cannot publish completion before its real settlement and stack removal events");
            assertThrows(IllegalArgumentException.class, () -> game.table.seat(0).action(request(obsolete, Map.of("action", "ok"))));
        }
    }

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void aCompletionBetweenObservationCopiesRetriesUntilItsActualDrawAndSettlementAreIncluded() throws Exception {
        try (var game = new RulesScenario("execution-coherent-settlement", Map.of(0,
                List.of(MARE, "Island", "Island", "Island")), "Island", "Rhys the Redeemed", true)) {
            var main = main(game);
            var actor = game.table.game.getPlayers().get(0);
            var activity = new MatchActivity(actor.getView());
            var projection = new MatchExecutions(game.table.game.getExecutions(), actor.getView(), activity);
            activity.executionFilter(projection::reference);
            game.table.game.subscribeToEvents(projection); game.table.game.subscribeToEvents(activity);
            start(game, main, permanent(main.state(), MARE), "Draw a card");
            var payment = payment(game);
            String id = execution(payment);
            assertTrue(game.answer(payment, Map.of("action", "ok")));
            var stacked = game.until(i -> i.seat() == 0 && priority(i) && i.state().getAsJsonArray("stack").size() == 1);
            var copies = new java.util.concurrent.atomic.AtomicInteger();
            var publication = MatchActivity.capture(() -> {
                var copied = activity.frame();
                if (copies.incrementAndGet() == 1) {
                    assertTrue(copied.entries().stream().noneMatch(entry -> Objects.equals(entry.executionId(), id)
                            && Set.of("draw", "resolved").contains(entry.kind())));
                    assertTrue(game.answer(stacked, Map.of("action", "ok")));
                    try {
                        game.until(i -> i.seat() == 0 && priority(i) && i.state().getAsJsonArray("stack").isEmpty());
                    } catch (Exception error) { throw new AssertionError(error); }
                }
                return copied;
            }, projection::frame);
            assertEquals(copies.get(), 2, "A real resolution between the two immutable frame reads forces a retry");
            var completed = publication.executions().occurrences().stream().filter(item -> item.id().equals(id)).findFirst().orElseThrow();
            assertEquals(completed.step(), "complete"); assertEquals(completed.outcome(), "resolved");
            var facts = publication.activity().entries().stream().filter(entry -> Objects.equals(entry.executionId(), id)).toList();
            assertTrue(facts.stream().anyMatch(entry -> entry.kind().equals("draw") && entry.executionStep().equals("resolution")));
            assertTrue(facts.stream().anyMatch(entry -> entry.kind().equals("resolved") && entry.executionStep().equals("resolution")));
        }
    }

    @DataProvider public Object[][] lostTargets() {
        return new Object[][] {{"Giant Growth", "+3/+3", "Forest", 1, false},
                {"Nyxborn Shieldmate", "Bestow", "Plains", 3, true},
                {"Pouncing Shoreshark", "Mutate", "Island", 4, true}};
    }

    @Test(groups = "canonical-interaction", dataProvider = "lostTargets", timeOut = 90000)
    public void aLostTargetCompletesOnlyAfterCleanupAndPreservesCreatureFallbacks(String spell, String route,
                                                                                 String land, int landCount, boolean creatureFallback) throws Exception {
        var battlefield = new ArrayList<>(Collections.nCopies(landCount, land)); battlefield.add("Grizzly Bears");
        try (var game = new RulesScenario("execution-lost-target-" + route.replaceAll("[^A-Za-z]", ""), Map.of(0,
                battlefield, 1, List.of("Seal of Removal")), spell, "Rhys the Redeemed")) {
            var main = main(game);
            var hand = zone(viewer(main.state()), "Hand").getAsJsonArray("cards").get(0).getAsJsonObject();
            start(game, main, hand, route);
            var target = game.until(i -> i.seat() == 0 && text(i.prompt(), "inputType").equals("InputSelectTargets"));
            assertTrue(game.answer(target, Map.of("action", "card", "key", text(permanent(target.state(), "Grizzly Bears"), "key"))));
            var payment = payment(game);
            assertTrue(game.answer(payment, Map.of("action", "ok")));
            var response = game.until(i -> i.seat() == 1 && priority(i) && i.state().getAsJsonArray("stack").size() == 1);
            String id = text(response.state().getAsJsonArray("stack").get(0).getAsJsonObject(), "executionId");
            var events = new ArrayList<ExecutionTracker.Committed>();
            var listener = new Object() {
                @Subscribe public void committed(ExecutionTracker.Committed event) {
                    if (event.execution() != null && event.execution().id().equals(id)) events.add(event);
                }
            };
            game.table.game.subscribeToEvents(listener);
            start(game, response, permanent(response.state(), "Seal of Removal"), "Return target creature");
            target = game.until(i -> i.seat() == 1 && text(i.prompt(), "inputType").equals("InputSelectTargets"));
            var bear = zone(player(target.state(), 0), "Battlefield").getAsJsonArray("cards").asList().stream()
                    .map(value -> value.getAsJsonObject()).filter(card -> text(card, "name").equals("Grizzly Bears")).findFirst().orElseThrow();
            assertTrue(game.answer(target, Map.of("action", "card", "key", text(bear, "key"))));
            var resolved = game.until(i -> i.seat() == 0 && priority(i) && i.state().getAsJsonArray("stack").isEmpty());
            assertEquals(text(occurrence(resolved.state(), id), "outcome"), creatureFallback ? "resolved" : "countered");
            assertTrue(zone(viewer(resolved.state()), creatureFallback ? "Battlefield" : "Graveyard").getAsJsonArray("cards").asList().stream()
                    .anyMatch(value -> text(value.getAsJsonObject(), "name").equals(spell)));
            var facts = activities(resolved.state(), id);
            assertTrue(facts.stream().anyMatch(entry -> text(entry, "kind").equals("resolved")
                    && text(entry, "outcome").equals(creatureFallback ? "resolved" : "notResolved")));
            assertTrue(facts.stream().anyMatch(entry -> text(entry, "kind").equals(creatureFallback ? "arrived" : "moved")
                    && text(entry, "cardName").equals(spell)));
            assertFalse(events.isEmpty());
            assertTrue(events.stream().noneMatch(event -> event.execution().step().equals("complete")),
                    "No owned committed event, including graveyard movement and stack removal, can follow the terminal transition");
            assertTrue(facts.stream().allMatch(entry -> !text(entry, "executionStep").equals("complete")));
        }
    }

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void concedingDuringOwnedPaymentClearsTheDecisionAndPublishesSurvivingSeats() throws Exception {
        try (var game = new RulesScenario("execution-concede-payment", Map.of(0,
                List.of(MARE, "Island", "Island", "Island")), "Island", "Rhys the Redeemed", true)) {
            var main = main(game);
            start(game, main, permanent(main.state(), MARE), "Draw a card");
            var payment = payment(game);
            assertFalse(execution(payment).isEmpty());
            game.table.seat(0).concede(request(payment, Map.of()));
            var departed = game.state(0);
            assertTrue(viewer(departed).get("eliminated").getAsBoolean());
            assertTrue(departed.get("prompt").isJsonNull());
            assertNotNull(departed.getAsJsonObject("executions"));
            assertThrows(IllegalArgumentException.class, () -> game.table.seat(0).action(request(payment, Map.of("action", "ok"))));
            var surviving = game.until(i -> i.seat() != 0 && priority(i));
            assertNotEquals(surviving.state().get("viewerId").getAsInt(), departed.get("viewerId").getAsInt());
        }
    }

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void nestedManaResponseResolutionAndCancellationKeepOneOccurrencePerActivation() throws Exception {
        try (var game = new RulesScenario("execution-loch-mare", Map.of(0, List.of(
                MARE, "Island", "Island", "Island", "Island", "Island", "Island")),
                "Island", "Rhys the Redeemed", true)) {
            var main = main(game);
            int hand = count(viewer(main.state()), "Hand");
            start(game, main, permanent(main.state(), MARE), "Draw a card");
            var payment = payment(game);
            String original = execution(payment);
            assertEquals(text(payment.prompt(), "executionStep"), "cost");
            var parent = occurrence(payment.state(), original);
            assertEquals(text(parent, "kind"), "activation");
            assertEquals(text(parent, "budgetRootId"), original);
            assertEquals(text(parent, "sourceName"), MARE);
            assertEquals(text(parent, "sourceCardId"), text(permanent(payment.state(), MARE), "visualId"));
            assertEquals(parent.get("actorPlayerId"), payment.state().get("viewerId"));
            assertNotEquals(original, text(payment.prompt(), "id"));
            start(game, payment, untappedIsland(payment.state()), "Add {U}");
            payment = game.until(i -> i.seat() == 0 && mana(i) && i.prompt().get("okEnabled").getAsBoolean()
                    && tappedIslands(i.state()) == 1);
            assertEquals(execution(payment), original, "A replaced input scope still pays for the same activation");
            start(game, payment, untappedIsland(payment.state()), "Add {U}");
            // Paying the final shard completes this actual input directly;
            // there is no fabricated extra confirmation after payment.
            var stacked = game.until(i -> i.seat() == 0 && priority(i) && i.state().getAsJsonArray("stack").size() == 1);
            assertEquals(text(stacked.state().getAsJsonArray("stack").get(0).getAsJsonObject(), "executionId"), original);
            assertEquals(counter(stacked.state(), MARE, "-1/-1"), 2);
            var manaChildren = occurrences(stacked.state()).stream().filter(value -> text(value, "parentId").equals(original)).toList();
            assertEquals(manaChildren.size(), 2);
            for (var child : manaChildren) {
                assertEquals(text(child, "kind"), "mana");
                assertEquals(text(child, "budgetRootId"), original);
                assertEquals(text(child, "outcome"), "resolved");
            }
            var counterCost = activities(stacked.state(), original).stream().filter(value -> text(value, "kind").equals("counters"))
                    .findFirst().orElseThrow();
            assertEquals(text(counterCost, "executionStep"), "cost");
            assertEquals(text(counterCost.getAsJsonObject("cause"), "id"), original);
            assertEquals(counterCost.getAsJsonObject("change").get("before").getAsInt(), 3);
            assertEquals(counterCost.getAsJsonObject("change").get("after").getAsInt(), 2);

            // A response is a new announced action, never a child guessed from
            // the unrelated pending stack top of the same physical card.
            start(game, stacked, permanent(stacked.state(), MARE), "Draw a card");
            payment = payment(game);
            String response = execution(payment);
            assertNotEquals(response, original);
            assertEquals(text(occurrence(payment.state(), response), "parentId"), "");
            assertTrue(game.answer(payment, Map.of("action", "ok")));
            var two = game.until(i -> i.seat() == 0 && priority(i) && i.state().getAsJsonArray("stack").size() == 2);
            assertEquals(two.state().getAsJsonArray("stack").asList().stream()
                    .map(value -> text(value.getAsJsonObject(), "executionId")).collect(java.util.stream.Collectors.toSet()), Set.of(original, response));
            var resolved = game.until(i -> i.seat() == 0 && priority(i) && i.state().getAsJsonArray("stack").isEmpty());
            assertEquals(count(viewer(resolved.state()), "Hand"), hand + 2);
            assertEquals(counter(resolved.state(), MARE, "-1/-1"), 1);
            for (String id : List.of(original, response)) {
                assertEquals(text(occurrence(resolved.state(), id), "outcome"), "resolved");
                assertTrue(activities(resolved.state(), id).stream().anyMatch(value -> text(value, "kind").equals("draw")
                        && text(value, "executionStep").equals("resolution")));
                assertOrdered(resolved.state(), id, List.of("preparation", "choice", "cost", "stack", "resolution", "complete"));
            }

            start(game, resolved, permanent(resolved.state(), MARE), "Draw a card");
            payment = payment(game);
            String cancelled = execution(payment);
            start(game, payment, untappedIsland(payment.state()), "Add {U}");
            payment = game.until(i -> i.seat() == 0 && mana(i) && i.prompt().get("cancelEnabled").getAsBoolean()
                    && tappedIslands(i.state()) == 5);
            assertEquals(execution(payment), cancelled);
            var obsolete = payment;
            assertTrue(game.answer(payment, Map.of("action", "cancel")));
            var afterCancel = game.until(i -> i.seat() == 0 && priority(i));
            assertEquals(text(occurrence(afterCancel.state(), cancelled), "outcome"), "cancelled");
            assertEquals(counter(afterCancel.state(), MARE, "-1/-1"), 1);
            assertEquals(count(viewer(afterCancel.state()), "Hand"), hand + 2);
            assertTrue(afterCancel.state().getAsJsonArray("stack").isEmpty());
            assertThrows(IllegalArgumentException.class, () -> game.table.seat(0).action(request(obsolete, Map.of("action", "ok"))));
            for (int seat = 1; seat < 4; seat++) {
                var other = game.state(seat);
                assertEquals(text(occurrence(other, original), "outcome"), "resolved");
                assertTrue(other.get("prompt").isJsonNull());
                assertFalse(other.getAsJsonObject("executions").toString().contains("sourceCardKey"));
            }
            Files.writeString(game.evidence.resolve("executions.json"), JSON.toJson(afterCancel.state()));
        }
    }

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void copiedActivationAndRepeatedDrawTriggersAreDistinctAndRetainTheirActualProducer() throws Exception {
        try (var game = new RulesScenario("execution-copies-draws", Map.of(0, List.of(MARE,
                "Rings of Brighthearth", "Chasm Skulker", "Island", "Island", "Island", "Island", "Island", "Island")),
                "Island", "Rhys the Redeemed", true)) {
            var main = main(game);
            int hand = count(viewer(main.state()), "Hand");
            int counters = counter(main.state(), "Chasm Skulker", "+1/+1");
            long baseline = main.state().getAsJsonObject("executions").get("lastSequence").getAsLong();
            start(game, main, permanent(main.state(), MARE), "Draw a card");
            var payment = payment(game);
            String original = execution(payment);
            assertTrue(game.answer(payment, Map.of("action", "ok")));
            var ringPayment = payment(game);
            String rings = execution(ringPayment);
            assertNotEquals(rings, original);
            assertEquals(text(occurrence(ringPayment.state(), rings), "parentId"), original);
            assertEquals(text(occurrence(ringPayment.state(), rings), "budgetRootId"), rings,
                    "Causal parentage does not merge independent trigger decision budgets");
            assertTrue(game.answer(ringPayment, Map.of("action", "ok")));
            var resolved = game.until(i -> i.seat() == 0 && priority(i) && i.state().getAsJsonArray("stack").isEmpty());
            assertEquals(count(viewer(resolved.state()), "Hand"), hand + 2);
            assertEquals(counter(resolved.state(), MARE, "-1/-1"), 2, "The copy did not pay the original's counter cost twice");
            assertEquals(counter(resolved.state(), "Chasm Skulker", "+1/+1"), counters + 2);
            var copied = occurrences(resolved.state()).stream().filter(value -> text(value, "sourceName").equals(MARE)
                    && text(value, "parentId").equals(rings)).toList();
            assertEquals(copied.size(), 1);
            String copy = text(copied.get(0), "id");
            assertNotEquals(copy, original);
            var draws = activities(resolved.state(), original).stream().filter(value -> text(value, "kind").equals("draw")).count()
                    + activities(resolved.state(), copy).stream().filter(value -> text(value, "kind").equals("draw")).count();
            assertEquals(draws, 2L);
            var triggers = occurrences(resolved.state()).stream().filter(value -> text(value, "sourceName").equals("Chasm Skulker")
                    && Set.of(original, copy).contains(text(value, "parentId"))).toList();
            assertEquals(triggers.size(), 2);
            assertNotEquals(text(triggers.get(0), "id"), text(triggers.get(1), "id"));
            assertEquals(triggers.stream().map(value -> text(value, "parentId")).collect(java.util.stream.Collectors.toSet()), Set.of(original, copy));
            for (var trigger : triggers) {
                String id = text(trigger, "id");
                assertEquals(text(trigger, "outcome"), "resolved");
                assertEquals(text(trigger, "budgetRootId"), id);
                assertTrue(activities(resolved.state(), id).stream().anyMatch(value -> text(value, "kind").equals("counters")));
            }
            assertTrue(resolved.state().getAsJsonObject("executions").get("lastSequence").getAsLong() > baseline);
            Files.writeString(game.evidence.resolve("executions.json"), JSON.toJson(resolved.state()));
        }
    }

    private static Input main(RulesScenario game) throws Exception {
        return game.until(i -> i.seat() == 0 && priority(i) && text(i.state(), "phaseKey").equals("MAIN1"));
    }
    private static Input payment(RulesScenario game) throws Exception {
        return game.until(i -> i.seat() == 0 && mana(i) && i.prompt().get("okEnabled").getAsBoolean());
    }
    /** Source callbacks can publish intermediate payment progress. Observe it
     * without answering a still-running source or consuming another choice. */
    private static Input awaitOwnedPayment(RulesScenario game, String owner,
                                           java.util.function.Predicate<Input> settled) throws Exception {
        return awaitOwnedPayment(game, owner, settled, null);
    }
    private static Input awaitOwnedPayment(RulesScenario game, String owner,
                                           java.util.function.Predicate<Input> settled,
                                           java.util.function.Consumer<Input> expectedSourceChoice) throws Exception {
        long deadline = System.nanoTime() + java.util.concurrent.TimeUnit.SECONDS.toNanos(20);
        while (System.nanoTime() < deadline) {
            for (int seat = 0; seat < 4; seat++) {
                var state = game.state(seat);
                if (state.get("prompt").isJsonNull()) continue;
                var input = new Input(seat, state, state.getAsJsonObject("prompt"));
                if (seat != 0) {
                    assertTrue(priority(input), "A new required choice must be handled explicitly: " + input.prompt());
                    continue;
                }
                if (!mana(input)) {
                    assertNotNull(expectedSourceChoice, "A new required choice must be handled explicitly: " + input.prompt());
                    expectedSourceChoice.accept(input);
                    continue;
                }
                assertEquals(execution(input), owner, "Every intermediate payment retains its actual owner");
                assertEquals(text(input.prompt(), "executionStep"), "cost");
                assertEquals(text(occurrence(state, owner), "step"), "cost");
                if (settled.test(input)) return input;
            }
            Thread.sleep(5);
        }
        throw new AssertionError("Timed out observing the actual payment progress: " + game.table.testViews());
    }
    private static boolean unpaid(Input input, String symbol) {
        var parts = input.prompt().getAsJsonArray("unpaidManaParts");
        return parts != null && parts.size() == 1 && text(parts.get(0).getAsJsonObject(), "symbol").equals(symbol)
                && parts.get(0).getAsJsonObject().get("count").getAsInt() == 1;
    }
    private static boolean priority(Input input) { return text(input.prompt(), "inputType").equals("InputPassPriority"); }
    private static boolean mana(Input input) { return text(input.prompt(), "inputType").startsWith("InputPayMana"); }
    private static String execution(Input input) {
        String id = text(input.prompt(), "executionId");
        assertFalse(id.isBlank(), "The current decision references its actual execution"); return id;
    }
    private static List<JsonObject> occurrences(JsonObject state) {
        return state.getAsJsonObject("executions").getAsJsonArray("occurrences").asList().stream().map(value -> value.getAsJsonObject()).toList();
    }
    private static JsonObject occurrence(JsonObject state, String id) {
        return occurrences(state).stream().filter(value -> text(value, "id").equals(id)).findFirst().orElseThrow();
    }
    private static List<JsonObject> activities(JsonObject state, String execution) {
        return state.getAsJsonArray("activity").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "executionId").equals(execution)).toList();
    }
    private static void assertOrdered(JsonObject state, String execution, List<String> expected) {
        var actual = state.getAsJsonObject("executions").getAsJsonArray("transitions").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "executionId").equals(execution)).map(value -> text(value, "step")).toList();
        assertEquals(actual, expected);
    }
    private static JsonObject permanent(JsonObject state, String name) {
        return zone(viewer(state), "Battlefield").getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "name").equals(name)).findFirst().orElseThrow();
    }
    private static JsonObject untappedIsland(JsonObject state) {
        return zone(viewer(state), "Battlefield").getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "name").equals("Island") && !value.get("tapped").getAsBoolean()).findFirst().orElseThrow();
    }
    private static long tappedIslands(JsonObject state) {
        return zone(viewer(state), "Battlefield").getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "name").equals("Island") && value.get("tapped").getAsBoolean()).count();
    }
    private static int counter(JsonObject state, String source, String type) {
        var counters = permanent(state, source).getAsJsonObject("counters");
        return counters.has(type) ? counters.get(type).getAsInt() : 0;
    }
    private static void start(RulesScenario game, Input input, JsonObject source, String contains) {
        var actions = JSON.toJsonTree(game.table.seat(input.seat()).sourceActions(request(input, Map.of("key", text(source, "key"))))).getAsJsonObject();
        var action = actions.getAsJsonArray("actions").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "text").contains(contains)).findFirst().orElseThrow();
        assertTrue(game.answer(input, Map.of("action", "useSourceAction", "key", text(actions, "sourceCardKey"), "actionId", text(action, "id"))));
    }
}
