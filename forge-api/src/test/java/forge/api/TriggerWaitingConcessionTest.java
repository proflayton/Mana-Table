package forge.api;

import com.google.common.eventbus.Subscribe;
import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import forge.game.ability.AbilityKey;
import forge.game.event.GameEventGameFinished;
import forge.game.trigger.TriggerHandler;
import forge.game.trigger.TriggerType;
import forge.game.trigger.TriggerWaiting;
import org.testng.annotations.Test;

import java.lang.management.ManagementFactory;
import java.lang.reflect.Field;
import java.nio.file.Files;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.EnumMap;
import java.util.IdentityHashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicReference;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** The queue observer preserves real ArrayList contents and copying semantics.
 * It pauses after a genuine toArray snapshot, before the caller clears the queue.
 * It never supplies null, substitutes a trigger effect, or changes production code. */
public class TriggerWaitingConcessionTest {
    @Test(timeOut = 90000)
    public void aConcessionCannotDrainTheSameLandfallBatchTwice() throws Exception {
        try (var scenario = new RulesScenario("waiting-trigger-concession", Map.of(
                0, List.of("Jaddi Offshoot"), 2, List.of("Grizzly Bears")))) {
            var main = main(scenario);
            var ids = scenario.table.game.getPlayers().stream().map(player -> player.getId()).toList();
            int initialLife = viewer(main.state()).get("life").getAsInt();
            String session = text(main.state(), "id");
            try (var queue = new QueueObserver(scenario.table.game.getTriggerHandler())) {
                var concession = new Worker("ordinary-api-concede", () -> concede(scenario, 2, session));
                try {
                    var forest = zone(viewer(main.state()), "Hand").getAsJsonArray("cards").asList().stream()
                            .map(card -> card.getAsJsonObject()).filter(card -> text(card, "name").equals("Forest")
                                    && card.get("selectable").getAsBoolean()).findFirst().orElseThrow();
                    assertTrue(scenario.answer(main, Map.of("action", "card", "key", text(forest, "key"))));
                    queue.facts.put("scopedForestPlayAccepted", true);
                    queue.facts.put("originalPlayers", ids);
                    queue.awaitSnapshot();
                    assertTrue(queue.firstStack.stream().noneMatch(frame -> frame.contains("MatchSession.concede")),
                            "The first real drain must run on the ordinary game path, before the API concession");
                    concession.start();
                    StateBoundaryContention.await(concession.thread, -1);
                    queue.contenderBoundary = "blocked-on-game-state-boundary";
                    assertEquals(scenario.table.game.getPlayers().stream().map(player -> player.getId()).toList(), ids,
                            "Neither admission nor cleanup can change the held trigger batch's participants");
                    queue.release.countDown();
                    concession.finish();
                    queue.facts.put("concessionWorkerExited", !concession.thread.isAlive());
                    awaitConcession(scenario.table, scenario.table.seat(2));
                    assertFalse(queue.hasDuplicateWork(), "Each waiting trigger must belong to exactly one drain batch");
                    var afterLand = scenario.until(input -> input.seat() == 0
                            && text(input.state(), "phaseKey").equals("MAIN1")
                            && text(input.prompt(), "inputType").equals("InputPassPriority")
                            && input.state().getAsJsonArray("stack").isEmpty()
                            && zone(viewer(input.state()), "Battlefield").getAsJsonArray("cards").asList().stream()
                            .anyMatch(card -> text(card.getAsJsonObject(), "name").equals("Forest")));
                    queue.facts.put("initialLife", initialLife);
                    queue.facts.put("lifeAfterLandfall", viewer(afterLand.state()).get("life").getAsInt());
                    assertFalse(queue.lossDrainStack.isEmpty(),
                            "The ordinary scoped concession must execute the same production loss-trigger drain as CI");
                    assertEquals(viewer(afterLand.state()).get("life").getAsInt(), initialLife + 1,
                            "One real Forest must resolve exactly one Jaddi Offshoot landfall trigger");
                    assertEquals(scenario.table.game.getPlayers().stream().map(player -> player.getId()).toList(),
                            List.of(ids.get(0), ids.get(1), ids.get(3)));
                    assertTrue(player(afterLand.state(), ids.get(2)).get("eliminated").getAsBoolean());
                    assertTrue(scenario.state(2).get("prompt").isJsonNull());
                    assertTrue(scenario.answer(afterLand, Map.of("action", "ok")));
                    var next = scenario.until(input -> input.seat() == 1
                            && text(input.state(), "phaseKey").equals("MAIN1")
                            && text(input.prompt(), "inputType").equals("InputPassPriority"));
                    assertNotEquals(text(next.prompt(), "id"), text(afterLand.prompt(), "id"));
                    assertEquals(next.state().get("activePlayerId").getAsInt(), ids.get(0).intValue());
                    for (int seat = 0; seat < 4; seat++) assertPrivateZones(scenario.state(seat));
                } finally {
                    queue.release.countDown();
                    try { concession.finish(); }
                    finally { queue.save(scenario, "concession-queue.json"); }
                }
            }
            assertNormalTerminal(scenario, session, ids.get(0));
        }
    }

