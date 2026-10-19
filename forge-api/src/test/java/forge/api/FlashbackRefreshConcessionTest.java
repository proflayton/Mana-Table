package forge.api;

import com.google.common.eventbus.Subscribe;
import com.google.gson.Gson;
import com.google.gson.JsonObject;
import forge.game.event.GameEventGameFinished;
import forge.game.event.GameEventZone;
import forge.game.phase.PhaseHandler;
import forge.game.zone.ZoneType;
import org.testng.annotations.Test;

import java.util.List;
import java.util.Map;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicReference;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

public class FlashbackRefreshConcessionTest {
    @Test(timeOut = 90000)
    public void aConcessionDuringFlashbackRefreshDoesNotInvalidateThePlayerTraversal() throws Exception {
        // This real continuous effect makes the phase's affected-card set nonempty,
        // so its normal state-based-action path refreshes each player's Flashback view.
        try (var scenario = new RulesScenario("flashback-refresh-concession",
                Map.of(0, List.of("Glorious Anthem", "Grizzly Bears")))) {
            var opening = scenario.until(input -> text(input.prompt(), "inputType").equals("InputSelectEntitiesFromList")
                    && input.prompt().getAsJsonArray("playerChoices").size() == 4);
            var initialPlayers = scenario.table.game.getPlayers().stream().map(player -> player.getId()).toList();
            int departedId = initialPlayers.get(2);
            String sessionId = text(opening.state(), "id");
            var departed = new AtomicBoolean();
            var capturedRefresh = new AtomicBoolean();
            var refreshed = new CopyOnWriteArrayList<Integer>();
            var callbackFailure = new AtomicReference<Throwable>();
            var finished = new CountDownLatch(1);
            var listener = new Object() {
                @Subscribe public void zoneChanged(GameEventZone event) {
                    if (event.zoneType() != ZoneType.Flashback || capturedRefresh.get() || !insidePhaseRefresh()) return;
                    try {
                        refreshed.add(event.player().getId());
                        if (departed.compareAndSet(false, true)) {
                            // Runs synchronously inside the first refresh event, after
                            // the old live-list iterator has already yielded a player.
                            // The ordinary concession removes another player before
                            // that iterator asks for its next element: no sleep or race.
                            assertEquals(scenario.table.game.getPlayers().size(), 4);
                            concede(scenario, 2, sessionId);
                            assertEquals(scenario.table.game.getPlayers().size(), 3);
                            assertFalse(scenario.table.game.getPlayers().stream()
                                    .anyMatch(player -> player.getId() == departedId));
                        }
                        if (refreshed.size() == initialPlayers.size()) capturedRefresh.set(true);
                    } catch (Throwable failure) {
                        // Guava logs subscriber failures instead of propagating them.
                        // Keep the original failure for an explicit test-thread assertion.
                        callbackFailure.compareAndSet(null, failure);
                    }
                }

                @Subscribe public void gameFinished(GameEventGameFinished event) { finished.countDown(); }
            };
            scenario.table.game.subscribeToEvents(listener);
            try {
                assertTrue(scenario.answer(opening, Map.of("action", "player", "playerId", initialPlayers.get(0))));
                var main = scenario.until(input -> input.seat() == 0
                        && text(input.state(), "phaseKey").equals("MAIN1")
                        && text(input.prompt(), "inputType").equals("InputPassPriority"));
                assertNull(callbackFailure.get(), "The deterministic concession callback must succeed");
                assertTrue(departed.get(), "The real PhaseHandler Flashback refresh must invoke the concession hook");
                assertTrue(capturedRefresh.get(), "The interrupted refresh must still visit every original player");
                assertEquals(refreshed, initialPlayers,
                        "A stable traversal updates every original player once, including all surviving players");
                assertEquals(scenario.table.game.getPlayers().size(), 3);
                assertTrue(player(main.state(), departedId).get("eliminated").getAsBoolean());
                assertTrue(scenario.state(2).get("prompt").isJsonNull(), "The departed player cannot retain a decision");
                var bears = zone(viewer(main.state()), "Battlefield").getAsJsonArray("cards").asList().stream()
                        .map(card -> card.getAsJsonObject()).filter(card -> text(card, "name").equals("Grizzly Bears"))
                        .findFirst().orElseThrow();
                assertEquals(bears.get("power").getAsInt(), 3, "The real continuous effect is active");
                assertEquals(bears.get("toughness").getAsInt(), 3);

                var forest = zone(viewer(main.state()), "Hand").getAsJsonArray("cards").asList().stream()
                        .map(card -> card.getAsJsonObject()).filter(card -> text(card, "name").equals("Forest")
                                && card.get("selectable").getAsBoolean()).findFirst().orElseThrow();
                assertTrue(scenario.answer(main, Map.of("action", "card", "key", text(forest, "key"))));
                var afterLand = scenario.until(input -> input.seat() == 0
                        && text(input.state(), "phaseKey").equals("MAIN1")
                        && text(input.prompt(), "inputType").equals("InputPassPriority")
                        && zone(viewer(input.state()), "Battlefield").getAsJsonArray("cards").asList().stream()
                        .map(card -> card.getAsJsonObject()).anyMatch(card -> text(card, "name").equals("Forest")));
                assertNotEquals(text(afterLand.prompt(), "id"), text(main.prompt(), "id"),
                        "The surviving player must execute a normal scoped action after the refresh");
                for (int seat = 0; seat < 4; seat++) assertPrivateZones(scenario.state(seat));

                concede(scenario, 1, sessionId);
                concede(scenario, 3, sessionId);
                assertTrue(finished.await(5, TimeUnit.SECONDS), "The surviving game must finish and release its game thread");
                for (int seat = 0; seat < 4; seat++) {
                    var snapshot = scenario.table.seat(seat).state();
                    assertNull(snapshot.get("error"));
                    assertNull(snapshot.get("prompt"));
                    var state = new Gson().toJsonTree(snapshot).getAsJsonObject();
                    assertEquals(text(state, "status"), "finished", state.toString());
                    assertEquals(text(state, "result"), seat == 0 ? "Victory" : "Defeat");
                    assertEquals(state.getAsJsonArray("players").asList().stream()
                            .map(player -> player.getAsJsonObject()).filter(player -> !player.get("eliminated").getAsBoolean()).count(), 1L);
                }
            } finally {
                scenario.table.game.unsubscribeFromEvents(listener);
            }
        }
    }

    private static boolean insidePhaseRefresh() {
        return StackWalker.getInstance().walk(frames -> frames.anyMatch(frame ->
                frame.getClassName().equals(PhaseHandler.class.getName()) && frame.getMethodName().equals("checkStateBasedEffects")));
    }

    private static void concede(RulesScenario scenario, int seat, String sessionId) {
        var request = new JsonObject();
        request.addProperty("sessionId", sessionId);
        scenario.table.seat(seat).concede(request);
    }

    private static void assertPrivateZones(JsonObject state) {
        assertNotEquals(text(state, "status"), "error", state.toString());
        int viewerId = state.get("viewerId").getAsInt();
        for (var entry : state.getAsJsonArray("players")) {
            var player = entry.getAsJsonObject();
            assertTrue(zone(player, "Library").getAsJsonArray("cards").isEmpty());
            if (player.get("id").getAsInt() != viewerId) {
                assertTrue(zone(player, "Hand").getAsJsonArray("cards").isEmpty(), "Another player's hand stays private");
            }
        }
    }
}
