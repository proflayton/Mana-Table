package forge.gui.control;

import com.google.common.eventbus.Subscribe;
import forge.game.card.CardView;
import forge.game.event.GameEvent;
import forge.game.event.GameEventCardChangeZone;
import forge.game.event.GameEventCombatUpdate;
import forge.game.event.GameEventPlayerPriority;
import forge.game.event.GameEventTurnEnded;
import forge.gui.interfaces.IGuiGame;

import java.util.ArrayList;
import java.util.List;
import java.util.Observable;
import java.util.Observer;
import java.util.stream.Collectors;

/**
 * Buffers game events and flushes them to the GUI in batches.
 *
 * <p>Flush triggers:
 * <ul>
 *   <li>Priority: a player receives it once the game has settled, so a batch holds whole actions
 *       and what one player did is sent before anyone chooses what to do next. A pass with nothing
 *       but redraw hints since the last batch sends nothing</li>
 *   <li>End of turn: normally no player receives priority in the cleanup step, so what happened in
 *       it is sent before the turn ends and never travels with the next turn's start</li>
 *   <li>Input queue change: registered as {@link Observer} on player InputQueues,
 *       ensuring events are delivered before the game thread blocks for input</li>
 *   <li>Sync points: explicit {@link #flush()} from {@code flushPendingEvents()}</li>
 *   <li>Combat assignments: publish immediately while players keep the same input</li>
 * </ul>
 *
 * <p>No daemon thread. Game events and input callbacks can access this buffer
 * from different threads. Queue operations are atomic; GUI dispatch happens
 * outside the queue lock so callbacks can query or enqueue further events.
 */
public class GameEventForwarder implements Observer {
    private static final long FLUSH_INTERVAL_NS = 500_000_000L;
    private static final int FLUSH_SIZE_THRESHOLD = 50;

    private final IGuiGame gui;
    private final List<GameEvent> pendingEvents = new ArrayList<>();
    private long lastFlushTime = System.nanoTime();
    /** Whether anything but a redraw hint is waiting, which is what makes a pass of priority worth a batch. */
    private boolean somethingHappened;

    public GameEventForwarder(IGuiGame gui) {
        this.gui = gui;
    }

    @Subscribe
    public void receiveGameEvent(GameEvent ev) {
        final boolean flushPreviousTurn;
        synchronized (pendingEvents) {
            flushPreviousTurn = ev instanceof GameEventTurnEnded && somethingHappened;
        }
        if (flushPreviousTurn) flush();
        boolean shouldFlush;
        synchronized (pendingEvents) {
            pendingEvents.add(ev);
            if (!(ev instanceof GameEventPlayerPriority)) somethingHappened |= !ev.isRedrawHint();
            shouldFlush = ev instanceof GameEventCombatUpdate
                    || ev instanceof GameEventPlayerPriority && somethingHappened
                    || pendingEvents.size() >= FLUSH_SIZE_THRESHOLD
                    || (System.nanoTime() - lastFlushTime) >= FLUSH_INTERVAL_NS;
        }
        // There may be no next event while a player reviews an assignment.
        // Every seat must see that attack/block without waiting for confirmation.
        if (shouldFlush) {
            flush();
        }
    }

    public void flush() {
        final List<GameEvent> batch;
        synchronized (pendingEvents) {
            if (pendingEvents.isEmpty()) return;
            batch = new ArrayList<>(pendingEvents);
            pendingEvents.clear();
            somethingHappened = false;
            lastFlushTime = System.nanoTime();
        }
        gui.handleGameEvents(batch);
    }

    public boolean hasPendingEvents() {
        synchronized (pendingEvents) {
            return !pendingEvents.isEmpty();
        }
    }
    public boolean hasPendingZoneChange(Object... args) {
        final List<Integer> zoneChangers;
        synchronized (pendingEvents) {
            zoneChangers = pendingEvents.stream().filter(GameEventCardChangeZone.class::isInstance).map(ev -> ((GameEventCardChangeZone) ev).card().getId()).collect(Collectors.toList());
        }
        if (zoneChangers.isEmpty()) {
            return false;
        }
        for (Object obj : args) {
            if (obj instanceof CardView cv && zoneChangers.contains(cv.getId())) {
                return true;
            }
            if (obj instanceof Iterable<?> it) {
                for (Object e : it) {
                    if (e instanceof CardView cv && zoneChangers.contains(cv.getId())) {
                        return true;
                    }
                }
            }
        }
        return false;
    }

    /**
     * Called when an InputQueue changes (setInput/removeInput/clearInputs).
     * Flushes any pending events on the game thread before it blocks for input.
     */
    @Override
    public void update(Observable o, Object arg) {
        flush();
    }

}
