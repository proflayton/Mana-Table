using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Mana.AI;
using Mana.Contracts;

internal static class AiRestorationRegression
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var state = GameplayUndoRegression.State();
        var other = new ExecutionOccurrence("other", "other", ExecutionKind.Spell, ExecutionStep.Complete) { Outcome = ExecutionOutcome.Resolved, ActorPlayerId = 1 };
        state = state with { Activity = [Damage(1, 0, 1, "mana"), Damage(2, 1, 0, "other")], Executions = state.Executions! with {
            Occurrences = state.Executions.Occurrences.Add(other) } };
        using var seat = new AiSeatSession(state.Id, 0, new Immediate(), 7, new(TimeSpan.FromSeconds(10), 128));
        seat.Observe(new(state, actionCoverage: AiActionCoverage.Complete)); await seat.EvaluateAsync();
        var receipt = seat.TakeCommand()!; seat.Report(receipt, AiExecutionReceipt.Accepted);
        var audit = seat.History;
        check(seat.Plan != null && audit.Decisions.Any(record => record.Outcome == AiDecisionOutcome.Accepted),
            "The restoration fixture starts with accepted immutable AI audit evidence and retained strategy");
        var restored = GameplayUndoRegression.Restored(state) with { Decision = null, Status = "resolving" };
        seat.ObserveRestoration(restored);
        var history = seat.History;
        var assessed = CasualTableAssessment.Assess(restored, history).Single();
        check(seat.Plan == null && seat.PlanVersion > 1 && seat.TakeCommand() == null
                && history.Decisions.Take(audit.Decisions.Length).SequenceEqual(audit.Decisions),
            "A restoration without an owned decision revokes AI strategy and proposals while preserving its exact earlier receipt history");
        check(history.Events.SequenceEqual(audit.Events) && history.ImpactEvents!.Value.SequenceEqual(audit.ImpactEvents!.Value)
                && history.ReversedExecutionIds.Contains("mana") && assessed.RecentFocus == 0 && assessed.Pressure == 5,
            "Derived social memory excludes the reversed execution while retaining its evidence and unrelated pressure");
        var encoded = JsonSerializer.Deserialize<AiSeatHistory>(JsonSerializer.Serialize(history))!;
        check(encoded.ReversedExecutionIds.Contains("mana") && CasualTableAssessment.Assess(restored, encoded).Single().Pressure == 5,
            "Recorded AI evidence retains restoration attribution without fabricating an inverse damage event");
        var replayState = restored with { Status = "playing", Decision = GameplayUndoRegression.Restored(state).Decision };
        var replay = await AiDecisionReplay.EvaluateAsync(new(1, "restored", "restore-memory.v1", replayState,
            new(0, ActionDomainCoverage.Complete, [], []), new(), encoded, 7), new Immediate());
        check(replay.Status == AiSeatStatus.Ready && replay.Proposal?.DecisionId == replayState.Decision!.Id,
            "A private replay validates and evaluates restoration-aware history through the same production seat session");
        seat.ObserveRestoration(restored);
        check(seat.History.ReversedExecutionIds.SetEquals(history.ReversedExecutionIds) && seat.PlanVersion > 1,
            "Republishing a restoration cannot append its contribution or revive an old plan");
        var missed = restored with { Revision = restored.Revision + 1, BoardRevision = restored.BoardRevision + 1,
            Undo = new(10, 3, null, new(3, "later-action", "evicted", 0)) };
        seat.ObserveRestoration(missed);
        check(seat.History.Events.Length == audit.Events.Length && CasualTableAssessment.Assess(missed, seat.History).Single().Pressure == 0,
            "Missing restoration ancestry rebases derived social effects without deleting retained audit facts or inventing attribution");
        await LatePolicyAndBudget(state, check);
        using var pending = new AiSeatSession(state.Id, 0, new Immediate(), 7, new(TimeSpan.FromSeconds(10), 128));
        pending.Observe(new(state, actionCoverage: AiActionCoverage.Complete)); await pending.EvaluateAsync();
        var submitted = pending.TakeCommand()!; pending.ObserveRestoration(restored);
        pending.Report(submitted, AiExecutionReceipt.Accepted);
        check(pending.Plan == null && pending.History.Decisions.Last() is { Outcome: AiDecisionOutcome.Accepted, PlanTransitionApplied: false },
            "A late accepted receipt remains truthful audit evidence without reinstalling its pre-restoration resource plan");
        var clock = new Clock(); var pacing = new TimedAiHostPacing(TimeSpan.FromSeconds(1), clock);
        var progress = new AiTableProgress(state.Id, 0, state.Turn, 0, [], 0);
        pacing.Observe([progress]); check(pacing.Ready(state.Id, 0), "An idle bot begins without a fabricated public-action delay");
        pacing.Observe([progress with { RestorationVersion = 1 }]);
        check(!pacing.Ready(state.Id, 0), "A restoration establishes a fresh bot presentation beat without caching a command");
        clock.Advance(TimeSpan.FromSeconds(1)); check(pacing.Ready(state.Id, 0), "The restored board's bot beat uses ordinary elapsed time");
    }

    private static async Task LatePolicyAndBudget(GameSnapshot state, Action<bool, string> check)
    {
        var clock = new Clock(); var policy = new Controlled();
        using var seat = new AiSeatSession(state.Id, 0, policy, 7, new(TimeSpan.FromSeconds(10), 128), clock);
        seat.Observe(new(state, actionCoverage: AiActionCoverage.Complete)); var old = seat.EvaluateAsync(); var request = await policy.Request(1);
        clock.Advance(TimeSpan.FromSeconds(2));
        var restored = GameplayUndoRegression.Restored(state);
        seat.ObserveRestoration(restored with { Decision = null, Status = "resolving" });
        request.Result.SetResult(Answer(request.Context)); await old;
        check(seat.TakeCommand() == null && seat.History.Decisions.Any(record => record.ReasonCode == "action-restored"),
            "A policy ignoring cancellation cannot submit a pre-restoration answer after its board was revoked");
        seat.Observe(new(restored, actionCoverage: AiActionCoverage.Complete)); var next = seat.EvaluateAsync(); var fresh = await policy.Request(2);
        check(fresh.Context.RemainingTime <= TimeSpan.FromSeconds(8)
                && fresh.Context.Frame.Stamp.RestorationVersion == 1 && fresh.Context.Plan == null,
            "Fresh restored authority carries its restoration fence and remaining AI budget rather than refunding thinking time");
        fresh.Result.SetResult(Answer(fresh.Context)); await next;
        check(seat.TakeCommand()?.Command.DecisionId == restored.Decision!.Id,
            "The same AI seat resumes with a fresh scoped answer after restoration");
    }

    private static ActivityEntry Damage(long id, int actor, int target, string execution) => new(id, 3, "MAIN1", "damage", target, "Damage", null, null) {
        Impact = new("damage", 5, PlayerId: target, SourceControllerId: actor, RecipientControllerId: target),
        Cause = new(null, "Cause", "Ability", actor), ExecutionId = execution, ExecutionStep = ExecutionStep.Complete };
    private static AiProposal.Answer Answer(AiDecisionRequest request) => new(new PassPriority(request.Frame.State.Id, request.Frame.Stamp.DecisionId), "restore-probe");
    private sealed class Immediate : IAiPolicy
    {
        public AiPolicyDescriptor Descriptor { get; } = new("restore-memory.v1", AiTasks.All);
        public Task<AiProposal> EvaluateAsync(AiDecisionRequest request, CancellationToken cancellation) => Task.FromResult<AiProposal>(Answer(request) with {
            PlanTransition = new(request.PlanVersion, new(request.Frame.State.Id, 0, request.Frame.State.Turn, request.PlanVersion + 1,
                AiPlanPurpose.Develop, [], []) { CreatedFrom = request.Frame.Stamp }) });
    }
    private sealed class Controlled : IAiPolicy
    {
        public AiPolicyDescriptor Descriptor { get; } = new("restore-late.v1", AiTasks.All);
        private readonly ConcurrentQueue<Entry> entries = new();
        public Task<AiProposal> EvaluateAsync(AiDecisionRequest request, CancellationToken cancellation)
        { var entry = new Entry(request, new(TaskCreationOptions.RunContinuationsAsynchronously)); entries.Enqueue(entry); return entry.Result.Task; }
        internal async Task<Entry> Request(int number)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (entries.Count < number) await Task.Delay(1, timeout.Token);
            return entries.ToArray()[number - 1];
        }
    }
    private sealed record Entry(AiDecisionRequest Context, TaskCompletionSource<AiProposal> Result);
    private sealed class Clock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        internal void Advance(TimeSpan elapsed) => ticks += elapsed.Ticks;
    }
}
