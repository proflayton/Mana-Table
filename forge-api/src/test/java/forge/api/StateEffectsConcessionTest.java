package forge.api;

import com.google.common.eventbus.Subscribe;
import com.google.gson.Gson;
import com.google.gson.JsonObject;
import forge.game.GameAction;
import forge.game.card.CardCollectionView;
import forge.game.event.GameEventGameFinished;
import forge.game.player.Player;
import forge.game.zone.PlayerZone;
import forge.game.zone.ZoneType;
import org.testng.annotations.DataProvider;
import org.testng.annotations.Test;

import java.lang.reflect.Field;
import java.nio.file.Files;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

public class StateEffectsConcessionTest {
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
                assertTrue(finished.await(5, TimeUnit.SECONDS), "The remaining game must finish and release its game thread");
                for (int seat = 0; seat < 4; seat++) {
                    var snapshot = scenario.table.seat(seat).state();
                    assertNull(snapshot.get("error"));
                    assertNull(snapshot.get("prompt"));
                    var state = new Gson().toJsonTree(snapshot).getAsJsonObject();
                    assertEquals(text(state, "status"), "finished");
                    assertEquals(text(state, "result"), seat == 0 ? "Victory" : "Defeat");
                    assertEquals(state.getAsJsonArray("players").asList().stream().map(player -> player.getAsJsonObject())
                            .filter(player -> !player.get("eliminated").getAsBoolean()).count(), 1L);
                }
            } finally {
                scenario.table.game.unsubscribeFromEvents(listener);
            }
        }
    }

    /** Observes either the direct per-player Graveyard reads in checkStateEffects,
     * or the real devotion query inside afterStaticAbilityLayer. The first direct
     * read belongs to non-battlefield checks; the second to Commander checks.
     * Unrelated queries and nested concession cleanup are not counted. */
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
        return StackWalker.getInstance().walk(frames -> frames
                .dropWhile(frame -> !frame.getClassName().equals(Player.class.getName())
                        || !frame.getMethodName().equals("getCardsIn"))
                .dropWhile(frame -> frame.getClassName().equals(Player.class.getName())
                        && frame.getMethodName().equals("getCardsIn"))
                .findFirst().map(frame -> frame.getClassName().equals(GameAction.class.getName())
                        && frame.getMethodName().equals("checkStateEffects")).orElse(false));
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
