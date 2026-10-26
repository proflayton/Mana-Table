package forge.api;

import com.google.common.eventbus.Subscribe;
import forge.game.card.CardView;
import forge.game.event.GameEventCardChangeZone;
import forge.game.event.GameEventShuffle;
import forge.game.spellability.SpellAbility;
import forge.game.zone.ZoneType;
import java.util.*;

/** Opaque occurrence identities, shared across projections and revoked on concealment. */
final class MatchIdentities {
    private final Map<CardView, String> ids = new HashMap<>();
    private final Map<CardView, String> concealed = new HashMap<>();
    private final Map<String, String> effects = new LinkedHashMap<>();
    private final Map<SpellAbility, String> pendingEffects = new WeakHashMap<>();
    synchronized String id(CardView card) { return ids.computeIfAbsent(card, ignored -> UUID.randomUUID().toString()); }
    synchronized void forget(CardView card, String priorId) { ids.remove(card, priorId); }
    synchronized String concealed(CardView card) { return concealed.computeIfAbsent(card, ignored -> UUID.randomUUID().toString()); }
    // The engine owns these abilities through targeting and stacking. Weak keys
    // preserve the same opaque identity without retaining finished abilities.
    synchronized String pendingEffect(SpellAbility ability) {
        return pendingEffects.computeIfAbsent(ability, ignored -> UUID.randomUUID().toString());
    }
    synchronized String effect(String instanceId) {
        String id = effects.computeIfAbsent(instanceId, ignored -> UUID.randomUUID().toString());
        // The feed retains 120 entries. Keep a larger bounded correlation window,
        // without publishing engine counters or retaining live stack objects.
        if (effects.size() > 512) effects.remove(effects.keySet().iterator().next());
        return id;
    }
    @Subscribe public synchronized void moved(GameEventCardChangeZone event) {
        concealed.remove(event.card());
        var to = event.to() == null ? null : event.to().zoneType();
        var from = event.from() == null ? null : event.from().zoneType();
        // Drawing an already-visible top card preserves the authorized viewer's
        // occurrence. Library entry/shuffle and other concealed moves revoke it.
        if (to == ZoneType.Library || to == ZoneType.Hand && from != ZoneType.Library || event.card().isFaceDown()) ids.remove(event.card());
    }
    @Subscribe public synchronized void shuffled(GameEventShuffle event) {
        ids.keySet().removeIf(card -> card.getZone() == ZoneType.Library && event.player().equals(card.getController()));
    }
}
