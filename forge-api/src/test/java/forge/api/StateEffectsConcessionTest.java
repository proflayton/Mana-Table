package forge.api;

import com.google.common.eventbus.Subscribe;
import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import forge.StaticData;
import forge.deck.Deck;
import forge.game.Game;
import forge.game.GameAction;
import forge.game.card.CardCollectionView;
import forge.game.event.GameEventGameFinished;
import forge.game.player.Player;
import forge.game.player.RegisteredPlayer;
import forge.game.zone.PlayerZone;
import forge.game.zone.ZoneType;
import org.testng.annotations.DataProvider;
import org.testng.annotations.Test;

import java.lang.reflect.Field;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.CyclicBarrier;
import java.util.concurrent.ExecutorCompletionService;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

public class StateEffectsConcessionTest {
    @Test(timeOut = 90000)
    public void controllerReadsRemainValidWhilePublicControlOverridesAreRemoved() throws Exception {
        if (StaticData.instance() == null) EngineResources.load(Path.of("../forge-gui/res"));
        var players = new ArrayList<RegisteredPlayer>();
        for (int seat = 0; seat < 3; seat++) players.add(MatchTable.human(new Deck(), "Constructed", "Controller seat " + seat));
        // No rules loop is started. Exercise the actual public control APIs on
        // registered players without modifying a running game's state.
        try (var table = new MatchTable("Constructed", players, Path.of("../forge-gui/res"), Files.createTempDirectory("controller-snapshot"))) {
            var subject = table.game.getPlayers().get(0);
            var older = table.game.getPlayers().get(1);
            var newer = table.game.getPlayers().get(2);
            var baseController = subject.getController();
            var olderController = older.getController();
            var newerController = newer.getController();
            subject.addController(20, newer, newerController, false);
            subject.addController(10, older, olderController, false);
            assertSame(subject.getController(), newerController, "Latest timestamp wins regardless of insertion order");
            assertSame(subject.getControllingPlayer(), newer);
            subject.removeController(20, false);
            assertSame(subject.getController(), olderController);
            assertSame(subject.getControllingPlayer(), older);
            subject.removeController(older);
            assertSame(subject.getController(), baseController);
            assertNull(subject.getControllingPlayer());

            subject.addControlledWhileSearching(20, newer);
            subject.addControlledWhileSearching(10, older);
            var retained = subject.getControlledWhileSearching();
            assertNotNull(retained);
            assertEquals(retained.getKey(), Long.valueOf(20));
            assertSame(retained.getValue(), newer);
            subject.addControlledWhileSearching(30, null);
            var nullable = subject.getControlledWhileSearching();
            assertNotNull(nullable, "A latest entry with a null player still exists and masks older entries");
            assertEquals(nullable.getKey(), Long.valueOf(30));
            assertNull(nullable.getValue());
            subject.removeControlledWhileSearching(30);
            assertSame(subject.getControlledWhileSearching().getValue(), newer);
            subject.removeControlledWhileSearching(20);
            assertSame(subject.getControlledWhileSearching().getValue(), older);
            subject.removeControlledWhileSearching(10);
            assertNull(subject.getControlledWhileSearching());
            assertSame(retained.getValue(), newer, "A previously returned entry remains a snapshot after removal");

            var barrier = new CyclicBarrier(2);
            var workers = Executors.newFixedThreadPool(2);
            var completed = new ExecutorCompletionService<Void>(workers);
            try {
                completed.submit(() -> {
                    for (int batch = 0; batch < 64; batch++) {
                        barrier.await();
                        for (int index = 0; index < 8; index++) {
                            subject.addController(100, older, olderController, false);
                            subject.addController(200, newer, newerController, false);
                            subject.removeController(200, false);
                            subject.clearController();
                            subject.addControlledWhileSearching(100, older);
                            subject.addControlledWhileSearching(200, index % 2 == 0 ? newer : null);
                            subject.removeControlledWhileSearching(200);
                            subject.removeControlledWhileSearching(100);
                        }
                    }
                    return null;
                });
                completed.submit(() -> {
                    for (int batch = 0; batch < 64; batch++) {
                        barrier.await();
                        for (int index = 0; index < 128; index++) {
                            var control = subject.getController();
                            assertTrue(control == baseController || control == olderController || control == newerController);
                            var owner = subject.getControllingPlayer();
                            assertTrue(owner == null || owner == older || owner == newer);
                            var search = subject.getControlledWhileSearching();
                            assertTrue(search == null || search.getKey() == 100 && search.getValue() == older
                                    || search.getKey() == 200 && (search.getValue() == newer || search.getValue() == null));
                        }
                    }
                    return null;
                });
                for (int index = 0; index < 2; index++) {
                    var result = completed.poll(5, TimeUnit.SECONDS);
                    assertNotNull(result, "Bounded public controller reads/writes must complete without deadlock");
                    result.get();
                }
            } finally {
                workers.shutdownNow();
                assertTrue(workers.awaitTermination(5, TimeUnit.SECONDS), "Controller workers must stop before closing the unstarted table");
            }
            assertSame(subject.getController(), baseController);
            assertNull(subject.getControllingPlayer());
            assertNull(subject.getControlledWhileSearching());
        }
    }

