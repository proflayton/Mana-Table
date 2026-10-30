package forge.api;

import com.google.gson.Gson;
import com.google.gson.JsonObject;
import forge.gamemodes.match.input.InputPassPriority;
import forge.gui.interfaces.IGuiGame;
import forge.player.PlayerControllerHuman;
import org.testng.annotations.DataProvider;
import org.testng.annotations.Test;

import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Proxy;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;
import java.nio.file.Files;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** The production source-action capability observes first, then invokes exactly one engine ability. */
public class SourceActionsTest {
    private static final Gson JSON = new Gson();

    @DataProvider(name = "sourceQueryAudiences")
    public Object[][] sourceQueryAudiences() { return new Object[][] { { false }, { true } }; }

    @Test(dataProvider = "sourceQueryAudiences", timeOut = 90000)
    public void sourceQueryValuesRejectNestedMutationAndRetainTheirActualActionAuthority(boolean automated) throws Exception {
        var field = Map.of(0, List.of("Forest", "Forest", "Forest"));
        try (var game = automated ? RulesScenario.automated("source-query-values-ai", field, "Manalith", "Rhys the Redeemed")
                : new RulesScenario("source-query-values-human", field, "Manalith", "Rhys the Redeemed")) {
            var input = main(game);
            HeadlessPlatform.query(game.table, () -> null);
            var session = game.session(0);
            record ReadValues(Object facts, com.google.gson.JsonElement retained, JsonObject mana) { }
            var values = HeadlessPlatform.query(game.table, () -> {
                var before = game.state(0);
                var spell = card(before, "Hand", "Manalith");
                var sourceRequest = request(input, Map.of("key", text(spell, "key")));
                Map<String, Object> reply = automated ? session.automatedObservation() : session.sourceActions(sourceRequest);
                Object facts = automated ? reply.get("domain") : reply;
                var retained = JSON.toJsonTree(facts);
                assertTrue(retained.getAsJsonObject().getAsJsonArray("actions").size() > 0);
                if (automated) assertTrue(retained.toString().contains("\"operationGraph\""), "The actual private domain includes structured effect facts");
                else {
                    var preview = retained.getAsJsonObject().getAsJsonArray("actions").get(0).getAsJsonObject().getAsJsonObject("costPreview");
                    assertNotNull(preview);
                    assertEquals(preview.getAsJsonObject("funding").getAsJsonArray("sources").size(), 3);
                }
                assertFrozenSourceValue(facts, automated ? "private domain" : "human source reply");
                Map<String, Object> repeated = automated ? session.automatedObservation() : session.sourceActions(sourceRequest);
                Object again = automated ? repeated.get("domain") : repeated;
                assertEquals(JSON.toJsonTree(again), retained, "A consumer cannot rewrite a later source read through an earlier value tree");
                if (automated) assertSame(again, facts, "A repeated AI read retains the same committed immutable source query");
                assertEquals(game.state(0), before, "Reading and rejected value mutations do not advance the published game");
                var forest = card(before, "Battlefield", "Forest");
                var mana = JSON.toJsonTree(session.sourceActions(request(input, Map.of("key", text(forest, "key"))))).getAsJsonObject();
                return new ReadValues(facts, retained, mana);
            });
            var facts = values.facts(); var retained = values.retained(); var mana = values.mana();
            Files.writeString(game.evidence.resolve("source-query-before-mutation.json"), JSON.toJson(retained));
            assertTrue(game.answer(input, invoke(mana, mana.getAsJsonArray("actions").get(0).getAsJsonObject())),
                    "Freezing descriptions does not freeze or replace the live engine action authority");
            var paid = game.until(next -> next.seat() == 0 && priority(next)
                    && viewer(next.state()).getAsJsonObject("mana").get("G").getAsInt() == 1);
            assertEquals(zone(viewer(paid.state()), "Battlefield").getAsJsonArray("cards").asList().stream()
                    .map(value -> value.getAsJsonObject()).filter(value -> value.get("tapped").getAsBoolean()).count(), 1L);
            assertEquals(JSON.toJsonTree(facts), retained, "The original source facts cannot change with later resource expenditure");
            assertThrows(IllegalArgumentException.class, () -> session.action(request(input,
                    invoke(mana, mana.getAsJsonArray("actions").get(0).getAsJsonObject()))));
            assertNull(session.state().get("error"));
            Files.writeString(game.evidence.resolve("source-query-live-continuation.json"), JSON.toJson(Map.of(
                    "audience", automated ? "automated" : "human", "retained", retained, "paid", paid.state())));
        }
    }

