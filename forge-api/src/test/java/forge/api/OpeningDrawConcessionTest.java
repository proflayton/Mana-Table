package forge.api;

import com.google.gson.JsonObject;
import forge.game.Game;
import forge.game.GameAction;
import forge.game.card.Card;
import forge.game.card.CardCollectionView;
import forge.game.player.Player;
import forge.game.replacement.ReplacementHandler;
import forge.game.zone.PlayerZone;
import forge.game.zone.ZoneType;
import org.testng.annotations.DataProvider;
import org.testng.annotations.Test;

import java.lang.reflect.Field;
import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Proxy;
import java.util.ArrayList;
import java.util.Iterator;
import java.util.Map;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicReference;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

public class OpeningDrawConcessionTest {
    @DataProvider public Object[][] scanZones() { return new Object[][]{{ZoneType.Library}, {ZoneType.Command}}; }

    @Test(dataProvider = "scanZones", timeOut = 90000)
    public void concessionDuringTheOpeningDrawDoesNotInvalidateItsCardScan(ZoneType scanZone) throws Exception {
        try (var scenario = new RulesScenario("opening-draw-concession-" + scanZone.name(), Map.of())) {
            var choosing = scenario.until(input -> text(input.prompt(), "inputType").equals("InputSelectEntitiesFromList"));
            var departed = scenario.table.game.getRegisteredPlayers().get(2);
            var cards = new ArrayList<Card>();
            departed.getCardsIn(scanZone).forEach(cards::add);
            assertFalse(cards.isEmpty(), "The scan must reach a real card before the concession");
            var request = new JsonObject();
            request.addProperty("sessionId", text(choosing.state(), "id"));
            try (var hook = new ZoneScan(departed, scanZone, () -> scenario.table.seat(2).concede(request))) {
                assertTrue(scenario.answer(choosing, Map.of("action", "player", "playerId", scenario.table.game.getRegisteredPlayers().get(0).getId())));
                var main = scenario.until(input -> input.seat() == 0
                        && text(input.state(), "phaseKey").equals("MAIN1")
                        && text(input.prompt(), "inputType").equals("InputPassPriority"));
                assertTrue(hook.fired.get(), "Concede during the real opening-draw replacement scan, after its first iterator element");
                assertEquals(hook.iterated, cards.size(), "The detached iterator remains usable after the real zone is cleared");
                assertTrue(departed.hasLost());
                assertEquals(scenario.table.game.getPlayers().size(), 3);
                for (Card card : cards) {
                    assertTrue(card.getZone().is(ZoneType.None));
                    assertNull(scenario.table.game.findById(card.getId()), "A copied, ceased card cannot be found in the game");
                }
                for (int seat = 0; seat < 4; seat++) assertPrivateZones(scenario.state(seat));
                assertTrue(scenario.state(2).get("prompt").isJsonNull());
                assertNotNull(main.prompt());
                var visited = new ArrayList<Integer>();
                scenario.table.game.forEachCardInGame(card -> { visited.add(card.getId()); return false; }, true);
                assertEquals(visited.size(), 1, "Visitors still stop at the first rejected card");
            }
        }
    }

    private static void assertPrivateZones(JsonObject state) {
        int viewerId = state.get("viewerId").getAsInt();
        for (var entry : state.getAsJsonArray("players")) {
            var player = entry.getAsJsonObject();
            assertTrue(zone(player, "Library").getAsJsonArray("cards").isEmpty());
            if (player.get("id").getAsInt() != viewerId) assertTrue(zone(player, "Hand").getAsJsonArray("cards").isEmpty());
        }
    }

    private static final class ZoneScan implements AutoCloseable {
        private final Map<ZoneType, PlayerZone> zones;
        private final PlayerZone original;
        private final ZoneType zone;
        private final Runnable concede;
        private final AtomicBoolean fired = new AtomicBoolean();
        private int iterated;

        @SuppressWarnings("unchecked")
        ZoneScan(Player player, ZoneType zone, Runnable concede) throws Exception {
            this.zone = zone;
            this.concede = concede;
            Field field = Player.class.getDeclaredField("zones");
            field.setAccessible(true);
            zones = (Map<ZoneType, PlayerZone>) field.get(player);
            original = zones.get(zone);
            zones.put(zone, new PlayerZone(zone, player) {
                @Override public CardCollectionView getCards(boolean filter) {
                    var cards = original.getCards(filter);
                    if (fired.get() || !openingReplacementScan()) return cards;
                    return (CardCollectionView) Proxy.newProxyInstance(CardCollectionView.class.getClassLoader(),
                            new Class<?>[]{CardCollectionView.class}, (proxy, method, args) -> {
                                Object result;
                                try { result = method.invoke(cards, args); }
                                catch (InvocationTargetException error) { throw error.getCause(); }
                                if (method.getName().equals("iterator")) return observed((Iterator<Card>) result);
                                if (method.getName().equals("threadSafeIterable")) {
                                    var copied = (Iterable<Card>) result;
                                    return (Iterable<Card>) () -> observed(copied.iterator());
                                }
                                return result;
                            });
                }
            });
        }

        private static boolean openingReplacementScan() {
            var frames = StackWalker.getInstance().walk(stack -> stack.toList());
            return frames.stream().anyMatch(frame -> frame.getClassName().equals(Game.class.getName())
                    && frame.getMethodName().equals("forEachCardInGame"))
                    && frames.stream().anyMatch(frame -> frame.getClassName().equals(GameAction.class.getName())
                    && frame.getMethodName().equals("startGame"))
                    && frames.stream().anyMatch(frame -> frame.getClassName().equals(ReplacementHandler.class.getName())
                    && frame.getMethodName().equals("getReplacementList"));
        }

        private Iterator<Card> observed(Iterator<Card> delegate) {
            return new Iterator<>() {
                @Override public boolean hasNext() { return delegate.hasNext(); }
                @Override public Card next() {
                    Card card = delegate.next();
                    iterated++;
                    if (fired.compareAndSet(false, true)) {
                        close(); // Restore the actual zone before genuine loss cleanup mutates it.
                        var failure = new AtomicReference<Throwable>();
                        var worker = new Thread(() -> {
                            try { concede.run(); } catch (Throwable error) { failure.set(error); }
                        }, "opening-draw-api-concede");
                        worker.setDaemon(true);
                        worker.start();
                        try { worker.join(5000); }
                        catch (InterruptedException error) { Thread.currentThread().interrupt(); throw new AssertionError(error); }
                        assertFalse(worker.isAlive(), "The ordinary API concession completes while the game scan is paused");
                        if (failure.get() != null) throw new AssertionError("The real concession failed", failure.get());
                    }
                    return card;
                }
            };
        }

        @Override public void close() { zones.put(zone, original); }
    }
}
