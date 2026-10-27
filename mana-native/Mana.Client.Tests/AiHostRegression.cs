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
        var evidence = new Diagnostics();
        await using (var host = new AiTableHost(unknown, seat => new SeatPolicy(seat), 42, diagnostics: evidence)) {
            await host.StepAsync(); await host.StepAsync();
            check(unknown.Commands.Count == 1 && host.LastFault?.ReasonCode == "execution-unknown" && unknown.Faults.Count == 1,
                "A lost engine receipt stops that bot table without a retry, pass, concession or legacy fallback");
            check(evidence.Errors.Single() is IOException { Message: "receipt lost" },
                "An unknown receipt retains its original host exception without sending that message through the public fault port");
        }
        var failedObservation = new Provider { Observations = [Frame(0)], ObserveFailureAfterFirst = true };
        evidence = new();
        await using (var host = new AiTableHost(failedObservation, seat => new SeatPolicy(seat), 42, diagnostics: evidence)) {
            await host.StartAsync();
            await failedObservation.Faulted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        check(evidence.Errors.Single() is IOException { Message: "observation failed", InnerException: InvalidDataException }
            && evidence.Faults.First().SeatId == -1 && failedObservation.Faults.Single() == "provider-failed",
            "A background provider failure retains the original cause chain without blaming the first bot or publishing private exception details");
        var failedStorage = new Provider { Observations = [Frame(0)], Unknown = true };
        await using (var host = new AiTableHost(failedStorage, seat => new SeatPolicy(seat), 42, diagnostics: new Diagnostics { Throw = true })) {
            await host.StepAsync();
            check(host.LastFault?.ReasonCode == "execution-unknown" && failedStorage.Faults.Count == 1,
                "Unavailable diagnostic storage never prevents the host from stopping an unsafe automated match");
        }
        var paced = new Provider { Observations = [Frame(0)] };
        var gate = new PaceGate();
        await using (var host = new AiTableHost(paced, seat => new SeatPolicy(seat), 42, pacing: gate)) {
            await host.StepAsync();
            check(paced.Commands.Count == 0 && gate.Last is { GameId: "host-game" },
                "A host presentation beat withholds a bot command without changing its legal offers or consulting private faces");
            var fresh = Frame(0, 2);
            paced.Observations = [fresh with { State = fresh.State with { Decision = fresh.State.Decision! with { Id = "fresh-after-beat" } } }];
            gate.Open = true;
            await host.StepAsync();
            check(paced.Commands.Single().Command is KeepOpeningHand { DecisionId: "fresh-after-beat" },
                "Finishing a presentation beat re-observes and answers the current prompt instead of retaining an older command");
        }
        var clock = new Clock();
        var cadence = new TimedAiHostPacing(TimeSpan.FromSeconds(2.4), clock);
        var baseline = new AiTableProgress("pace-game", 0, 1, 0, [], 1);
        cadence.Observe([baseline]);
        check(cadence.Ready(baseline.GameId, baseline.SeatId), "An unchanged opening table does not insert an artificial bot pause");
        var announced = baseline with { StackIds = ["actual-cast"], PublicActionId = 2 };
        var otherAudience = announced with { SeatId = 1, PublicActionId = 19 };
        cadence.Observe([announced, otherAudience]);
        check(!cadence.Ready(announced.GameId, announced.SeatId), "A public cast receives an announcement beat before another bot action");
        clock.Advance(TimeSpan.FromSeconds(2));
        cadence.Observe([announced, otherAudience]);
        check(!cadence.Ready(announced.GameId, announced.SeatId), "Polling an unchanged announced effect does not finish its beat early");
        clock.Advance(TimeSpan.FromSeconds(.5));
        cadence.Observe([announced, otherAudience]);
        check(cadence.Ready(announced.GameId, announced.SeatId) && cadence.Ready(otherAudience.GameId, otherAudience.SeatId),
            "Divergent viewer-local activity IDs cannot restart each other's beats or stall multiplayer bots");
        cadence.Observe([announced with { StackIds = [], PublicActionId = 3 }, otherAudience]);
        check(!cadence.Ready(announced.GameId, announced.SeatId),
            "Actual resolution receives its own beat before the next automated action");
        cadence.Observe([]);
        check(!cadence.Ready(announced.GameId, announced.SeatId), "Leaving the match clears its public cadence state");
    }

    private sealed class Clock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        internal void Advance(TimeSpan duration) => ticks += duration.Ticks;
    }

    private sealed class PaceGate : IAiHostPacing
    {
        internal bool Open;
        internal AiTableProgress? Last;
        public void Observe(ImmutableArray<AiTableProgress> progress) => Last = progress.FirstOrDefault();
        public bool Ready(string gameId, int seatId) => Open;
    }

    private sealed class Diagnostics : IAiHostDiagnostics
    {
        internal bool Throw;
        internal List<AiHostFault> Faults = [];
        internal List<Exception> Errors = [];
        public void Report(AiHostFault fault, Exception? error) {
            if (Throw) throw new IOException("storage unavailable");
            Faults.Add(fault);
            if (error != null) Errors.Add(error);
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
        internal bool Reject, Unknown, ObserveFailureAfterFirst;
        private int observations;
        internal TaskCompletionSource Faulted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal List<(int Seat, GameCommand Command)> Commands = [];
        internal List<string> Faults = [];
        public Task EnableAutomatedSeatsAsync() => Task.CompletedTask;
        public Task<ImmutableArray<AutomatedSeatObservation>> ObserveAutomatedSeatsAsync() => ObserveFailureAfterFirst && observations++ > 0
            ? Task.FromException<ImmutableArray<AutomatedSeatObservation>>(new IOException("observation failed", new InvalidDataException("original provider cause")))
            : Task.FromResult(Observations);
        public Task ExecuteAutomatedAsync(int seatId, GameCommand command) {
            Commands.Add((seatId, command));
            if (Reject) throw new EngineCommandRejectedException("stale");
            if (Unknown) throw new IOException("receipt lost");
            return Task.CompletedTask;
        }
        public Task ReportAutomationFaultAsync(string gameId, int seatId, string reasonCode) { Faults.Add(reasonCode); Faulted.TrySetResult(); return Task.CompletedTask; }
    }
}
