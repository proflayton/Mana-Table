package forge.api;

import com.google.common.eventbus.Subscribe;
import forge.game.GameLogEntryType;
import forge.game.event.*;

import java.util.Collection;
import java.util.Collections;
import java.util.IdentityHashMap;
import java.util.Set;
import java.util.concurrent.atomic.AtomicLong;

/** A finite stack consent cannot survive a new action or an intervening choice. */
final class StackInterruption {
    private final AtomicLong version = new AtomicLong();
    private final AtomicLong actionVersion = new AtomicLong();
    private Set<Object> choices = Collections.newSetFromMap(new IdentityHashMap<>());

    long version() { return version.get(); }
    long actionVersion() { return actionVersion.get(); }
    void action() { version.incrementAndGet(); actionVersion.incrementAndGet(); }

    synchronized void observeChoices(Collection<?> current) {
        var next = Collections.newSetFromMap(new IdentityHashMap<Object, Boolean>());
        next.addAll(current);
        if (next.stream().anyMatch(choice -> !choices.contains(choice))) version.incrementAndGet();
        choices = next;
    }

    @Subscribe public void event(GameEvent event) {
        // Mana abilities resolve immediately without creating a stack item.
        // The typed MANA event is emitted by that actual ability path; neither
        // a mana-pool change nor a formatted description identifies an action.
        if (event instanceof GameEventSpellAbilityCast cast) {
            version.incrementAndGet();
            if (!cast.copied() && cast.si() != null && !cast.si().isTrigger()) actionVersion.incrementAndGet();
        } else if (event instanceof GameEventLandPlayed
                || event instanceof GameEventAddLog log && log.type() == GameLogEntryType.MANA
                || event instanceof GameEventSnapshotRestored || event instanceof GameEventGameRestarted) action();
    }
}
