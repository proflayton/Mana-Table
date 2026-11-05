using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Mana.AI;
using Mana.Contracts;

namespace Mana.Table;

// One actual background host owns this port. Authored current decisions and
// provider barriers test receipt order, not Magic effects or input consumption.
internal static class PolledReceiptProcessingChecks
{
    private const int AcceptedTarget = 320;

    internal static async Task RunAsync(string profile, Action<bool, string> check)
    {
        var inner = new CurrentInputs();
        var port = new PolledLobbyPorts.BotPort(inner);
        var gate = new HostReceiptGate(port);
        var host = new AiTableHost(gate, _ => new OfferedPolicy(), 42, new(TimeSpan.FromSeconds(3), 2048));
        var trace = new List<object>();
        void Record(string stage)
        {
            var snapshot = port.CaptureProcessing();
            trace.Add(new { stage, snapshot, reads = inner.Reads,
                commands = inner.Commands.Count, retained = host.Histories.Select(history => new {
                    records = history.Decisions.Length,
                    accepted = history.Decisions.Count(row => row.Outcome == AiDecisionOutcome.Accepted),
                    nonPass = history.Decisions.Count(row => row.Outcome == AiDecisionOutcome.Accepted
                        && row.Command is nameof(UseOfferedAction) or nameof(PlayCard))
                }).ToArray() });
        }
        try {
            await host.StartAsync();
            await gate.ProviderAcknowledged.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var raw = port.CaptureProcessing(); Record("provider-ack-before-host-receipt");
            check(raw.Accepted == 1 && raw.Executing == 0 && raw.ProcessedAccepted == 0
                    && raw.ProcessedNonPassBySeat.IsEmpty
                    && host.Histories.Sum(history => history.Decisions.Count(row => row.Outcome == AiDecisionOutcome.Accepted)) == 0,
                "A successful provider ACK and Executing zero do not certify a host receipt before the actual AI host resumes");

            gate.ReleaseHostReceipt.TrySetResult();
            await inner.FirstNextEntry.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var firstProcessed = port.CaptureProcessing(); Record("next-observe-entry-before-provider-return");
            check(firstProcessed.Accepted == 1 && firstProcessed.ProcessedAccepted == 1
                    && firstProcessed.ProcessedNonPassBySeat.GetValueOrDefault(0) == 1 && inner.Commands.Count == 1,
                "The next real observation entry certifies the previous host batch before that observation returns, without inventing another command");

            inner.ReleaseSupersededRead.TrySetResult();
            await inner.RepeatedEntry.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var repeated = port.CaptureProcessing(); Record("superseded-read-next-entry");
            check(repeated.Accepted == 1 && repeated.ProcessedAccepted == 1
                    && repeated.ProcessedNonPassBySeat.GetValueOrDefault(0) == 1 && inner.Commands.Count == 1
                    && host.LastFault == null,
                "A superseded discovery followed by another real entry cannot recount an already processed receipt or replay its command");

            inner.ReleaseFreshRead.TrySetResult();
            await inner.FinalEntry.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var finished = port.CaptureProcessing(); Record("history-ring-evicted-original-receipts");
            var histories = host.Histories;
            int retainedAccepted = histories.Sum(history => history.Decisions.Count(row => row.Outcome == AiDecisionOutcome.Accepted));
            check(histories.Length == 2 && histories.All(history => history.Decisions.Length == 128)
                    && retainedAccepted < finished.Accepted
                    && histories.All(history => history.Decisions.All(row => row.Command != nameof(UseOfferedAction))),
                "The unchanged default 128-record AI histories evict earlier acceptance and development records during sustained real host processing");
            check(finished.Accepted == AcceptedTarget && finished.ProcessedAccepted == AcceptedTarget && finished.Executing == 0
                    && finished.ProcessedNonPassBySeat.Count == 2
                    && finished.ProcessedNonPassBySeat.GetValueOrDefault(0) == 1
                    && finished.ProcessedNonPassBySeat.GetValueOrDefault(1) == 1
                    && inner.Commands.Count == AcceptedTarget
                    && inner.Commands.Select(row => ((DecisionCommand)row.Command).DecisionId).Distinct().Count() == AcceptedTarget,
                "Cumulative processed receipts and both seats' non-pass witnesses survive ring eviction with every current command submitted exactly once");
            check(raw.ProcessedAccepted == 0 && raw.ProcessedNonPassBySeat.IsEmpty
                    && firstProcessed.ProcessedAccepted == 1 && firstProcessed.ProcessedNonPassBySeat.Count == 1,
                "Captured receipt snapshots remain immutable while the same host processes later batches");

            await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Record("disposed-after-held-read");
            check(port.CaptureProcessing() == finished && inner.Commands.Count == AcceptedTarget
                    && inner.Faults.IsEmpty && port.Faults.IsEmpty && host.LastFault == null,
                "Cancelling and joining the host at its held next read creates no late receipt, replay or fault qualification");
        } finally {
            gate.ReleaseHostReceipt.TrySetResult(); inner.ReleaseSupersededRead.TrySetResult(); inner.ReleaseFreshRead.TrySetResult();
            await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await File.WriteAllTextAsync(Path.Combine(profile, "polled-receipt-processing.json"), JsonSerializer.Serialize(trace));
        }
    }

