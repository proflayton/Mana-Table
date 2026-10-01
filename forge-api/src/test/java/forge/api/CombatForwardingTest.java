package forge.api;

import forge.game.event.GameEvent;
import forge.game.event.GameEventCombatUpdate;
import forge.gui.control.GameEventForwarder;
import forge.gui.interfaces.IGuiGame;
import org.testng.annotations.Test;

import java.lang.reflect.Proxy;
import java.util.ArrayList;
import java.util.List;

import static org.testng.Assert.*;

public class CombatForwardingTest {
    @Test(timeOut = 15000) public void concurrentInputFlushesAndZoneQueriesNeverLoseEvents() throws Exception {
        forge.util.Localizer.getInstance().initialize("en-US", "../forge-gui/res/languages");
        forge.util.Lang.createInstance("en-US");
        var delivered = new java.util.concurrent.atomic.AtomicInteger();
        var gui = (IGuiGame) Proxy.newProxyInstance(IGuiGame.class.getClassLoader(), new Class<?>[]{IGuiGame.class}, (proxy, method, args) -> {
            if (method.getName().equals("handleGameEvents")) delivered.addAndGet(((List<?>) args[0]).size());
            return null;
        });
        var forwarder = new GameEventForwarder(gui);
        var start = new java.util.concurrent.CountDownLatch(1);
        var workers = java.util.concurrent.Executors.newFixedThreadPool(3);
        final int count = 100000;
        try {
            var writer = workers.submit(() -> {
                start.await();
                for (int i = 0; i < count; i++) forwarder.receiveGameEvent(new forge.game.event.GameEventCombatChanged());
                return null;
            });
            var query = workers.submit(() -> {
                start.await();
                for (int i = 0; i < count; i++) forwarder.hasPendingZoneChange();
                return null;
            });
            var flush = workers.submit(() -> {
                start.await();
                for (int i = 0; i < count; i++) forwarder.flush();
                return null;
            });
            start.countDown();
            for (var worker : List.of(writer, query, flush)) worker.get(10, java.util.concurrent.TimeUnit.SECONDS);
            forwarder.flush();
            assertEquals(delivered.get(), count, "Every queued event must be delivered exactly once");
            assertFalse(forwarder.hasPendingEvents());
        } finally { workers.shutdownNow(); }
    }

    @Test public void keepsBlockedStatusAfterTheLastBlockerLeaves() throws Exception {
        var attacker = new forge.game.card.CardView(1, null);
        var unblocked = new forge.game.card.CardView(2, null);
        var defender = new forge.game.player.PlayerView(3, null);
        var combat = new forge.game.combat.CombatView(null);
        combat.addAttackingBand(List.of(attacker), defender, List.of(), List.of());
        combat.addAttackingBand(List.of(unblocked), defender, null, List.of());
        assertTrue(combat.isBlocked(attacker));
        assertFalse(combat.isBlocked(unblocked));
        assertTrue(combat.getBlockers(attacker).isEmpty());
        var bytes = new java.io.ByteArrayOutputStream();
        try (var out = new java.io.ObjectOutputStream(bytes)) { out.writeObject(combat); }
        try (var in = new java.io.ObjectInputStream(new java.io.ByteArrayInputStream(bytes.toByteArray()))) {
            var remote = (forge.game.combat.CombatView) in.readObject();
            assertTrue(remote.isBlocked(attacker));
            assertFalse(remote.isBlocked(unblocked));
        }
    }

    @Test public void publishesEachAssignmentWithoutWaitingForAnotherEventOrInput() {
        forge.util.Localizer.getInstance().initialize("en-US", "../forge-gui/res/languages");
        forge.util.Lang.createInstance("en-US");
        var batches = new ArrayList<List<GameEvent>>();
        var gui = (IGuiGame) Proxy.newProxyInstance(IGuiGame.class.getClassLoader(), new Class<?>[]{IGuiGame.class}, (proxy, method, args) -> {
            if (method.getName().equals("handleGameEvents")) batches.add(new ArrayList<>((List<GameEvent>) args[0]));
            return null;
        });
        var forwarder = new GameEventForwarder(gui);
        var assignment = new GameEventCombatUpdate(List.of(), List.of());
        forwarder.receiveGameEvent(assignment);
        assertEquals(batches, List.of(List.of(assignment)));
        assertFalse(forwarder.hasPendingEvents());
        forwarder.receiveGameEvent(assignment);
        assertEquals(batches.size(), 2, "Same-input edits must reach other players immediately");
    }
}