    @DataProvider(name = "traversals")
    public Object[][] traversals() {
        return new Object[][]{{"other-zones", 1}, {"player-rules", 2}, {"static-layer", 1}};
    }

    @Test(dataProvider = "traversals", timeOut = 90000)
    public void aConcessionDuringStateEffectsKeepsTheOriginalParticipants(String traversal, int graveyardRead) throws Exception {
        try (var scenario = new RulesScenario("state-effects-concession-" + traversal, Map.of(
                0, List.of("Glorious Anthem", "Grizzly Bears"), 1, List.of("Llanowar Elves"),
                2, List.of("Elvish Mystic"), 3, List.of("Runeclaw Bear")))) {
            var main = scenario.until(input -> input.seat() == 0
                    && text(input.state(), "phaseKey").equals("MAIN1")
                    && text(input.prompt(), "inputType").equals("InputPassPriority"));
            var participants = new ArrayList<>(scenario.table.game.getPlayers());
            var initialIds = participants.stream().map(Player::getId).toList();
            int departedId = initialIds.get(2);
            String sessionId = text(main.state(), "id");
            var finished = new CountDownLatch(1);
            var listener = new Object() {
                @Subscribe public void gameFinished(GameEventGameFinished event) { finished.countDown(); }
            };
            scenario.table.game.subscribeToEvents(listener);
            try {
                RulesScenario.Input afterLand;
                try (var hook = new ParticipantReads(participants, traversal, graveyardRead, () -> concede(scenario, 2, sessionId))) {
                    try {
                        // A normal scoped land play resumes the real phase loop and its
                        // state-based actions. The test never calls checkStateEffects itself.
                        var forest = zone(viewer(main.state()), "Hand").getAsJsonArray("cards").asList().stream()
                                .map(card -> card.getAsJsonObject()).filter(card -> text(card, "name").equals("Forest")
                                        && card.get("selectable").getAsBoolean()).findFirst().orElseThrow();
                        assertTrue(scenario.answer(main, Map.of("action", "card", "key", text(forest, "key"))));
                        afterLand = scenario.until(input -> input.seat() == 0
                                && text(input.state(), "phaseKey").equals("MAIN1")
                                && text(input.prompt(), "inputType").equals("InputPassPriority")
                                && zone(viewer(input.state()), "Battlefield").getAsJsonArray("cards").asList().stream()
                                .map(card -> card.getAsJsonObject()).anyMatch(card -> text(card, "name").equals("Forest")));
                        assertTrue(hook.conceded, "The production participant traversal must execute the real concession");
                        assertEquals(hook.visited, initialIds,
                                "The selected traversal must visit every original participant in order, including the departed player");
                    } finally {
                        Files.writeString(scenario.evidence.resolve("participant-traversal.json"), new Gson().toJson(Map.of(
                                "traversal", traversal, "graveyardRead", graveyardRead,
                                "originalParticipants", initialIds, "visited", hook.visited, "conceded", hook.conceded)));
                    }
                }
                assertEquals(scenario.table.game.getPlayers().size(), 3);
                assertFalse(scenario.table.game.getPlayers().contains(participants.get(2)));
                assertEquals(afterLand.state().get("turn").getAsInt(), 1);
                assertTrue(player(afterLand.state(), departedId).get("eliminated").getAsBoolean());
                assertTrue(scenario.state(2).get("prompt").isJsonNull());
                var bears = zone(viewer(afterLand.state()), "Battlefield").getAsJsonArray("cards").asList().stream()
                        .map(card -> card.getAsJsonObject()).filter(card -> text(card, "name").equals("Grizzly Bears"))
                        .findFirst().orElseThrow();
                assertEquals(bears.get("power").getAsInt(), 3, "The genuine continuous effect remains applied after state-based actions");
                assertEquals(bears.get("toughness").getAsInt(), 3);

                assertTrue(scenario.answer(afterLand, Map.of("action", "ok")));
                var afterPass = scenario.until(input -> input.seat() == 1
                        && text(input.state(), "phaseKey").equals("MAIN1")
                        && text(input.prompt(), "inputType").equals("InputPassPriority"));
                assertNotEquals(text(afterPass.prompt(), "id"), text(afterLand.prompt(), "id"),
                        "A survivor's scoped pass must hand fresh priority to the next surviving seat");
                assertEquals(afterPass.state().get("turn").getAsInt(), 1);
                assertEquals(afterPass.state().get("activePlayerId").getAsInt(), initialIds.get(0).intValue());
                var publicBoard = zone(player(afterPass.state(), initialIds.get(0)), "Battlefield").getAsJsonArray("cards").asList().stream()
                        .map(card -> card.getAsJsonObject()).toList();
                assertTrue(publicBoard.stream().anyMatch(card -> text(card, "name").equals("Forest")),
                        "The next surviving player still sees the played land on the public battlefield");
                var publicBears = publicBoard.stream().filter(card -> text(card, "name").equals("Grizzly Bears")).findFirst().orElseThrow();
                assertEquals(publicBears.get("power").getAsInt(), 3);
                assertEquals(publicBears.get("toughness").getAsInt(), 3);
                assertTrue(scenario.state(2).get("prompt").isJsonNull());
                for (int seat = 0; seat < 4; seat++) assertPrivateZones(scenario.state(seat));

                concede(scenario, 1, sessionId);
                concede(scenario, 3, sessionId);
                long terminalDeadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(5);
                boolean announced = finished.await(Math.max(0, terminalDeadline - System.nanoTime()), TimeUnit.NANOSECONDS);
                var terminalViews = terminalViews(scenario, terminalDeadline, announced, initialIds.get(0));
                for (int seat = 0; seat < 4; seat++) {
                    var snapshot = terminalViews.get(seat);
                    assertNull(snapshot.get("error"));
                    assertNull(snapshot.get("prompt"));
                    var state = new Gson().toJsonTree(snapshot).getAsJsonObject();
                    assertEquals(text(state, "status"), "finished");
                    assertEquals(text(state, "result"), seat == 0 ? "Victory" : "Defeat");
                    assertEquals(state.getAsJsonArray("players").asList().stream().map(player -> player.getAsJsonObject())
                            .filter(player -> !player.get("eliminated").getAsBoolean()).map(player -> player.get("id").getAsInt()).toList(),
                            List.of(initialIds.get(0)), "Every terminal projection must name the exact sole surviving player");
                    assertPrivateZones(state);
                }
            } finally {
                scenario.table.game.unsubscribeFromEvents(listener);
            }
        }
    }

