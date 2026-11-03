using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.Table;

internal sealed record AiCausalPaymentEvidence(long Sequence, string ExecutionId, string Kind, int PayerId,
    ImmutableArray<ManaBillPart> BillBefore, ImmutableArray<ManaBillPart> BillAfter,
    ImmutableDictionary<string, int>? Spent);

internal sealed partial class AiCausalMechanicEvidence
{
    private sealed class CastAttempt(AutomatedSeatObservation before, GameCommand command, string sourceId,
        string sourceName, Ledger ledger)
    {
        internal readonly AutomatedSeatObservation Before = before;
        internal readonly GameCommand Command = command;
        internal readonly string SourceId = sourceId, SourceName = sourceName;
        internal readonly long PriorActivity = before.State.Activity.Select(item => item.Id).DefaultIfEmpty().Max();
        internal readonly long PriorSequence = before.State.Executions!.LastSequence;
        internal readonly long PriorHistory = ledger.HistoryCursor;
        internal readonly int Epoch = ledger.Epoch, HistoryEpoch = ledger.HistoryEpoch;
        internal readonly bool HistoryBaseline = ledger.HasHistory;
        internal readonly ImmutableHashSet<string> PriorOccurrences = before.State.Executions!.Occurrences
            .Select(item => item.Id).ToImmutableHashSet(StringComparer.Ordinal);
        internal readonly ImmutableHashSet<string> Traxos = before.State.Viewer!.Zone("Battlefield").Cards
            .Where(card => !card.FaceDown && card.Name == "Traxos, Scourge of Kroog")
            .Select(CardViews.Identity).ToImmutableHashSet(StringComparer.Ordinal);
        internal string ExecutionId = "";
        internal bool Ambiguous, Competing, UntapRecorded, PaymentRecorded, HistoryFinished;
    }
    private readonly List<CastAttempt> casts = [];
    private readonly HashSet<(string Game, int Player, string Execution)> restoredPayments = [];
    private readonly HashSet<(string Game, int Player)> historyUnavailable = [];
    private const int MaximumHistoryRecords = 1024, MaximumHistoryBytes = 4 * 1024 * 1024;

    internal long HistoryCursor(string gameId, int playerId) =>
        ledgers.TryGetValue((gameId, playerId), out var ledger) ? ledger.HistoryCursor : 0;

    internal ImmutableArray<AiCausalPaymentEvidence> PaymentsFor(string gameId, int playerId, string executionId) =>
        !ledgers.TryGetValue((gameId, playerId), out var ledger) ? [] : ledger.History.Values
            .Where(item => item.ExecutionId == executionId && item.Payment?.PayerId == playerId
                && item.Payment.BillBefore != null && item.Payment.BillAfter != null)
            .Select(item => new AiCausalPaymentEvidence(item.Sequence, executionId, item.Payment!.Kind, playerId,
                item.Payment.BillBefore!.Value, item.Payment.BillAfter!.Value,
                item.Payment.Spent)).ToImmutableArray();

    internal ImmutableArray<ActivityEntry> CastFactsFor(string gameId, int playerId, string executionId) =>
        !ledgers.TryGetValue((gameId, playerId), out var ledger) ? [] : ledger.History.Values
            .Where(item => item.ExecutionId == executionId && item.Activity is { Kind: "cast" } cast
                && cast.ExecutionId == executionId && cast.PlayerId == playerId)
            .Select(item => item.Activity!).ToImmutableArray();

    internal bool NeedsHistory(string gameId, int playerId) =>
        ledgers.TryGetValue((gameId, playerId), out var ledger)
        && (!ledger.HasHistory || ledger.HistoryBacklog || casts.Any(item => item.Before.State.Id == gameId && item.Before.SeatId == playerId
            && !item.Ambiguous && !item.HistoryFinished)
            || witnesses.Any(item => item.WitnessId == "required-colorless-payment"
                && item.Before.State.Id == gameId && item.Before.SeatId == playerId));

    internal void HistoryUnavailable(string gameId, int playerId)
    {
        if (!ledgers.ContainsKey((gameId, playerId)))
            throw new InvalidDataException("Unsupported history still belongs to a previously admitted audience.");
        historyUnavailable.Add((gameId, playerId));
    }
    internal bool IsHistoryUnavailable(string gameId, int playerId) => historyUnavailable.Contains((gameId, playerId));

