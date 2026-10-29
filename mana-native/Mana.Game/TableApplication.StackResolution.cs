using Mana.Client;
using Mana.Contracts;
using Mana.Magic;
using System.Collections.Immutable;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private StackResolutionIntent? stackResolutionIntent;
    private FiniteResolutionIntent? finiteResolutionIntent;
    private GameSnapshot? repeatedDeclineSnapshot;
    private FiniteResolutionIntent? repeatedDeclineOffer;
    private FiniteResolutionProgress? lastFiniteProgress;
    private ImmutableHashSet<string> repeatedPresentationExecutions = ImmutableHashSet<string>.Empty;
    private GameSnapshot? stackEligibilitySnapshot;
    private bool cachedStackEligibility;
    private bool ResolvingCapturedStack => stackResolutionIntent?.IsActive == true || finiteResolutionIntent?.IsActive == true;
    private int CapturedStackRemaining => finiteResolutionIntent?.Progress.RemainingCount ?? stackResolutionIntent?.Remaining ?? 0;
    private bool AutomatedCapturedPayment => finiteResolutionIntent is { IsActive: true, DeclinesOptionalPayments: true } finite
        && match?.Decision?.Payload is PaymentPayload { OptionalDecline: { } offer }
        && finite.CapturedExecutionIds.Contains(offer.ProducingTriggerExecutionId);
    private FiniteResolutionIntent? RepeatedDeclineOffer
    {
        get {
            if (!ReferenceEquals(repeatedDeclineSnapshot, match)) {
                repeatedDeclineSnapshot = match; repeatedDeclineOffer = null;
                if (match is { Decision.Payload: PaymentPayload { OptionalDecline: { } offer } } state)
                    repeatedDeclineOffer = FiniteResolutionIntent.TryArmQueueWithDecline(state,
                        state.Stack.Select(item => item.Id), offer);
            }
            return fixture == null && !session.NeedsRecovery && finiteResolutionIntent?.IsActive != true
                && repeatedDeclineOffer?.DeclineProgress?.CapturedCount > 1 ? repeatedDeclineOffer : null;
        }
    }
    private bool CanOfferStackResolution
    {
        get {
            if (!ReferenceEquals(stackEligibilitySnapshot, match)) {
                stackEligibilitySnapshot = match;
                cachedStackEligibility = match is { Stack.Length: > 1 } state && StackResolutionIntent.TryArm(state) != null;
            }
            return fixture == null && !session.NeedsRecovery && cachedStackEligibility;
        }
    }

    private void ArmStackResolution()
    {
        if (!CanOfferStackResolution || busy || match == null) return;
        StopQuietPriority(); session.CancelAdvance();
        held = false; priorityAttention.Resume();
        finiteResolutionIntent = FiniteResolutionIntent.TryArm(match, match.Stack.Select(item => item.Id));
        if (finiteResolutionIntent != null) BeginFinitePresentation(match, finiteResolutionIntent);
        else stackResolutionIntent = StackResolutionIntent.TryArm(match);
        error = ""; commandFailure = false;
    }

    private void ArmRepeatedDecline()
    {
        if (busy || match == null || RepeatedDeclineOffer is not { } intent) return;
        StopQuietPriority(); StopStackResolution(); session.CancelAdvance();
        held = false; priorityAttention.Resume(); finiteResolutionIntent = intent;
        BeginFinitePresentation(match, intent);
        error = ""; commandFailure = false;
    }

    private void BeginFinitePresentation(GameSnapshot state, FiniteResolutionIntent intent)
    {
        lastFiniteProgress = null;
        var groups = state.Stack.Where(item => intent.CapturedExecutionIds.Contains(item.ExecutionId))
            .Select(item => (Execution: item.ExecutionId, Group: item.Repetition?.GroupKey ?? "execution:" + item.ExecutionId)).ToList();
        if (state.Decision?.Payload is PaymentPayload { OptionalDecline: { } offer }
            && intent.CapturedExecutionIds.Contains(offer.ProducingTriggerExecutionId))
            groups.Add((offer.ProducingTriggerExecutionId, offer.GroupKey));
        var repeatedGroups = groups.GroupBy(item => item.Group, StringComparer.Ordinal)
            .Where(group => group.Select(item => item.Execution).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        var repeated = groups.Where(item => repeatedGroups.Contains(item.Group)).ToArray();
        repeatedPresentationExecutions = repeated.Select(item => item.Execution).ToImmutableHashSet(StringComparer.Ordinal);
        matchPresentation.BeginFinite(repeated, now);
    }

    private void StopStackResolution()
    {
        if (stackResolutionIntent == null && finiteResolutionIntent == null) return;
        stackResolutionIntent?.Stop(); stackResolutionIntent = null;
        if (finiteResolutionIntent is { } finite) {
            lastFiniteProgress = finite.Progress;
            if (finite.StopReason == "completed" && finite.Progress.UnknownCount == 0) {
                notice = finite.DeclineProgress is { } declined
                    ? finite.SourceName + " \u00b7 " + declined.CompletedCount + " / " + declined.CapturedCount + " source effects completed \u00b7 " + finite.Progress.CompletedCount + " / " + finite.Progress.CapturedCount + " completed"
                    : finite.SourceName + " \u00b7 " + finite.Progress.CompletedCount + " / " + finite.Progress.CapturedCount + " completed";
                noticeUntil = now + 2.6;
            } else if (match?.FiniteStackYield is { Status: FiniteStackYieldStatus.Revoked } stopped) {
                string reason = stopped.ReasonCode switch {
                    "outside-action" => "A new action needs your attention.",
                    "required-choice" or "payment-changed" or "unobserved-choice-completion" => "A choice needs your attention.",
                    "history-overflow" or "execution-gap" => "Earlier results could not be confirmed.",
                    "phase-changed" => "The phase changed.",
                    "match-ended" => "The game ended.",
                    _ => "Review the current stack before continuing."
                };
                notice = "Resolution stopped \u00b7 " + finite.Progress.CompletedCount + " / " + finite.Progress.CapturedCount + " completed. " + reason;
                noticeUntil = now + 4;
            }
        }
        finiteResolutionIntent?.Stop(); finiteResolutionIntent = null;
        repeatedPresentationExecutions = ImmutableHashSet<string>.Empty;
        matchPresentation.EndFinite(); session.CancelAdvance();
    }

    private bool UpdateStackResolution(bool interactionBlocked)
    {
        if (finiteResolutionIntent is { IsActive: false }) StopStackResolution();
        if (!ResolvingCapturedStack) return false;
        if (finiteResolutionIntent is { } finite) {
            if (held || fixture != null || session.NeedsRecovery || commandFailure || match == null || !finite.CanContinue(match)) {
                StopStackResolution(); return false;
            }
            bool repeatedCurrent = RepeatedExecution(match, match.Decision?.Payload is PaymentPayload { OptionalDecline: { } currentOffer }
                ? currentOffer.ProducingTriggerExecutionId : match.Stack.FirstOrDefault()?.ExecutionId ?? "", repeatedPresentationExecutions);
            bool ready = repeatedCurrent ? matchPresentation.ReadyForFinite(now) : matchPresentation.ReadyForStack(now);
            if (interactionBlocked) { finite.Pause(); session.CancelAdvance(); return true; }
            if (!ready) {
                // A captured group's progress pulse is visual pacing within
                // the same consent. Reading and unrelated beats still revoke.
                if (!repeatedCurrent) session.CancelAdvance();
                return true;
            }
            finite.Resume();
            var repeated = repeatedPresentationExecutions;
            if (!busy && !polling && session.AdvanceFinite(finite, (before, after) => !FiniteProgress(finite, repeated, before, after)
                && PresentationPacing.ShouldPresent(before, after))) pendingActionLabel = "Resolving the captured effects.";
            return true;
        }
        if (held || fixture != null || session.NeedsRecovery || commandFailure || match == null
            || !stackResolutionIntent!.CanContinue(match)) { StopStackResolution(); return false; }
        // Readable public beats and inspection pause the local continuation,
        // without stopping the authoritative game clock or retaining old input.
        if (interactionBlocked || !matchPresentation.ReadyForStack(now)) { session.CancelAdvance(); return true; }
        if (!busy && !polling && session.AdvanceStack(stackResolutionIntent, PresentationPacing.ShouldPresent))
            pendingActionLabel = "Resolving the captured stack.";
        return true;
    }

    private static bool FiniteProgress(FiniteResolutionIntent finite, IReadOnlySet<string> repeated, GameSnapshot? before, GameSnapshot? after)
    {
        if (before == null || after == null
            || before.Id != after.Id || before.ViewerId != after.ViewerId || before.Turn != after.Turn
            || before.PhaseKey != after.PhaseKey || before.ActivePlayerId != after.ActivePlayerId
            || before.StackActionVersion != after.StackActionVersion || before.ResolutionChoiceVersion != after.ResolutionChoiceVersion
            || after.Status is "finished" or "error" || !before.Notices.SequenceEqual(after.Notices)) return false;
        bool Includes(string id) => id.Length > 0 && finite.IncludesExecution(after, id) && RepeatedExecution(after, id, repeated);
        var oldStack = before.Stack.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        if (after.Stack.Any(item => !oldStack.Contains(item.Id) && !Includes(item.ExecutionId))) return false;
        var currentStack = after.Stack.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        if (before.Stack.Any(item => !currentStack.Contains(item.Id) && !Includes(item.ExecutionId))) return false;
        long activity = before.Activity.Select(item => item.Id).DefaultIfEmpty().Max();
        var entries = after.Activity.Where(item => item.Id > activity).ToArray();
        long sequence = before.Executions?.LastSequence ?? 0;
        var transitions = after.Executions?.Transitions.Where(item => item.Sequence > sequence).ToArray() ?? [];
        if (entries.Any(item => !Includes(item.ExecutionId)) || transitions.Any(item => !Includes(item.ExecutionId))) return false;
        if (after.Decision is { Payload: not PriorityPayload } decision
            && (decision.Payload is not PaymentPayload { OptionalDecline: { } offer }
                || !finite.DeclinesOptionalPayments || !Includes(offer.ProducingTriggerExecutionId))) return false;
        return entries.Length > 0 || transitions.Length > 0
            || !before.Stack.Select(item => item.Id).SequenceEqual(after.Stack.Select(item => item.Id));
    }

    private static bool RepeatedExecution(GameSnapshot state, string id, IReadOnlySet<string> repeated)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (id.Length > 0 && seen.Add(id)) {
            if (repeated.Contains(id)) return true;
            if (ExecutionFrames.Find(state, id)?.ParentId is not { } parent) return false;
            id = parent;
        }
        return false;
    }
}