    @Test(timeOut = 90000)
    public void anInflightRepeatedConcessionCannotInvalidateTheLossRevealTraversal() throws Exception {
        try (var scenario = new RulesScenario("loss-cleanup-concession", Map.of(
                0, List.of("Glorious Anthem", "Grizzly Bears"), 3, List.of("Runeclaw Bear")))) {
            var main = scenario.until(input -> input.seat() == 0
                    && text(input.state(), "phaseKey").equals("MAIN1")
                    && text(input.prompt(), "inputType").equals("InputPassPriority"));
            var participants = new ArrayList<>(scenario.table.game.getPlayers());
            var originalIds = participants.stream().map(Player::getId).toList();
            String sessionId = text(main.state(), "id");
            var forest = zone(viewer(main.state()), "Hand").getAsJsonArray("cards").asList().stream()
                    .map(card -> card.getAsJsonObject()).filter(card -> text(card, "name").equals("Forest")
                            && card.get("selectable").getAsBoolean()).findFirst().orElseThrow();
            assertTrue(scenario.answer(main, Map.of("action", "card", "key", text(forest, "key"))));
            var afterLand = scenario.until(input -> input.seat() == 0
                    && text(input.state(), "phaseKey").equals("MAIN1")
                    && text(input.prompt(), "inputType").equals("InputPassPriority")
                    && zone(viewer(input.state()), "Battlefield").getAsJsonArray("cards").asList().stream()
                    .map(card -> card.getAsJsonObject()).anyMatch(card -> text(card, "name").equals("Forest")));

            concede(scenario, 2, sessionId);
            concede(scenario, 1, sessionId);
            assertEquals(scenario.table.game.getPlayers().stream().map(Player::getId).toList(),
                    List.of(originalIds.get(0), originalIds.get(3)));
            assertTrue(scenario.answer(afterLand, Map.of("action", "ok")));
            var remainingOpponent = scenario.until(input -> input.seat() == 3
                    && text(input.state(), "phaseKey").equals("MAIN1")
                    && text(input.prompt(), "inputType").equals("InputPassPriority"));
            var survivor = remainingOpponent;
            assertNotEquals(text(survivor.prompt(), "id"), text(afterLand.prompt(), "id"),
                    "A normal scoped pass must hand fresh priority to the other remaining player");
            assertEquals(survivor.state().get("turn").getAsInt(), 1);
            assertEquals(survivor.state().get("activePlayerId").getAsInt(), originalIds.get(0).intValue());
            var publicBoard = zone(player(survivor.state(), originalIds.get(0)), "Battlefield").getAsJsonArray("cards").asList().stream()
                    .map(card -> card.getAsJsonObject()).toList();
            assertTrue(publicBoard.stream().anyMatch(card -> text(card, "name").equals("Forest")));
            var bears = publicBoard.stream().filter(card -> text(card, "name").equals("Grizzly Bears")).findFirst().orElseThrow();
            assertEquals(bears.get("power").getAsInt(), 3);
            assertEquals(bears.get("toughness").getAsInt(), 3);
            assertTrue(scenario.state(1).get("prompt").isJsonNull());
            assertTrue(scenario.state(2).get("prompt").isJsonNull());
            for (int seat = 0; seat < 4; seat++) assertPrivateZones(scenario.state(seat));

            var finished = new CountDownLatch(1);
            var listener = new Object() {
                @Subscribe public void gameFinished(GameEventGameFinished event) { finished.countDown(); }
            };
            scenario.table.game.subscribeToEvents(listener);
            try (var hook = new LossRevealRead(participants.get(0), scenario.table.game, () -> {
                assertTrue(Thread.holdsLock(scenario.table.gate), "The callback must occur during the real synchronous session concession");
                assertFalse(scenario.table.game.isGameOver(), "The repeated request must arrive while the first cleanup is still in flight");
                var request = new JsonObject();
                request.addProperty("sessionId", sessionId);
                var response = scenario.table.seat(3).concede(request);
                assertNotNull(response, "The normal scoped-session repeated concession must be accepted");
                assertNull(response.get("error"));
            })) {
                try {
                    concede(scenario, 3, sessionId);
                    assertTrue(hook.fired, "The production reveal cleanup must execute the restoring zone hook");
                    assertEquals(hook.reads, 2, "Remove the departing player only after the outer iterator yielded its second participant");
                    assertTrue(hook.exactRevealPath, "The query must come directly from Game.onPlayerLost to Player.revealFaceDownCards");
                    assertEquals(hook.before, List.of(originalIds.get(0), originalIds.get(3)));
                    assertEquals(hook.after, List.of(originalIds.get(0)), "The repeated real concession must remove the departing participant inside the paused traversal");
                    long terminalDeadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(5);
                    boolean announced = finished.await(Math.max(0, terminalDeadline - System.nanoTime()), TimeUnit.NANOSECONDS);
                    var finalViews = terminalViews(scenario, terminalDeadline, announced, originalIds.get(0));
                    for (int seat = 0; seat < 4; seat++) {
                        var snapshot = finalViews.get(seat);
                        assertNull(snapshot.get("error"));
                        assertNull(snapshot.get("prompt"));
                        var state = new Gson().toJsonTree(snapshot).getAsJsonObject();
                        assertEquals(text(state, "status"), "finished");
                        assertEquals(text(state, "result"), seat == 0 ? "Victory" : "Defeat");
                        assertEquals(state.getAsJsonArray("players").asList().stream().map(player -> player.getAsJsonObject())
                                .filter(player -> !player.get("eliminated").getAsBoolean()).map(player -> player.get("id").getAsInt()).toList(),
                                List.of(originalIds.get(0)));
                        assertPrivateZones(state);
                    }
                } finally {
                    Files.writeString(scenario.evidence.resolve("loss-reveal-traversal.json"), new GsonBuilder().serializeNulls().create().toJson(Map.of(
                            "hookFired", hook.fired, "exactRevealPath", hook.exactRevealPath,
                            "matchingReads", hook.reads, "before", hook.before, "after", hook.after, "stack", hook.stack)));
                }
            } finally {
                scenario.table.game.unsubscribeFromEvents(listener);
            }
        }
    }

