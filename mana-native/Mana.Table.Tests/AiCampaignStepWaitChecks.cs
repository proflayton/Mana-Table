using System.Collections.Immutable;
using Mana.AI;
using Mana.Contracts;

namespace Mana.Table;

internal static class AiCampaignStepWaitChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var clock = new EngineDiagnosticsChecks.Clock();
        var port = new Port(); var policy = new CountingPolicy();
        await using var host = new AiTableHost(port, _ => policy, 42);
        var wait = new AiCampaignStepWait("manual-cycle", clock);
        bool stop = false;
        async Task Drive()
        {
            for (int cycle = 0; cycle < 3 && !stop; cycle++) {
                await host.StepAsync();
                var current = await port.ObservePublicAsync();
                if (current.ViewerId != -1 || current.Decision != null || current.Error != null
                    || port.Last.Any(row => row.State.Id != current.Id || row.State.ViewerId != row.SeatId))
                    throw new InvalidDataException("The controlled independent spectator/private boundary is invalid.");
                await wait.WaitAsync(current);
            }
        }
        var driving = Drive();
        try {
            await Until(() => clock.PendingTimers == 1 || driving.IsCompleted);
            if (driving.IsCompleted) await driving;
            check(port.Reads == 1 && port.PublicReads == 1 && policy.Calls == 0 && port.Commands.Count == 0,
                "The first independently validated manual public observation baselines through the original one-millisecond timer");
            clock.Advance(TimeSpan.FromMilliseconds(1));
            await Until(() => clock.PendingTimers == 1 || driving.IsCompleted);
            if (driving.IsCompleted) await driving;
            Console.WriteLine($"Manual active-cycle witness: privateReads={port.Reads}; publicReads={port.PublicReads}; policy={policy.Calls}; commands={port.Commands.Count}; timers={clock.PendingTimers}");
            check(port.Reads == 3 && port.PublicReads == 3 && policy.Calls == 1
                    && port.Commands is [PassPriority { DecisionId: "independent-current-priority" }]
                    && host.Histories.Sum(history => history.Decisions.Count(row => row.Outcome == AiDecisionOutcome.Accepted)) == 1
                    && host.LastFault == null,
                "Validated manual public progress cooperatively reaches one independent current policy scope before another timer, without a command or control explaining that progress");
            check(clock.GetTimestamp() == TimeSpan.TicksPerMillisecond && !driving.IsCompleted,
                "An unchanged public follow-up restores the original timer without advancing the fake clock or spinning through another read");
        } finally {
            stop = true; clock.Advance(TimeSpan.FromMilliseconds(1));
            await driving.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) { timeout.Token.ThrowIfCancellationRequested(); await Task.Yield(); }
    }

    private static GameSnapshot State(long board, long revision, int viewer, bool prompt = false)
        => new("manual-cycle", "playing", null, null, viewer, 1, "First main", "MAIN1", 0,
            Enumerable.Range(0, 4).Select(id => new Player(id, "Seat", 40, true, false,
                ImmutableDictionary<string, int>.Empty, [new("Battlefield", 0, [], null)])).ToImmutableArray(), [], null,
            prompt ? new("independent-current-priority", new(), new PriorityPayload(new("Pass", true))) : null) {
                Revision = revision, BoardRevision = board, ActionClass = prompt ? "priority" : "resolving",
                DecisionOwnerId = prompt ? 0 : null, PriorityPlayerId = prompt ? 0 : null
            };

    // An authored independent stream, not a rules game or cached private frame.
    private sealed class Port : IAutomatedSeatProvider
    {
        internal int Reads, PublicReads;
        internal readonly List<GameCommand> Commands = [];
        internal ImmutableArray<AutomatedSeatObservation> Last = [];
        public Task EnableAutomatedSeatsAsync(CancellationToken cancellation = default) => Task.CompletedTask;
        public Task<ImmutableArray<AutomatedSeatObservation>> ObserveAutomatedSeatsAsync(CancellationToken cancellation = default)
        {
            int read = ++Reads;
            Last = Enumerable.Range(0, 4).Select(seat => new AutomatedSeatObservation(seat,
                State(read == 1 ? 1 : 2, read, seat, prompt: read == 3 && seat == 0),
                read == 3 && seat == 0 ? new(read, ActionDomainCoverage.Complete, [], []) : DecisionActionDomain.None,
                new())).ToImmutableArray();
            return Task.FromResult(Last);
        }
        internal Task<GameSnapshot> ObservePublicAsync()
        {
            int read = ++PublicReads;
            return Task.FromResult(State(read == 1 ? 1 : 2, read, -1));
        }
        public Task ExecuteAutomatedAsync(int seatId, GameCommand command, CancellationToken cancellation = default)
        {
            var row = Last.Single(row => row.SeatId == seatId);
            if (command is not DecisionCommand decision
                || !new AiDecisionFrame(row.State, domain: row.Domain, semantics: row.Semantics).Allows(decision))
                throw new EngineCommandRejectedException("Only the independently observed current fixture input is offered.");
            Commands.Add(command); return Task.CompletedTask;
        }
        public Task ReportAutomationFaultAsync(string gameId, int seatId, string reasonCode, CancellationToken cancellation = default)
            => Task.CompletedTask;
    }

    private sealed class CountingPolicy : IAiPolicy
    {
        private readonly CasualCommanderPolicy inner = new();
        internal int Calls;
        public AiPolicyDescriptor Descriptor => inner.Descriptor;
        public Task<AiProposal> EvaluateAsync(AiDecisionRequest request, CancellationToken cancellation)
        { Calls++; return inner.EvaluateAsync(request, cancellation); }
    }
}
