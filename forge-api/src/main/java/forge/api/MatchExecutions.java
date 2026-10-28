package forge.api;

import com.google.common.eventbus.Subscribe;
import forge.game.ExecutionTracker;
import forge.game.card.CardView;
import forge.game.player.PlayerView;
import forge.game.zone.ZoneType;

import java.util.*;

/** Audience admission is performed at rules emission time. Historical engine
 * views and private execution identities never cross this boundary. */
final class MatchExecutions {
    record Occurrence(String id, String parentId, String budgetRootId, String kind, Integer actorPlayerId,
                      String sourceCardId, String sourceName, String step, String outcome, String ruleKind, String ruleResult) { }
    record Transition(long sequence, String executionId, String step, String outcome, String ruleResult) { }
    record Frame(long firstSequence, long lastSequence, List<Occurrence> occurrences, List<Transition> transitions) { }
    private final ExecutionTracker rules;
    private final PlayerView viewer;
    private final MatchActivity activity;
    private final Map<String, Occurrence> admitted = new LinkedHashMap<>();
    // Activity can be copied by a network reader while the rules subscriber is
    // admitting a source. Never take this monitor from inside Activity's lock.
    private volatile Map<String, Occurrence> readable = Map.of();
    private final Deque<Transition> transitions = new ArrayDeque<>();
    private Set<String> pinnedStack = Set.of();
    private volatile Frame latest = new Frame(1, 0, List.of(), List.of());

    MatchExecutions(ExecutionTracker rules, PlayerView viewer, MatchActivity activity) {
        this.rules = rules; this.viewer = viewer; this.activity = activity;
    }

    @Subscribe public synchronized void changed(ExecutionTracker.Transition transition) {
        var occurrence = transition.occurrence();
        CardView card = occurrence.source();
        boolean own = Objects.equals(occurrence.actorPlayerId(), viewer.getId());
        boolean publicZone = card != null && card.getZone() != null && switch (card.getZone()) {
            case Battlefield, Stack, Graveyard, Exile, Command -> true;
            default -> false;
        };
        boolean visible = card != null && !card.isFaceDown() && card.canBeShownTo(viewer)
                && (card.getZone() != ZoneType.Hand || own);
        // Preparation of a hidden spell stays private even if this observer
        // happens to know its printed name from an unrelated previous event.
        if (!own && !publicZone && !admitted.containsKey(occurrence.id())) { copyFrame(); return; }
        String cardId = visible ? activity.visualId(card) : null;
        String name = visible ? occurrence.sourceName() : null;
        admitted.put(occurrence.id(), new Occurrence(occurrence.id(), occurrence.parentId(), occurrence.budgetRootId(),
                occurrence.kind(), occurrence.actorPlayerId(), cardId, name, occurrence.step(), occurrence.outcome(),
                occurrence.ruleKind(), occurrence.ruleResult()));
        transitions.addLast(new Transition(transition.sequence(), occurrence.id(), occurrence.step(), occurrence.outcome(), occurrence.ruleResult()));
        copyFrame();
    }

    private void trim() {
        long first = rules.firstSequence();
        while (!transitions.isEmpty() && transitions.peekFirst().sequence() < first) transitions.removeFirst();
        Set<String> retained = new HashSet<>();
        for (var occurrence : rules.occurrences()) retained.add(occurrence.id());
        retained.addAll(pinnedStack);
        for (String id : List.copyOf(retained)) {
            var occurrence = admitted.get(id);
            if (occurrence != null) retained.add(occurrence.budgetRootId());
        }
        admitted.keySet().removeIf(id -> !retained.contains(id));
        readable = Map.copyOf(admitted);
    }

    ExecutionTracker.Reference reference(ExecutionTracker.Reference reference) {
        if (reference == null || !readable.containsKey(reference.id())) return null;
        return reference;
    }

    /** Some rules actions intentionally have no player or source. Their mere
     * execution is not public evidence: admit only the exact occurrence which
     * is now committing an audience-visible result. Earlier filtered sequence
     * numbers stay filtered; no retrospective transition is manufactured. */
    synchronized ExecutionTracker.Reference reference(ExecutionTracker.Reference reference, forge.game.event.GameEvent event) {
        if (reference == null) return null;
        if (!readable.containsKey(reference.id())) {
            var occurrence = rules.occurrence(reference.id());
            if (occurrence == null || !"rules".equals(occurrence.kind()) || !"stateBased".equals(occurrence.ruleKind())
                    || occurrence.source() != null || occurrence.actorPlayerId() != null || !activity.publicCommitted(event)) return null;
            admitted.put(occurrence.id(), new Occurrence(occurrence.id(), occurrence.parentId(), occurrence.budgetRootId(),
                    occurrence.kind(), null, null, null, occurrence.step(), occurrence.outcome(), occurrence.ruleKind(), occurrence.ruleResult()));
            copyFrame();
        }
        return reference(reference);
    }

    /** Only the session currently publishing its owned required input may call
     * this path. Other audiences still need an actual public committed result. */
    synchronized ExecutionTracker.Reference referenceDecision(ExecutionTracker.Reference reference) {
        if (reference == null) return null;
        if (!readable.containsKey(reference.id())) {
            var occurrence = rules.occurrence(reference.id());
            if (occurrence == null || !"rules".equals(occurrence.kind()) || !"stateBased".equals(occurrence.ruleKind())
                    || occurrence.source() != null || occurrence.actorPlayerId() != null || occurrence.step().equals("complete")) return null;
            admitted.put(occurrence.id(), new Occurrence(occurrence.id(), occurrence.parentId(), occurrence.budgetRootId(),
                    occurrence.kind(), null, null, null, occurrence.step(), occurrence.outcome(), occurrence.ruleKind(), occurrence.ruleResult()));
            copyFrame();
        }
        return reference(reference);
    }
    ExecutionTracker.Reference reference(String id) {
        var occurrence = id == null ? null : readable.get(id);
        return occurrence == null ? null : new ExecutionTracker.Reference(id, occurrence.step());
    }

    /** A resolving observation can retain the preceding immutable board. Keep
     * its stack references valid until the next rules-owned board publication. */
    synchronized void pinStack(Collection<String> ids) {
        pinnedStack = Set.copyOf(ids);
        copyFrame();
    }

    private void copyFrame() {
        trim();
        List<Occurrence> occurrences = new ArrayList<>();
        for (var occurrence : admitted.values()) {
            String parent = admitted.containsKey(occurrence.parentId()) ? occurrence.parentId() : null;
            String root = admitted.containsKey(occurrence.budgetRootId()) ? occurrence.budgetRootId() : occurrence.id();
            occurrences.add(new Occurrence(occurrence.id(), parent, root, occurrence.kind(), occurrence.actorPlayerId(),
                    occurrence.sourceCardId(), occurrence.sourceName(), occurrence.step(), occurrence.outcome(),
                    occurrence.ruleKind(), occurrence.ruleResult()));
        }
        latest = new Frame(rules.firstSequence(), rules.lastSequence(), List.copyOf(occurrences), List.copyOf(transitions));
    }

    /** An event-time value copy; safe for IPC polling without reading rules objects. */
    Frame frame() { return latest; }
}