    private static final class LossRevealRead implements AutoCloseable {
        private final Map<ZoneType, PlayerZone> zones;
        private final PlayerZone original;
        private boolean fired;
        private int reads;
        private boolean exactRevealPath;
        private List<Integer> before = List.of();
        private List<Integer> after = List.of();
        private List<String> stack = List.of();

        @SuppressWarnings("unchecked")
        LossRevealRead(Player observer, Game game, Runnable repeatConcession) throws Exception {
            Field field = Player.class.getDeclaredField("zones");
            field.setAccessible(true);
            zones = (Map<ZoneType, PlayerZone>) field.get(observer);
            original = zones.get(ZoneType.Exile);
            assertNotNull(original);
            zones.put(ZoneType.Exile, new PlayerZone(ZoneType.Exile, observer) {
                @Override public CardCollectionView getCards(boolean filter) {
                    boolean matching = StackWalker.getInstance().walk(frames -> frames
                            .dropWhile(frame -> !frame.getClassName().equals(Player.class.getName())
                                    || !frame.getMethodName().equals("revealFaceDownCards"))
                            .skip(1).findFirst().map(frame -> frame.getClassName().equals(Game.class.getName())
                                    && frame.getMethodName().equals("onPlayerLost")).orElse(false));
                    // Each outer participant's reveal queries observer0's Exile
                    // once. Remove after the second participant was yielded:
                    // the live iterator cursor2 must then see the smaller list.
                    if (fired || !matching || ++reads != 2) return original.getCards(filter);
                    fired = true;
                    exactRevealPath = matching;
                    stack = StackWalker.getInstance().walk(frames -> frames.limit(16).map(Object::toString).toList());
                    // This is the ordinary reveal's inner owned-card query, not a
                    // replacement for reveal rules or an observation of every outer player.
                    // Restore the real zone before the genuine nested cleanup touches it.
                    LossRevealRead.this.close();
                    before = game.getPlayers().stream().map(Player::getId).toList();
                    repeatConcession.run();
                    after = game.getPlayers().stream().map(Player::getId).toList();
                    return original.getCards(filter);
                }
            });
        }