    @Test(timeOut = 90000)
    public void aConcurrentEnqueueBelongsToTheNextDrain() throws Exception {
        try (var scenario = new RulesScenario("waiting-trigger-concurrent-enqueue", Map.of())) {
            main(scenario);
            var handler = scenario.table.game.getTriggerHandler();
            try (var queue = new QueueObserver(handler)) {
                var first = new ObservedParams("first", queue.observed, null);
                var next = new ObservedParams("next", queue.observed, null);
                handler.runTrigger(TriggerType.Foretell, first, true);
                var drain = new Worker("first-queue-drain", handler::runWaitingTriggers);
                var producer = new Worker("concurrent-held-enqueue", () -> handler.runTrigger(TriggerType.Foretell, next, true));
                try {
                    drain.start();
                    queue.awaitSnapshot();
                    producer.start();
                    queue.contenderBoundary = awaitBlockedOrCompleted(producer, queue.observed);
                    queue.release.countDown();
                    drain.finish();
                    producer.finish();
                    handler.runWaitingTriggers();
                    queue.facts.put("firstCalls", first.calls.get());
                    queue.facts.put("nextCalls", next.calls.get());
                    queue.facts.put("callbackUnderQueueMonitor", first.underQueueMonitor.get() || next.underQueueMonitor.get());
                    assertEquals(first.calls.get(), 1, "The first captured batch must execute once");
                    assertEquals(next.calls.get(), 1, "A concurrent enqueue must survive for exactly one later drain");
                    assertFalse(first.underQueueMonitor.get() || next.underQueueMonitor.get(),
                            "Trigger predicates and callbacks must execute outside the waiting-queue monitor");
                    assertFalse(queue.hasDuplicateWork());
                    assertTrue(queue.observed.isEmpty());
                } finally {
                    queue.release.countDown();
                    try { drain.finish(); }
                    finally {
                        try { producer.finish(); }
                        finally { queue.save(scenario, "enqueue-queue.json"); }
                    }
                }
            }
        }
    }

    @Test(timeOut = 90000)
    public void nestedDrainAndClearKeepCapturedWorkAndReleaseTheQueueMonitor() throws Exception {
        try (var scenario = new RulesScenario("waiting-trigger-reentrant-clear", Map.of())) {
            main(scenario);
            var handler = scenario.table.game.getTriggerHandler();
            try (var queue = new QueueObserver(handler, false)) {
                var order = new CopyOnWriteArrayList<String>();
                var cleared = new ObservedParams("cleared", queue.observed, () -> order.add("cleared"));
                var nested = new ObservedParams("nested", queue.observed, () -> order.add("nested"));
                var first = new ObservedParams("first", queue.observed, () -> {
                    order.add("first");
                    handler.runTrigger(TriggerType.Foretell, cleared, true);
                    handler.clearWaitingTriggers();
                    handler.runTrigger(TriggerType.Foretell, nested, true);
                    handler.runWaitingTriggers();
                });
                var capturedSibling = new ObservedParams("captured-sibling", queue.observed, () -> order.add("captured-sibling"));
                try {
                    handler.runTrigger(TriggerType.Foretell, first, true);
                    handler.runTrigger(TriggerType.Foretell, capturedSibling, true);
                    handler.runWaitingTriggers();
                    queue.facts.put("callbackOrder", order);
                    queue.facts.put("clearedCalls", cleared.calls.get());
                    queue.facts.put("callbackUnderQueueMonitor", List.of(first, nested, capturedSibling).stream()
                            .anyMatch(params -> params.underQueueMonitor.get()));
                    assertEquals(order, List.of("first", "nested", "captured-sibling"),
                            "Explicit clear affects pending work; nested draining cannot consume the captured outer sibling");
                    for (var params : List.of(first, nested, capturedSibling)) {
                        assertEquals(params.calls.get(), 1);
                        assertFalse(params.underQueueMonitor.get(), "No trigger callback may run under the queue monitor: " + params.label);
                    }
                    assertEquals(cleared.calls.get(), 0);
                    assertFalse(queue.hasDuplicateWork());
                    assertTrue(queue.observed.isEmpty());
                } finally { queue.save(scenario, "reentrant-queue.json"); }
            }
        }
    }

    private static RulesScenario.Input main(RulesScenario scenario) throws Exception {
        return scenario.until(input -> input.seat() == 0 && text(input.state(), "phaseKey").equals("MAIN1")
                && text(input.prompt(), "inputType").equals("InputPassPriority")
                && input.state().getAsJsonArray("stack").isEmpty());
    }

