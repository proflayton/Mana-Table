using Mana.Client;
using Mana.Contracts;

namespace Mana.Table.Presentation;

// Local consent to skip quiet windows, never authority to answer a decision.
// Observe before input: acknowledging one displayed response cannot acknowledge
// a cast or trigger which arrives while that response is in flight.
internal sealed class PriorityAttention
{
    private string? game, token, required, observedDecision;
    private int viewer, turn;
    private int? active;
    private long activity;
    private long? interruption;
    private long revision;
    private AcknowledgedResponse? acknowledged;
    internal bool NeedsResponse { get; private set; }

    internal void Observe(GameSnapshot? state)
    {
        if (state == null || game != state.Id || viewer != state.ViewerId) {
            game = state?.Id; viewer = state?.ViewerId ?? 0;
            turn = state?.Turn ?? 0; active = state?.ActivePlayerId;
            activity = LatestActivity(state); interruption = state?.StackInterruptionVersion;
            token = null; required = null; acknowledged = null;
            NeedsResponse = state?.Stack.Length > 0;
        }
        if (state == null) return;
        if (turn != state.Turn || active != state.ActivePlayerId) {
            turn = state.Turn; active = state.ActivePlayerId;
            token = null; required = null; acknowledged = null; NeedsResponse = false;
        }
        var progress = acknowledged?.Observe(state, activity);
        if (progress == ResponseProgress.Interrupted) Interrupt();
        bool expectedResolution = progress is ResponseProgress.Waiting or ResponseProgress.Resolved;
        // The provider classifies routine history; a turn boundary independently
        // retires the old wait. Preserve real actions arriving in that same poll.
        if (!expectedResolution && QuietPriorityPolicy.HasInterruption(state, activity)
            || interruption is { } previous && state.StackInterruptionVersion is { } current && previous != current)
            Interrupt();
        var choice = state.Decision is { Payload: not PriorityPayload } decision ? decision.Id : null;
        if (choice != null && choice != required) Interrupt();
        required = choice;
        if (state.Decision?.Payload is PriorityPayload { QuietYieldToken.Length: > 0 } priority) {
            if (token != null && token != priority.QuietYieldToken && progress != ResponseProgress.Resolved) Interrupt();
            token = priority.QuietYieldToken;
        }
        if (progress == ResponseProgress.Resolved) acknowledged = null;
        activity = Math.Max(activity, LatestActivity(state));
        interruption = state.StackInterruptionVersion;
        revision = state.Revision; observedDecision = state.Decision?.Id;
    }

    // Called only after the session accepts the exact current command. A plain
    // mode change cannot manufacture evidence that a stack response was passed.
    internal void Submitted(GameSnapshot state, DecisionCommand command)
    {
        if (game != state.Id || viewer != state.ViewerId || revision != state.Revision
            || observedDecision != state.Decision?.Id || !GameCommandPermissions.Allows(state, command)) return;
        Resume();
        if (command is PassPriority) acknowledged = AcknowledgedResponse.Capture(state);
    }

    internal void Resume() { acknowledged = null; NeedsResponse = false; }
    internal void Interrupt() { acknowledged = null; NeedsResponse = true; }
    private static long LatestActivity(GameSnapshot? state) => state?.Activity.Select(entry => entry.Id).DefaultIfEmpty().Max() ?? 0;

    private enum ResponseProgress { Waiting, Resolved, Interrupted }