    // Consumes the same provider ledger as ordinary match history. Its archival
    // ordinal is distinct from the execution-transition and activity cursors.
    internal void ObserveHistory(HistoryBatch batch)
    {
        HistoryFacts.Validate(batch);
        var key = (batch.GameId, batch.ViewerId);
        if (!ledgers.TryGetValue(key, out var ledger))
            throw new InvalidDataException("History needs its previously admitted owned observation.");
        if (batch.NextSequence < ledger.HistoryCursor) return;
        if (batch.FirstSequence > ledger.HistoryCursor + 1) ResetHistory();
        foreach (var record in batch.Records) {
            if (record.Sequence <= ledger.HistoryCursor) {
                if (ledger.History.TryGetValue(record.Sequence, out var prior)
                    && !System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.JsonSerializer.SerializeToNode(prior),
                        System.Text.Json.JsonSerializer.SerializeToNode(record)))
                    throw new InvalidDataException("An immutable owned history ordinal changed its committed fact.");
                continue;
            }
            if (record.Sequence != ledger.HistoryCursor + 1) ResetHistory();
            ledger.HistoryCursor = record.Sequence;
            ledger.History.Add(record.Sequence, record);
            ledger.HistoryBytes += HistoryFacts.RecordBytes(record);
            if (record.Execution is { } execution) {
                var next = execution.Occurrence;
                if (!ledger.Executions.TryGetValue(next.Id, out var prior)
                    || prior.Step != ExecutionStep.Complete || next.Step == ExecutionStep.Complete)
                    ledger.Executions[next.Id] = next with {
                        SourceCardId = next.SourceCardId ?? prior?.SourceCardId,
                        SourceName = next.SourceName ?? prior?.SourceName
                    };
                if (execution.Transition is { Step: ExecutionStep.Preparation } preparation)
                    ledger.Preparations.TryAdd(next.Id, preparation.Sequence);
            }
            if (record.Activity is { } activity) ledger.Activities.TryAdd(activity.Id, activity);
            if (record.Payment is { Kind: "restored" or "cancelled" } payment) {
                if (record.ExecutionId is { } executionId)
                    Invalidate(executionId);
                if (payment.RestoredExecutionId is { } restored)
                    Invalidate(restored);

                void Invalidate(string execution)
                {
                    // Only pending or previously credited roots need a durable
                    // revocation marker. Other cancellations remain in the same
                    // bounded history tail, rather than an unbounded index.
                    if (casts.Any(item => item.Before.State.Id == batch.GameId && item.Before.SeatId == payment.PayerId
                            && item.ExecutionId == execution)
                        || witnesses.Any(item => item.WitnessId == "required-colorless-payment"
                            && item.Before.State.Id == batch.GameId && item.Before.SeatId == payment.PayerId && item.ExecutionId == execution))
                        restoredPayments.Add((batch.GameId, payment.PayerId, execution));
                }
            }
            if (ledger.History.Count > MaximumHistoryRecords || ledger.HistoryBytes > MaximumHistoryBytes) ResetHistory();
            if (ledger.Executions.Count > maximumOccurrences || ledger.Activities.Count > maximumActivities) {
                ledger.Executions.Clear(); ledger.Activities.Clear(); ledger.Preparations.Clear(); ledger.OwnedTerminal.Clear(); ledger.Epoch++;
                incomplete.Add(key);
            }
        }
        // Empty retained history can move past a dropped oversized record.
        if (batch.NextSequence > ledger.HistoryCursor) {
            ResetHistory();
            ledger.HistoryCursor = batch.NextSequence;
        }
        ledger.HasHistory = true;
        ledger.HistoryBacklog = batch.HasMore;
        if (!batch.HasMore)
            foreach (var attempt in casts.Where(item => item.Before.State.Id == batch.GameId && item.Before.SeatId == batch.ViewerId
                && ledger.Executions.TryGetValue(item.ExecutionId, out var execution) && execution.Step == ExecutionStep.Complete))
                attempt.HistoryFinished = true;