        @Override public void close() { zones.put(ZoneType.Exile, original); }
    }

    private static List<Map<String, Object>> terminalViews(RulesScenario scenario, long deadline, boolean announced, int winnerId) throws Exception {
        var json = new GsonBuilder().serializeNulls().create();
        var observed = new ArrayList<Object>();
        List<Map<String, Object>> latest = List.of();
        String previous = "";
        try {
            while (true) {
                // Match fires GameFinished before returning to MatchTable.start's
                // finally/publish. The event is not a cached-projection barrier.
                // testViews captures one passive cohort under the table gate.
                latest = scenario.table.testViews();
                String encoded = json.toJson(latest);
                if (!encoded.equals(previous)) {
                    if (observed.size() < 16) observed.add(Map.of("remainingNanos", deadline - System.nanoTime(), "views", latest));
                    previous = encoded;
                }
                boolean ready = latest.size() == 4;
                for (int seat = 0; seat < latest.size(); seat++) {
                    var snapshot = latest.get(seat);
                    assertNull(snapshot.get("error"), encoded);
                    var state = json.toJsonTree(snapshot).getAsJsonObject();
                    assertPrivateZones(state);
                    var survivors = state.getAsJsonArray("players").asList().stream().map(player -> player.getAsJsonObject())
                            .filter(player -> !player.get("eliminated").getAsBoolean()).map(player -> player.get("id").getAsInt()).toList();
                    ready &= text(state, "status").equals("finished") && snapshot.get("prompt") == null
                            && text(state, "result").equals(seat == 0 ? "Victory" : "Defeat") && survivors.equals(List.of(winnerId));
                }
                assertTrue(announced, "The engine must announce game completion within the terminal deadline");
                assertTrue(System.nanoTime() < deadline, "The exact terminal projections must settle within the original five-second budget: " + encoded);
                if (ready) return latest;
                Thread.sleep(5);
            }
        } finally {
            Files.writeString(scenario.evidence.resolve("terminal-projections.json"), json.toJson(Map.of(
                    "announced", announced, "observed", observed, "lastViews", latest)));
        }
    }

