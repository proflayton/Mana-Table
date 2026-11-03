using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.Table;

internal sealed record AiCausalMechanicWitness(string WitnessId, AutomatedSeatObservation Before,
    GameCommand Command, string SourceCardId, string SourceName, string ExecutionId, long AfterBoardRevision,
    ImmutableArray<long> ActivityIds, string Consequence, bool IntegrityFailure = false)
{
    public ImmutableArray<string> CausalExecutionIds { get; init; } = [];
    public int? ObservedCounterDelta { get; init; }
    public ImmutableArray<long> HistorySequences { get; init; } = [];
    public ImmutableArray<AiCausalPaymentEvidence> Payments { get; init; } = [];
    public string Coverage => "Positive committed-event ancestry, not exhaustive event history. Activity has no exported lower-retention watermark; sparse IDs cannot establish its completeness.";
}

// Host-private, bounded historical evidence. Neither command receipts, a budget
// root nor nearby card changes prove that one rules occurrence caused another.
// The campaign and actual-engine journey deliberately consume this same reader.
internal sealed partial class AiCausalMechanicEvidence(int maximumAttempts = 64, int maximumOccurrences = 512,
    int maximumActivities = 1024)
{
    private sealed class Ledger
    {
        internal readonly Dictionary<string, ExecutionOccurrence> Executions = new(StringComparer.Ordinal);
        internal readonly Dictionary<long, ActivityEntry> Activities = [];
        internal readonly Dictionary<string, long> Preparations = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, (ExecutionOutcome Outcome, long Board)> OwnedTerminal = new(StringComparer.Ordinal);
        internal readonly Dictionary<long, HistoryRecord> History = [];
        internal long Sequence, Board;
        internal long HistoryCursor, HistoryBytes;
        internal int Epoch, HistoryEpoch;
        internal bool HasFrame, HasHistory, HistoryBacklog;
    }
    private sealed class Attempt(AutomatedSeatObservation before, GameCommand command, ActionCost cost,
        string sourceId, string sourceName, string selectedId, string executionId, int epoch, bool drawRoute)
    {
        internal readonly AutomatedSeatObservation Before = before;
        internal readonly GameCommand Command = command;
        internal readonly ActionCost Cost = cost;
        internal readonly string SourceId = sourceId, SourceName = sourceName, SelectedId = selectedId;
        internal readonly long PriorActivity = before.State.Activity.Select(item => item.Id).DefaultIfEmpty().Max();
        internal readonly long PriorSequence = before.State.Executions?.LastSequence ?? 0;
        internal readonly int Epoch = epoch;
        internal readonly bool DrawRoute = drawRoute;
        internal string ExecutionId = executionId;
        internal bool CostRecorded, LochRecorded, PartnerRecorded, Ambiguous, CompetingExecutions;
    }
    private readonly Dictionary<(string Game, int Player), Ledger> ledgers = [];
    private readonly List<Attempt> attempts = [];
    private readonly List<AiCausalMechanicWitness> witnesses = [];
    private readonly HashSet<(string Game, int Player)> incomplete = [];
    private const int MaximumAudiences = 16, MaximumWitnessesPerKind = 8;

    internal ImmutableArray<AiCausalMechanicWitness> Witnesses => witnesses.Where(item =>
        item.WitnessId != "required-colorless-payment"
        || !restoredPayments.Contains((item.Before.State.Id, item.Before.SeatId, item.ExecutionId))).ToImmutableArray();
    internal bool IsIncomplete(string gameId, int playerId) => incomplete.Contains((gameId, playerId));

    internal void Receipt(AutomatedSeatObservation before, GameCommand command, bool accepted)
    {
        if (!accepted) return;
        if (before.SeatId != before.State.ViewerId || command.GameId != before.State.Id
            || command is not DecisionCommand decision || decision.DecisionId != before.State.Decision?.Id
            || !GameCommandPermissions.Allows(before.State, command))
            throw new InvalidDataException("Causal evidence requires an accepted exact-audience current command.");
        if (before.State.Executions == null) {
            // Without the accepted command's retained cursor, the first later
            // frame could disclose an older activation on this same source.
            incomplete.Add((before.State.Id, before.SeatId));
            return;
        }
        ObserveOwned(before);
        if (!ledgers.TryGetValue((before.State.Id, before.SeatId), out var ledger)) return;
        if (command is UseOfferedAction offered) {
            var source = before.Domain.Sources.SingleOrDefault(item => item.SourceCardKey == offered.Card
                && item.GameId == before.State.Id && item.DecisionId == decision.DecisionId
                && item.Actions.Any(action => action.Id == offered.ActionId));
            var semantics = before.Domain.Actions.SingleOrDefault(item => item.ActionId == offered.ActionId);
            if (source == null || semantics == null || semantics.ManaAbility) return;
            var physical = CardViews.InZones(before.State).FirstOrDefault(item => item.PlayerId == before.SeatId
                && CardViews.Identity(item.Card) == source.SourceCardId).Card;
            if (physical == null) return;
            if (source.Actions.Single(item => item.Id == offered.ActionId).Kind == "cast")
                AnchorCast(before, command, source.SourceCardId, physical.Name, ledger);
            // An accepted initiation with no disclosed occurrence must not
            // borrow a later accepted activation on the same physical source.
            foreach (var older in attempts.Where(item => Same(item, before.State.Id, before.SeatId)
                && item.SourceId == source.SourceCardId && item.ExecutionId.Length == 0
                && (item.Before.State.Decision?.Id != before.State.Decision?.Id || item.Command != command)))
                older.Ambiguous = true;
            foreach (var cost in semantics.Costs.Where(SupportedCost)) {
                if (semantics.Costs.Count(item => item.Kind == cost.Kind) != 1) continue;
                Add(new(before, command, cost, source.SourceCardId, physical.Name, source.SourceCardId, "",
                    ledger.Epoch, semantics.Effect == ActionEffectKind.Draw && semantics.EffectAmount == 1));
            }
        } else if (command is SelectCard selected && before.Semantics.CostPayment is { } payment
            && payment.PayerId == before.SeatId && payment.SourceCardId.Length > 0
            && payment.CurrentCostIndex is int index && index >= 0 && index < payment.RemainingCosts.Length
            && SupportedCost(payment.RemainingCosts[index])
            && payment.RemainingCosts.Count(item => item.Kind == payment.RemainingCosts[index].Kind) == 1
            && before.State.Decision!.ExecutionId.Length > 0) {
            var physical = CardViews.InZones(before.State).FirstOrDefault(item => item.Card.Key == selected.Card).Card;
            if (physical == null) return;
            string sourceName = CardViews.InZones(before.State).FirstOrDefault(item =>
                CardViews.Identity(item.Card) == payment.SourceCardId).Card?.Name
                ?? before.State.Decision.Context.Source?.Card?.Name ?? "";
            Add(new(before, command, payment.RemainingCosts[index], payment.SourceCardId, sourceName,
                CardViews.Identity(physical), before.State.Decision.ExecutionId, ledger.Epoch, false));
        }

        void Add(Attempt attempt)
        {
            if (attempts.Any(item => item.Before.State.Id == before.State.Id && item.Before.SeatId == before.SeatId
                && item.Before.State.Decision?.Id == before.State.Decision?.Id && item.Command == command
                && item.Cost == attempt.Cost)) return;
            if (attempts.Count >= maximumAttempts) {
                var retired = attempts[0];
                incomplete.Add((retired.Before.State.Id, retired.Before.SeatId));
                attempts.RemoveAt(0);
            }
            attempts.Add(attempt);
        }
    }

    internal void ObserveOwned(AutomatedSeatObservation observation)
    {
        var ledger = Read(observation);
        if (ledger == null) return;
        ObserveCastOwned(observation, ledger);
        foreach (var attempt in attempts.Where(item => Same(item, observation.State.Id, observation.SeatId)).ToArray()) {
            if (attempt.Epoch != ledger.Epoch || attempt.Ambiguous) continue;
            if (attempt.ExecutionId.Length == 0) {
                var candidates = ledger.Executions.Values.Where(item => item.Kind == ExecutionKind.Activation
                    && item.ActorPlayerId == observation.SeatId && item.SourceCardId == attempt.SourceId
                    && Fresh(observation.State.Executions, item.Id, attempt.PriorSequence)).ToArray();
                // A continuation can disclose its exact owner after initiation.
                if (observation.State.Decision is { ExecutionId.Length: > 0 } decision
                    && ledger.Executions.TryGetValue(decision.ExecutionId, out var current)
                    && current.Kind == ExecutionKind.Activation && current.ActorPlayerId == observation.SeatId
                    && current.SourceCardId == attempt.SourceId
                    && decision.ExecutionStep is ExecutionStep.Preparation or ExecutionStep.Choice or ExecutionStep.Cost
                    && attempt.Before.State.Executions?.Occurrences.Any(item => item.Id == current.Id) != true) {
                    candidates = [current];
                    attempt.CompetingExecutions = false;
                }
                if (candidates.Length > 1) { attempt.CompetingExecutions = true; continue; }
                if (candidates.Length == 1 && !attempt.CompetingExecutions) attempt.ExecutionId = candidates[0].Id;
            }
            if (!ledger.Executions.TryGetValue(attempt.ExecutionId, out var execution)
                || execution.ActorPlayerId != observation.SeatId || execution.SourceCardId != attempt.SourceId) continue;
            var costs = ledger.Activities.Values.Where(entry => entry.Id > attempt.PriorActivity
                && entry.ExecutionId == attempt.ExecutionId && entry.ExecutionStep == ExecutionStep.Cost
                && entry.CardId == attempt.SelectedId
                && (attempt.SelectedId != attempt.SourceId || entry.PlayerId == observation.SeatId)
                && MatchesCost(attempt.Cost, entry)).ToArray();
            if (costs.Length != 1) continue;
            if (!attempt.CostRecorded) {
                Emit(attempt, "source-cost-ownership", observation.State.BoardRevision, [costs[0].Id],
                    "An accepted exact-source action/input was followed by its exact committed " + attempt.Cost.Kind
                    + " Cost-stage mutation of " + attempt.SelectedId + ".", attempt.SelectedId != attempt.SourceId);
                attempt.CostRecorded = true;
            }
        }
    }

    internal void ObservePublic(GameSnapshot state)
    {
        ObserveCastPublic(state);
        foreach (var attempt in attempts.Where(item => item.Before.State.Id == state.Id).ToArray()) {
            if (attempt.Ambiguous || !attempt.CostRecorded || attempt.SelectedId != attempt.SourceId
                || !ledgers.TryGetValue((state.Id, attempt.Before.SeatId), out var ledger)
                || attempt.Epoch != ledger.Epoch || state.BoardRevision < ledger.Board
                || !ledger.Executions.TryGetValue(attempt.ExecutionId, out var activation)
                || activation.Step != ExecutionStep.Complete || activation.Outcome != ExecutionOutcome.Resolved) continue;
            int player = attempt.Before.SeatId;
            var costs = ledger.Activities.Values.Where(entry => entry.Id > attempt.PriorActivity
                && entry.ExecutionId == attempt.ExecutionId && entry.ExecutionStep == ExecutionStep.Cost
                && entry.CardId == attempt.SourceId && entry.PlayerId == player && MatchesCost(attempt.Cost, entry)).ToArray();
            var draws = ledger.Activities.Values.Where(entry => entry.Id > attempt.PriorActivity
                && entry.ExecutionId == attempt.ExecutionId && entry.ExecutionStep == ExecutionStep.Resolution
                && entry.Kind == "draw" && entry.Count == 1 && entry.PlayerId == player).ToArray();
            if (costs.Length != 1 || draws.Length != 1 || draws[0].Id <= costs[0].Id) continue;
            var visibleSource = state.Players.SingleOrDefault(item => item.Id == player)?.Zone("Battlefield").Cards
                .FirstOrDefault(card => CardViews.Identity(card) == attempt.SourceId);
            if (visibleSource != null && costs[0].Change is { Kind: "counters" } paid
                && visibleSource.Counters.GetValueOrDefault(paid.Property) != paid.After) continue;
            if (attempt.DrawRoute && attempt.SourceName == "Loch Mare" && !attempt.LochRecorded) {
                Emit(attempt, "loch-mare-counter-cost-draw", state.BoardRevision, [costs[0].Id, draws[0].Id],
                    "The exact owned activation committed its source counter cost, drew one actual card in Resolution and completed Resolved.");
                attempt.LochRecorded = true;
            }
            if (!attempt.DrawRoute || attempt.PartnerRecorded) continue;
            var triggers = ledger.Executions.Values.Where(item => item.Kind == ExecutionKind.Trigger
                && item.ActorPlayerId == player && item.SourceName == "Toothy, Imaginary Friend" && item.SourceCardId != null
                && item.Step == ExecutionStep.Complete && item.Outcome == ExecutionOutcome.Resolved
                && item.ParentId == attempt.ExecutionId).ToArray();
            // One attributed card draw, one disclosed trigger and one real
            // replacement. A same-budget sibling is never an ancestor.
            if (triggers.Length != 1) continue;
            var trigger = triggers[0];
            var replacements = ledger.Executions.Values.Where(item => item.Kind == ExecutionKind.Rules
                && item.RuleKind == ExecutionRuleKind.Replacement && item.ActorPlayerId == player
                && item.SourceName == "Pir, Imaginative Rascal" && item.SourceCardId != null
                && item.Step == ExecutionStep.Complete && item.Outcome == ExecutionOutcome.Resolved
                && item.RuleResult is ExecutionRuleResult.Updated or ExecutionRuleResult.Replaced
                && Descendant(ledger, item.Id, trigger.Id)).ToArray();
            if (replacements.Length != 1) continue;
            var counters = ledger.Activities.Values.Where(entry => entry.Id > draws[0].Id && entry.PlayerId == player
                && entry.CardId == trigger.SourceCardId && entry.ExecutionStep == ExecutionStep.Resolution
                && entry.Change is { Kind: "counters", Property: "+1/+1" } change && change.After > change.Before
                && entry.Cause is { } cause && cause.CardId == trigger.SourceCardId && cause.ControllerId == player
                && (entry.ExecutionId == trigger.Id || Descendant(ledger, entry.ExecutionId, trigger.Id))).ToArray();
            if (counters.Length != 1) continue;
            var recipient = state.Players.SingleOrDefault(item => item.Id == player)?.Zone("Battlefield").Cards
                .FirstOrDefault(card => CardViews.Identity(card) == trigger.SourceCardId);
            if (recipient != null && recipient.Counters.GetValueOrDefault("+1/+1") != counters[0].Change!.After) continue;
            var historical = CardViews.InZones(attempt.Before.State).Where(item => item.PlayerId == player).Select(item => item.Card);
            if (!historical.Any(card => CardViews.Identity(card) == trigger.SourceCardId && card.Name == "Toothy, Imaginary Friend")
                || !historical.Any(card => CardViews.Identity(card) == replacements[0].SourceCardId && card.Name == "Pir, Imaginative Rascal")) continue;
            int delta = counters[0].Change!.After - counters[0].Change!.Before;
            Emit(attempt, "pir-toothy-draw-counters", state.BoardRevision, [costs[0].Id, draws[0].Id, counters[0].Id],
                "The attributed effect draw produced Toothy trigger " + trigger.Id + ", with Pir replacement "
                + replacements[0].Id + " and a committed +" + delta + " +1/+1 counter result on that exact Toothy occurrence.",
                executionIds: [attempt.ExecutionId, trigger.Id, replacements[0].Id], counterDelta: delta);
            attempt.PartnerRecorded = true;
        }
    }

    private Ledger? Read(AutomatedSeatObservation observation)
    {
        if (observation.SeatId != observation.State.ViewerId) throw new InvalidDataException("Causal evidence cannot mix observation audiences.");
        var state = observation.State;
        var key = (state.Id, observation.SeatId);
        if (!ledgers.TryGetValue(key, out var ledger)) {
            if (ledgers.Count >= MaximumAudiences) { incomplete.Add(key); return null; }
            ledgers.Add(key, ledger = new());
        }
        if (state.BoardRevision < ledger.Board) return ledger;
        var frame = state.Executions;
        if (frame != null && frame.LastSequence < ledger.Sequence) return ledger;
        if (frame != null && ledger.HasFrame && frame.FirstSequence > ledger.Sequence + 1) {
            ledger.Epoch++;
            ledger.Executions.Clear(); ledger.Activities.Clear(); ledger.Preparations.Clear(); ledger.OwnedTerminal.Clear();
            incomplete.Add(key);
        }
        if (frame != null) {
            foreach (var transition in frame.Transitions.Where(item => item.Step == ExecutionStep.Preparation))
                ledger.Preparations.TryAdd(transition.ExecutionId, transition.Sequence);
            foreach (var execution in frame.Occurrences) {
                if (execution is { Step: ExecutionStep.Complete, Outcome: { } outcome })
                    ledger.OwnedTerminal[execution.Id] = (outcome, state.BoardRevision);
                if (ledger.Executions.TryGetValue(execution.Id, out var prior)) {
                    // Retain a once-admitted historical source binding when a
                    // paid/sacrificed source subsequently becomes anonymous.
                    ledger.Executions[execution.Id] = execution with {
                        SourceCardId = execution.SourceCardId ?? prior.SourceCardId,
                        SourceName = execution.SourceName ?? prior.SourceName
                    };
                } else ledger.Executions.Add(execution.Id, execution);
            }
            ledger.Sequence = Math.Max(ledger.Sequence, frame.LastSequence);
            ledger.HasFrame = true;
        }
        foreach (var entry in state.Activity) ledger.Activities.TryAdd(entry.Id, entry);
        ledger.Board = Math.Max(ledger.Board, state.BoardRevision);
        if (ledger.Executions.Count > maximumOccurrences || ledger.Activities.Count > maximumActivities) {
            ledger.Epoch++;
            ledger.Executions.Clear(); ledger.Activities.Clear(); ledger.Preparations.Clear(); ledger.OwnedTerminal.Clear();
            incomplete.Add(key);
        }
        return ledger;
    }

    private void Emit(Attempt attempt, string id, long board, ImmutableArray<long> events, string consequence,
        bool integrityFailure = false, ImmutableArray<string> executionIds = default, int? counterDelta = null)
        => EmitWitness(new(id, attempt.Before, attempt.Command, attempt.SourceId, attempt.SourceName, attempt.ExecutionId,
            board, events, consequence, integrityFailure) {
                CausalExecutionIds = executionIds.IsDefault ? [attempt.ExecutionId] : executionIds,
                ObservedCounterDelta = counterDelta
            });

    private void EmitWitness(AiCausalMechanicWitness witness)
    {
        if (witnesses.Count(item => item.Before.State.Id == witness.Before.State.Id && item.Before.SeatId == witness.Before.SeatId
            && item.WitnessId == witness.WitnessId && item.IntegrityFailure == witness.IntegrityFailure) >= MaximumWitnessesPerKind
            || witnesses.Any(item => item.Before.State.Id == witness.Before.State.Id && item.Before.SeatId == witness.Before.SeatId
                && item.WitnessId == witness.WitnessId && item.ExecutionId == witness.ExecutionId
                && item.ActivityIds.SequenceEqual(witness.ActivityIds))) return;
        witnesses.Add(witness);
    }
    private static bool SupportedCost(ActionCost cost) => cost.FromSource
        && (cost.Kind == ActionCostKind.Sacrifice || cost.Kind == ActionCostKind.RemoveCounters && cost.Amount is > 0);
    private static bool MatchesCost(ActionCost cost, ActivityEntry entry) => cost.Kind switch {
        ActionCostKind.Sacrifice => entry.Impact is { Kind: "zone", FromZone: "Battlefield", ToZone: "Graveyard" or "Exile" },
        ActionCostKind.RemoveCounters => entry.Change is { Kind: "counters" } change && change.Property.Length > 0
            && (cost.CounterType.Length == 0 || change.Property == cost.CounterType)
            && cost.Amount is int amount && change.Before - change.After == amount,
        _ => false
    };
    private static bool Same(Attempt attempt, string game, int player) => attempt.Before.State.Id == game && attempt.Before.SeatId == player;
    private static bool Fresh(ExecutionFrame? frame, string id, long prior) =>
        frame?.Transitions.Any(item => item.ExecutionId == id && item.Sequence > prior && item.Step == ExecutionStep.Preparation) == true;
    private static bool Descendant(Ledger ledger, string child, string ancestor)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (ledger.Executions.TryGetValue(child, out var current) && current.ParentId is { } parent && seen.Add(child)) {
            if (parent == ancestor) return ledger.Executions.ContainsKey(ancestor);
            child = parent;
        }
        return false;
    }
}