    // Historical causality bounds local attention. It never grants a pass: a
    // resumed quiet continuation still needs the provider's fresh permission.
    private sealed record AcknowledgedResponse(string Game, int Viewer, int Turn, int? Active, string Phase,
        long Fence, long Sequence, StackItem Top, ExecutionOccurrence Execution)
    {
        internal static AcknowledgedResponse? Capture(GameSnapshot state)
        {
            if (state.Stack.IsDefaultOrEmpty || state.StackInterruptionVersion is not >= 0
                || state.Executions is not { } frame || state.Stack[0] is not { Id.Length: > 0, ExecutionId.Length: > 0 } top
                || ExecutionFrames.Find(state, top.ExecutionId) is not { Step: ExecutionStep.Stack } occurrence
                || occurrence.Kind is not (ExecutionKind.Spell or ExecutionKind.Activation or ExecutionKind.Trigger)) return null;
            return new(state.Id, state.ViewerId, state.Turn, state.ActivePlayerId, state.PhaseKey,
                state.StackInterruptionVersion.Value, frame.LastSequence, top, occurrence);
        }

        internal ResponseProgress Observe(GameSnapshot state, long afterActivity)
        {
            if (state.Id != Game || state.ViewerId != Viewer || state.Turn != Turn || state.ActivePlayerId != Active
                || state.PhaseKey != Phase || state.StackInterruptionVersion != Fence || state.Viewer?.Eliminated != false
                || state.Status is not ("playing" or "resolving") || state.Executions is not { } frame
                || frame.LastSequence < Sequence || frame.FirstSequence > Sequence + 1
                || state.Decision is { Payload: not PriorityPayload }
                || state.ActionClass is not ("" or "priority" or "resolving")) return ResponseProgress.Interrupted;
            var occurrence = ExecutionFrames.Find(state, Top.ExecutionId);
            var permitted = PermittedExecutions(state);
            if (occurrence == null || occurrence.Kind != Execution.Kind || occurrence.ActorPlayerId != Execution.ActorPlayerId
                || occurrence.BudgetRootId != Execution.BudgetRootId || occurrence.ParentId != Execution.ParentId
                || !RetainsActivity(state, afterActivity)
                || frame.Transitions.Any(item => item.Sequence > Sequence && (!permitted.Contains(item.ExecutionId)
                    || item.ExecutionId != Top.ExecutionId && item.Step is not (ExecutionStep.Preparation or ExecutionStep.Cost or ExecutionStep.Resolution or ExecutionStep.Complete)))
                || state.Activity.Any(entry => entry.Id > afterActivity && entry.InterruptsQuietYield
                    && entry.Kind is not ("priority" or "phase") && (!permitted.Contains(entry.ExecutionId)
                        || entry.ExecutionStep is not (ExecutionStep.Resolution or ExecutionStep.Complete)
                        || entry.Kind is "cast" or "ability" or "land"))) return ResponseProgress.Interrupted;
            if (state.Stack.Any(item => item.Id == Top.Id)) {
                var current = state.Stack[0];
                return current.Id == Top.Id && current.ExecutionId == Top.ExecutionId && current.Kind == Top.Kind
                    && current.ControllerId == Top.ControllerId && current.Targets.Select(Target).SequenceEqual(Top.Targets.Select(Target))
                    ? ResponseProgress.Waiting : ResponseProgress.Interrupted;
            }
            // Passing one response does not acknowledge the next older item.
            if (!state.Stack.IsEmpty || occurrence is not { Step: ExecutionStep.Complete, Outcome: ExecutionOutcome.Resolved }
                || !frame.Transitions.Any(item => item.Sequence > Sequence && item.ExecutionId == Top.ExecutionId
                    && item.Step == ExecutionStep.Complete && item.Outcome == ExecutionOutcome.Resolved)) return ResponseProgress.Interrupted;
            // Resolving publications can expose completion before returning a
            // local priority offer. Retain the proof until that fresh offer,
            // rather than treating its later token as an unrelated change.
            return state.Decision?.Payload is PriorityPayload { QuietYieldToken.Length: > 0 }
                ? ResponseProgress.Resolved : ResponseProgress.Waiting;
        }

        private HashSet<string> PermittedExecutions(GameSnapshot state)
        {
            var permitted = new HashSet<string>(StringComparer.Ordinal) { Top.ExecutionId };
            var rejected = new HashSet<string>(StringComparer.Ordinal);
            var occurrences = state.Executions!.Occurrences.ToDictionary(item => item.Id, StringComparer.Ordinal);
            var stacked = state.Stack.Select(item => item.ExecutionId).ToHashSet(StringComparer.Ordinal);
            foreach (var candidate in occurrences.Values) {
                string id = candidate.Id;
                var path = new HashSet<string>(StringComparer.Ordinal);
                while (!permitted.Contains(id)) {
                    if (rejected.Contains(id) || !path.Add(id) || !occurrences.TryGetValue(id, out var item)
                        || item.Kind != ExecutionKind.Rules || item.RuleKind is not (ExecutionRuleKind.Replacement or ExecutionRuleKind.StateBased)
                        || item.BudgetRootId != Execution.BudgetRootId || string.IsNullOrWhiteSpace(item.SourceCardId)
                        || string.IsNullOrWhiteSpace(item.ParentId) || stacked.Contains(id)
                        || item.Step is not (ExecutionStep.Preparation or ExecutionStep.Resolution or ExecutionStep.Complete)
                        || item.Step == ExecutionStep.Complete && item.Outcome != ExecutionOutcome.Resolved) {
                        rejected.UnionWith(path); break;
                    }
                    id = item.ParentId;
                }
                // A historical Cost phase alone does not prove a paid cost or
                // player choice (mandatory replacement templates can cost zero).
                // Current Cost/Choice and committed Cost changes still stop us.
                // Matching a budget root alone is not ancestry. Every link must
                // be a disclosed, typed non-stack rules action leading back to
                // the exact response the player passed. New triggers stay new.
                if (permitted.Contains(id)) permitted.UnionWith(path);
            }
            return permitted;
        }

        private static (string Kind, string Id, int? Player) Target(StackTarget target) => (target.Kind, target.Id, target.PlayerId);

        private static bool RetainsActivity(GameSnapshot state, long after)
        {
            // Unlike the audience-filtered execution stream, the provider's
            // public activity IDs are contiguous. Atomic group eviction can
            // leave a gap inside the retained tail as well as at its start.
            long last = after;
            foreach (var entry in state.Activity.Where(entry => entry.Id > after)) {
                if (entry.Id != last + 1) return false;
                last = entry.Id;
            }
            return LatestActivity(state) >= after;
        }
    }
}
