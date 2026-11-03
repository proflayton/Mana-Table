using System.Collections.Immutable;
using System.Text.Json;
using Mana.AI;
using Mana.Contracts;

internal static partial class AiHostRegression
{
    private enum ProgressTask { Keep, Payment, Selection, Combat }
    private sealed record ProgressResult(string Case, bool QuietThrough44, bool FaultOnceAt45, bool AuthorityPreserved,
        int Calls, int Commands, int Reports, int Accepted, int Unknown);

    private static async Task ProgressAsync(Action<bool, string> check)
    {
        var results = new List<ProgressResult>();
        foreach (var task in Enum.GetValues<ProgressTask>()) results.Add(await AcceptedFrameProgress(task));
        results.Add(await AcceptedFrameProgress(ProgressTask.Keep, paced: true));
        results.Add(await AcceptedFrameProgress(ProgressTask.Keep, stale: true));
        results.Add(await AcceptedFrameProgress(ProgressTask.Payment, staleDomain: true));
        await FreshProgressScope(check);
        foreach (string waiting in new[] { "resolved", "human", "finished", "missing-seat" })
            await AcceptedFrameRetired(waiting, check);
        check(results.All(result => result.QuietThrough44 && result.FaultOnceAt45 && result.AuthorityPreserved),
            "Repeated successful provider polls cannot hide an accepted decision that never advances; its45s fault is exact, once-only and never replays the known accepted command: "
                + JsonSerializer.Serialize(results));
        foreach (var result in results) {
            check(result.FaultOnceAt45, result.Case + ": exact accepted scope without progress faults once at45s, independently of pacing");
            check(result.AuthorityPreserved, result.Case + ": no-progress diagnostics preserve the original Accepted receipt and issue no replay, fallback or fabricated outcome");
        }
    }

    private static async Task<ProgressResult> AcceptedFrameProgress(ProgressTask task, bool paced = false, bool stale = false,
        bool staleDomain = false)
    {
        var original = ProgressFrame(task, revision: stale ? 2 : 1, domainVersion: staleDomain ? 2 : 1);
        var provider = new ProgressProvider(original);
        var policy = new ProgressPolicy();
        var diagnostics = new Diagnostics();
        var clock = new ProviderDeadlineClock();
        var gate = new ProgressPacing();
        await using var host = new AiTableHost(provider, _ => policy, 42, diagnostics: diagnostics,
            pacing: paced ? gate : null, operationClock: clock);
        await host.StepAsync().WaitAsync(TimeSpan.FromSeconds(5));
        if (paced) gate.Open = false;
        if (stale) provider.Current = ProgressFrame(task, "stale-different-id", 1);
        if (staleDomain) provider.Current = original with { Domain = original.Domain with { Version = 1 } };
        bool quiet = true;
        for (int poll = 0; poll < 4; poll++) {
            clock.Advance(TimeSpan.FromSeconds(11));
            await host.StepAsync().WaitAsync(TimeSpan.FromSeconds(5));
            quiet &= host.LastFault == null && provider.Reports.Count == 0 && provider.Commands.Count == 1 && policy.Calls == 1;
        }
        clock.Advance(TimeSpan.FromSeconds(1));
        await host.StepAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await host.StepAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await host.StepAsync().WaitAsync(TimeSpan.FromSeconds(5));
        int accepted = host.Histories.Sum(history => history.Decisions.Count(record => record.Outcome == AiDecisionOutcome.Accepted));
        int unknown = host.Histories.Sum(history => history.Decisions.Count(record => record.Outcome == AiDecisionOutcome.ExecutionUnknown));
        bool fault = host.LastFault is { GameId: "host-progress", SeatId: 0, ReasonCode: "accepted-no-progress" }
            && provider.Reports.SequenceEqual(["accepted-no-progress"])
            && diagnostics.Faults.Count == 1 && diagnostics.Errors is [TimeoutException];
        bool unchanged = provider.Commands.Count == 1 && policy.Calls == 1 && accepted == 1 && unknown == 0
            && provider.Current.State.Status == "playing" && provider.Current.State.Outcome == null
            && (!staleDomain || ReferenceEquals(provider.Current.State, original.State)
                && original.Domain.Version == 2 && provider.Current.Domain.Version == 1)
            && provider.Commands.Single() is DecisionCommand submitted && submitted.GameId == original.State.Id
            && submitted.DecisionId == original.State.Decision!.Id && GameCommandPermissions.Allows(original.State, submitted);
        return new(task + (paced ? "/pacing-closed" : stale ? "/stale-observation" : staleDomain ? "/stale-domain-version" : ""), quiet, fault, unchanged,
            policy.Calls, provider.Commands.Count, provider.Reports.Count, accepted, unknown);
    }

