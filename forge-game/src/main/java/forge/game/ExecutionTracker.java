package forge.game;

import forge.game.card.CardView;
import forge.game.event.Event;
import forge.game.event.GameEvent;
import forge.game.spellability.SpellAbility;
import forge.game.trigger.WrappedAbility;

import java.util.*;

/** Rules-thread occurrences. These identities describe execution, never permission
 * to answer an input. An ability template is reusable; an occurrence is not. */
public final class ExecutionTracker {
    public enum Step {
        CHOICE("choice"), COST("cost"), STACK("stack"), RESOLUTION("resolution");
        private final String wire;
        Step(String wire) { this.wire = wire; }
    }
    public enum Outcome {
        RESOLVED("resolved"), DECLINED("declined"), CANCELLED("cancelled"), COUNTERED("countered"), FAILED("failed");
        private final String wire;
        Outcome(String wire) { this.wire = wire; }
    }
    public record Reference(String id, String step) { }
    public record Occurrence(String id, String parentId, String budgetRootId, String kind,
                             Integer actorPlayerId, CardView source, String sourceName,
                             String step, String outcome) { }
    public record Transition(long sequence, Occurrence occurrence) implements Event { }
    /** Synchronous event-time cause, copied before any further rules work. */
    public record Committed(GameEvent event, Reference execution) implements Event { }
    private final Game game;
    private final Map<String, Occurrence> occurrences = new LinkedHashMap<>();
    private final Deque<Transition> history = new ArrayDeque<>();
    private final Deque<String> executing = new ArrayDeque<>();
    private long sequence;

    ExecutionTracker(Game game) { this.game = game; }

    /** A trigger can be announced before its controller starts paying costs. */
    public String prepare(SpellAbility ability) { return prepare(ability, currentId()); }
    public String prepare(SpellAbility ability, String parentId) {
        Occurrence existing = occurrence(ability);
        if (existing != null && !existing.step().equals("complete")) return existing.id();
        Occurrence parent = occurrences.get(parentId);
        String id = UUID.randomUUID().toString();
        String kind = ability.isManaAbility() ? "mana" : ability.isTrigger() ? "trigger"
                : ability.isSpell() ? "spell" : ability.isActivatedAbility() ? "activation" : "rules";
        var actor = ability.getActivatingPlayer();
        var source = ability.getHostCard();
        Occurrence occurrence = new Occurrence(id, parent == null ? null : parent.id(),
                parent == null || kind.equals("trigger") ? id : parent.budgetRootId(), kind, actor == null ? null : actor.getId(),
                CardView.get(source), source == null ? null : source.getName(), "preparation", null);
        bind(ability, id);
        publish(occurrence);
        return id;
    }

    /** Continuation copies (cost alternatives, stack snapshots) retain occurrence.
     * Real spell/ability copies do not call this and get their own preparation. */
    public void continueExecution(SpellAbility from, SpellAbility to) {
        if (from != null && to != null && from.getExecutionId() != null) bind(to, from.getExecutionId());
    }

    private void bind(SpellAbility ability, String id) {
        ability.setExecutionId(id);
        if (ability instanceof WrappedAbility wrapped) bind(wrapped.getWrappedAbility(), id);
        for (var sub = ability.getSubAbility(); sub != null; sub = sub.getSubAbility()) sub.setExecutionId(id);
    }

    public Scope enter(SpellAbility ability) {
        if (occurrence(ability) == null) prepare(ability);
        return enterCause(ability.getExecutionId());
    }

    /** Restore an explicitly captured deferred cause, never the latest stack entry. */
    public Scope enterCause(String id) {
        if (id != null && occurrences.containsKey(id)) executing.push(id);
        else id = null;
        return new Scope(id);
    }

    public final class Scope implements AutoCloseable {
        private final String id;
        private boolean closed;
        private Scope(String id) { this.id = id; }
        @Override public void close() {
            if (closed) return;
            if (id == null) { closed = true; return; }
            if (!Objects.equals(executing.peek(), id)) throw new IllegalStateException("Execution scopes closed out of order");
            executing.pop(); closed = true;
        }
    }

    public String currentId() { return executing.peek(); }
    public Reference current() {
        Occurrence occurrence = occurrences.get(currentId());
        return occurrence == null ? null : new Reference(occurrence.id(), occurrence.step());
    }
    public Reference reference(SpellAbility ability) {
        Occurrence occurrence = occurrence(ability);
        return occurrence == null ? null : new Reference(occurrence.id(), occurrence.step());
    }
    public Occurrence occurrence(SpellAbility ability) {
        return ability == null ? null : occurrences.get(ability.getExecutionId());
    }
    public Occurrence occurrence(String id) { return occurrences.get(id); }

    public void step(SpellAbility ability, Step step) { transition(ability, step.wire, null); }
    public void complete(SpellAbility ability, Outcome outcome) { transition(ability, "complete", outcome.wire); }
    private void transition(SpellAbility ability, String step, String outcome) {
        Occurrence prior = occurrence(ability);
        if (prior == null || prior.step().equals("complete")) return;
        if (prior.step().equals(step) && Objects.equals(prior.outcome(), outcome)) return;
        var source = ability.getHostCard();
        publish(new Occurrence(prior.id(), prior.parentId(), prior.budgetRootId(), prior.kind(),
                prior.actorPlayerId(), CardView.get(source), source == null ? prior.sourceName() : source.getName(), step, outcome));
    }

    private void publish(Occurrence occurrence) {
        occurrences.put(occurrence.id(), occurrence);
        Transition transition = new Transition(++sequence, occurrence);
        history.addLast(transition);
        while (history.size() > 512) history.removeFirst();
        Set<String> retained = new HashSet<>(executing);
        for (var item : history) retained.add(item.occurrence().id());
        for (var item : occurrences.values()) if (!item.step().equals("complete")) retained.add(item.id());
        // A completed caller may still own a nested stack copy's allowance.
        // Retain those exact roots without recursively retaining an unbounded
        // chain of old triggered-action causes.
        Set<String> roots = new HashSet<>();
        for (String id : retained) {
            var item = occurrences.get(id);
            if (item != null) roots.add(item.budgetRootId());
        }
        retained.addAll(roots);
        occurrences.entrySet().removeIf(item -> item.getValue().step().equals("complete") && !retained.contains(item.getKey()));
        game.fireEvent(transition);
    }

    public long firstSequence() { return history.isEmpty() ? sequence + 1 : history.peekFirst().sequence(); }
    public long lastSequence() { return sequence; }
    public List<Occurrence> occurrences() { return List.copyOf(occurrences.values()); }
}