    private static String awaitBlockedOrCompleted(Worker worker, Object queue) throws Exception {
        long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(5);
        while (System.nanoTime() < deadline) {
            if (worker.done.await(1, TimeUnit.MILLISECONDS)) return "completed-before-first-clear";
            var info = ManagementFactory.getThreadMXBean().getThreadInfo(worker.thread.getId());
            if (info != null && info.getThreadState() == Thread.State.BLOCKED && info.getLockInfo() != null
                    && info.getLockInfo().getIdentityHashCode() == System.identityHashCode(queue)) return "blocked-on-queue-monitor";
        }
        throw new AssertionError("The contender neither completed nor reached the actual queue monitor within five seconds");
    }

    private static final class Worker {
        final CountDownLatch done = new CountDownLatch(1);
        final AtomicReference<Throwable> failure = new AtomicReference<>();
        final Thread thread;
        boolean started;
        Worker(String name, Runnable action) {
            thread = new Thread(() -> {
                try { action.run(); }
                catch (Throwable error) { failure.set(error); }
                finally { done.countDown(); }
            }, name);
            thread.setDaemon(true);
        }
        void start() { started = true; thread.start(); }
        void finish() throws Exception {
            if (!started) return;
            assertTrue(done.await(10, TimeUnit.SECONDS), "Owned worker did not complete: " + thread.getName());
            thread.join(1000);
            assertFalse(thread.isAlive(), "Owned worker must exit: " + thread.getName());
            if (failure.get() != null) throw new AssertionError("Owned worker failed: " + thread.getName(), failure.get());
        }
    }

    private static final class ObservedParams extends EnumMap<AbilityKey, Object> {
        final String label;
        final Object queue;
        final Runnable callback;
        final AtomicInteger calls = new AtomicInteger();
        final AtomicBoolean underQueueMonitor = new AtomicBoolean();
        ObservedParams(String label, Object queue, Runnable callback) {
            super(AbilityKey.class);
            this.label = label;
            this.queue = queue;
            this.callback = callback;
        }
        @Override public boolean containsKey(Object key) {
            if (key == AbilityKey.Destination) {
                calls.incrementAndGet();
                if (Thread.holdsLock(queue)) underQueueMonitor.set(true);
                if (callback != null) callback.run();
            }
            return super.containsKey(key);
        }
    }

    private static final class QueueObserver implements AutoCloseable {
        final TriggerHandler handler;
        final Field field;
        final List<TriggerWaiting> original;
        final GatedList observed = new GatedList();
        final CountDownLatch snapshot = new CountDownLatch(1);
        final CountDownLatch release = new CountDownLatch(1);
        final List<Batch> batches = new CopyOnWriteArrayList<>();
        final Map<TriggerWaiting, Integer> ids = Collections.synchronizedMap(new IdentityHashMap<>());
        final Map<String, Object> facts = new java.util.LinkedHashMap<>();
        final AtomicInteger nextId = new AtomicInteger();
        final AtomicBoolean gated = new AtomicBoolean();
        final boolean pause;
        volatile List<String> firstStack = List.of();
        volatile List<String> lossDrainStack = List.of();
        volatile String contenderBoundary = "not-started";
        QueueObserver(TriggerHandler handler) throws Exception { this(handler, true); }
        @SuppressWarnings("unchecked")
        QueueObserver(TriggerHandler handler, boolean pause) throws Exception {
            this.handler = handler;
            this.pause = pause;
            field = TriggerHandler.class.getDeclaredField("waitingTriggers");
            field.setAccessible(true);
            original = (List<TriggerWaiting>) field.get(handler);
            synchronized (original) {
                assertTrue(original.isEmpty(), "Install the observational queue only at a clean input boundary");
                field.set(handler, observed);
            }
        }
        final class GatedList extends ArrayList<TriggerWaiting> {
            @Override public boolean isEmpty() {
                if (drainCopy()) {
                    var stack = StackWalker.getInstance().walk(frames -> frames
                            .map(frame -> frame.getClassName() + "." + frame.getMethodName()).toList());
                    if (stack.stream().anyMatch(frame -> frame.equals("forge.game.trigger.TriggerHandler.onPlayerLost"))) lossDrainStack = stack;
                }
                return super.isEmpty();
            }
            @Override public Object[] toArray() {
                Object[] copied = super.toArray();
                if (copied.length == 0 || !drainCopy()) return copied;
                var stack = StackWalker.getInstance().walk(frames -> frames
                        .map(frame -> frame.getClassName() + "." + frame.getMethodName()).toList());
                var work = Arrays.stream(copied).map(item -> ids.computeIfAbsent((TriggerWaiting) item,
                        ignored -> nextId.incrementAndGet())).toList();
                batches.add(new Batch(Thread.currentThread().getName(), work, Thread.holdsLock(this), stack));
                if (pause && gated.compareAndSet(false, true)) {
                    firstStack = stack;
                    snapshot.countDown();
                    try { assertTrue(release.await(10, TimeUnit.SECONDS), "The controlled snapshot must be released"); }
                    catch (InterruptedException interrupted) { Thread.currentThread().interrupt(); throw new AssertionError(interrupted); }
                }
                return copied;
            }
        }
        void awaitSnapshot() throws Exception {
            assertTrue(snapshot.await(5, TimeUnit.SECONDS), "The real production drain must reach a nonempty queue snapshot");
        }
        boolean hasDuplicateWork() {
            var seen = new java.util.HashSet<Integer>();
            return batches.stream().flatMap(batch -> batch.work.stream()).anyMatch(id -> !seen.add(id));
        }
        void save(RulesScenario scenario, String name) throws Exception {
            Files.writeString(scenario.evidence.resolve(name), new GsonBuilder().setPrettyPrinting().serializeNulls().create()
                    .toJson(Map.of("batches", batches, "contenderBoundary", contenderBoundary,
                            "duplicateWork", hasDuplicateWork(), "firstStack", firstStack, "lossDrainStack", lossDrainStack, "facts", facts)));
        }
        @Override public void close() throws Exception {
            release.countDown();
            synchronized (observed) {
                synchronized (original) {
                    original.clear();
                    original.addAll(observed);
                    field.set(handler, original);
                }
            }
        }
    }

