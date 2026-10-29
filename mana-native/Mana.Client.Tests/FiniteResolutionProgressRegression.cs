using System.Collections.Immutable;
using Mana.Client;
using Mana.Contracts;

internal static class FiniteResolutionProgressRegression
{
    internal static void Run(Action<bool, string> check)
    {
        var initial = FiniteResolutionRegression.State(3);
        var intent = FiniteResolutionIntent.TryArm(initial, initial.Stack.Select(item => item.Id))!;
        var projection = intent.CaptureProgressProjection();
        check(projection.Observe(initial) && projection.Progress is { CapturedCount: 3, CompletedCount: 0, UnknownCount: 0 }
                && projection.DeclineProgress == null,
            "Accepted finite progress starts from the exact captured queue without inventing optional-payment consent");
        var laterCapture = initial with { Revision = 10 };
        var laterProjection = FiniteResolutionIntent.TryArm(laterCapture, laterCapture.Stack.Select(item => item.Id))!.CaptureProgressProjection();
        check(!laterProjection.Observe(FiniteResolutionRegression.State(3, completed: 1, revision: 9))
                && laterProjection.Progress.CompletedCount == 0 && laterProjection.Observe(laterCapture),
            "A progress projection cannot accept an outcome from before its immutable consent-capture revision");
        var afterOne = FiniteResolutionRegression.State(3, completed: 1, revision: 2);
        intent.CanContinue(afterOne);
        check(intent.Progress.CompletedCount == 1 && projection.Progress.CompletedCount == 0,
            "A worker observing a real outcome cannot advance presentation before its publication is accepted");
        var capturedAfterWorker = intent.CaptureProgressProjection();
        check(capturedAfterWorker.Observe(initial) && capturedAfterWorker.Progress.CompletedCount == 0,
            "Creating a presentation projection after worker progress still uses the original immutable capture revision and membership");
        check(projection.Observe(afterOne) && projection.Progress is { CompletedCount: 1, RemainingCount: 2, ResolvedCount: 1 },
            "Accepted finite progress advances only when its owner delivers the corresponding authoritative publication");

        var evicted = afterOne with { Revision = 3, Executions = new(2, 1,
            afterOne.Executions!.Occurrences.RemoveAt(0), []) };
        check(projection.Observe(evicted) && projection.Progress is { CompletedCount: 1, RemainingCount: 2, UnknownCount: 0 },
            "An accepted terminal outcome survives eviction from the provider's bounded execution-history tail");
        var missing = evicted with { Revision = 4, Stack = evicted.Stack.RemoveAt(1),
            Executions = evicted.Executions! with { Occurrences = evicted.Executions.Occurrences.RemoveAt(1) } };
        check(projection.Observe(missing) && projection.Progress is { CompletedCount: 1, RemainingCount: 2, UnknownCount: 1 },
            "A disappearing unconfirmed member remains unfinished and explicitly unknown rather than being inferred resolved");
        var prior = projection.Progress;
        check(!projection.Observe(afterOne) && !projection.Observe(missing with { Id = "other-game", Revision = 99 })
                && !projection.Observe(missing with { ViewerId = 1, Revision = 99 }) && projection.Progress == prior,
            "Stale, foreign-game and foreign-viewer publications cannot mutate captured presentation progress");

        var completed = FiniteResolutionRegression.State(3, completed: 3, revision: 5,
            outcomes: [ExecutionOutcome.Resolved, ExecutionOutcome.Countered, ExecutionOutcome.Declined]);
        check(projection.Observe(completed) && projection.Progress is { CompletedCount: 3, RemainingCount: 0, UnknownCount: 0,
                ResolvedCount: 1, CounteredCount: 1, DeclinedCount: 1 },
            "Later accepted terminal evidence recovers missing members and preserves each actual outcome category");
        prior = projection.Progress;
        var contradiction = FiniteResolutionRegression.State(3, completed: 3, revision: 6,
            outcomes: [ExecutionOutcome.Resolved, ExecutionOutcome.Failed, ExecutionOutcome.Declined]);
        check(!projection.Observe(contradiction) && projection.Progress == prior
                && !projection.Observe(afterOne with { Revision = 6 }) && projection.Progress == prior,
            "Contradictory terminal outcomes and live regressions are rejected atomically without rewriting settled presentation history");
        foreach (var replacement in new[] {
            completed.Executions!.Occurrences[0] with { ActorPlayerId = 1 },
            completed.Executions.Occurrences[0] with { SourceCardId = "replacement-source" },
            completed.Executions.Occurrences[0] with { Kind = ExecutionKind.Activation },
            completed.Executions.Occurrences[0] with { ParentId = "execution-1" },
            completed.Executions.Occurrences[0] with { BudgetRootId = "execution-1" }
        }) {
            var changedOwner = completed with { Revision = 6,
                Executions = completed.Executions with { Occurrences = completed.Executions.Occurrences.SetItem(0, replacement) } };
            check(!projection.Observe(changedOwner) && projection.Progress == prior,
                "An exact execution ID cannot confirm progress after its captured actor, source, kind or causal ownership changes");
        }

        var paying = FiniteResolutionRegression.State(3, paying: true);
        var other = paying.Viewer!.Zone("Battlefield").Cards[0] with { VisualId = "other-source", Key = "other-current", CombatId = "other-source" };
        paying = paying with {
            Players = paying.Players.SetItem(0, paying.Viewer with { Zones = [new("Battlefield", 2,
                [paying.Viewer.Zone("Battlefield").Cards[0], other], null)] }),
            Stack = paying.Stack.SetItem(1, paying.Stack[1] with { Card = other, Repetition = new("different-group", "other-source") }),
            Executions = paying.Executions! with { Occurrences = paying.Executions.Occurrences.SetItem(2,
                paying.Executions.Occurrences[2] with { SourceCardId = "other-source" }) }
        };
        var offer = ((PaymentPayload)paying.Decision!.Payload).OptionalDecline!;
        var mixed = FiniteResolutionIntent.TryArmQueueWithDecline(paying, paying.Stack.Select(item => item.Id), offer)!;
        var mixedProgress = mixed.CaptureProgressProjection();
        check(mixedProgress.Observe(paying) && mixedProgress.Progress.CapturedCount == 3
                && mixedProgress.DeclineProgress is { CapturedCount: 2, CompletedCount: 0 },
            "Accepted progress captures the exact mixed queue and a distinct optional-cost source subset once");
        var mixedComplete = completed with {
            Executions = completed.Executions! with { Occurrences = completed.Executions.Occurrences.SetItem(2,
                completed.Executions.Occurrences[2] with { SourceCardId = "other-source" }) }
        };
        check(mixedProgress.Observe(mixedComplete) && mixedProgress.Progress.CompletedCount == 3
                && mixedProgress.DeclineProgress is { CompletedCount: 2, ResolvedCount: 1, CounteredCount: 1, DeclinedCount: 0 },
            "Optional-source progress counts only exact captured source effects, independently of whole-queue outcomes or matching names");
    }
}
