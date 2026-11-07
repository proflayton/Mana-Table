using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mana.AI;
using Mana.Client;
using Mana.Contracts;

namespace Mana.Table;

// Transparent receipt counters shared by real ordinary-polled lobby journeys.
// Bot observations never enter a connected human reader.
internal static class PolledLobbyPorts
{
    internal enum PlayerReceiptKind { Input, ObservationAcknowledgement, Control }
    internal sealed record PlayerReceiptCounts(int Total, int Inputs, int ObservationAcknowledgements, int Controls)
    {
        internal bool Conserved => Total == Inputs + ObservationAcknowledgements + Controls;
    }
    internal static PlayerReceiptKind ClassifyPlayerReceipt(GameCommand command) => command switch {
        AcknowledgePriorityObservation => PlayerReceiptKind.ObservationAcknowledgement,
        GrantPriorityContinuation or RevokePriorityContinuation or GrantFiniteStackYield or RevokeFiniteStackYield
            or IgnorePriorityAbility or RestorePriorityAbility or UndoAction or UndoLastAction or PassForNow or PassTurn => PlayerReceiptKind.Control,
        PassPriority or AutoPassPriority or KeepOpeningHand or MulliganOpeningHand or PayMana or AutoPayOptionalPayment or CancelPayment
            or CompleteAttackers or CompleteBlockers or CompleteSelection or CancelSelection or AcceptConfirmation or DeclineConfirmation
            or AcknowledgeDecision or AcknowledgeReveal or SelectCard or PlayCard or ActivateCard or UseManaSource or SpendPoolMana
            or SelectPlayer or ToggleAttackPlayer or ToggleAttackPermanent or ToggleBlock or ChooseOptions or OrderOptions
            or ChooseNumber or EnterText or AllocateAmounts or SkipAllocation or UseOfferedAction or PlaceFromHand => PlayerReceiptKind.Input,
        _ => throw new InvalidDataException("The polled receipt observer has no explicit semantic class for " + command.GetType().Name)
    };
    internal sealed record CommandLatency(string Kind, long StartedTicks, double Milliseconds);
    internal sealed class ReadPort(IMatchConnection inner) : IMatchConnection
    {
        public bool SupportsPriorityContinuation => inner.SupportsPriorityContinuation;
        private readonly ConcurrentDictionary<string, GameSnapshot> decisions = new(StringComparer.Ordinal);
        internal Action<GameSnapshot?>? Observed { get; init; }
        internal Action<GameCommand, GameSnapshot?>? Acknowledged { get; init; }
        private int hold;
        private int reads, accepted, inputs, observationAcknowledgements, controls, failures;
        private readonly object receiptGate = new();
        internal int Reads => Volatile.Read(ref reads);
        internal PlayerReceiptCounts ReceiptCounts { get { lock (receiptGate) return new(accepted, inputs, observationAcknowledgements, controls); } }
        internal int Accepted => ReceiptCounts.Total;
        internal int InputAccepted => ReceiptCounts.Inputs;
        internal int Failures => Volatile.Read(ref failures);
        internal ConcurrentQueue<CommandLatency> CommandLatencies { get; } = new();
        internal GameSnapshot? Held { get; private set; }
        internal int AcceptedAtCapture { get; private set; }
        internal int InputAcceptedAtCapture { get; private set; }
        internal TaskCompletionSource Captured { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // Only the owner thread may arm immediately before Client.Poll marks
        // Polling. An existing metadata readback must finish first, otherwise
        // that read could steal the intended ordinary-poll witness.
        internal bool TryArmOrdinaryPoll(ClientSession client)
            => !client.Closed && !client.Busy && !client.Polling && !client.PriorityObservationInFlight
                && Interlocked.CompareExchange(ref hold, 1, 0) == 0;
        public async Task<GameSnapshot?> ObserveAsync()
        {
            Interlocked.Increment(ref reads);
            var state = await inner.ObserveAsync();
            if (state?.Decision is { } decision) decisions[decision.Id] = state;
            Observed?.Invoke(state);
            if (Interlocked.Exchange(ref hold, 0) == 1) {
                Held = state;
                var captured = ReceiptCounts; AcceptedAtCapture = captured.Total; InputAcceptedAtCapture = captured.Inputs;
                Captured.TrySetResult(); await Release.Task;
            }
            return state;
        }
        public async Task ExecuteAsync(GameCommand command)
        {
            long started = Stopwatch.GetTimestamp();
            try {
                var kind = ClassifyPlayerReceipt(command);
                var before = command is DecisionCommand decision ? decisions.GetValueOrDefault(decision.DecisionId) : null;
                await inner.ExecuteAsync(command);
                lock (receiptGate) {
                    accepted++;
                    switch (kind) {
                        case PlayerReceiptKind.Input: inputs++; break;
                        case PlayerReceiptKind.ObservationAcknowledgement: observationAcknowledgements++; break;
                        case PlayerReceiptKind.Control: controls++; break;
                    }
                }
                CommandLatencies.Enqueue(new(command.GetType().Name, started, Stopwatch.GetElapsedTime(started).TotalMilliseconds));
                Acknowledged?.Invoke(command, before);
            }
            catch { Interlocked.Increment(ref failures); throw; }
        }
        public Task ConcedeAsync(string gameId) => throw new InvalidOperationException("The polled prefix never forces concession.");
    }