    private static async Task FreshProgressScope(Action<bool, string> check)
    {
        var provider = new ProgressProvider(ProgressFrame(ProgressTask.Keep));
        var policy = new ProgressPolicy(); var clock = new ProviderDeadlineClock();
        await using var host = new AiTableHost(provider, _ => policy, 42, operationClock: clock);
        await host.StepAsync().WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(44));
        provider.Current = ProgressFrame(ProgressTask.Keep, "fresh-authority", 2);
        await host.StepAsync().WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(44));
        await host.StepAsync().WaitAsync(TimeSpan.FromSeconds(5));
        check(host.LastFault == null && provider.Reports.Count == 0 && policy.Calls == 2
                && provider.Commands.Select(command => ((DecisionCommand)command).DecisionId).SequenceEqual(["accepted-current", "fresh-authority"]),
            "A valid newer authoritative scope clears the old accepted wait and gets its own allowance; elapsed time from the preceding input cannot fault it");
        provider.Current = provider.Current with { State = provider.Current.State with {
            Revision = 3, BoardRevision = 3, Decision = null, DecisionOwnerId = null, PriorityPlayerId = null, ActionClass = "resolving"
        } };
        clock.Advance(TimeSpan.FromSeconds(46));
        await host.StepAsync().WaitAsync(TimeSpan.FromSeconds(5));
        check(host.LastFault == null && provider.Commands.Count == 2 && provider.Reports.Count == 0,
            "A genuinely retired accepted input remains retired beyond its former deadline instead of generating a delayed fault");
    }

    private static async Task AcceptedFrameRetired(string waiting, Action<bool, string> check)
    {
        var provider = new ProgressProvider(ProgressFrame(ProgressTask.Keep));
        var policy = new ProgressPolicy(); var clock = new ProviderDeadlineClock();
        await using var host = new AiTableHost(provider, _ => policy, 42, operationClock: clock);
        await host.StepAsync().WaitAsync(TimeSpan.FromSeconds(5));
        if (waiting == "missing-seat") provider.Present = false;
        else provider.Current = provider.Current with { State = provider.Current.State with {
            Revision = 2, BoardRevision = 2, Decision = null,
            DecisionOwnerId = waiting == "human" ? 1 : null, PriorityPlayerId = waiting == "human" ? 1 : null,
            ActivePlayerId = waiting == "human" ? 1 : 0, ActionClass = waiting == "human" ? "priority" : "resolving",
            Status = waiting == "finished" ? "finished" : "playing"
        } };
        clock.Advance(TimeSpan.FromSeconds(46));
        await host.StepAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await host.StepAsync().WaitAsync(TimeSpan.FromSeconds(5));
        check(host.LastFault == null && provider.Reports.Count == 0 && provider.Commands.Count == 1 && policy.Calls == 1,
            "An accepted input followed by " + waiting + " never borrows another player's/engine's waiting time or fabricates a45s failure");
    }

    private static AutomatedSeatObservation ProgressFrame(ProgressTask task, string decision = "accepted-current", long revision = 1,
        long domainVersion = 1)
    {
        DecisionPayload payload = task switch {
            ProgressTask.Keep => new OpeningHandPayload(new("Keep", true), new("Mulligan", true)),
            ProgressTask.Payment => new PaymentPayload(new("Pay", true), new("Cancel", false)),
            ProgressTask.Selection => new EntitySelectionPayload(new("Done", false), new("Cancel", false), [1], new(1, 1)),
            ProgressTask.Combat => new AttackPayload(new("Attack", true)),
            _ => throw new ArgumentOutOfRangeException(nameof(task))
        };
        var players = Enumerable.Range(0, 2).Select(seat => new Player(seat, seat == 0 ? "Bot" : "Human", 40, seat == 1, false,
            ImmutableDictionary<string, int>.Empty, [new("Hand", 7, [], null)])).ToImmutableArray();
        return new(0, new GameSnapshot("host-progress", "playing", null, null, 0, task == ProgressTask.Keep ? 0 : 1,
            task == ProgressTask.Keep ? "Pregame" : "Current input", task == ProgressTask.Keep ? "PREGAME" : task == ProgressTask.Combat ? "COMBAT_DECLARE_ATTACKERS" : "MAIN1",
            task == ProgressTask.Keep ? null : 0, players, [], task == ProgressTask.Combat ? new([], [], null) : null,
            new(decision, new(), payload)) { Revision = revision, BoardRevision = revision, DecisionOwnerId = 0 },
            task == ProgressTask.Payment ? new(domainVersion, ActionDomainCoverage.Complete, [], []) : DecisionActionDomain.None, new());
    }

    private sealed class ProgressPolicy : IAiPolicy
    {
        internal int Calls;
        public AiPolicyDescriptor Descriptor { get; } = new("accepted-progress-test.v1", AiTasks.All);
        public Task<AiProposal> EvaluateAsync(AiDecisionRequest request, CancellationToken cancellation)
        {
            Interlocked.Increment(ref Calls);
            var state = request.Frame.State; var decision = state.Decision!;
            DecisionCommand command = decision.Payload switch {
                OpeningHandPayload => new KeepOpeningHand(state.Id, decision.Id),
                PaymentPayload => new PayMana(state.Id, decision.Id),
                EntitySelectionPayload => new SelectPlayer(state.Id, decision.Id, 1),
                AttackPayload => new CompleteAttackers(state.Id, decision.Id),
                _ => throw new InvalidDataException("Unexpected accepted-progress fixture input.")
            };
            return Task.FromResult<AiProposal>(new AiProposal.Answer(command, "answer-current"));
        }
    }
    private sealed class ProgressPacing : IAiHostPacing
    {
        internal bool Open = true;
        public void Observe(ImmutableArray<AiTableProgress> progress) { }
        public bool Ready(string gameId, int seatId) => Open;
    }
    private sealed class ProgressProvider(AutomatedSeatObservation current) : IAutomatedSeatProvider
    {
        internal AutomatedSeatObservation Current = current;
        internal bool Present = true;
        internal List<GameCommand> Commands { get; } = [];
        internal List<string> Reports { get; } = [];
        public Task EnableAutomatedSeatsAsync(CancellationToken cancellation = default) => Task.CompletedTask;
        public Task<ImmutableArray<AutomatedSeatObservation>> ObserveAutomatedSeatsAsync(CancellationToken cancellation = default)
            => Task.FromResult<ImmutableArray<AutomatedSeatObservation>>(Present ? [Current] : []);
        public Task ExecuteAutomatedAsync(int seatId, GameCommand command, CancellationToken cancellation = default)
        {
            if (seatId != Current.SeatId || !GameCommandPermissions.Allows(Current.State, command))
                throw new InvalidDataException("An unauthorized progress-fixture command was submitted.");
            Commands.Add(command); return Task.CompletedTask;
        }
        public Task ReportAutomationFaultAsync(string gameId, int seatId, string reasonCode, CancellationToken cancellation = default)
        {
            if (gameId != Current.State.Id || seatId != Current.SeatId) throw new InvalidDataException("Foreign progress fault audience.");
            Reports.Add(reasonCode); return Task.CompletedTask;
        }
    }
}
