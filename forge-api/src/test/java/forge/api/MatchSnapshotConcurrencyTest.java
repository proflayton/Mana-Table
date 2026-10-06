package forge.api;

import forge.StaticData;
import forge.deck.Deck;
import forge.game.spellability.StackItemView;
import forge.trackable.TrackableCollection;
import forge.trackable.TrackableProperty;
import org.testng.annotations.Test;

import java.nio.file.Path;
import java.util.Iterator;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.TimeoutException;
import java.util.concurrent.atomic.AtomicBoolean;

import static org.testng.Assert.*;

public class MatchSnapshotConcurrencyTest {
    @Test(timeOut = 30000)
    public void pollingWaitsForAllSeatProjectionsAndRetainedSnapshotsStayDetached() throws Exception {
        var resources = Path.of("../forge-gui/res");
        if (StaticData.instance() == null) EngineResources.load(resources);
        var seats = List.of(MatchTable.human(new Deck(), "Constructed", "Host"),
                MatchTable.human(new Deck(), "Constructed", "Guest"));
        var copying = new CountDownLatch(1);
        var resume = new CountDownLatch(1);
        var paused = new AtomicBoolean(true);
        var workers = Executors.newFixedThreadPool(2);
        try (var table = new MatchTable("Constructed", seats, resources, Path.of("target/snapshot-regression"))) {
            // No game thread is started: control a real projection boundary directly.
            table.publish();
            var retained = table.seat(0).state();
            var oldLife = firstPlayer(retained).get("life");
            table.game.getPlayers().get(0).getView().set(TrackableProperty.Life, 13);
            table.game.getView().set(TrackableProperty.Stack, new TrackableCollection<StackItemView>() {
                @Override public Iterator<StackItemView> iterator() {
                    if (paused.compareAndSet(false, true)) {
                        copying.countDown();
                        try {
                            if (!resume.await(5, TimeUnit.SECONDS)) throw new AssertionError("Projection did not resume");
                        } catch (InterruptedException e) { Thread.currentThread().interrupt(); throw new AssertionError(e); }
                    }
                    return super.iterator();
                }
            });
            // Installing a tracked collection also traverses it; only pause publication.
            paused.set(false);
            var update = workers.submit(table::publish);
            try {
                assertTrue(copying.await(3, TimeUnit.SECONDS));
                var polling = new CountDownLatch(1);
                var read = workers.submit(() -> { polling.countDown(); return table.testViews(); });
                assertTrue(polling.await(3, TimeUnit.SECONDS));
                assertThrows(TimeoutException.class, () -> read.get(150, TimeUnit.MILLISECONDS));
                assertEquals(firstPlayer(retained).get("life"), oldLife, "Retained snapshots cannot alias engine views");
                resume.countDown();
                update.get(3, TimeUnit.SECONDS);
                var views = read.get(3, TimeUnit.SECONDS);
                assertEquals(views.size(), 2);
                for (var view : views) {
                    assertEquals(firstPlayer(view).get("life"), 13);
                    assertEquals(view.get("boardRevision"), views.get(0).get("boardRevision"));
                    assertTrue(((Number) view.get("boardRevision")).longValue() > ((Number) retained.get("boardRevision")).longValue());
                }
                assertEquals(firstPlayer(retained).get("life"), oldLife);
            } finally { resume.countDown(); }
        } finally {
            resume.countDown();
            workers.shutdownNow();
        }
    }

    private static Map<?, ?> firstPlayer(Map<String, Object> state) {
        return (Map<?, ?>) ((List<?>) state.get("players")).get(0);
    }
}
