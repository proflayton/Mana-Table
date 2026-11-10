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
                      String sourceCardId, String sourceName, String step, String outcome, String ruleKind, String ruleResult,
                      String stableRouteId) {
        Occurrence(String id, String parentId, String budgetRootId, String kind, Integer actorPlayerId,
                   String sourceCardId, String sourceName, String step, String outcome, String ruleKind, String ruleResult) {
            this(id, parentId, budgetRootId, kind, actorPlayerId, sourceCardId, sourceName, step, outcome, ruleKind, ruleResult, null);
        }
    }
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
    private final Map<Long, Transition> deliveryTransitions = new LinkedHashMap<>();
    private final Set<String> deliveryExecutions = new HashSet<>();
    private boolean deliveryStarted, deliveryOverflow;
    private Set<String> pinnedStack = Set.of();
    private String finiteGrantId = "";
    private Set<String> finiteRoots = Set.of();
    private final Set<String> finiteDescendants = new HashSet<>();
    // A missing originally-private caller is different from admitted evidence
    // lost at capacity. Keep that gap with its referring row so new consent
    // cannot silently reinterpret a truncated lineage as originally parentless.
    private final Set<String> incompleteAncestry = new HashSet<>();
    private boolean finiteOverflow;
    private volatile Frame latest = new Frame(1, 0, List.of(), List.of());
    @FunctionalInterface interface HistorySink { void accept(Occurrence occurrence, Transition transition, boolean publicSource); }
    private HistorySink history = (occurrence, transition, disclosed) -> { };
    void history(HistorySink sink) { history = sink; }

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
                occurrence.ruleKind(), occurrence.ruleResult(), visible ? occurrence.stableRouteId() : null));
        transitions.addLast(new Transition(transition.sequence(), occurrence.id(), occurrence.step(), occurrence.outcome(), occurrence.ruleResult()));
        retainDelivery(transitions.peekLast());
        recordHistory(admitted.get(occurrence.id()), transitions.peekLast(), publicZone && visible);
        retainFinite(occurrence.id());
        copyFrame();
    }

    private void trim() {
        long first = rules.firstSequence();
        while (!transitions.isEmpty() && transitions.peekFirst().sequence() < first) transitions.removeFirst();
        Set<String> retained = new LinkedHashSet<>();
        for (var occurrence : rules.occurrences()) retained.add(occurrence.id());
        retained.addAll(pinnedStack);
        retained.addAll(finiteDescendants);
        retained.addAll(deliveryExecutions);
        var pending = new ArrayDeque<>(retained);
        int ancestors = 0;
        while (!pending.isEmpty()) {
            String id = pending.removeFirst();
            var occurrence = admitted.get(id);
            if (occurrence == null) continue;
            if (incompleteAncestry.contains(id)) finiteOverflow = true;
            // Only close over this audience's earlier admission. Never fetch a
            // private/missing caller from the global rules tracker. Triggers
            // have independent budget roots but still retain their real cause.
            for (String dependency : new String[] { occurrence.parentId(), occurrence.budgetRootId() }) {
                if (dependency == null || retained.contains(dependency) || !admitted.containsKey(dependency)) continue;
                if (ancestors >= MatchActivity.MAXIMUM_FINITE_ENTRIES) {
                    incompleteAncestry.add(id); finiteOverflow = true;
                } else {
                    retained.add(dependency); pending.addLast(dependency); ancestors++;
                }
            }
        }
        admitted.keySet().removeIf(id -> !retained.contains(id));
        incompleteAncestry.retainAll(admitted.keySet());
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
                    occurrence.kind(), null, null, null, occurrence.step(), occurrence.outcome(), occurrence.ruleKind(), occurrence.ruleResult(), null));
            recordHistory(admitted.get(occurrence.id()), null, true);
            retainFinite(occurrence.id());
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
                    occurrence.kind(), null, null, null, occurrence.step(), occurrence.outcome(), occurrence.ruleKind(), occurrence.ruleResult(), null));
            retainFinite(occurrence.id());
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

    synchronized void finiteGrant(String id, Collection<String> roots) {
        if (finiteGrantId.equals(id)) return;
        finiteGrantId = id; finiteRoots = Set.copyOf(roots); finiteDescendants.clear(); finiteOverflow = false;
        for (String candidate : admitted.keySet()) retainFinite(candidate);
        copyFrame();
    }
    synchronized boolean finiteOverflow() { return finiteOverflow; }
    synchronized void beginDelivery() { deliveryStarted = true; }
    /** Gaps within the retained rules range can be audience-private. A cursor
     * before that range cannot certify which public transitions were lost. */
    synchronized void beginDelivery(long through) {
        deliveryStarted = true;
        if (through < rules.firstSequence() - 1) deliveryOverflow = true;
        for (var transition : transitions) if (transition.sequence() > through) retainDelivery(transition);
        copyFrame();
    }
    private void retainDelivery(Transition transition) {
        if (!deliveryStarted) return;
        if (deliveryTransitions.size() >= MatchActivity.MAXIMUM_FINITE_ENTRIES) deliveryOverflow = true;
        else {
            deliveryTransitions.put(transition.sequence(), transition);
            deliveryExecutions.add(transition.executionId());
        }
    }
    synchronized boolean deliveryOverflow() { return deliveryOverflow; }
    synchronized void acknowledgeDelivery(long through) {
        deliveryTransitions.keySet().removeIf(sequence -> sequence <= through);
        deliveryExecutions.clear();
        for (var transition : deliveryTransitions.values()) deliveryExecutions.add(transition.executionId());
        copyFrame();
    }
    synchronized void retireDelivery() {
        deliveryStarted = false; deliveryOverflow = false; deliveryTransitions.clear(); deliveryExecutions.clear(); copyFrame();
    }

    // Called from Activity while its monitor is held: immutable audience copy
    // only. Taking this projection's monitor here would invert admission order.
    boolean finiteDescendant(String id, Set<String> roots) { return descendant(id, roots, readable); }
    private void retainFinite(String id) {
        if (!descendant(id, finiteRoots, admitted) || finiteDescendants.contains(id)) return;
        if (finiteDescendants.size() >= MatchActivity.MAXIMUM_FINITE_ENTRIES) finiteOverflow = true;
        else finiteDescendants.add(id);
    }
    private static boolean descendant(String id, Set<String> roots, Map<String, Occurrence> known) {
        if (id == null || roots.isEmpty()) return false;
        var visited = new HashSet<String>();
        for (var current = known.get(id); current != null && visited.add(current.id()); current = current.parentId() == null ? null : known.get(current.parentId()))
            if (roots.contains(current.id())) return true;
        return false;
    }

    private void copyFrame() {
        trim();
        List<Occurrence> occurrences = new ArrayList<>();
        for (var occurrence : admitted.values()) {
            String parent = admitted.containsKey(occurrence.parentId()) ? occurrence.parentId() : null;
            String root = admitted.containsKey(occurrence.budgetRootId()) ? occurrence.budgetRootId() : occurrence.id();
            occurrences.add(new Occurrence(occurrence.id(), parent, root, occurrence.kind(), occurrence.actorPlayerId(),
                    occurrence.sourceCardId(), occurrence.sourceName(), occurrence.step(), occurrence.outcome(),
                    occurrence.ruleKind(), occurrence.ruleResult(), occurrence.stableRouteId()));
        }
        var delivered = new TreeMap<Long, Transition>(deliveryTransitions);
        for (var transition : transitions) delivered.put(transition.sequence(), transition);
        long first = delivered.isEmpty() ? rules.firstSequence() : Math.min(rules.firstSequence(), delivered.firstKey());
        latest = new Frame(first, rules.lastSequence(), List.copyOf(occurrences), List.copyOf(delivered.values()));
    }

    /** An event-time value copy; safe for IPC polling without reading rules objects. */
    Frame frame() { return latest; }

    private void recordHistory(Occurrence occurrence, Transition transition, boolean publicSource) {
        // Admission of a public child never grants this viewer its hidden
        // caller's identity. Apply the same closure as the live frame.
        String parent = admitted.containsKey(occurrence.parentId()) ? occurrence.parentId() : null;
        String root = admitted.containsKey(occurrence.budgetRootId()) ? occurrence.budgetRootId() : occurrence.id();
        history.accept(new Occurrence(occurrence.id(), parent, root, occurrence.kind(), occurrence.actorPlayerId(),
                occurrence.sourceCardId(), occurrence.sourceName(), occurrence.step(), occurrence.outcome(),
                occurrence.ruleKind(), occurrence.ruleResult(), occurrence.stableRouteId()), transition, publicSource);
    }
}
