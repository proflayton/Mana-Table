using System.Collections.Immutable;
using Mana.AI;
using Mana.Contracts;

internal static class AiHostRegression
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        static AutomatedSeatObservation Frame(int seat, long revision = 1) {
            var players = Enumerable.Range(0, 2).Select(id => new Player(id, "Seat", 40, false, false,
                ImmutableDictionary<string, int>.Empty, [new("Hand", 7,
                    id == seat ? [new Card { Name = "Known only here", VisualId = "private-" + id }] : [], null)])).ToImmutableArray();
            var state = new GameSnapshot("host-game", "playing", null, null, seat, 0, "Pregame", "PREGAME", null,
                players, [], null, new("keep-" + seat, new(), new OpeningHandPayload(new("Keep", true), new("Mulligan", true)))) {
                Revision = revision, DecisionOwnerId = seat
            };
            return new(seat, state, DecisionActionDomain.None, new());
        }
        var provider = new Provider { Observations = [Frame(0), Frame(1)] };
        await using (var host = new AiTableHost(provider, seat => new SeatPolicy(seat), 42)) {
            await host.StepAsync(); await host.StepAsync();
            check(provider.Commands.Count == 2 && provider.Commands.Select(item => item.Seat).Distinct().Count() == 2
                && host.LastFault == null, "The bot host uses independent seat observations and hands off each answer exactly once");
            check(host.Histories.All(history => history.Decisions.Count(record => record.Outcome == AiDecisionOutcome.Accepted) == 1),
                "Host history records actual per-seat engine acceptance without inventing resolution");
            provider.Observations = [];
            await host.StepAsync();
            check(host.Histories.IsEmpty, "Leaving a table revokes its bot sessions and private history");
        }
        var rejected = new Provider { Observations = [Frame(0)], Reject = true };
        await using (var host = new AiTableHost(rejected, seat => new SeatPolicy(seat), 42)) {
            await host.StepAsync(); await host.StepAsync(); await host.StepAsync();
            check(rejected.Commands.Count == 1 && host.LastFault?.ReasonCode == "rejected-command" && rejected.Faults.Count == 1,
                "A rejected unchanged decision becomes an explicit bot fault instead of replaying or silently stalling");
        }
        var unknown = new Provider { Observations = [Frame(0)], Unknown = true };
        await using (var host = new AiTableHost(unknown, seat => new SeatPolicy(seat), 42)) {
            await host.StepAsync(); await host.StepAsync();
            check(unknown.Commands.Count == 1 && host.LastFault?.ReasonCode == "execution-unknown" && unknown.Faults.Count == 1,
                "A lost engine receipt stops that bot table without a retry, pass, concession or legacy fallback");
        }
    }

    private sealed class SeatPolicy(int seat) : IAiPolicy
    {
        public AiPolicyDescriptor Descriptor { get; } = new("host-test.v1", AiTasks.All);
        public Task<AiProposal> EvaluateAsync(AiDecisionRequest request, CancellationToken cancellation)
        {
            var state = request.Frame.State;
            if (state.ViewerId != seat || state.Players.Any(player => player.Id != seat && !player.Zone("Hand").Cards.IsEmpty))
                throw new InvalidOperationException("Cross-seat knowledge");
            return Task.FromResult<AiProposal>(new AiProposal.Answer(new KeepOpeningHand(state.Id, state.Decision!.Id), "keep"));
        }
    }
    private sealed class Provider : IAutomatedSeatProvider
    {
        internal ImmutableArray<AutomatedSeatObservation> Observations = [];
        internal bool Reject, Unknown;
        internal List<(int Seat, GameCommand Command)> Commands = [];
        internal List<string> Faults = [];
        public Task EnableAutomatedSeatsAsync() => Task.CompletedTask;
        public Task<ImmutableArray<AutomatedSeatObservation>> ObserveAutomatedSeatsAsync() => Task.FromResult(Observations);
        public Task ExecuteAutomatedAsync(int seatId, GameCommand command) {
            Commands.Add((seatId, command));
            if (Reject) throw new EngineCommandRejectedException("stale");
            if (Unknown) throw new IOException("receipt lost");
            return Task.CompletedTask;
        }
        public Task ReportAutomationFaultAsync(string gameId, int seatId, string reasonCode) { Faults.Add(reasonCode); return Task.CompletedTask; }
    }
}