    private record Batch(String thread, List<Integer> work, boolean queueMonitorHeld, List<String> stack) { }

    private static boolean drainCopy() {
        return StackWalker.getInstance().walk(frames -> frames
                .filter(frame -> frame.getClassName().equals(TriggerHandler.class.getName())).findFirst()
                .map(frame -> frame.getMethodName().equals("runWaitingTriggers")).orElse(false));
    }

    private static void concede(RulesScenario scenario, int seat, String session) {
        var request = new JsonObject();
        request.addProperty("sessionId", session);
        scenario.table.seat(seat).concede(request);
    }

    private static void assertPrivateZones(JsonObject state) {
        int viewer = state.get("viewerId").getAsInt();
        for (var entry : state.getAsJsonArray("players")) {
            var player = entry.getAsJsonObject();
            assertTrue(zone(player, "Library").getAsJsonArray("cards").isEmpty());
            if (player.get("id").getAsInt() != viewer) assertTrue(zone(player, "Hand").getAsJsonArray("cards").isEmpty());
        }
    }

    private static void assertNormalTerminal(RulesScenario scenario, String session, int winner) throws Exception {
        var proof = new java.util.LinkedHashMap<String, Object>();
        proof.put("maximumSeconds", 5);
        proof.put("exactTerminalViewsObserved", false);
        var announced = new CountDownLatch(1);
        var listener = new Object() {
            @Subscribe public void finished(GameEventGameFinished event) { announced.countDown(); }
        };
        scenario.table.game.subscribeToEvents(listener);
        try {
            concede(scenario, 1, session);
            awaitConcession(scenario.table, scenario.table.seat(1));
            concede(scenario, 3, session);
            long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(5);
            assertTrue(announced.await(Math.max(0, deadline - System.nanoTime()), TimeUnit.NANOSECONDS));
            while (System.nanoTime() < deadline) {
                var views = scenario.table.testViews();
                boolean ready = true;
                for (int seat = 0; seat < 4; seat++) {
                    var view = views.get(seat);
                    var state = new GsonBuilder().serializeNulls().create().toJsonTree(view).getAsJsonObject();
                    ready &= view.get("error") == null && view.get("prompt") == null
                            && text(state, "status").equals("finished")
                            && text(state, "result").equals(seat == 0 ? "Victory" : "Defeat")
                            && state.getAsJsonArray("players").asList().stream().map(player -> player.getAsJsonObject())
                            .filter(player -> !player.get("eliminated").getAsBoolean())
                            .map(player -> player.get("id").getAsInt()).toList().equals(List.of(winner));
                    assertPrivateZones(state);
                }
                if (ready) {
                    proof.put("exactTerminalViewsObserved", true);
                    proof.put("views", views);
                    return;
                }
                new CountDownLatch(1).await(1, TimeUnit.MILLISECONDS);
            }
            fail("All four exact terminal projections must settle within the original five-second budget");
        } finally {
            try {
                proof.put("gameFinishedAnnounced", announced.getCount() == 0);
                Files.writeString(scenario.evidence.resolve("terminal-projections.json"),
                        new GsonBuilder().setPrettyPrinting().serializeNulls().create().toJson(proof));
            } finally { scenario.table.game.unsubscribeFromEvents(listener); }
        }
    }
}