        void ResetHistory()
        {
            ledger.History.Clear(); ledger.HistoryBytes = 0; ledger.HistoryEpoch++;
            incomplete.Add(key);
        }
    }

    private void AnchorCast(AutomatedSeatObservation before, GameCommand command, string sourceId,
        string sourceName, Ledger ledger)
    {
        foreach (var older in casts.Where(item => item.Before.State.Id == before.State.Id && item.Before.SeatId == before.SeatId
            && item.SourceId == sourceId && item.ExecutionId.Length == 0
            && (item.Before.State.Decision?.Id != before.State.Decision?.Id || item.Command != command))) older.Ambiguous = true;
        if (casts.Any(item => item.Before.State.Id == before.State.Id && item.Before.SeatId == before.SeatId
            && item.Command == command && item.Before.State.Decision?.Id == before.State.Decision?.Id)) return;
        if (casts.Count >= maximumAttempts) {
            var retired = casts[0];
            incomplete.Add((retired.Before.State.Id, retired.Before.SeatId));
            if (!witnesses.Any(item => item.WitnessId == "required-colorless-payment"
                && item.Before.State.Id == retired.Before.State.Id && item.Before.SeatId == retired.Before.SeatId
                && item.ExecutionId == retired.ExecutionId))
                restoredPayments.Remove((retired.Before.State.Id, retired.Before.SeatId, retired.ExecutionId));
            casts.RemoveAt(0);
        }
        casts.Add(new(before, command, sourceId, sourceName, ledger));
    }

    private void ObserveCastOwned(AutomatedSeatObservation observation, Ledger ledger)
    {
        foreach (var attempt in casts.Where(item => item.Before.State.Id == observation.State.Id && item.Before.SeatId == observation.SeatId)) {
            if (attempt.Epoch != ledger.Epoch || attempt.Ambiguous || attempt.ExecutionId.Length > 0) continue;
            var candidates = ledger.Executions.Values.Where(item => item.Kind == ExecutionKind.Spell
                && item.ActorPlayerId == observation.SeatId && item.SourceCardId == attempt.SourceId
                && !attempt.PriorOccurrences.Contains(item.Id)
                && ledger.Preparations.TryGetValue(item.Id, out long prepared) && prepared > attempt.PriorSequence).ToArray();
            if (observation.State.Decision is { ExecutionId.Length: > 0 } decision
                && decision.ExecutionStep is ExecutionStep.Preparation or ExecutionStep.Choice or ExecutionStep.Cost
                && ledger.Executions.TryGetValue(decision.ExecutionId, out var current)
                && current.Kind == ExecutionKind.Spell && current.ActorPlayerId == observation.SeatId
                && current.SourceCardId == attempt.SourceId && !attempt.PriorOccurrences.Contains(current.Id)) {
                candidates = [current]; attempt.Competing = false;
            }
            if (candidates.Length > 1) { attempt.Competing = true; continue; }
            if (candidates.Length == 1 && !attempt.Competing) attempt.ExecutionId = candidates[0].Id;
        }
    }

    private void ObserveCastPublic(GameSnapshot state)
    {
        foreach (var attempt in casts.Where(item => item.Before.State.Id == state.Id).ToArray()) {
            int actor = attempt.Before.SeatId;
            if (attempt.Ambiguous || !ledgers.TryGetValue((state.Id, actor), out var ledger)
                || attempt.Epoch != ledger.Epoch || state.BoardRevision < ledger.Board
                || !ledger.Executions.TryGetValue(attempt.ExecutionId, out var spell)
                || spell.Kind != ExecutionKind.Spell || spell.ActorPlayerId != actor || spell.SourceCardId != attempt.SourceId) continue;
            var actualCasts = ledger.Activities.Values.Where(item => item.Id > attempt.PriorActivity && item.Kind == "cast"
                && item.ExecutionId == attempt.ExecutionId && item.PlayerId == actor && item.CardId == attempt.SourceId).ToArray();
            if (actualCasts.Length != 1) continue;
            var actualCast = actualCasts[0];
            if (!attempt.UntapRecorded && actualCast.HistoricSpell == true) {
                var triggers = ledger.Executions.Values.Where(item => item.Kind == ExecutionKind.Trigger
                    && item.ParentId == attempt.ExecutionId && item.ActorPlayerId == actor
                    && item.SourceName == "Traxos, Scourge of Kroog" && item.SourceCardId is { } traxos
                    && attempt.Traxos.Contains(traxos) && item.Step == ExecutionStep.Complete
                    && item.Outcome == ExecutionOutcome.Resolved
                    && ledger.OwnedTerminal.TryGetValue(item.Id, out var terminal)
                    && terminal.Outcome == ExecutionOutcome.Resolved && state.BoardRevision >= terminal.Board).ToArray();
                if (triggers.Length == 1) {
                    var trigger = triggers[0];
                    var untaps = ledger.Activities.Values.Where(item => item.Id > actualCast.Id && item.Kind == "tapped"
                        && item.CardId == trigger.SourceCardId && item.PlayerId == actor && item.ExecutionId == trigger.Id
                        && item.ExecutionStep == ExecutionStep.Resolution
                        && item.Change is { Kind: "state", Property: "tapped", Before: 1, After: 0 }).ToArray();
                    var current = state.Players.SingleOrDefault(item => item.Id == actor)?.Zone("Battlefield").Cards
                        .FirstOrDefault(card => CardViews.Identity(card) == trigger.SourceCardId);
                    if (untaps.Length == 1 && current?.Tapped != true) {
                        EmitWitness(new("historic-traxos-untap", attempt.Before, attempt.Command, attempt.SourceId, attempt.SourceName,
                            attempt.ExecutionId, state.BoardRevision, [actualCast.Id, untaps[0].Id],
                            "The rules-certified historic cast produced a distinct Traxos trigger and its exact committed 1→0 untap in Resolution.") {
                                CausalExecutionIds = [attempt.ExecutionId, trigger.Id]
                            });
                        attempt.UntapRecorded = true;
                    }
                }
            }
            if (attempt.PaymentRecorded || !attempt.HistoryBaseline || attempt.HistoryEpoch != ledger.HistoryEpoch
                || spell.Step != ExecutionStep.Complete || spell.Outcome != ExecutionOutcome.Resolved
                || !ledger.OwnedTerminal.TryGetValue(spell.Id, out var resolved)
                || resolved.Outcome != ExecutionOutcome.Resolved || state.BoardRevision < resolved.Board
                || restoredPayments.Contains((state.Id, actor, attempt.ExecutionId))) continue;
            var payments = ledger.History.Values.Where(record => record.Sequence > attempt.PriorHistory
                && record.ExecutionId == attempt.ExecutionId && record.Payment?.PayerId == actor).ToArray();
            if (payments.Any(record => record.Payment?.Kind is "cancelled" or "restored")
                || ledger.History.Values.Any(record => record.Sequence > attempt.PriorHistory
                    && record.Payment is { Kind: "restored", PayerId: var payer } restored
                    && payer == actor && restored.RestoredExecutionId == attempt.ExecutionId)) continue;
            var spent = payments.Where(record => record.Payment is { Kind: "spent", BillBefore: { } before,
                    BillAfter: { } after, Spent: { } mana }
                && CountC(before) > CountC(after) && mana.GetValueOrDefault("C") == CountC(before) - CountC(after)
                && mana.Values.Sum() == mana.GetValueOrDefault("C")).ToArray();
            var complete = payments.Where(record => record.Payment is { Kind: "completed", BillBefore: { } before, BillAfter: { IsEmpty: true } }
                && CountC(before) > 0 && (record.SourceCardId == null || record.SourceCardId == attempt.SourceId)).ToArray();
            if (spent.Length == 0 || complete.Length != 1 || spent.Any(item => item.Sequence >= complete[0].Sequence)) continue;
            var committed = spent.Append(complete[0]).ToImmutableArray();
            EmitWitness(new("required-colorless-payment", attempt.Before, attempt.Command, attempt.SourceId, attempt.SourceName,
                attempt.ExecutionId, state.BoardRevision, [actualCast.Id],
                "Actual C consumption removed an explicit {C} bill component; that owned payment completed and the same spell resolved without a reported cancellation/restoration.") {
                    CausalExecutionIds = [attempt.ExecutionId],
                    HistorySequences = committed.Select(item => item.Sequence).ToImmutableArray(),
                    Payments = committed.Select(item => new AiCausalPaymentEvidence(item.Sequence, attempt.ExecutionId,
                        item.Payment!.Kind, actor, item.Payment.BillBefore!.Value, item.Payment.BillAfter!.Value,
                        item.Payment.Spent)).ToImmutableArray()
                });
            attempt.PaymentRecorded = true;
        }
    }

    private static int CountC(ImmutableArray<ManaBillPart> parts) => parts.Where(item => item.Symbol == "{C}").Sum(item => item.Count);
}