    /** Observes either the direct per-player Graveyard reads in checkStateEffectsOwned,
     * or the real devotion query inside afterStaticAbilityLayer. The first direct
     * read belongs to non-battlefield checks; the second to Commander checks.
     * The private traversal body must be called by the public execution-owning
     * wrapper. Unrelated queries and nested concession cleanup are not counted. */
    private static final class ParticipantReads implements AutoCloseable {
        private final List<Slot> slots = new ArrayList<>();
        private final List<Integer> visited = new CopyOnWriteArrayList<>();
        private volatile boolean conceded;

        @SuppressWarnings("unchecked")
        ParticipantReads(List<Player> participants, String traversal, int targetRead, Runnable concede) throws Exception {
            Field field = Player.class.getDeclaredField("zones");
            field.setAccessible(true);
            for (Player player : participants) {
                var zones = (Map<ZoneType, PlayerZone>) field.get(player);
                slots.add(new Slot(player, zones, traversal, targetRead, concede));
            }
            slots.forEach(Slot::install);
        }

        private final class Slot {
            private final Map<ZoneType, PlayerZone> zones;
            private final PlayerZone original;
            private final PlayerZone wrapper;
            private int reads;
            private boolean consumed;

            Slot(Player player, Map<ZoneType, PlayerZone> zones, String traversal, int targetRead, Runnable concede) {
                this.zones = zones;
                original = zones.get(ZoneType.Graveyard);
                assertNotNull(original);
                wrapper = new PlayerZone(ZoneType.Graveyard, player) {
                    @Override public CardCollectionView getCards(boolean filter) {
                        if (!traversalRead(traversal) || ++reads != targetRead) return original.getCards(filter);
                        consumed = true;
                        restore();
                        visited.add(player.getId());
                        if (!conceded) {
                            conceded = true;
                            // The original outer iterator has already yielded this player.
                            // Restore actual zones before normal concession removes another
                            // participant; no rules or zone-cleanup behavior is substituted.
                            slots.forEach(Slot::restore);
                            try {
                                assertEquals(player.getGame().getPlayers().size(), 4);
                                concede.run();
                                assertEquals(player.getGame().getPlayers().size(), 3);
                            } finally {
                                slots.stream().filter(slot -> !slot.consumed).forEach(Slot::install);
                            }
                        }
                        return original.getCards(filter);
                    }
                };
            }

            void install() { zones.put(ZoneType.Graveyard, wrapper); }
            void restore() { zones.put(ZoneType.Graveyard, original); }
        }

        @Override public void close() { slots.forEach(Slot::restore); }
    }

    private static boolean traversalRead(String traversal) {
        if (traversal.equals("static-layer")) {
            return StackWalker.getInstance().walk(frames -> frames.anyMatch(frame ->
                    frame.getClassName().equals(Player.class.getName())
                            && frame.getMethodName().equals("afterStaticAbilityLayer")));
        }
        return StackWalker.getInstance().walk(frames -> {
            var callers = frames.dropWhile(frame -> !frame.getClassName().equals(Player.class.getName())
                            || !frame.getMethodName().equals("getCardsIn"))
                    .dropWhile(frame -> frame.getClassName().equals(Player.class.getName())
                            && frame.getMethodName().equals("getCardsIn"))
                    .limit(2).toList();
            return callers.size() == 2 && callers.get(0).getClassName().equals(GameAction.class.getName())
                    && callers.get(0).getMethodName().equals("checkStateEffectsOwned")
                    && callers.get(1).getClassName().equals(GameAction.class.getName())
                    && callers.get(1).getMethodName().equals("checkStateEffects");
        });
    }

    private static void concede(RulesScenario scenario, int seat, String sessionId) {
        var request = new JsonObject();
        request.addProperty("sessionId", sessionId);
        scenario.table.seat(seat).concede(request);
    }

    private static void assertPrivateZones(JsonObject state) {
        int viewerId = state.get("viewerId").getAsInt();
        for (var entry : state.getAsJsonArray("players")) {
            var player = entry.getAsJsonObject();
            assertTrue(zone(player, "Library").getAsJsonArray("cards").isEmpty());
            if (player.get("id").getAsInt() != viewerId) {
                assertTrue(zone(player, "Hand").getAsJsonArray("cards").isEmpty());
            }
        }
    }
}
