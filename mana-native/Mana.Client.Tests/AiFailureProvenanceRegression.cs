using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;
using Mana.AI;
using Mana.Contracts;

internal static class AiFailureProvenanceRegression
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var provider = new Provider();
        var diagnostics = new Diagnostics();
        await using var host = new AiTableHost(provider,
            _ => new Policy(_ => new AiProposal.Unavailable("legal-text-domain", "private policy detail")), 42,
            diagnostics: diagnostics);
        await host.StepAsync();
        using var fault = JsonDocument.Parse(JsonSerializer.Serialize(host.LastFault));
        var history = host.Histories.Single().Decisions.Single();
        using var record = JsonDocument.Parse(JsonSerializer.Serialize(history));
        check(fault.RootElement.TryGetProperty("Failure", out var failure)
                && failure.GetProperty("Code").GetString() == "legal-text-domain"
                && record.RootElement.TryGetProperty("Failure", out var retained)
                && retained.GetProperty("Code").GetString() == "legal-text-domain"
                && provider.Faults.SequenceEqual(["unavailable"]) && provider.Commands.Count == 0,
            "An unavailable capability survives session/history/host private evidence while the public fault reason and zero-dispatch transcript remain unchanged");
        await host.StepAsync();
        check(provider.Observations == 2 && provider.Faults.Count == 1 && provider.Commands.Count == 0
                && diagnostics.Faults.Single().Failure == history.Failure
                && !JsonSerializer.Serialize(history).Contains("private policy detail", StringComparison.Ordinal)
                && history.Failure is { Kind: AiFailureKind.CapabilityUnavailable, Task: AiTask.OpeningHand,
                    EpisodeId: "input:failure-input", RemainingWork: 4096, Stamp.DecisionId: "failure-input" },
            "Private failure provenance reaches the host once, retains its exact task/episode/accounting, and never retains policy Detail or changes settled fault behavior");
        await SessionCauses(check);
        await WorkOwnership(check);
        await ReceiptsAndNoProgress(check);
        await ReplayCompatibility(check);
        await LateResults(check);
        await CancellationCauses(check);
        await HandoffGuard(check);
        await RouteLimit(check);
    }

    private static async Task SessionCauses(Action<bool, string> check)
    {
        var frame = Frame();
        foreach (string code in new[] { "", "bad\nprivate-face", new string('a', 97), "space private-face", "\u03a3" }) {
            using var session = Session(new Policy(_ => new AiProposal.Unavailable(code, "private-face")));
            session.Observe(frame);
            var result = await session.EvaluateAsync();
            check(result.Status == AiSeatStatus.Invalid && session.TakeCommand() == null
                    && result.Failure is { Kind: AiFailureKind.InvalidPolicyOutput, Code: "invalid-capability" }
                    && !JsonSerializer.Serialize(session.History).Contains("private-face", StringComparison.Ordinal),
                "An invalid capability identifier is classified without retaining the unvalidated identifier or private policy detail");
        }
        using (var compatible = Session(new Policy(_ => new AiProposal.Unavailable("capability.v2_optional", "")))) {
            compatible.Observe(frame);
            check((await compatible.EvaluateAsync()).Failure is { Code: "capability.v2_optional", Kind: AiFailureKind.CapabilityUnavailable },
                "Existing bounded machine codes retain their ASCII dot/underscore/hyphen compatibility");
        }
        using (var threw = Session(new Policy(_ => throw new InvalidOperationException("private exception face")))) {
            threw.Observe(frame);
            var result = await threw.EvaluateAsync();
            check(result.Status == AiSeatStatus.Invalid && result.Failure is { Kind: AiFailureKind.PolicyException, Code: "policy-failed" }
                    && !JsonSerializer.Serialize(threw.History).Contains("private exception face", StringComparison.Ordinal)
                    && !JsonSerializer.Serialize(result).Contains("private exception face", StringComparison.Ordinal),
                "A thrown policy retains its private failure category without retaining exception text in the view or history");
        }
        using (var invalid = Session(new Policy(_ => new AiProposal.Answer(new KeepOpeningHand(frame.State.Id, "foreign-input"), "forged")))) {
            invalid.Observe(frame);
            var result = await invalid.EvaluateAsync();
            check(result.Status == AiSeatStatus.Invalid && invalid.TakeCommand() == null
                    && result.Failure is { Kind: AiFailureKind.InvalidPolicyOutput, Code: "unoffered-answer" }
                    && invalid.History.Decisions.Single().AnswerEvidence == null,
                "An unoffered proposal retains a contract cause and cannot leak its forged command into failure evidence");
        }
        using (var policyTimeout = Session(new Policy(_ => throw new TimeoutException("private provider detail")))) {
            policyTimeout.Observe(frame);
            var result = await policyTimeout.EvaluateAsync();
            check(result.Status == AiSeatStatus.BudgetExceeded
                    && result.Failure is { Kind: AiFailureKind.PolicyException, Code: "policy-timeout" },
                "A timeout thrown by the policy is not falsely reported as expiration of the session's own active-time allowance");
        }
        using (var absent = Session(new Policy(_ => null!))) {
            absent.Observe(frame);
            check((await absent.EvaluateAsync()).Failure is { Kind: AiFailureKind.InvalidPolicyOutput, Code: "missing-proposal" },
                "A null policy proposal is distinguished from a thrown policy exception without inventing an answer");
        }
        var clock = new Clock();
        using (var timed = Session(new Policy(request => {
            clock.Advance(TimeSpan.FromSeconds(6));
            return Keep(request);
        }), new(TimeSpan.FromSeconds(5), 8), clock)) {
            timed.Observe(frame);
            var result = await timed.EvaluateAsync();
            check(result.Status == AiSeatStatus.BudgetExceeded && timed.TakeCommand() == null
                    && result.Failure is { Kind: AiFailureKind.TimeLimit, Code: "time-budget", RemainingWork: 8 }
                    && result.Failure.Elapsed == TimeSpan.FromSeconds(6),
                "Actual active-time exhaustion is distinguished from work limits without raising or restarting the decision allowance");
        }
    }

    private static async Task WorkOwnership(Action<bool, string> check)
    {
        var frame = Frame();
        using (var named = Session(new Policy(_ => new AiProposal.Unavailable("work-budget", "arbitrary capability name")))) {
            named.Observe(frame);
            check((await named.EvaluateAsync()).Failure is { Kind: AiFailureKind.CapabilityUnavailable, Code: "work-budget", RemainingWork: 4096 },
                "A capability named work-budget cannot by itself claim session-owned computation exhaustion");
        }
        using (var bounded = Session(new Policy(request => {
            request.Work.TrySpend(2);
            return AiProposal.Unavailable.FromWorkLimit(request.Work);
        }), new(TimeSpan.FromSeconds(5), 1))) {
            bounded.Observe(frame);
            var result = await bounded.EvaluateAsync();
            check(result.Status == AiSeatStatus.Unavailable && result.Failure is { Kind: AiFailureKind.WorkLimit, Code: "work-budget", RemainingWork: 1 }
                    && bounded.TakeCommand() == null,
                "An actual refused episode spend keeps its legacy Unavailable status and reports a work limit even when insufficient units remain");
        }
        using (var local = Session(new Policy(request => {
            var child = request.Work.CreateSlice(1, reserve: 2).CreateSlice(1);
            child.TrySpend(); child.TrySpend();
            return AiProposal.Unavailable.FromWorkLimit(child);
        }), new(TimeSpan.FromSeconds(5), 8))) {
            local.Observe(frame);
            var result = await local.EvaluateAsync();
            check(result.Status == AiSeatStatus.Unavailable && result.Failure is { Kind: AiFailureKind.WorkLimit, Code: "local-work-budget", RemainingWork: 7 },
                "A genuinely exhausted descendant is reported as a local search limit while retaining the parent episode's reserved work");
        }
        using (var optimized = Session(new Policy(request => {
            var child = request.Work.CreateSlice(1);
            child.TrySpend(); child.TrySpend();
            return Keep(request);
        }), new(TimeSpan.FromSeconds(5), 8))) {
            optimized.Observe(frame);
            var result = await optimized.EvaluateAsync();
            check(result.Status == AiSeatStatus.Ready && result.Failure == null
                    && optimized.TakeCommand()?.Command is KeepOpeningHand
                    && optimized.History.Decisions.All(record => record.Failure == null),
                "A valid completed answer remains Ready after a local optimization limit; informational evidence cannot turn it into a failure");
        }
        AiWorkBudget? old = null;
        using (var owner = Session(new Policy(request => {
            old = request.Work; old.TrySpend(5000);
            return AiProposal.Unavailable.FromWorkLimit(old);
        }))) {
            owner.Observe(frame); await owner.EvaluateAsync();
            using var foreign = Session(new Policy(_ => AiProposal.Unavailable.FromWorkLimit(old!)));
            foreign.Observe(frame);
            check((await foreign.EvaluateAsync()).Failure is { Kind: AiFailureKind.CapabilityUnavailable, RemainingWork: 4096 },
                "Another session's refused work cannot certify a work-limit cause for the current seat allowance");
        }
        AiWorkBudget? priorEpisode = null;
        int calls = 0;
        using (var episodes = Session(new Policy(request => {
            if (calls++ == 0) {
                priorEpisode = request.Work; priorEpisode.TrySpend(5000);
            }
            return AiProposal.Unavailable.FromWorkLimit(priorEpisode!);
        }))) {
            episodes.Observe(frame); await episodes.EvaluateAsync();
            episodes.Observe(Frame("next-episode", 2));
            check((await episodes.EvaluateAsync()).Failure is { Kind: AiFailureKind.CapabilityUnavailable, EpisodeId: "input:next-episode", RemainingWork: 4096 },
                "An old episode's exhausted budget cannot become current work evidence after an ordinary fresh input admission");
        }
        using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(AiProposal.Unavailable.FromWorkLimit(old!)));
        check(!serialized.RootElement.TryGetProperty("LimitedWork", out _)
                && typeof(AiFailure).GetProperties().All(property => property.PropertyType != typeof(AiWorkBudget)
                    && !typeof(GameCommand).IsAssignableFrom(property.PropertyType)),
            "The transient limited-budget reference and executable commands cannot enter serialized immutable failure evidence");
    }

    private static async Task ReceiptsAndNoProgress(Action<bool, string> check)
    {
        var frame = Frame();
        using (var session = Session(new Policy(Keep))) {
            session.Observe(frame); await session.EvaluateAsync();
            var ticket = session.TakeCommand()!;
            session.Observe(Frame("later-input", 2));
            session.Report(ticket, AiExecutionReceipt.Unknown);
            check(session.View.Status == AiSeatStatus.NeedsRecovery
                    && session.View.Failure is { Kind: AiFailureKind.ExecutionUncertain, Code: "execution-unknown", Stamp.DecisionId: "failure-input" }
                    && session.View.Stamp?.DecisionId == "later-input"
                    && session.History.Decisions.Last().Failure == session.View.Failure && session.TakeCommand() == null,
                "An uncertain receipt retains the original submitted stamp and accounting after the session has observed another input");
        }
        using (var session = Session(new Policy(Keep))) {
            session.Observe(frame); await session.EvaluateAsync(); session.TakeCommand();
            session.Dispose();
            check(session.History.Decisions.Last().Failure is { Kind: AiFailureKind.ExecutionUncertain, Code: "seat-closed-with-submission" }
                    && session.View.Status == AiSeatStatus.Closed,
                "Closing with an outstanding handoff retains an unknown receipt cause without retaining an actionable command");
        }
        var unknown = new Provider { LoseReceipt = true };
        var diagnostics = new Diagnostics();
        await using (var host = new AiTableHost(unknown, _ => new Policy(Keep), 42, diagnostics: diagnostics)) {
            await host.StepAsync(); await host.StepAsync();
            check(unknown.Commands.Count == 1 && unknown.Faults.SequenceEqual(["execution-unknown"])
                    && host.LastFault?.Failure is { Kind: AiFailureKind.ExecutionUncertain, Stamp.DecisionId: "failure-input" }
                    && diagnostics.Faults.Single().Failure == host.LastFault.Failure,
                "A lost actual provider receipt carries the submitted cause to private host diagnostics while still stopping once without replay");
        }
        var clock = new Clock(); var stalled = new Provider();
        await using (var host = new AiTableHost(stalled, _ => new Policy(Keep), 42, operationClock: clock)) {
            await host.StepAsync();
            clock.Advance(TimeSpan.FromSeconds(44)); await host.StepAsync();
            check(host.LastFault == null && stalled.Commands.Count == 1,
                "Adding provenance does not shorten the existing accepted-command observation deadline");
            clock.Advance(TimeSpan.FromSeconds(1)); await host.StepAsync(); await host.StepAsync();
            check(host.LastFault is { ReasonCode: "accepted-no-progress", Failure.Kind: AiFailureKind.ObservationNoProgress }
                    && host.LastFault.Failure.Stamp.DecisionId == "failure-input"
                    && stalled.Commands.Count == 1 && stalled.Faults.SequenceEqual(["accepted-no-progress"]),
                "The unchanged 45-second no-progress fault identifies the actual accepted input without replay or false policy time exhaustion");
        }
        var failedStorage = new Provider();
        await using (var host = new AiTableHost(failedStorage, _ => new Policy(_ => new AiProposal.Unavailable("required-selection", "")),
            42, diagnostics: new Diagnostics { Throw = true })) {
            await host.StepAsync();
            check(failedStorage.Faults.SequenceEqual(["unavailable"]) && failedStorage.Commands.Count == 0
                    && host.LastFault?.Failure?.Code == "required-selection",
                "A throwing private diagnostic sink cannot suppress the original terminal fault or change its typed cause");
        }
    }

    private static async Task ReplayCompatibility(Action<bool, string> check)
    {
        var frame = Frame();
        AiDecisionRecord retained;
        using (var session = Session(new Policy(_ => new AiProposal.Unavailable("exact-capability", "private detail")))) {
            session.Observe(frame); await session.EvaluateAsync(); retained = session.History.Decisions.Single();
        }
        var captured = new AiDecisionReplayCase(1, "failure-replay", "failure-probe.v1", frame.State, frame.Domain, frame.Semantics,
            new([retained], []), 42);
        var restored = JsonSerializer.Deserialize<AiDecisionReplayCase>(JsonSerializer.Serialize(captured))!;
        bool sawTyped = false;
        var replay = await AiDecisionReplay.EvaluateAsync(restored, new Policy(request => {
            sawTyped = request.History.Decisions.Single().Failure == retained.Failure;
            return Keep(request);
        }));
        check(sawTyped && replay.Status == AiSeatStatus.Ready,
            "Typed failure history round-trips through private replay without becoming a command or suppressing a valid new proposal");
        var legacy = JsonNode.Parse(JsonSerializer.Serialize(captured))!;
        legacy["History"]!["Decisions"]![0]!.AsObject().Remove("Failure");
        var old = JsonSerializer.Deserialize<AiDecisionReplayCase>(legacy.ToJsonString())!;
        bool sawAbsent = false;
        replay = await AiDecisionReplay.EvaluateAsync(old, new Policy(request => {
            sawAbsent = request.History.Decisions.Single().Failure == null;
            return Keep(request);
        }));
        check(sawAbsent && replay.Status == AiSeatStatus.Ready && old.FormatVersion == 1,
            "Older format-one replay JSON without optional failure evidence stays readable with honestly absent provenance");
        var foreign = new AiFailure(AiFailureKind.CapabilityUnavailable, "exact-capability", retained.Stamp with { SeatId = 1 },
            retained.Task, retained.EpisodeId, retained.RemainingWork, retained.Elapsed);
        bool rejected = false;
        try { await AiDecisionReplay.EvaluateAsync(captured with { History = new([retained with { Failure = foreign }], []) }, new Policy(Keep)); }
        catch (ArgumentException) { rejected = true; }
        check(rejected, "Replay rejects failure evidence borrowed from another seat rather than exposing it as current policy knowledge");
    }

    private static async Task LateResults(Action<bool, string> check)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = new TaskCompletionSource<AiProposal>(TaskCreationOptions.RunContinuationsAsynchronously);
        var policy = new AsyncPolicy(async (request, cancellation) => {
            if (request.Frame.Stamp.DecisionId == "failure-input") { started.SetResult(); return await held.Task; }
            return Keep(request);
        });
        using var session = Session(policy);
        session.Observe(Frame()); var old = session.EvaluateAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.Observe(Frame("current-input", 2));
        var current = await session.EvaluateAsync();
        held.SetResult(new AiProposal.Unavailable("private-stale-failure", "private stale detail"));
        var cancelled = await old.WaitAsync(TimeSpan.FromSeconds(5));
        check(current.Status == AiSeatStatus.Ready && session.View.Status == AiSeatStatus.Ready
                && session.View.Failure == null && cancelled.Status == AiSeatStatus.Cancelled && cancelled.Failure == null
                && session.History.Decisions.All(record => record.Failure == null)
                && session.TakeCommand()?.Command is KeepOpeningHand { DecisionId: "current-input" },
            "A late unsuccessful result from a cancelled observation cannot install failure evidence or replace the usable current proposal");
    }

    private static async Task CancellationCauses(Action<bool, string> check)
    {
        var provider = new Provider(); int calls = 0;
        await using (var host = new AiTableHost(provider, _ => new Policy(_ => {
            calls++; throw new OperationCanceledException("private cancellation detail");
        }), 42)) {
            await host.StepAsync(); await host.StepAsync();
            var retained = host.Histories.Single().Decisions.Single();
            check(retained.Outcome == AiDecisionOutcome.Cancelled
                    && retained.Failure is { Kind: AiFailureKind.PolicyException, Code: "policy-cancelled" }
                    && provider.Observations == 2 && provider.Commands.Count == 0 && provider.Faults.Count == 0 && calls == 1
                    && !JsonSerializer.Serialize(retained).Contains("private cancellation detail", StringComparison.Ordinal),
                "A policy-thrown cancellation with a live owner retains its private cause while preserving the legacy cached Cancelled/no-dispatch/no-fault transcript");
        }
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stopping = new CancellationTokenSource();
        using var session = Session(new AsyncPolicy(async (request, cancellation) => {
            started.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, cancellation); return Keep(request);
        }));
        session.Observe(Frame()); var evaluation = session.EvaluateAsync(stopping.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); stopping.Cancel();
        var cancelled = await evaluation.WaitAsync(TimeSpan.FromSeconds(5));
        check(cancelled.Status == AiSeatStatus.Cancelled && cancelled.Failure == null
                && session.History.Decisions.Last().Failure == null && session.TakeCommand() == null,
            "Actual caller cancellation remains ordinary owner retirement without a falsely attributed policy failure");
    }

    private static async Task HandoffGuard(Action<bool, string> check)
    {
        int calls = 0;
        using var session = Session(new Policy(request => {
            calls++; return new AiProposal.Answer(new UseOfferedAction(request.Frame.State.Id,
                request.Frame.Stamp.DecisionId, "source-key", "action"), "owned-offer");
        }));
        var frame = RouteFrame(0); session.Observe(frame);
        var ready = await session.EvaluateAsync();
        // Deliberately violate immutable-input ownership through its managed
        // array alias. Exercise the existing final handoff guard, not a new read.
        var aliased = ImmutableCollectionsMarshal.AsArray(frame.SourceActions.Single().Actions)!;
        aliased[0] = new("retired-offer", "activate", "Changed outside the frame contract");
        check(ready.Status == AiSeatStatus.Ready && session.TakeCommand() == null
                && session.View.Status == AiSeatStatus.BudgetExceeded
                && session.View.Failure is { Kind: AiFailureKind.InvalidPolicyOutput, Code: "handoff-authority" }
                && session.History.Decisions.Last().Failure == session.View.Failure
                && session.History.Decisions.All(record => record.Outcome is not (AiDecisionOutcome.Submitted or AiDecisionOutcome.Accepted))
                && (await session.EvaluateAsync()).Failure == session.View.Failure && calls == 1,
            "A null handoff from the existing last authority guard retains its descriptor and legacy status without dispatch, a second evaluation or new observation");
    }

    private static async Task RouteLimit(Action<bool, string> check)
    {
        using var session = Session(new Policy(request => new AiProposal.Answer(
            new UseOfferedAction(request.Frame.State.Id, request.Frame.Stamp.DecisionId, "source-key", "action"), "route")),
            new(TimeSpan.FromSeconds(5), 8, 8));
        for (int index = 0; index <= 4096; index++) {
            var frame = RouteFrame(index); session.Observe(frame);
            await session.EvaluateAsync();
            if (session.TakeCommand() is not { } ticket) throw new InvalidOperationException("The declared route allowance ended before its existing boundary.");
            session.Report(ticket, AiExecutionReceipt.Accepted);
        }
        session.Observe(RouteFrame(4097));
        var result = await session.EvaluateAsync();
        check(result.Status == AiSeatStatus.BudgetExceeded && result.Failure is { Kind: AiFailureKind.RouteLimit, Code: "route-limit", RemainingWork: 8 }
                && session.TakeCommand() == null,
            "Actual bounded route-history saturation reports its own session-proven cause without being mislabeled as exhausted CPU time or work");
    }

    private static AiDecisionFrame RouteFrame(int index)
    {
        var frame = Frame("route-" + index, index + 1);
        var source = new Card { Name = "Owned source", VisualId = "source-id", Key = "source-key" };
        var state = frame.State with { Turn = 1, Players = frame.State.Players.Select(player => player.Id == 0
            ? player with { Zones = [new("Battlefield", 1, [source], null)] } : player).ToImmutableArray(),
            Decision = new(frame.Stamp.DecisionId, new(), new PriorityPayload(new("Pass", true))) };
        var offers = new SourceActionSet(state.Id, state.Decision.Id, source.Key, source.VisualId, [new("action", "activate", "Owned route")]);
        var action = new ActionSemantics("action", ActionEffectKind.Draw, [ActionEffectKind.Draw], [], ActionAffordability.Payable) { StableId = "stable-" + index };
        return new(state, domain: new(index + 1, ActionDomainCoverage.Complete, [offers], [action]));
    }

    private static AiSeatSession Session(IAiPolicy policy, AiSeatLimits? limits = null, TimeProvider? clock = null)
        => new("failure-game", 0, policy, 42, limits ?? new(TimeSpan.FromSeconds(5), 4096), clock);
    private static AiProposal Keep(AiDecisionRequest request)
        => new AiProposal.Answer(new KeepOpeningHand(request.Frame.State.Id, request.Frame.Stamp.DecisionId), "keep");

    private static AiDecisionFrame Frame(string id = "failure-input", long revision = 1)
    {
        var state = new GameSnapshot("failure-game", "playing", null, null, 0, 0, "Pregame", "PREGAME", null,
            [new Player(0, "Actor", 40, false, false, ImmutableDictionary<string, int>.Empty, []),
             new Player(1, "Opponent", 40, false, false, ImmutableDictionary<string, int>.Empty, [])],
            [], null, new(id, new(), new OpeningHandPayload(new("Keep", true), new("Mulligan", true)))) {
                Revision = revision, BoardRevision = revision, DecisionOwnerId = 0
            };
        return new(state, domain: DecisionActionDomain.None);
    }

    private sealed class AsyncPolicy(Func<AiDecisionRequest, CancellationToken, Task<AiProposal>> answer) : IAiPolicy
    {
        public AiPolicyDescriptor Descriptor { get; } = new("async-failure-probe.v1", AiTasks.All);
        public Task<AiProposal> EvaluateAsync(AiDecisionRequest request, CancellationToken cancellation) => answer(request, cancellation);
    }

    private sealed class Clock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        internal void Advance(TimeSpan value) => ticks += value.Ticks;
    }

    private sealed class Diagnostics : IAiHostDiagnostics
    {
        internal bool Throw;
        internal readonly List<AiHostFault> Faults = [];
        public void Report(AiHostFault fault, Exception? error)
        {
            if (Throw) throw new IOException("Unavailable private diagnostics");
            Faults.Add(fault);
        }
    }

    private sealed class Policy(Func<AiDecisionRequest, AiProposal> answer) : IAiPolicy
    {
        public AiPolicyDescriptor Descriptor { get; } = new("failure-probe.v1", AiTasks.All);
        public Task<AiProposal> EvaluateAsync(AiDecisionRequest request, CancellationToken cancellation)
            => Task.FromResult(answer(request));
    }

    private sealed class Provider : IAutomatedSeatProvider
    {
        internal readonly List<GameCommand> Commands = [];
        internal readonly List<string> Faults = [];
        internal int Observations;
        internal bool LoseReceipt;
        public Task EnableAutomatedSeatsAsync(CancellationToken cancellation = default) => Task.CompletedTask;
        public Task<ImmutableArray<AutomatedSeatObservation>> ObserveAutomatedSeatsAsync(CancellationToken cancellation = default)
        {
            Observations++;
            var frame = Frame();
            return Task.FromResult<ImmutableArray<AutomatedSeatObservation>>([new(0, frame.State, frame.Domain, frame.Semantics)]);
        }
        public Task ExecuteAutomatedAsync(int seatId, GameCommand command, CancellationToken cancellation = default)
        {
            Commands.Add(command);
            return LoseReceipt ? Task.FromException(new IOException("Lost receipt")) : Task.CompletedTask;
        }
        public Task ReportAutomationFaultAsync(string gameId, int seatId, string reasonCode, CancellationToken cancellation = default)
        {
            Faults.Add(reasonCode); return Task.CompletedTask;
        }
    }
}