    @SuppressWarnings("unchecked")
    private static void assertFrozenSourceValue(Object value, String path) {
        if (value instanceof Map<?, ?> map) {
            assertThrows(UnsupportedOperationException.class,
                    () -> ((Map<Object, Object>)map).put("__source_query_consumer_probe", null));
            for (var entry : map.entrySet()) assertFrozenSourceValue(entry.getValue(), path + "." + entry.getKey());
        } else if (value instanceof List<?> list) {
            assertThrows(UnsupportedOperationException.class, () -> ((List<Object>)list).add(null));
            for (int index = 0; index < list.size(); index++) assertFrozenSourceValue(list.get(index), path + "[" + index + "]");
        }
    }

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void aRealStackActivationRetainsItsImmutableExtraSourcePortraitAndCurrentActionHandle() throws Exception {
        // The real opening seven plus the multiplayer first draw contains all
        // eight cards, independent of shuffle. Stop before any second draw.
        var library = List.of("Lightning Storm", "Mountain", "Mountain", "Mountain", "Mountain", "Mountain", "Mountain", "Mountain");
        try (var game = RulesScenario.automated("source-query-stack-portrait", Map.of(0, List.of("Mountain", "Mountain", "Mountain")),
                "Mountain", "Rhys the Redeemed", Map.of(0, library))) {
            var input = main(game);
            assertEquals(count(viewer(input.state()), "Hand"), 8);
            // External policies pay their own costs; Forge's human Auto button
            // is deliberately unavailable. Fund through three actual abilities.
            for (int amount = 1; amount <= 3; amount++) {
                var mountain = zone(viewer(input.state()), "Battlefield").getAsJsonArray("cards").asList().stream()
                        .map(value -> value.getAsJsonObject()).filter(value -> text(value, "name").equals("Mountain")
                                && !value.get("tapped").getAsBoolean()).findFirst().orElseThrow();
                var mana = query(game, input, mountain);
                var manaAction = mana.getAsJsonArray("actions").get(0).getAsJsonObject();
                assertEquals(text(manaAction, "kind"), "activate");
                assertTrue(game.answer(input, invoke(mana, manaAction)));
                final int expected = amount;
                input = game.until(next -> next.seat() == 0 && priority(next)
                        && viewer(next.state()).getAsJsonObject("mana").get("R").getAsInt() == expected);
            }
            var spell = query(game, input, card(input.state(), "Hand", "Lightning Storm"));
            assertTrue(game.answer(input, invoke(spell, castAction(spell))));
            finishTargetedCast(game, "Lightning Storm", null, "R");
            var stacked = game.until(next -> next.seat() == 0 && priority(next) && next.state().getAsJsonArray("stack").size() == 1);
            assertEquals(viewer(stacked.state()).getAsJsonObject("mana").get("R").getAsInt(), 0,
                    "The actual cast spends all three units produced through the real source actions");
            HeadlessPlatform.query(game.table, () -> null);
            var session = game.session(0);
            record StackRead(Map<String, Object> raw, JsonObject observation, JsonObject source) { }
            var values = HeadlessPlatform.query(game.table, () -> {
                var before = JSON.toJsonTree(session.state());
                var raw = session.automatedObservation();
                var observation = JSON.toJsonTree(raw).getAsJsonObject();
                var extra = observation.getAsJsonObject("state").getAsJsonArray("actionSources");
                assertEquals(extra.size(), 1, "The actual stack ability has one physical extra source");
                var portrait = extra.get(0).getAsJsonObject();
                assertEquals(text(portrait, "zone"), "Stack");
                assertEquals(text(portrait, "decisionId"), text(stacked.prompt(), "id"));
                var card = portrait.getAsJsonObject("card");
                assertEquals(text(card, "name"), "Lightning Storm");
                assertFalse(card.get("selectable").getAsBoolean());
                assertFalse(card.get("highlighted").getAsBoolean());
                assertFalse(text(card, "key").isEmpty());
                assertFalse(text(card, "visualId").isEmpty());
                assertFalse(text(card, "combatId").isEmpty());
                var source = observation.getAsJsonObject("domain").getAsJsonArray("sources").asList().stream()
                        .map(value -> value.getAsJsonObject()).filter(value -> text(value, "sourceCardKey").equals(text(card, "key")))
                        .findFirst().orElseThrow();
                assertEquals(text(source, "sourceCardId"), text(card, "visualId"));
                assertFalse(source.getAsJsonArray("actions").isEmpty());
                assertTrue(source.getAsJsonArray("actions").asList().stream().map(value -> value.getAsJsonObject())
                        .allMatch(action -> text(action, "kind").equals("activate")));
                var rawExtras = ((Map<?, ?>)raw.get("state")).get("actionSources");
                assertFrozenSourceValue(rawExtras, "extra stack source portraits");
                assertFrozenSourceValue(raw.get("domain"), "matching stack domain");
                var repeated = session.automatedObservation();
                assertSame(repeated.get("domain"), raw.get("domain"));
                assertSame(((Map<?, ?>)repeated.get("state")).get("actionSources"), rawExtras,
                        "The cache owns the domain and extra portraits together");
                assertEquals(JSON.toJsonTree(repeated), observation);
                assertEquals(JSON.toJsonTree(session.state()), before);
                return new StackRead(raw, observation, source);
            });
            Files.writeString(game.evidence.resolve("stack-source-before-activation.json"), JSON.toJson(values.observation()));
            var action = values.source().getAsJsonArray("actions").get(0).getAsJsonObject();
            assertTrue(game.answer(stacked, invoke(values.source(), action)), "The extra source's exact retained handle runs its real stack activation");
            var discard = game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").equals("InputSelectCardsFromList"));
            assertTrue(game.answer(discard, Map.of("action", "card", "key", text(card(discard.state(), "Hand", "Mountain"), "key"))));
            var activated = game.until(next -> next.seat() == 0 && priority(next) && next.state().getAsJsonArray("stack").size() == 2);
            assertEquals(count(viewer(activated.state()), "Hand"), 6);
            assertEquals(count(viewer(activated.state()), "Graveyard"), 1);
            assertEquals(text(card(activated.state(), "Graveyard", "Mountain"), "name"), "Mountain");
            var changeTargets = game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").equals("InputConfirm"));
            assertTrue(game.answer(changeTargets, Map.of("action", "cancel")), "Keep the original real spell target");
            String charge = forge.game.card.CounterEnumType.CHARGE.getName();
            var charged = game.until(next -> next.seat() == 0 && priority(next) && next.state().getAsJsonArray("stack").size() == 1
                    && next.state().getAsJsonArray("stack").get(0).getAsJsonObject().getAsJsonObject("card").getAsJsonObject("counters").has(charge));
            assertEquals(charged.state().getAsJsonArray("stack").get(0).getAsJsonObject()
                    .getAsJsonObject("card").getAsJsonObject("counters").get(charge).getAsInt(), 2);
            var refreshed = JSON.toJsonTree(session.automatedObservation()).getAsJsonObject();
            assertEquals(refreshed.getAsJsonObject("state").getAsJsonArray("actionSources").get(0).getAsJsonObject()
                    .getAsJsonObject("card").getAsJsonObject("counters").get(charge).getAsInt(), 2);
            assertEquals(JSON.toJsonTree(values.raw()), values.observation(), "The old portrait remains unchanged after a real counter event");
            assertThrows(IllegalArgumentException.class, () -> session.action(request(stacked, invoke(values.source(), action))));
            assertNull(session.state().get("error"));
            Files.writeString(game.evidence.resolve("stack-source-after-activation.json"), JSON.toJson(Map.of(
                    "activated", activated.state(), "charged", charged.state(), "refreshed", refreshed)));
        }
    }

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void anExplicitFlexibleManaSourceLeavesItsRealColorChoiceToTheHuman() throws Exception {
        try (var game = new RulesScenario("source-actions-explicit-mana-color", Map.of(0,
                List.of("Temple of Epiphany", "Island", "Island", "Island")), "Island", "Balmor, Battlemage Captain")) {
            var main = main(game);
            var commander = query(game, main, card(main.state(), "Command", "Balmor, Battlemage Captain"));
            assertTrue(game.answer(main, invoke(commander, castAction(commander))));
            var payment = game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").startsWith("InputPayMana"));
            var temple = query(game, payment, card(payment.state(), "Battlefield", "Temple of Epiphany"));
            var ability = temple.getAsJsonArray("actions").get(0).getAsJsonObject();
            HeadlessPlatform.query(game.table, () -> null);
            var before = game.state(0);
            for (var malformed : List.of(Map.of("colors", Map.of("G", 1)), Map.of("colors", Map.of("R", 0)),
                    Map.of("colors", Map.of("R", 1025)), Map.of("colors", Map.of("R", 1), "actionId", "invented"))) {
                var answer = new java.util.LinkedHashMap<String, Object>(invoke(temple, ability));
                answer.put("manaOutput", malformed);
                assertThrows(IllegalArgumentException.class, () -> game.table.seat(0).action(request(payment, answer)));
                assertEquals(game.state(0), before, "An invalid or unoffered output cannot consume the input, tap its source or spend mana");
            }
            assertTrue(game.answer(payment, invoke(temple, ability)));
            var choice = game.until(next -> next.seat() == 0 && text(next.prompt(), "context").equals("colorChoice"));
            var options = choice.prompt().getAsJsonArray("choices").asList().stream().map(value -> value.getAsJsonObject()).toList();
            var bill = choice.prompt().getAsJsonObject("manaPaymentContext");
            assertNotNull(bill, "The genuine color chooser retains the actual parent cost without borrowing a past prompt");
            assertEquals(bill.get("payerId"), choice.state().get("viewerId"));
            assertEquals(bill.getAsJsonArray("unpaidManaParts"), payment.prompt().getAsJsonArray("unpaidManaParts"));
            var paidFor = bill.getAsJsonObject("source");
            assertEquals(text(paidFor, "cardId"), text(paidFor.getAsJsonObject("card"), "visualId"),
                    "A cost source not yet on the published stack links only its existing authorized readonly portrait");
            assertEquals(text(paidFor.getAsJsonObject("card"), "name"), "Balmor, Battlemage Captain");
            assertEquals(text(paidFor.getAsJsonObject("card"), "key"), "");
            assertFalse(paidFor.getAsJsonObject("card").get("selectable").getAsBoolean());
            assertFalse(paidFor.getAsJsonObject("card").get("highlighted").getAsBoolean());
            assertFalse(choice.prompt().has("usablePoolManaColors"), "Read-only nested context cannot mint pool-payment gestures");
            assertEquals(options.stream().map(value -> text(value, "mana")).collect(java.util.stream.Collectors.toSet()), java.util.Set.of("{U}", "{R}"),
                    "Explicitly selecting the flexible ability must not silently guess blue from the current UR bill");
            Files.writeString(game.evidence.resolve("actual-human-color-choice.json"), JSON.toJson(choice.state()));
            int red = options.stream().filter(value -> text(value, "mana").equals("{R}")).findFirst().orElseThrow().get("index").getAsInt();
            assertTrue(game.answer(choice, Map.of("action", "choice", "choices", List.of(red))));
            var blue = game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").startsWith("InputPayMana")
                    && card(next.state(), "Battlefield", "Temple of Epiphany").get("tapped").getAsBoolean());
            assertEquals(blue.prompt().getAsJsonArray("unpaidManaParts"), JSON.toJsonTree(List.of(Map.of("symbol", "{U}", "count", 1))));
            assertFalse(blue.prompt().has("manaPaymentContext"), "The nested context is consumed with its exact color choice");
            var island = query(game, blue, card(blue.state(), "Battlefield", "Island"));
            assertTrue(game.answer(blue, invoke(island, island.getAsJsonArray("actions").get(0).getAsJsonObject())));
            var resolved = game.until(next -> next.seat() == 0 && priority(next) && next.state().getAsJsonArray("stack").isEmpty()
                    && card(next.state(), "Battlefield", "Balmor, Battlemage Captain") != null);
            assertEquals(zone(viewer(resolved.state()), "Battlefield").getAsJsonArray("cards").asList().stream()
                    .map(value -> value.getAsJsonObject()).filter(value -> text(value, "name").equals("Island") && value.get("tapped").getAsBoolean()).count(), 1L);
            assertThrows(IllegalArgumentException.class, () -> game.table.seat(0).action(request(payment, invoke(temple, ability))));
        }
    }

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void anOrdinaryPriorityManaColorChoiceCannotInheritAPaymentContext() throws Exception {
        try (var game = new RulesScenario("source-actions-color-without-payment", Map.of(0, List.of("Temple of Epiphany")), "Island", "Balmor, Battlemage Captain")) {
            var main = main(game);
            var temple = query(game, main, card(main.state(), "Battlefield", "Temple of Epiphany"));
            assertTrue(game.answer(main, invoke(temple, temple.getAsJsonArray("actions").get(0).getAsJsonObject())));
            var choice = game.until(next -> next.seat() == 0 && text(next.prompt(), "context").equals("colorChoice"));
            assertFalse(choice.prompt().has("manaPaymentContext"));
            int red = choice.prompt().getAsJsonArray("choices").asList().stream().map(value -> value.getAsJsonObject())
                    .filter(value -> text(value, "mana").equals("{R}")).findFirst().orElseThrow().get("index").getAsInt();
            assertTrue(game.answer(choice, Map.of("action", "choice", "choices", List.of(red))));
            var floated = game.until(next -> next.seat() == 0 && priority(next)
                    && viewer(next.state()).getAsJsonObject("mana").get("R").getAsInt() == 1);
            assertFalse(floated.prompt().has("manaPaymentContext"));
        }
    }

    @Test(timeOut = 90000)
    public void aQueuedDisplayPublicationIsDistinctFromAReadOnlySourceQuery() throws Exception {
        try (var game = new RulesScenario("source-actions-queued-publication", Map.of(0, List.of("Forest")))) {
            var input = main(game);
            HeadlessPlatform.query(game.table, () -> null);
            var commander = card(game.state(0), "Command", "Rhys the Redeemed");
            var originalCatalog = query(game, input, commander);
            var before = game.state(0);
            var entered = new CountDownLatch(1);
            var publish = new CountDownLatch(1);
            var reader = java.util.concurrent.Executors.newSingleThreadExecutor(task -> {
                var thread = new Thread(task, "source-read-interleaving"); thread.setDaemon(true); return thread;
            });
            try {
                HeadlessPlatform.later(() -> {
                    entered.countDown();
                    try {
                        if (!publish.await(3, TimeUnit.SECONDS)) throw new IllegalStateException("Test publication was not released");
                    } catch (InterruptedException error) {
                        Thread.currentThread().interrupt(); throw new IllegalStateException(error);
                    }
                    // HeadlessPlatform's ordinary completion publishes this
                    // unchanged input, as any queued display callback would.
                });
                assertTrue(entered.await(3, TimeUnit.SECONDS));
                var queuedRead = reader.submit(() -> query(game, input, commander));
                publish.countDown();
                var interleavedCatalog = queuedRead.get(3, TimeUnit.SECONDS);
                var afterPublication = game.state(0);
                assertTrue(afterPublication.get("revision").getAsLong() > before.get("revision").getAsLong(),
                        "An independent display publication can advance revision before a queued source query runs");
                assertEquals(text(afterPublication.getAsJsonObject("prompt"), "id"), text(input.prompt(), "id"));
                assertEquals(interleavedCatalog.get("revision"), afterPublication.get("revision"), "Read-only preview facts name their actual published frame");
                var originalFacts = originalCatalog.deepCopy(); originalFacts.remove("revision");
                var currentFacts = interleavedCatalog.deepCopy(); currentFacts.remove("revision");
                assertEquals(currentFacts, originalFacts, "The unchanged input keeps its exact source catalog, action handles and cost facts");
                var stalePreview = request(input, Map.of("key", text(commander, "key"), "expectedRevision", before.get("revision").getAsLong()));
                assertThrows(IllegalArgumentException.class, () -> game.table.seat(0).sourceActions(stalePreview));
                var currentPreview = request(input, Map.of("key", text(commander, "key"), "expectedRevision", afterPublication.get("revision").getAsLong()));
                assertEquals(JSON.toJsonTree(game.table.seat(0).sourceActions(currentPreview)), interleavedCatalog);
                var beforeFacts = before.deepCopy(); beforeFacts.remove("revision"); beforeFacts.remove("boardRevision");
                var afterFacts = afterPublication.deepCopy(); afterFacts.remove("revision"); afterFacts.remove("boardRevision");
                assertEquals(afterFacts, beforeFacts, "Publishing without a game action preserves every board, mana, prompt and card-key fact");

                HeadlessPlatform.query(game.table, () -> null);
                var settled = game.state(0);
                assertEquals(query(game, input, commander), interleavedCatalog);
                assertEquals(query(game, input, commander), interleavedCatalog);
                assertEquals(game.state(0), settled, "After the display queue is drained, repeated source reads preserve the exact snapshot too");
            } finally {
                publish.countDown(); reader.shutdownNow();
            }
        }
    }

    @Test(timeOut = 90000)
    public void aGlobalPublicationDuringStoppedPriorityPublishesEveryAudienceWithoutRevivingItsAuthority() throws Exception {
        try (var game = RulesScenario.automated("source-actions-stopped-priority-publication", Map.of(0, List.of("Forest")),
                "Forest", "Rhys the Redeemed")) {
            var first = main(game);
            var actor = game.table.game.getRegisteredPlayers().get(0);
            var controller = (PlayerControllerHuman)actor.getController();
            var session = game.session(0);
            var awaited = new AtomicInteger();
            var trace = new ArrayList<Object>();
            try {
                HeadlessPlatform.query(game.table, () -> {
                    var before = JSON.toJsonTree(session.state()).getAsJsonObject();
                    var original = new Input(0, before, before.getAsJsonObject("prompt"));
                    assertEquals(text(original.prompt(), "id"), text(first.prompt(), "id"));
                    var priority = (InputPassPriority)controller.getInputQueue().getInput();
                    assertTrue(priority.isPriorityActive());
                    assertSame(controller.getInputProxy().getInput(), priority);
                    var forest = card(before, "Battlefield", "Forest");
                    var catalog = JSON.toJsonTree(session.sourceActions(request(original,
                            Map.of("key", text(forest, "key"))))).getAsJsonObject();
                    var mana = catalog.getAsJsonArray("actions").get(0).getAsJsonObject();
                    long priorBoard = before.get("boardRevision").getAsLong();
                    trace.add(Map.of("label", "before-pass", "state", before));
                    var originalGui = controller.getGui();
                    controller.setGui((IGuiGame)Proxy.newProxyInstance(IGuiGame.class.getClassLoader(), new Class<?>[] { IGuiGame.class },
                            (proxy, method, args) -> {
                                if (method.getName().equals("awaitNextInput")) {
                                    assertEquals(awaited.incrementAndGet(), 1);
                                    assertFalse(priority.isPriorityActive(), "The actual pass finishes priority before its await callback");
                                    assertSame(controller.getInputQueue().getInput(), priority);
                                    assertSame(controller.getInputProxy().getInput(), priority);
                                    // An unrelated seat can publish in precisely this
                                    // handoff. Do not use publishInput, whose own
                                    // display reconciliation would hide the defect.
                                    game.table.publish();
                                    var views = game.table.automatedSeats().stream()
                                            .map(seat -> JSON.toJsonTree(seat.state()).getAsJsonObject()).toList();
                                    var spectator = JSON.toJsonTree(game.table.spectatorState()).getAsJsonObject();
                                    trace.add(Map.of("label", "stopped-owner-global-publication", "views", views, "spectator", spectator));
                                    long board = spectator.get("boardRevision").getAsLong();
                                    assertTrue(board > priorBoard);
                                    assertEquals(views.size(), 4);
                                    for (var view : views) {
                                        assertEquals(view.get("boardRevision").getAsLong(), board,
                                                "Every seat must publish this same new board even while its former priority remains queued");
                                        assertTrue(!view.has("prompt") || view.get("prompt").isJsonNull());
                                        assertEquals(text(view, "status"), "playing");
                                        assertEquals(text(view, "actionClass"), "resolving");
                                        assertTrue(!view.has("decisionOwnerId") || view.get("decisionOwnerId").isJsonNull());
                                        assertTrue(!view.has("priorityPlayerId") || view.get("priorityPlayerId").isJsonNull());
                                        var publicActor = player(view, actor.getId());
                                        assertEquals(count(publicActor, "Battlefield"), 1);
                                        var publicForest = zone(publicActor, "Battlefield").getAsJsonArray("cards").get(0).getAsJsonObject();
                                        assertEquals(text(publicForest, "name"), "Forest"); assertFalse(publicForest.get("tapped").getAsBoolean());
                                    }
                                    assertTrue(!spectator.has("prompt") || spectator.get("prompt").isJsonNull());
                                    assertThrows(IllegalArgumentException.class, () -> session.action(request(original,
                                            Map.of("action", "passPriority", "expectedTask", "priority"))));
                                    assertThrows(IllegalArgumentException.class, () -> session.action(request(original, invoke(catalog, mana))));
                                    assertThrows(IllegalArgumentException.class, () -> session.sourceActions(request(original,
                                            Map.of("key", text(forest, "key")))));
                                    var passive = JSON.toJsonTree(session.automatedObservation()).getAsJsonObject();
                                    assertEquals(text(passive.getAsJsonObject("domain"), "coverage"), "NotApplicable");
                                    assertTrue(passive.getAsJsonObject("domain").getAsJsonArray("sources").isEmpty());
                                    assertEquals(passive.get("state"), JSON.toJsonTree(session.state()));
                                    assertEquals(passive.getAsJsonObject("state").get("boardRevision").getAsLong(), board);
                                    assertEquals(actor.getManaPool().totalMana(), 0);
                                    assertFalse(actor.getCardsIn(forge.game.zone.ZoneType.Battlefield).get(0).isTapped());
                                }
                                try { return method.invoke(originalGui, args); }
                                catch (InvocationTargetException failure) { throw failure.getCause(); }
                            }));
                    try { priority.passPriority(); }
                    finally { controller.setGui(originalGui); }
                    assertEquals(awaited.get(), 1);
                    return null;
                });
                var successor = game.until(next -> priority(next) && next.seat() != 0);
                assertNotEquals(text(successor.prompt(), "id"), text(first.prompt(), "id"));
                assertTrue(game.answer(successor, Map.of("action", "passPriority", "expectedTask", "priority")));
                var continued = game.until(next -> priority(next) && !text(next.prompt(), "id").equals(text(successor.prompt(), "id")));
                assertNull(game.session(continued.seat()).state().get("error"));
                assertEquals(awaited.get(), 1, "The real pass and its resumed rules loop do not replay the old await callback");
                assertEquals(actor.getManaPool().totalMana(), 0);
                trace.add(Map.of("label", "normal-priority-continuation", "seat", continued.seat(), "state", continued.state()));
            } finally {
                Files.writeString(game.evidence.resolve("stopped-priority-publication.json"), JSON.toJson(trace));
            }
        }
    }

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void linkedPreparedSpellsCastFromTheirCreatureAndRevokeConsumedOrReplacedCopies() throws Exception {
        String creature = "Emeritus of Ideation", spell = "Ancestral Recall";
        try (var game = new RulesScenario("source-actions-prepared", Map.of(0,
                List.of(creature, "Island", "Island", "Plains", "Plains", "Plains")), "Cloudshift", "Rhys the Redeemed")) {
            var input = main(game);
            // Registered fixture permanents bypass entering replacements. A real
            // blink exercises the creature's actual enters-prepared rules.
            input = blinkPreparedSource(game, input, creature);
            var source = card(input.state(), "Battlefield", creature);
            assertTrue(prepared(source), "The engine publishes the current non-keyword Prepared designation");
            assertTrue(source.getAsJsonArray("states").asList().stream().map(value -> value.getAsJsonObject())
                    .anyMatch(state -> text(state, "key").equals("prepared") && text(state, "detail").equals(spell + " {U}")));
            assertTrue(source.get("selectable").getAsBoolean(), "A creature whose only action is its linked spell remains discoverable on the battlefield");
            var before = game.state(0);
            var actions = query(game, input, source);
            assertEquals(query(game, input, source), actions, "Repeated reading preserves the exact current action handle");
            assertEquals(game.state(0), before, "Reading a prepared spell does not cast, unprepare, or advance priority");
            var cast = castAction(actions);
            assertEquals(actions.getAsJsonArray("actions").size(), 1);
            assertEquals(text(cast, "zone"), "Exile");
            var preview = cast.getAsJsonObject("card");
            assertEquals(text(preview, "name"), spell);
            assertEquals(text(preview, "manaCost"), "{U}");
            assertEquals(text(preview, "artName"), spell, "The spell preview must not request the creature's full card artwork");
            assertEquals(text(preview, "artSet"), ""); assertEquals(text(preview, "collectorNumber"), "");
            assertEquals(text(preview, "key"), ""); assertFalse(preview.get("selectable").getAsBoolean());
            assertFalse(preview.get("highlighted").getAsBoolean());
            assertEquals(text(actions, "sourceCardId"), text(source, "visualId"));
            assertNotEquals(text(preview, "visualId"), text(source, "visualId"));
            assertTrue(input.state().getAsJsonArray("playAccesses").asList().stream().map(value -> value.getAsJsonObject())
                    .noneMatch(access -> text(access, "cardId").equals(text(preview, "visualId"))),
                    "A Prepared virtual spell remains linked to its creature instead of becoming an independent exile opportunity");
            Files.writeString(game.evidence.resolve("prepared-source-actions.json"), JSON.toJson(actions));

            assertTrue(game.answer(input, invoke(actions, cast)));
            finishTargetedCast(game, spell, null);
            input = game.until(next -> next.seat() == 0 && priority(next) && next.state().getAsJsonArray("stack").isEmpty()
                    && !prepared(card(next.state(), "Battlefield", creature)));
            assertEquals(count(viewer(input.state()), "Exile"), 0, "Casting consumes the prepared copy instead of leaving a stale exile entry");
            assertEquals(count(viewer(input.state()), "Hand"), count(viewer(before), "Hand") + 3, "The actual prepared Ancestral Recall resolves its draw-three effect");
            assertTrue(query(game, input, card(input.state(), "Battlefield", creature)).getAsJsonArray("actions").isEmpty());

            input = blinkPreparedSource(game, input, creature);
            var renewed = query(game, input, card(input.state(), "Battlefield", creature));
            var renewedCast = castAction(renewed);
            assertNotEquals(text(renewedCast.getAsJsonObject("card"), "visualId"), text(preview, "visualId"));
            assertThrows(IllegalArgumentException.class, () -> game.table.seat(0).action(request(
                    new Input(0, game.state(0), game.state(0).getAsJsonObject("prompt")), invoke(renewed, cast))));

            input = blinkPreparedSource(game, input, creature);
            var replaced = query(game, input, card(input.state(), "Battlefield", creature));
            assertEquals(count(viewer(input.state()), "Exile"), 1, "Leaving play removes the old prepared copy before re-entering creates its replacement");
            assertNotEquals(text(castAction(replaced).getAsJsonObject("card"), "visualId"),
                    text(renewedCast.getAsJsonObject("card"), "visualId"));
            var current = input;
            assertThrows(IllegalArgumentException.class, () -> game.table.seat(0).action(request(current, invoke(replaced, renewedCast))));
            assertTrue(prepared(card(input.state(), "Battlefield", creature)));
            for (int seat = 1; seat < 4; seat++) {
                var other = game.state(seat);
                assertTrue(zone(player(other, input.state().get("viewerId").getAsInt()), "Hand").getAsJsonArray("cards").isEmpty(),
                        "A linked public spell never broadens another seat's hand visibility");
            }
        }
    }

    private static boolean prepared(JsonObject card) {
        return card != null && card.getAsJsonArray("states").asList().stream().map(value -> value.getAsJsonObject())
                .anyMatch(state -> text(state, "key").equals("prepared") && text(state, "kind").equals("status"));
    }

    private static Input blinkPreparedSource(RulesScenario game, Input input, String creature) throws Exception {
        var blink = query(game, input, card(input.state(), "Hand", "Cloudshift"));
        assertTrue(game.answer(input, invoke(blink, castAction(blink))));
        finishTargetedCast(game, "Cloudshift", creature);
        return game.until(next -> next.seat() == 0 && priority(next) && next.state().getAsJsonArray("stack").isEmpty()
                && prepared(card(next.state(), "Battlefield", creature)));
    }

    private static void finishTargetedCast(RulesScenario game, String spell, String targetCard) throws Exception {
        finishTargetedCast(game, spell, targetCard, null);
    }

    private static void finishTargetedCast(RulesScenario game, String spell, String targetCard, String poolColor) throws Exception {
        // Targeting/payment may publish intermediate prompts (including another
        // payment prompt). Their count is not the spell's completion condition.
        long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(45);
        while (System.nanoTime() < deadline) {
            var input = game.until(next -> next.seat() == 0 && (text(next.prompt(), "inputType").equals("InputSelectTargets")
                    || text(next.prompt(), "inputType").startsWith("InputPayMana")
                    || priority(next) && next.state().getAsJsonArray("stack").asList().stream().anyMatch(item -> text(item.getAsJsonObject(), "name").equals(spell))));
            if (priority(input)) return;
            if (text(input.prompt(), "inputType").equals("InputSelectTargets")) {
                assertTrue(game.answer(input, targetCard == null
                        ? Map.of("action", "player", "playerId", input.state().get("viewerId").getAsInt())
                        : Map.of("action", "card", "key", text(card(input.state(), "Battlefield", targetCard), "key"))));
            } else if (poolColor != null) {
                assertFalse(input.prompt().get("okEnabled").getAsBoolean(), "The external payer cannot use Forge Auto");
                assertTrue(input.prompt().getAsJsonArray("usablePoolManaColors").asList().stream()
                        .anyMatch(value -> value.getAsString().equals(poolColor)));
                assertTrue(viewer(input.state()).getAsJsonObject("mana").get(poolColor).getAsInt() > 0);
                assertFalse(input.prompt().getAsJsonArray("unpaidManaParts").isEmpty());
                if (!game.answer(input, Map.of("action", "spendPoolMana", "manaColor", poolColor))) continue;
            } else {
                assertTrue(input.prompt().get("okEnabled").getAsBoolean(), "The fixture supplies the real spell's mana cost");
                // Auto can complete while a republished payment is being read.
                // answer returns false only for exact stale-prompt replacement;
                // reobserve it rather than replaying OK against a new input.
                if (!game.answer(input, Map.of("action", "ok"))) continue;
            }
        }
        fail("The actual targeted spell did not reach the stack: " + spell);
    }

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void readingRealManaActionsDoesNotAnswerPriorityAndAnExplicitActionUsesItsExactSource() throws Exception {
        try (var game = new RulesScenario("source-actions-mana", Map.of(0, List.of("Concordant Crossroads", "Llanowar Elves", "Llanowar Elves")))) {
            var input = main(game);
            HeadlessPlatform.query(() -> null);
            var before = game.state(0);
            assertThrows(IllegalArgumentException.class, () -> game.table.seat(0).action(request(input,
                    Map.of("action", "ok", "expectedTask", "openingHand"))));
            assertEquals(game.state(0), before, "A typed opening-hand answer cannot become a priority pass");
            assertEquals(text(input.prompt(), "priorityAlternative"), "passTurn");
            game.rejected(input, Map.of("action", "priorityUndo"),
                    "In-game action Undo is not supported. Use the current priority controls.");
            assertEquals(game.state(0), before, "An unavailable undo must not fall through to passing the turn");
            var elves = zone(viewer(before), "Battlefield").getAsJsonArray("cards").asList().stream()
                    .map(value -> value.getAsJsonObject()).filter(card -> text(card, "name").equals("Llanowar Elves")).toList();
            var first = query(game, input, elves.get(0));
            assertEquals(query(game, input, elves.get(0)), first, "Repeated reads retain the same current opaque action");
            assertEquals(game.state(0), before, "Reading actions changes neither snapshot nor prompt nor mana nor source");
            assertEquals(text(first, "sourceCardId"), text(elves.get(0), "visualId"));
            var action = first.getAsJsonArray("actions").get(0).getAsJsonObject();
            assertEquals(first.getAsJsonArray("actions").size(), 1);
            assertEquals(text(action, "kind"), "activate");
            assertTrue(text(action, "text").contains("Add {G}"), "The engine's full ability description supplies its effect");
            assertTrue(text(action, "text").contains("{T}"), "The engine supplies the actual tap cost");
            var second = query(game, input, elves.get(1));
            assertNotEquals(text(second.getAsJsonArray("actions").get(0).getAsJsonObject(), "id"), text(action, "id"));
            assertThrows(IllegalArgumentException.class, () -> game.table.seat(0).action(request(input,
                    Map.of("action", "useSourceAction", "key", text(elves.get(1), "key"), "actionId", text(action, "id")))));
            assertEquals(game.state(0), before);
            assertTrue(game.answer(input, invoke(first, action)));
            var after = game.until(next -> next.seat() == 0 && priority(next)
                    && viewer(next.state()).getAsJsonObject("mana").get("G").getAsInt() == 1);
            var current = zone(viewer(after.state()), "Battlefield").getAsJsonArray("cards").asList().stream()
                    .map(value -> value.getAsJsonObject()).toList();
            assertTrue(current.stream().filter(card -> text(card, "visualId").equals(text(first, "sourceCardId")))
                    .findFirst().orElseThrow().get("tapped").getAsBoolean());
            assertFalse(current.stream().filter(card -> text(card, "visualId").equals(text(second, "sourceCardId")))
                    .findFirst().orElseThrow().get("tapped").getAsBoolean());
            assertTrue(after.state().getAsJsonArray("stack").isEmpty(), "A real mana ability does not create a fabricated ability chooser or stack item");
            assertThrows(IllegalArgumentException.class, () -> game.table.seat(0).action(request(input, invoke(first, action))));
            assertEquals(text(after.prompt(), "priorityAlternative"), "none");
            assertFalse(after.prompt().get("cancelEnabled").getAsBoolean(), "A paid mana action cannot offer in-game Undo");
            assertEquals(text(after.prompt(), "cancel"), "", "The priority prompt does not retain an Undo label");
            assertThrows(IllegalArgumentException.class, () -> game.table.seat(0).action(request(after,
                    Map.of("action", "priorityPassTurn"))));
            HeadlessPlatform.query(() -> null);
            var unchanged = game.state(0);
            for (String alias : List.of("priorityUndo", "cancel")) {
                game.rejected(after, Map.of("action", alias),
                        "In-game action Undo is not supported. Use the current priority controls.");
                assertEquals(game.state(0), unchanged, "Rejected " + alias + " preserves the exact answerable input and public state");
            }
            assertTrue(zone(viewer(unchanged), "Battlefield").getAsJsonArray("cards").asList().stream()
                    .map(value -> value.getAsJsonObject()).filter(source -> text(source, "visualId").equals(text(first, "sourceCardId")))
                    .findFirst().orElseThrow().get("tapped").getAsBoolean(), "Rejected Undo cannot untap the actual mana source");
            assertEquals(viewer(unchanged).getAsJsonObject("mana").get("G").getAsInt(), 1,
                    "Rejected Undo cannot remove or duplicate the produced mana");
            assertTrue(game.answer(after, Map.of("action", "passPriority", "expectedTask", "priority")),
                    "The same priority input remains usable through its explicit pass operation");
        }
    }

    @Test(timeOut = 90000)
    public void rejectedUndoCannotUntapAnArtifactOrLeaveItsProducedManaBehind() throws Exception {
        try (var game = new RulesScenario("source-actions-no-artifact-undo", Map.of(0, List.of("Sol Ring")))) {
            var input = main(game);
            var source = card(input.state(), "Battlefield", "Sol Ring");
            var offers = query(game, input, source);
            var action = offers.getAsJsonArray("actions").get(0).getAsJsonObject();
            var command = new java.util.LinkedHashMap<String, Object>(invoke(offers, action));
            command.put("manaOutput", Map.of("colors", Map.of("C", 2)));
            assertTrue(game.answer(input, command));
            var paid = game.until(next -> next.seat() == 0 && priority(next)
                    && viewer(next.state()).getAsJsonObject("mana").get("C").getAsInt() == 2);
            HeadlessPlatform.query(() -> null);
            var unchanged = game.state(0);
            assertEquals(text(paid.prompt(), "priorityAlternative"), "none");
            assertFalse(paid.prompt().get("cancelEnabled").getAsBoolean());
            for (String alias : List.of("priorityUndo", "cancel")) {
                game.rejected(paid, Map.of("action", alias),
                        "In-game action Undo is not supported. Use the current priority controls.");
                assertEquals(game.state(0), unchanged, "A rejected legacy priority alias cannot mutate the artifact, pool or input");
            }
            assertTrue(card(unchanged, "Battlefield", "Sol Ring").get("tapped").getAsBoolean());
            assertEquals(viewer(unchanged).getAsJsonObject("mana").get("C").getAsInt(), 2);
            assertTrue(query(game, paid, card(unchanged, "Battlefield", "Sol Ring")).getAsJsonArray("actions").isEmpty(),
                    "The artifact cannot be tapped again while its original mana remains in the pool");
        }
    }

    @Test(timeOut = 90000)
    public void anExactNonManaActionKeepsItsLaterRequiredChoiceInsteadOfChoosingTheOtherAbility() throws Exception {
        try (var game = new RulesScenario("source-actions-placement", Map.of(0, List.of("Gaea's Touch")))) {
            var input = main(game);
            var source = card(input.state(), "Battlefield", "Gaea's Touch");
            var actions = query(game, input, source);
            assertEquals(actions.getAsJsonArray("actions").size(), 2, "Both real abilities are available before selecting the source");
            var placement = actions.getAsJsonArray("actions").asList().stream().map(value -> value.getAsJsonObject())
                    .filter(action -> text(action, "text").contains("Forest")).findFirst().orElseThrow();
            assertTrue(game.answer(input, invoke(actions, placement)));
            var search = game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").equals("InputSelectEntitiesFromList"));
            assertNotNull(card(search.state(), "Battlefield", "Gaea's Touch"), "The exact action never substitutes the sacrifice ability");
            assertEquals(viewer(search.state()).getAsJsonObject("mana").get("G").getAsInt(), 0);
            assertEquals(text(search.prompt().getAsJsonObject("sourceCard"), "name"), "Gaea's Touch");
            assertThrows(IllegalArgumentException.class, () -> game.table.seat(0).sourceActions(request(search,
                    Map.of("key", text(card(search.state(), "Battlefield", "Gaea's Touch"), "key")))));
            assertEquals(text(game.state(0).getAsJsonObject("prompt"), "id"), text(search.prompt(), "id"));
        }
    }

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void humanManaPreviewsAreReadOnlyAndEachCurrentPaymentPublishesItsRemainingBill() throws Exception {
        try (var game = new RulesScenario("source-actions-human-mana-facts", Map.of(0, List.of("Forest", "Forest", "Forest")),
                "Manalith", "Rhys the Redeemed")) {
            var input = main(game);
            HeadlessPlatform.query(game.table, () -> null);
            var spell = card(input.state(), "Hand", "Manalith");
            var before = game.state(0);
            var catalog = query(game, input, spell);
            var cast = castAction(catalog);
            var preview = cast.getAsJsonObject("costPreview");
            assertNotNull(preview);
            assertEquals(preview.keySet(), java.util.Set.of("costs", "manaCostKnowledge", "funding"),
                    "A human cost preview exposes no operation graph, copied action handles or target domain");
            assertEquals(text(preview, "manaCostKnowledge"), "Unknown", "The declared cost never certifies final adjustments");
            assertEquals(preview.getAsJsonArray("costs").get(0).getAsJsonObject().get("amount").getAsInt(), 3);
            assertEquals(preview.getAsJsonObject("funding").getAsJsonArray("sources").size(), 3);
            assertEquals(query(game, input, spell), catalog);
            assertEquals(game.state(0), before, "Cost inspection cannot tap, spend, cast or replace the current source handles");
            var other = game.state(1);
            var stolen = request(input, Map.of("key", text(spell, "key")));
            stolen.addProperty("sessionId", text(other, "id"));
            assertThrows(IllegalArgumentException.class, () -> game.table.seat(1).sourceActions(stolen));
            assertTrue(zone(player(game.state(1), input.state().get("viewerId").getAsInt()), "Hand").getAsJsonArray("cards").isEmpty());

            assertTrue(game.answer(input, invoke(catalog, cast)));
            var payment = game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").startsWith("InputPayMana"));
            var initialPrompt = payment.prompt().deepCopy();
            assertEquals(initialPrompt.get("payerId"), payment.state().get("viewerId"));
            assertGenericUnpaid(initialPrompt, 3);
            assertEquals(initialPrompt.getAsJsonObject("fundingContext").getAsJsonArray("sources").size(), 3);
            assertTrue(initialPrompt.getAsJsonArray("usablePoolManaColors").isEmpty());
            for (int remaining = 3; remaining > 0; remaining--) {
                assertGenericUnpaid(payment.prompt(), remaining);
                assertEquals(payment.prompt().getAsJsonObject("fundingContext").getAsJsonArray("sources").size(), remaining,
                        "The current payment does not retain tapped resources from its initial publication");
                var forest = zone(viewer(payment.state()), "Battlefield").getAsJsonArray("cards").asList().stream()
                        .map(value -> value.getAsJsonObject()).filter(value -> text(value, "name").equals("Forest")
                                && !value.get("tapped").getAsBoolean()).findFirst().orElseThrow();
                var resources = query(game, payment, forest);
                var action = resources.getAsJsonArray("actions").get(0).getAsJsonObject();
                var previous = payment;
                assertTrue(game.answer(payment, invoke(resources, action)));
                if (remaining > 1) {
                    payment = game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").startsWith("InputPayMana"));
                    assertThrows(IllegalArgumentException.class, () -> query(game, previous, forest));
                    HeadlessPlatform.query(game.table, () -> { game.table.publish(); return null; });
                    var refreshed = game.state(0).getAsJsonObject("prompt");
                    assertEquals(refreshed.getAsJsonArray("unpaidManaParts"), payment.prompt().getAsJsonArray("unpaidManaParts"));
                    assertEquals(refreshed.getAsJsonObject("fundingContext"), payment.prompt().getAsJsonObject("fundingContext"));
                }
            }
            var committed = game.until(next -> next.seat() == 0 && priority(next) && !next.state().getAsJsonArray("stack").isEmpty());
            assertFalse(committed.prompt().has("unpaidMana"));
            assertFalse(committed.prompt().has("unpaidManaParts"));
            assertFalse(committed.prompt().has("payerId"));
            assertFalse(committed.prompt().has("fundingContext"));
            assertFalse(committed.prompt().has("usablePoolManaColors"));
            assertGenericUnpaid(initialPrompt, 3); // Historical copies cannot shrink with a later input.
        }
    }

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void aHumanCanSpendOnlyPublishedUsablePoolColorsAndTheCurrentBillShrinks() throws Exception {
        try (var game = new RulesScenario("source-actions-human-pool-facts", Map.of(0, List.of("Sol Ring", "Forest")),
                "Manalith", "Rhys the Redeemed")) {
            var input = main(game);
            var ring = query(game, input, card(input.state(), "Battlefield", "Sol Ring"));
            assertTrue(game.answer(input, invoke(ring, ring.getAsJsonArray("actions").get(0).getAsJsonObject())));
            input = game.until(next -> next.seat() == 0 && priority(next) && viewer(next.state()).getAsJsonObject("mana").get("C").getAsInt() == 2);
            var spells = query(game, input, card(input.state(), "Hand", "Manalith"));
            assertEquals(castAction(spells).getAsJsonObject("costPreview").getAsJsonObject("funding").getAsJsonObject("pool").get("C").getAsInt(), 2);
            assertTrue(game.answer(input, invoke(spells, castAction(spells))));
            var payment = game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").startsWith("InputPayMana"));
            for (int pool = 2; pool > 0; pool--) {
                assertEquals(payment.prompt().get("payerId"), payment.state().get("viewerId"));
                assertEquals(payment.prompt().getAsJsonArray("usablePoolManaColors").toString(), "[\"C\"]");
                assertGenericUnpaid(payment.prompt(), pool + 1);
                var current = payment;
                game.table.game.getAction().withState(() -> {
                    // An unrelated queued display publication is not a side
                    // effect of this rejected command. Keep this assertion at
                    // one state boundary while retaining full frame equality.
                    var before = game.state(0);
                    assertThrows(IllegalArgumentException.class, () -> game.table.seat(0).action(request(current,
                            Map.of("action", "spendPoolMana", "manaColor", "G"))));
                    assertEquals(game.state(0), before);
                });
                assertTrue(game.answer(payment, Map.of("action", "spendPoolMana", "manaColor", "C")));
                payment = game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").startsWith("InputPayMana"));
            }
            assertGenericUnpaid(payment.prompt(), 1);
            assertTrue(payment.prompt().getAsJsonArray("usablePoolManaColors").isEmpty());
            assertEquals(viewer(payment.state()).getAsJsonObject("mana").get("C").getAsInt(), 0);
            var forest = query(game, payment, card(payment.state(), "Battlefield", "Forest"));
            assertTrue(game.answer(payment, invoke(forest, forest.getAsJsonArray("actions").get(0).getAsJsonObject())));
            game.until(next -> next.seat() == 0 && priority(next) && !next.state().getAsJsonArray("stack").isEmpty());
        }
    }

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void aRealLargeXAnnouncementPublishesOneExactCountedGenericBillAndCanStillBeCancelled() throws Exception {
        try (var game = new RulesScenario("source-actions-human-large-x-bill", Map.of(), "Stonecoil Serpent", "Rhys the Redeemed")) {
            var input = main(game);
            var source = card(input.state(), "Hand", "Stonecoil Serpent");
            var actions = query(game, input, source);
            assertTrue(game.answer(input, invoke(actions, castAction(actions))));
            var announcement = game.until(next -> next.seat() == 0 && text(next.prompt(), "kind").equals("number"));
            assertTrue(game.answer(announcement, Map.of("action", "number", "value", 129)));
            var payment = game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").startsWith("InputPayMana"));
            assertGenericUnpaid(payment.prompt(), 129);
            assertTrue(payment.prompt().getAsJsonObject("fundingContext").getAsJsonArray("sources").isEmpty());
            assertTrue(payment.prompt().get("cancelEnabled").getAsBoolean());
            HeadlessPlatform.query(game.table, () -> {
                var player = game.table.game.getRegisteredPlayers().get(0);
                var controller = (forge.player.PlayerControllerHuman)player.getController();
                var actual = (forge.gamemodes.match.input.InputPayMana)controller.getInputQueue().getInput();
                assertEquals(actual.getPaidForAbility().getXManaCostPaid(), Integer.valueOf(129));
                assertEquals(ManaPaymentFacts.unpaid(actual), List.of("{129}"), "The private compatibility bill also avoids generic unit expansion");
                return null;
            });
            Files.writeString(game.evidence.resolve("large-x-payment.json"), JSON.toJson(payment.state()));
            assertTrue(game.answer(payment, Map.of("action", "cancel")));
            var resumed = game.until(next -> next.seat() == 0 && priority(next)
                    && card(next.state(), "Hand", "Stonecoil Serpent") != null);
            assertFalse(resumed.prompt().has("unpaidManaParts"));
            assertThrows(IllegalArgumentException.class, () -> game.table.seat(0).action(request(payment, Map.of("action", "cancel"))));
        }
    }

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void paymentUsesThePaymentInputsActualManaActionsWithoutASecondAbilityChooser() throws Exception {
        try (var game = new RulesScenario("source-actions-payment", Map.of(0, List.of("Forest")))) {
            var input = main(game);
            var commander = zone(viewer(input.state()), "Command").getAsJsonArray("cards").get(0).getAsJsonObject();
            var casts = query(game, input, commander);
            var cast = casts.getAsJsonArray("actions").get(0).getAsJsonObject();
            assertEquals(text(cast, "kind"), "cast");
            assertTrue(game.answer(input, invoke(casts, cast)));
            var payment = game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").startsWith("InputPayMana"));
            var forest = card(payment.state(), "Battlefield", "Forest");
            HeadlessPlatform.query(game.table, () -> null);
            var before = game.state(0);
            var mana = query(game, payment, forest);
            assertEquals(game.state(0), before);
            var action = mana.getAsJsonArray("actions").get(0).getAsJsonObject();
            assertEquals(text(action, "kind"), "activate");
            assertTrue(text(action, "text").contains("Add {G}"));
            assertTrue(game.answer(payment, invoke(mana, action)));
            long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(5);
            while (game.state(0).getAsJsonArray("stack").isEmpty() && System.nanoTime() < deadline) Thread.sleep(5);
            var paid = game.until(next -> next.seat() == 0 && priority(next) && !next.state().getAsJsonArray("stack").isEmpty());
            assertTrue(card(paid.state(), "Battlefield", "Forest").get("tapped").getAsBoolean());
            assertEquals(text(paid.state().getAsJsonArray("stack").get(0).getAsJsonObject(), "name"), "Rhys the Redeemed");
        }
    }

    @Test(timeOut = 90000)
    public void bothRealPartnerCommandersExposeIndependentCastsAndToothyCanRecastWithItsOwnTax() throws Exception {
        String toothy = "Toothy, Imaginary Friend", pir = "Pir, Imaginative Rascal";
        try (var game = new RulesScenario("source-actions-toothy-partners",
                Map.of(0, List.of("Island", "Island", "Island", "Island", "Forest", "Forest", "Forest", "Forest", "Seal of Removal")),
                "Island", List.of(toothy, pir))) {
            var input = main(game);
            assertEquals(zone(viewer(input.state()), "Command").getAsJsonArray("cards").size(), 2);
            assertEquals(zone(viewer(input.state()), "Battlefield").getAsJsonArray("cards").asList().stream()
                    .map(c -> c.getAsJsonObject()).filter(c -> text(c, "name").equals("Island") && !c.get("tapped").getAsBoolean()).count(), 4L);
            var toothyActions = query(game, input, card(input.state(), "Command", toothy));
            var pirActions = query(game, input, card(input.state(), "Command", pir));
            Files.writeString(game.evidence.resolve("before-first-cast.json"), JSON.toJson(input.state()));
            Files.writeString(game.evidence.resolve("partner-source-actions.json"), JSON.toJson(Map.of("toothy", toothyActions, "pir", pirActions)));
            var toothyCast = castAction(toothyActions);
            var pirCast = castAction(pirActions);
            assertNotEquals(text(toothyActions, "sourceCardKey"), text(pirActions, "sourceCardKey"));
            assertNotEquals(text(toothyCast, "id"), text(pirCast, "id"));
            var wrongPartner = invoke(pirActions, toothyCast);
            var unchanged = game.state(0);
            var initialInput = input;
            assertThrows(IllegalArgumentException.class, () -> game.table.seat(0).action(request(initialInput, wrongPartner)));
            assertEquals(game.state(0), unchanged, "One partner's opaque action cannot cast the other commander");

            castAndPay(game, input, toothyActions, toothy, "first-toothy");
            input = finishPartnerSearch(game);
            assertEquals(card(input.state(), "Battlefield", toothy).get("commanderTax").getAsInt(), 2);
            assertEquals(card(input.state(), "Command", pir).get("commanderTax").getAsInt(), 0);
            pirActions = query(game, input, card(input.state(), "Command", pir));
            castAndPay(game, input, pirActions, pir, "first-pir");
            input = finishPartnerSearch(game);
            assertEquals(card(input.state(), "Battlefield", pir).get("commanderTax").getAsInt(), 2);
            assertEquals(card(input.state(), "Battlefield", toothy).get("commanderTax").getAsInt(), 2);

            var seal = card(input.state(), "Battlefield", "Seal of Removal");
            var removal = query(game, input, seal);
            assertTrue(game.answer(input, invoke(removal, removal.getAsJsonArray("actions").get(0).getAsJsonObject())));
            var target = game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").equals("InputSelectTargets"));
            assertTrue(game.answer(target, Map.of("action", "card", "key", text(card(target.state(), "Battlefield", toothy), "key"))));
            var replacement = game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").equals("InputConfirm")
                    && text(next.prompt(), "message").toLowerCase(java.util.Locale.ROOT).contains("command"));
            assertTrue(game.answer(replacement, Map.of("action", "ok")));
            input = game.until(next -> next.seat() == 0 && priority(next) && text(next.state(), "phaseKey").equals("MAIN1")
                    && next.state().getAsJsonArray("stack").isEmpty() && card(next.state(), "Command", toothy) != null);
            assertEquals(card(input.state(), "Command", toothy).get("commanderTax").getAsInt(), 2);
            var land = query(game, input, card(input.state(), "Hand", "Island"));
            assertTrue(game.answer(input, invoke(land, land.getAsJsonArray("actions").get(0).getAsJsonObject())));
            input = game.until(next -> next.seat() == 0 && priority(next) && text(next.state(), "phaseKey").equals("MAIN1")
                    && next.state().getAsJsonArray("stack").isEmpty());
            int previousTurn = input.state().get("turn").getAsInt();
            assertTrue(game.answer(input, Map.of("action", "ok")));
            boolean nextTurn = false;
            for (int step = 0; step < 8; step++) {
                input = game.until(next -> next.seat() == 0 && priority(next) && text(next.state(), "phaseKey").equals("MAIN1")
                        && next.state().get("activePlayerId").getAsInt() == next.state().get("viewerId").getAsInt()
                        && next.state().get("turn").getAsInt() > previousTurn && next.state().getAsJsonArray("stack").isEmpty()
                        || text(next.state(), "phaseKey").equals("CLEANUP") && text(next.prompt(), "inputType").equals("InputSelectCardsFromList"));
                if (priority(input)) { nextTurn = true; break; }
                var discard = zone(viewer(input.state()), "Hand").getAsJsonArray("cards").asList().stream()
                        .map(value -> value.getAsJsonObject()).filter(value -> value.get("selectable").getAsBoolean()).findFirst().orElseThrow();
                assertTrue(game.answer(input, Map.of("action", "card", "key", text(discard, "key"))));
            }
            assertTrue(nextTurn, "The four-seat fixture reaches the caster's next main phase after explicit cleanup discards");
            toothyActions = query(game, input, card(input.state(), "Command", toothy));
            Files.writeString(game.evidence.resolve("before-taxed-recast.json"), JSON.toJson(input.state()));
            Files.writeString(game.evidence.resolve("taxed-toothy-source-actions.json"), JSON.toJson(toothyActions));
            castAndPay(game, input, toothyActions, toothy, "taxed-toothy");
            input = finishPartnerSearch(game);
            assertEquals(card(input.state(), "Battlefield", toothy).get("commanderTax").getAsInt(), 4);
            assertEquals(card(input.state(), "Battlefield", pir).get("commanderTax").getAsInt(), 2,
                    "Toothy's recast increments only Toothy's own cast count, including with its partner on the battlefield");
            Files.writeString(game.evidence.resolve("both-partners-after-taxed-cast.json"), JSON.toJson(input.state()));
        }
    }

    private static JsonObject castAction(JsonObject actions) {
        return actions.getAsJsonArray("actions").asList().stream().map(value -> value.getAsJsonObject())
                .filter(action -> text(action, "kind").equals("cast")).findFirst()
                .orElseThrow(() -> new AssertionError("A real current cast action is required: " + actions));
    }

    private static void castAndPay(RulesScenario game, Input input, JsonObject actions, String name, String evidence) throws Exception {
        assertTrue(game.answer(input, invoke(actions, castAction(actions))));
        var payment = game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").startsWith("InputPayMana"));
        Files.writeString(game.evidence.resolve(evidence + "-payment.json"), JSON.toJson(payment.state()));
        assertTrue(payment.prompt().get("okEnabled").getAsBoolean(), "The supplied untapped mana sources make Auto payment available");
        assertTrue(game.answer(payment, Map.of("action", "ok")));
        long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(5);
        while (game.state(0).getAsJsonArray("stack").isEmpty() && System.nanoTime() < deadline) Thread.sleep(5);
        var pending = game.until(next -> next.seat() == 0 && priority(next) && next.state().getAsJsonArray("stack").asList().stream()
                .map(item -> item.getAsJsonObject()).anyMatch(item -> text(item, "name").equals(name)));
        Files.writeString(game.evidence.resolve(evidence + "-on-stack.json"), JSON.toJson(pending.state()));
        assertTrue(viewer(pending.state()).getAsJsonObject("mana").entrySet().stream().allMatch(entry -> entry.getValue().getAsInt() == 0),
                "The real payment spends all mana produced for this commander");
    }

    private static Input finishPartnerSearch(RulesScenario game) throws Exception {
        var target = game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").equals("InputSelectTargets"));
        assertTrue(game.answer(target, Map.of("action", "player", "playerId", target.state().get("viewerId").getAsInt())));
        var search = game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").equals("InputConfirm"));
        assertTrue(text(search.prompt(), "message").toLowerCase(java.util.Locale.ROOT).contains("library"));
        assertTrue(game.answer(search, Map.of("action", "cancel")));
        return game.until(next -> next.seat() == 0 && priority(next) && text(next.state(), "phaseKey").equals("MAIN1")
                && next.state().getAsJsonArray("stack").isEmpty());
    }

    @Test(timeOut = 90000)
    public void aReadOnlySeatConnectionCanDescribeAndPlayALandButCannotQueryAnotherSeatsPrivateHandles() throws Exception {
        try (var game = new RulesScenario("source-actions-seat-routing", Map.of())) {
            var input = main(game);
            var land = zone(viewer(input.state()), "Hand").getAsJsonArray("cards").get(0).getAsJsonObject();
            try (var connection = SeatConnection.local(game.table.seat(0))) {
                var result = JSON.toJsonTree(connection.sourceActions(request(input, Map.of("key", text(land, "key"))))).getAsJsonObject();
                var action = result.getAsJsonArray("actions").get(0).getAsJsonObject();
                assertEquals(text(action, "kind"), "play");
                assertEquals(text(result, "sourceCardId"), text(land, "visualId"));
                assertThrows(IllegalArgumentException.class, () -> game.table.seat(1).sourceActions(request(input, Map.of("key", text(land, "key")))));
                assertTrue(zone(player(game.state(1), input.state().get("viewerId").getAsInt()), "Hand").getAsJsonArray("cards").isEmpty());
                connection.action(request(input, invoke(result, action)));
                var played = game.until(next -> next.seat() == 0 && priority(next) && count(viewer(next.state()), "Battlefield") == 1);
                assertEquals(viewer(played.state()).get("landPlaysUsed").getAsInt(), 1);
            }
        }
    }

    @Test(timeOut = 90000)
    public void aLateRulesMutationRevokesAnAcceptedQueuedActionAndRestoresTheUnansweredInput() throws Exception {
        try (var game = new RulesScenario("source-actions-late-revocation", Map.of(0, List.of("Sol Ring"), 1, List.of("Spellbook")))) {
            var input = main(game);
            var source = card(input.state(), "Battlefield", "Sol Ring");
            var actions = query(game, input, source);
            var action = actions.getAsJsonArray("actions").get(0).getAsJsonObject();
            var entered = new CountDownLatch(1);
            var release = new CountDownLatch(1);
            try {
                HeadlessPlatform.later(() -> { entered.countDown(); await(release); });
                assertTrue(entered.await(5, TimeUnit.SECONDS));
                var invocation = new java.util.LinkedHashMap<String, Object>(invoke(actions, action));
                invocation.put("manaOutput", Map.of("colors", Map.of("C", 2)));
                assertTrue(game.answer(input, invocation));
                var concede = new JsonObject(); concede.addProperty("sessionId", text(input.state(), "id"));
                game.table.seat(1).concede(concede);
                awaitConcession(game.table, game.table.seat(1));
                release.countDown();
                HeadlessPlatform.query(() -> null);
                var restored = game.state(0);
                assertEquals(text(restored.getAsJsonObject("prompt"), "id"), text(input.prompt(), "id"));
                assertFalse(card(restored, "Battlefield", "Sol Ring").get("tapped").getAsBoolean());
                assertEquals(viewer(restored).getAsJsonObject("mana").get("C").getAsInt(), 0);
                assertThrows(IllegalArgumentException.class, () -> game.table.seat(0).action(request(input, invoke(actions, action))));
                assertTrue(query(game, new Input(0, restored, restored.getAsJsonObject("prompt")), source).getAsJsonArray("actions").size() > 0);
            } finally { release.countDown(); }
        }
    }

    private static Input main(RulesScenario game) throws Exception {
        return game.until(input -> input.seat() == 0 && priority(input) && text(input.state(), "phaseKey").equals("MAIN1"));
    }
    private static void assertGenericUnpaid(JsonObject prompt, int amount) {
        assertFalse(prompt.has("unpaidMana"), "Current public bills use counted parts rather than unit expansion");
        var parts = prompt.getAsJsonArray("unpaidManaParts");
        assertEquals(parts.size(), 1);
        assertEquals(text(parts.get(0).getAsJsonObject(), "symbol"), "{" + amount + "}");
        assertEquals(parts.get(0).getAsJsonObject().get("count").getAsInt(), 1);
    }
    private static boolean priority(Input input) { return text(input.prompt(), "inputType").equals("InputPassPriority"); }
    private static JsonObject card(JsonObject state, String zone, String name) {
        return zone(viewer(state), zone).getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                .filter(card -> text(card, "name").equals(name)).findFirst().orElse(null);
    }
    private static JsonObject query(RulesScenario game, Input input, JsonObject source) {
        return JSON.toJsonTree(game.session(input.seat()).sourceActions(request(input, Map.of("key", text(source, "key"))))).getAsJsonObject();
    }
    private static Map<String, ?> invoke(JsonObject actions, JsonObject action) {
        return Map.of("action", "useSourceAction", "key", text(actions, "sourceCardKey"), "actionId", text(action, "id"));
    }
    private static JsonObject request(Input input, Map<String, ?> body) {
        var request = JSON.toJsonTree(body).getAsJsonObject();
        request.addProperty("sessionId", text(input.state(), "id")); request.addProperty("promptId", text(input.prompt(), "id"));
        return request;
    }
    private static void await(CountDownLatch latch) {
        var game = forge.game.GameAction.currentGame();
        try (var suspended = game == null ? null : game.getAction().suspendState()) {
            if (!latch.await(10, TimeUnit.SECONDS)) throw new AssertionError("Input queue was not released");
        }
        catch (InterruptedException failure) { Thread.currentThread().interrupt(); throw new AssertionError(failure); }
    }
}