    // Transparent real presence calls. Contract bytes exclude the IPC/TCP
    // envelope; elapsed times are observations, not a head-of-line guarantee.
    internal sealed class PresencePort(ITablePresence inner) : ITablePresence
    {
        internal sealed record Exchange(long StartedTicks, double Milliseconds, long Sequence,
            long BoardRevision, string? ContextId, TableInspectionSubject? Subject, int ContractBytes, string Outcome,
            string? ChoiceContextId, TableChoicePreview? Choice, TablePresenceFrame? Response);
        private int started, executing, maximumExecuting;
        internal int Started => Volatile.Read(ref started);
        internal int Executing => Volatile.Read(ref executing);
        internal int MaximumExecuting => Volatile.Read(ref maximumExecuting);
        internal ConcurrentQueue<Exchange> Exchanges { get; } = new();
        public bool SupportsTablePresence => inner.SupportsTablePresence;
        public bool SupportsTableChoices => inner.SupportsTableChoices;
        public async Task<TablePresenceResult> ExchangePresenceAsync(TablePresenceUpdate update, CancellationToken cancellation = default)
        {
            long started = Stopwatch.GetTimestamp();
            Interlocked.Increment(ref this.started);
            int active = Interlocked.Increment(ref executing), prior;
            do { prior = MaximumExecuting; if (active <= prior) break; }
            while (Interlocked.CompareExchange(ref maximumExecuting, active, prior) != prior);
            string outcome = "failed";
            TablePresenceFrame? response = null;
            try {
                var result = await inner.ExchangePresenceAsync(update, cancellation);
                outcome = result.GetType().Name;
                response = (result as TablePresenceResult.Current)?.Frame;
                return result;
            } finally {
                Exchanges.Enqueue(new(started, Stopwatch.GetElapsedTime(started).TotalMilliseconds, update.Sequence,
                    update.BoardRevision, update.ContextId, update.Subject, JsonSerializer.SerializeToUtf8Bytes(update).Length, outcome,
                    update.ChoiceContextId, update.Choice, response));
                Interlocked.Decrement(ref executing);
            }
        }
    }

    internal sealed class BotPort(IAutomatedSeatProvider inner) : IAutomatedSeatProvider, IAiHostDiagnostics
    {
        internal sealed record Receipt(int Seat, [property: JsonIgnore] GameCommand Command, ImmutableArray<string> StackExecutions)
        {
            public long MonotonicTicks { get; } = Stopwatch.GetTimestamp();
            public string Kind => Command.GetType().Name;
            public JsonElement Answer => JsonSerializer.SerializeToElement(Command, Command.GetType());
        }
        private readonly ConcurrentDictionary<(int Seat, string Decision), ImmutableArray<string>> decisions = new();
        internal ConcurrentQueue<Receipt> Receipts { get; } = new();
        internal sealed record ProcessingSnapshot(int Accepted, int ProcessedAccepted, int Executing,
            ImmutableDictionary<int, int> ProcessedNonPassBySeat);
        private readonly object receiptGate = new();
        private readonly Queue<Receipt> pendingProcessing = new();
        private ImmutableDictionary<int, int> processedNonPassBySeat = ImmutableDictionary<int, int>.Empty;
        private int processedAccepted;
        private int reads, accepted, executing;
        internal ConcurrentQueue<object> Faults { get; } = new();
        internal int Reads => Volatile.Read(ref reads);
        internal int Accepted => Volatile.Read(ref accepted);
        internal int Executing => Volatile.Read(ref executing);
        internal ProcessingSnapshot CaptureProcessing()
        {
            lock (receiptGate) return new(accepted, processedAccepted, executing, processedNonPassBySeat);
        }
        public Task EnableAutomatedSeatsAsync(CancellationToken cancellation = default) => inner.EnableAutomatedSeatsAsync(cancellation);
        public async Task<ImmutableArray<AutomatedSeatObservation>> ObserveAutomatedSeatsAsync(CancellationToken cancellation = default)
        {
            // This port has one AiTableHost reader. Its step semaphore spans the
            // prior batch's Execute returns and session.Report calls. Only this
            // next real entry confirms that downstream processing, not the ACK
            // itself, Executing==0, or the next read's eventual return.
            lock (receiptGate) {
                while (pendingProcessing.TryDequeue(out var receipt)) {
                    processedAccepted++;
                    if (receipt.Command is UseOfferedAction or PlayCard)
                        processedNonPassBySeat = processedNonPassBySeat.SetItem(receipt.Seat,
                            processedNonPassBySeat.GetValueOrDefault(receipt.Seat) + 1);
                }
            }
            var state = await inner.ObserveAutomatedSeatsAsync(cancellation);
            foreach (var item in state) if (item.State.Decision is { } decision)
                decisions[(item.SeatId, decision.Id)] = item.State.Stack.Select(stack => stack.ExecutionId).ToImmutableArray();
            Interlocked.Increment(ref reads); return state;
        }
        public async Task ExecuteAutomatedAsync(int seatId, GameCommand command, CancellationToken cancellation = default)
        {
            lock (receiptGate) Interlocked.Increment(ref executing);
            try {
                var stack = command is DecisionCommand decision ? decisions.GetValueOrDefault((seatId, decision.DecisionId), []) : [];
                await inner.ExecuteAutomatedAsync(seatId, command, cancellation);
                lock (receiptGate) {
                    var receipt = new Receipt(seatId, command, stack);
                    Receipts.Enqueue(receipt); pendingProcessing.Enqueue(receipt);
                    Interlocked.Increment(ref accepted);
                }
            }
            finally { lock (receiptGate) Interlocked.Decrement(ref executing); }
        }
        public Task ReportAutomationFaultAsync(string gameId, int seatId, string reason, CancellationToken cancellation = default)
            => inner.ReportAutomationFaultAsync(gameId, seatId, reason, cancellation);
        public void Report(AiHostFault fault, Exception? error) => Faults.Enqueue(new { fault, error = error?.ToString() });
    }
}