    private sealed class OfferedPolicy : IAiPolicy
    {
        public AiPolicyDescriptor Descriptor { get; } = new("polled-receipt-test.v1", AiTasks.All);
        public Task<AiProposal> EvaluateAsync(AiDecisionRequest request, CancellationToken cancellation)
        {
            var frame = request.Frame;
            DecisionCommand command = frame.Domain.Sources.FirstOrDefault() is { } source
                ? new UseOfferedAction(frame.State.Id, frame.State.Decision!.Id, source.SourceCardKey, source.Actions[0].Id)
                : new PassPriority(frame.State.Id, frame.State.Decision!.Id);
            return Task.FromResult<AiProposal>(new AiProposal.Answer(command, "answer-current-offer"));
        }
    }

    // Holds after the transparent port has observed an ACK, before the actual
    // AiTableHost continuation can call AiSeatSession.Report(Accepted).
    private sealed class HostReceiptGate(PolledLobbyPorts.BotPort port) : IAutomatedSeatProvider
    {
        private int acknowledged;
        internal TaskCompletionSource ProviderAcknowledged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseHostReceipt { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task EnableAutomatedSeatsAsync(CancellationToken cancellation = default) => port.EnableAutomatedSeatsAsync(cancellation);
        public Task<ImmutableArray<AutomatedSeatObservation>> ObserveAutomatedSeatsAsync(CancellationToken cancellation = default)
            => port.ObserveAutomatedSeatsAsync(cancellation);
        public async Task ExecuteAutomatedAsync(int seatId, GameCommand command, CancellationToken cancellation = default)
        {
            await port.ExecuteAutomatedAsync(seatId, command, cancellation);
            if (Interlocked.Increment(ref acknowledged) == 1) {
                ProviderAcknowledged.TrySetResult(); await ReleaseHostReceipt.Task.WaitAsync(cancellation);
            }
        }
        public Task ReportAutomationFaultAsync(string gameId, int seatId, string reason, CancellationToken cancellation = default)
            => port.ReportAutomationFaultAsync(gameId, seatId, reason, cancellation);
    }

    private sealed class CurrentInputs : IAutomatedSeatProvider
    {
        private int reads;
        private ImmutableArray<AutomatedSeatObservation> current = [];
        internal int Reads => Volatile.Read(ref reads);
        internal ConcurrentQueue<(int Seat, GameCommand Command)> Commands { get; } = new();
        internal ConcurrentQueue<string> Faults { get; } = new();
        internal TaskCompletionSource FirstNextEntry { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseSupersededRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource RepeatedEntry { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseFreshRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource FinalEntry { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task EnableAutomatedSeatsAsync(CancellationToken cancellation = default) => Task.CompletedTask;
        public async Task<ImmutableArray<AutomatedSeatObservation>> ObserveAutomatedSeatsAsync(CancellationToken cancellation = default)
        {
            int read = Interlocked.Increment(ref reads);
            if (read == 2) {
                FirstNextEntry.TrySetResult(); await ReleaseSupersededRead.Task.WaitAsync(cancellation);
                throw new EngineObservationSupersededException();
            }
            if (read == 3) { RepeatedEntry.TrySetResult(); await ReleaseFreshRead.Task.WaitAsync(cancellation); }
            if (Commands.Count == AcceptedTarget) {
                FinalEntry.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellation);
            }
            int active = Commands.Count % 2;
            current = [Frame(0, active, read, Commands.Count < 2), Frame(1, active, read, Commands.Count < 2)];
            return current;
        }
        public Task ExecuteAutomatedAsync(int seatId, GameCommand command, CancellationToken cancellation = default)
        {
            var state = current.Single(row => row.SeatId == seatId).State;
            if (!GameCommandPermissions.Allows(state, command)) throw new InvalidDataException("The authored host received a command outside its actual current seat input.");
            Commands.Enqueue((seatId, command)); return Task.CompletedTask;
        }
        public Task ReportAutomationFaultAsync(string gameId, int seatId, string reason, CancellationToken cancellation = default)
        { Faults.Enqueue(reason); return Task.CompletedTask; }
    }

    private static AutomatedSeatObservation Frame(int viewer, int active, int revision, bool nonPass)
    {
        bool owned = viewer == active;
        var card = new Card { Key = "current-land-" + viewer, VisualId = "physical-land-" + viewer,
            Name = "Forest", Type = "Basic Land - Forest", Selectable = owned && nonPass };
        var players = Enumerable.Range(0, 2).Select(id => new Player(id, "Bot " + id, 40, false, false,
            ImmutableDictionary<string, int>.Empty, [new("Hand", 1, id == viewer ? [card] : [], null)])).ToImmutableArray();
        var state = new GameSnapshot("receipt-processing-game", "playing", null, null, viewer, 1, "Main phase", "MAIN1", active,
            players, [], null, owned ? new("priority-" + revision, new(), new PriorityPayload(new("Pass", true))) : null) {
                Revision = revision, BoardRevision = revision, DecisionOwnerId = active, PriorityPlayerId = active
            };
        var domain = !owned ? DecisionActionDomain.None : nonPass
            ? new DecisionActionDomain(revision, ActionDomainCoverage.Complete,
                [new(state.Id, state.Decision!.Id, card.Key, card.VisualId,
                    [new("land-offer-" + revision, "play", "Play this land")]) { Revision = revision }],
                [new("land-offer-" + revision, ActionEffectKind.Land, [ActionEffectKind.Land], [], ActionAffordability.Payable)])
            : new(revision, ActionDomainCoverage.Complete, [], []);
        return new(viewer, state, domain, new());
    }
}
