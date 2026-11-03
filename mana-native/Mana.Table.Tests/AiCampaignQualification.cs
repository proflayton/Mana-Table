using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mana.Contracts;

namespace Mana.Table;

internal sealed record AiCampaignDeckBinding(string CandidateId, string SourceSha256, string SemanticVersionId,
    string SavedDeckId, long Revision, string Handle, string Family, ImmutableArray<string> IntendedMechanics,
    ImmutableArray<string> RequiredWitnessIds)
{
    public string PowerBand => "unassessed";
    public string Availability => "test-only campaign fixture; no shipping-catalog promotion";
}
internal sealed record AiCampaignWitnessOccurrence(string GameId, int SlotIndex, int PlayerId,
    string SourceCardId, string SourceName, string DecisionId, string CommandType, bool Accepted,
    long BeforeRevision, long BeforeBoardRevision, long AfterBoardRevision, string ExecutionId,
    ImmutableArray<long> ActivityIds, string Consequence);
internal sealed record AiCampaignDeckWitness(string Id, string Status, string Detail,
    ImmutableArray<AiCampaignWitnessOccurrence> Evidence);
internal sealed record AiCampaignDeckQualification(AiCampaignDeckBinding Deck, string ConfigurationId,
    string ConfigurationSha256, int SlotIndex, int PlayerId, int TeamId, string GameId,
    string MechanicalIntegrity, string SampledNaturalGame, string HumanReview,
    ImmutableArray<AiCampaignDeckWitness> RequiredWitnesses)
{
    public ImmutableArray<AiCampaignWitnessOccurrence> IntegrityFailures { get; init; } = [];
    public string Interpretation => "Observed mechanics are scoped to this semantic deck/configuration/runtime evidence. "
        + "Missing defining branches remain unobserved. A smoke prefix, natural finish or legality is not strategic qualification.";
}

internal static class AiCampaignQualification
{
    internal static ImmutableHashSet<string> SupportedWitnessIds { get; } = ImmutableHashSet.Create(StringComparer.Ordinal,
        "rhys-paid-tokens", "owned-equipment-attachment", "dual-commander-casts",
        "useful-opponent-removal", "source-cost-ownership");
    internal static async Task<AiCampaignDeckBinding> BindDeckAsync(IDeckWorkshop workshop, AiCampaignDeck manifest,
        DeckDetails committed, string handle)
    {
        manifest.Validate();
        if (committed.StructureValid == false || committed.SaveError.Length > 0 || committed.Problem.Length > 0
            || !committed.Issues.IsEmpty || string.IsNullOrWhiteSpace(committed.LibraryToken)
            || handle != "deck:" + committed.Id + ":" + committed.LibraryToken)
            throw new InvalidDataException("Qualification needs the actual legal committed campaign deck.");
        var version = await workshop.WorkshopAsync(committed.Id, committed.Revision);
        if (version.DeckId != committed.Id || version.Revision != committed.Revision || !Hash(version.VersionId))
            throw new InvalidDataException("The workshop did not pin this committed campaign content.");
        return new(manifest.Id, manifest.Hash, version.VersionId, committed.Id, committed.Revision, handle,
            manifest.Family, manifest.IntendedMechanics, manifest.RequiredWitnessIds);
    }

    internal static string DefinitionHash(string id, MatchSetupSpecification setup,
        ImmutableArray<AiCampaignDeckBinding> decks, object profile)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {
            id, setup, semanticDecksInSlotOrder = decks.Select(deck => new { deck.CandidateId, deck.SemanticVersionId }), profile
        }))));

    internal static bool Hash(string? value) => value?.Length == 64 && value.All(Uri.IsHexDigit);
}

// A bounded evidence collector, not a second rules evaluator. It credits only
// accepted current commands, exact actor/source executions and their reported
// consequences. Unrelated global BoardWitnesses never enter this collector.
internal sealed class AiCampaignQualificationTracker
{
    private sealed class Action(AutomatedSeatObservation observation, GameCommand command, string sourceId,
        string sourceName, string sourceType, string route, string executionId)
    {
        internal readonly AutomatedSeatObservation Before = observation;
        internal readonly GameCommand Command = command;
        internal readonly string SourceId = sourceId, SourceName = sourceName, SourceType = sourceType, Route = route;
        internal string ExecutionId = executionId;
        internal readonly long PriorActivity = observation.State.Activity.Select(item => item.Id).DefaultIfEmpty().Max();
        internal readonly long PriorExecutionSequence = observation.State.Executions?.LastSequence ?? 0;
        internal readonly HashSet<long> Effects = [];
        internal readonly List<ActivityEntry> Changes = [];
        internal bool Resolved;
    }
    private readonly ConfiguredSimulationStart accepted;
    private readonly ImmutableArray<AiCampaignDeckBinding> decks;
    private readonly string configurationId, definitionSha256;
    private readonly Dictionary<int, int> slots;
    private readonly List<Action> actions = [];
    private readonly Dictionary<(int Slot, string Witness), List<AiCampaignWitnessOccurrence>> observed = [];
    private readonly Dictionary<int, List<AiCampaignWitnessOccurrence>> wrongSourceCosts = [];
    private sealed record SourceCost(AutomatedSeatObservation Before, GameCommand Command, ActionCost Cost,
        string ExpectedId, Card Selected, string ExecutionId) { internal bool Recorded { get; set; } }
    private readonly List<SourceCost> sourceCosts = [];
    private readonly Dictionary<int, Dictionary<string, AiCampaignWitnessOccurrence>> commanderCasts = [];
    private const int MaximumActions = 512, MaximumWitnesses = 8;

    internal AiCampaignQualificationTracker(ImmutableArray<AiCampaignDeckBinding> orderedBindings,
        ConfiguredSimulationStart accepted, string configurationId, string definitionSha256)
    {
        ConfiguredSimulationFacts.ValidateState(accepted, accepted.State);
        if (orderedBindings.IsDefault || orderedBindings.Length != accepted.Participants.Length
            || !AiCampaignQualification.Hash(definitionSha256) || string.IsNullOrWhiteSpace(configurationId))
            throw new InvalidDataException("Invalid qualification configuration identity.");
        foreach (var deck in orderedBindings) {
            var manifest = AiCampaignDecks.All.SingleOrDefault(item => item.Id == deck.CandidateId);
            if (manifest == null || deck.SourceSha256 != manifest.Hash || !AiCampaignQualification.Hash(deck.SemanticVersionId)
                || !deck.RequiredWitnessIds.SequenceEqual(manifest.RequiredWitnessIds))
                throw new InvalidDataException("Unknown or changed campaign qualification content.");
        }
        if (!orderedBindings.Select(deck => deck.Handle).SequenceEqual(accepted.Setup.Decks, StringComparer.Ordinal))
            throw new InvalidDataException("Accepted simulation decks differ from the pinned semantic bindings.");
        this.accepted = accepted; decks = orderedBindings; this.configurationId = configurationId; this.definitionSha256 = definitionSha256;
        slots = accepted.Participants.ToDictionary(item => item.PlayerId, item => item.Index);
    }

    internal void Receipt(AutomatedSeatObservation? observation, GameCommand command, bool wasAccepted, string? error)
    {
        if (observation == null || !wasAccepted) return;
        Validate(observation);
        var state = observation.State;
        if (command.GameId != state.Id || command is not DecisionCommand decision || decision.DecisionId != state.Decision?.Id
            || !GameCommandPermissions.Allows(state, command)) throw new InvalidDataException("Qualification receipt has stale or foreign command scope.");
        if (command is SelectCard selected && observation.Semantics.CostPayment is { } cost
            && cost.CurrentCostIndex is int costIndex && costIndex >= 0 && costIndex < cost.RemainingCosts.Length
            && cost.RemainingCosts[costIndex].FromSource && cost.SourceCardId.Length > 0 && cost.PayerId == observation.SeatId
            && cost.RemainingCosts.Count(part => part.Kind == cost.RemainingCosts[costIndex].Kind) == 1
            && CardViews.InZones(state).FirstOrDefault(item => item.Card.Key == selected.Card).Card is { } selectedCard) {
            if (sourceCosts.Count < MaximumActions && state.Decision!.ExecutionId.Length > 0)
                sourceCosts.Add(new(observation, command, cost.RemainingCosts[costIndex], cost.SourceCardId,
                    selectedCard, state.Decision.ExecutionId));
        }
        if (actions.Count >= MaximumActions) return;
        SourceActionSet? source = command is UseOfferedAction use
            ? observation.Domain.Sources.SingleOrDefault(item => item.SourceCardKey == use.Card
                && item.Actions.Any(action => action.Id == use.ActionId)) : null;
        if (command is UseOfferedAction offered && observation.Domain.Actions.Any(item => item.ActionId == offered.ActionId && item.ManaAbility)) return;
        string sourceId = source?.SourceCardId ?? state.Decision?.Context.Source?.CardId ?? "";
        if (sourceId.Length == 0 && state.Decision?.Context.Source?.Card is { } portrait) sourceId = CardViews.Identity(portrait);
        if (sourceId.Length == 0) return;
        var physical = CardViews.InZones(state).FirstOrDefault(item => CardViews.Identity(item.Card) == sourceId);
        var execution = ExecutionFrames.Find(state, state.Decision?.ExecutionId);
        if (physical.Card == null || physical.PlayerId != observation.SeatId) {
            if (execution?.ActorPlayerId != observation.SeatId || execution.SourceCardId != sourceId) return;
        }
        string route = command is UseOfferedAction offer ? source!.Actions.Single(item => item.Id == offer.ActionId).Kind : "continuation";
        actions.Add(new(observation, command, sourceId, physical.Card?.Name ?? state.Decision?.Context.Source?.Card?.Name ?? "",
            physical.Card?.Type ?? "", route, execution?.ActorPlayerId == observation.SeatId && execution.SourceCardId == sourceId ? execution.Id : ""));
    }

    internal void ObserveOwned(AutomatedSeatObservation observation)
    {
        Validate(observation);
        foreach (var cost in sourceCosts.Where(item => !item.Recorded && item.Before.SeatId == observation.SeatId)) {
            var state = observation.State;
            var execution = ExecutionFrames.Find(state, cost.ExecutionId);
            if (state.BoardRevision <= cost.Before.State.BoardRevision || execution?.ActorPlayerId != observation.SeatId
                || execution.SourceCardId != cost.ExpectedId) continue;
            string selectedId = CardViews.Identity(cost.Selected);
            long prior = cost.Before.State.Activity.Select(item => item.Id).DefaultIfEmpty().Max();
            var mutation = state.Activity.FirstOrDefault(entry => entry.Id > prior && entry.CardId == selectedId
                && entry.ExecutionStep == ExecutionStep.Cost && Related(state, entry.ExecutionId, cost.ExecutionId)
                && (cost.Cost.Kind switch {
                    ActionCostKind.Sacrifice => entry.Impact is { Kind: "zone", FromZone: "Battlefield", ToZone: "Graveyard" or "Exile" },
                    ActionCostKind.RemoveCounters when cost.Cost.Amount is int amount && amount > 0 => entry.Change is { Kind: "counters" } change
                        && change.Property == cost.Cost.CounterType && change.Before - change.After == amount,
                    _ => false
                }));
            // A later tap or counter delta is not a cost receipt. Require the
            // rules-owned mutation inside this exact execution's Cost stage.
            // An already paid subcost remains real if its parent later cancels.
            if (mutation == null) continue;
            var proof = Occurrence(cost.Before, cost.Command, cost.ExpectedId, cost.Before.State.Decision?.Context.Source?.Card?.Name ?? "",
                cost.ExecutionId, state.BoardRevision, [mutation.Id], "Accepted source-cost selection followed by attributed committed " + cost.Cost.Kind + " of " + selectedId);
            if (selectedId != cost.ExpectedId) {
                if (!wrongSourceCosts.TryGetValue(observation.SeatId, out var failures)) wrongSourceCosts[observation.SeatId] = failures = [];
                if (failures.Count < MaximumWitnesses) failures.Add(proof);
            } else Credit(observation.SeatId, "source-cost-ownership", proof);
            cost.Recorded = true;
        }
        foreach (var action in actions.Where(item => item.Before.SeatId == observation.SeatId && item.Before.State.Id == observation.State.Id)) {
            var state = observation.State;
            if (action.ExecutionId.Length == 0 && state.Executions is { } frame) {
                var execution = frame.Occurrences.FirstOrDefault(item => item.ActorPlayerId == observation.SeatId
                    && item.SourceCardId == action.SourceId && frame.Transitions.Any(transition => transition.ExecutionId == item.Id
                        && transition.Sequence > action.PriorExecutionSequence));
                if (execution != null) action.ExecutionId = execution.Id;
            }
            foreach (var entry in state.Activity) {
                if (entry.Id <= action.PriorActivity || entry.ExecutionId.Length == 0 || action.ExecutionId.Length == 0
                    || !Related(state, entry.ExecutionId, action.ExecutionId) || !action.Effects.Add(entry.Id)) continue;
                if (entry.Kind == "resolved" && entry.Outcome == "resolved" && entry.CardId == action.SourceId) action.Resolved = true;
                // Event-time controller and exact producing occurrence must
                // agree; a teammate's identical card cannot supply this credit.
                if (entry.Cause?.CardId == action.SourceId && entry.Cause.ControllerId == observation.SeatId)
                    action.Changes.Add(entry);
            }
            if (ExecutionFrames.Find(state, action.ExecutionId) is { Step: ExecutionStep.Complete, Outcome: ExecutionOutcome.Resolved }) action.Resolved = true;
        }
    }

    internal void ObservePublic(GameSnapshot state)
    {
        ConfiguredSimulationFacts.ValidateState(accepted, state);
        foreach (var action in actions.Where(item => item.Resolved && item.ExecutionId.Length > 0 && state.BoardRevision >= item.Before.State.BoardRevision)) {
            int player = action.Before.SeatId;
            var current = state.Players.Single(item => item.Id == player);
            var before = action.Before.State.Viewer!;
            AiCampaignWitnessOccurrence Evidence(string consequence, IEnumerable<long>? events = null) => Occurrence(action.Before,
                action.Command, action.SourceId, action.SourceName, action.ExecutionId, state.BoardRevision,
                events?.ToImmutableArray() ?? action.Changes.Select(item => item.Id).ToImmutableArray(), consequence);
            if (action.SourceName == "Rhys the Redeemed" && action.Route != "cast") {
                var created = action.Changes.Where(entry => entry.Impact is { Kind: "zone", ToZone: "Battlefield" } impact
                    && impact.RecipientControllerId == player && current.Zone("Battlefield").Cards.Any(card => card.GamePieceType == "token"
                        && CardViews.Identity(card) == impact.CardId)).ToArray();
                if (created.Length > 0 && current.Zone("Battlefield").Cards.Count(card => card.GamePieceType == "token")
                    > before.Zone("Battlefield").Cards.Count(card => card.GamePieceType == "token"))
                    Credit(player, "rhys-paid-tokens", Evidence("The accepted Rhys activation resolved and its attributed owned token entries remain on the authoritative battlefield.", created.Select(item => item.Id)));
            }
            if (action.SourceType.Contains("Equipment", StringComparison.Ordinal)
                && current.Zone("Battlefield").Cards.FirstOrDefault(card => CardViews.Identity(card) == action.SourceId)?.AttachedTo is { } attachment
                && current.Zone("Battlefield").Cards.Any(card => CardViews.Identity(card) == attachment.Id)
                && before.Zone("Battlefield").Cards.FirstOrDefault(card => CardViews.Identity(card) == action.SourceId)?.AttachedTo?.Id != attachment.Id)
                Credit(player, "owned-equipment-attachment", Evidence("The accepted equipment action resolved and changed its attachment to an actual owned recipient."));
            if (action.Route == "cast" && decks[slots[player]].CandidateId == "pir-toothy-counter-ramp.v1"
                && action.SourceName is "Pir, Imaginative Rascal" or "Toothy, Imaginary Friend"
                && before.Zone("Command").Cards.Any(card => CardViews.Identity(card) == action.SourceId)
                && current.Zone("Battlefield").Cards.Any(card => CardViews.Identity(card) == action.SourceId)) {
                if (!commanderCasts.TryGetValue(player, out var commanders)) commanderCasts[player] = commanders = [];
                commanders[action.SourceName] = Evidence("This actual partner occurrence was cast from the player's command zone and resolved onto its battlefield.");
                if (commanders.Count == 2) foreach (var proof in commanders.Values) Credit(player, "dual-commander-casts", proof);
            }
            var removed = action.Changes.Where(entry => entry.Impact is { Kind: "zone", FromZone: "Battlefield" } impact
                && impact.RecipientControllerId is { } recipient && PlayerRelations.Between(state, player, recipient) == PlayerRelation.Opponent
                && impact.ToZone is "Graveyard" or "Exile" or "Hand"
                && !state.Players.SelectMany(item => item.Zone("Battlefield").Cards).Any(card => CardViews.Identity(card) == impact.CardId)).ToArray();
            if (removed.Length > 0) Credit(player, "useful-opponent-removal", Evidence("The accepted owned source resolved an attributed opponent permanent's committed battlefield departure.", removed.Select(item => item.Id)));
        }
    }

    internal ImmutableArray<AiCampaignDeckQualification> Summary(string stopReason, bool verifiedNaturalOutcome, string? mechanicalFailure)
        => accepted.Participants.OrderBy(item => item.Index).Select(participant => {
            bool incomplete = mechanicalFailure != null || stopReason is "time-budget" or "mechanical-failure";
            var witnesses = decks[participant.Index].RequiredWitnessIds.Select(id => {
                var proof = observed.GetValueOrDefault((participant.Index, id))?.ToImmutableArray() ?? [];
                bool failed = id == "source-cost-ownership" && wrongSourceCosts.TryGetValue(participant.PlayerId, out var errors) && errors.Count > 0;
                bool implemented = AiCampaignQualification.SupportedWitnessIds.Contains(id);
                return new AiCampaignDeckWitness(id, failed ? "failed" : !proof.IsEmpty ? "observed"
                        : !implemented ? "not-instrumented" : incomplete ? "inconclusive" : "not-observed",
                    failed ? "An accepted from-source cost selected a different actual occurrence."
                        : !proof.IsEmpty ? "Accepted command and authoritative attributed consequence recorded for this exact deck/player."
                        : !implemented ? "This defining-mechanic verifier is not implemented in this slice. It remains an explicit coverage gap, regardless of sampled game completion."
                        : "This defining branch was not proven. Offers, selected routes, source resolution alone and another deck's effects do not count.",
                    failed ? wrongSourceCosts[participant.PlayerId].ToImmutableArray() : proof);
            }).ToImmutableArray();
            return new AiCampaignDeckQualification(decks[participant.Index], configurationId, definitionSha256, participant.Index,
                participant.PlayerId, accepted.Setup.Specification.Seats.Single(item => item.Index == participant.Index).TeamId,
                accepted.State.Id, mechanicalFailure != null || witnesses.Any(item => item.Status == "failed")
                    || wrongSourceCosts.TryGetValue(participant.PlayerId, out var costFailures) && costFailures.Count > 0 ? "failed" : "no-observed-integrity-failure",
                verifiedNaturalOutcome ? "observed-natural-outcome" : "inconclusive", "pending", witnesses) {
                    IntegrityFailures = wrongSourceCosts.GetValueOrDefault(participant.PlayerId)?.ToImmutableArray() ?? []
                };
        }).ToImmutableArray();

    private void Validate(AutomatedSeatObservation observation)
    {
        if (observation.SeatId != observation.State.ViewerId || !slots.ContainsKey(observation.SeatId))
            throw new InvalidDataException("Unknown qualification observation audience.");
        ConfiguredSimulationFacts.ValidateOwnedState(accepted, observation.State);
    }
    private void Credit(int player, string id, AiCampaignWitnessOccurrence proof)
    {
        int slot = slots[player];
        if (!decks[slot].RequiredWitnessIds.Contains(id)) return;
        if (!observed.TryGetValue((slot, id), out var records)) observed[(slot, id)] = records = [];
        if (records.Count < MaximumWitnesses && !records.Any(item => item.ExecutionId == proof.ExecutionId && item.DecisionId == proof.DecisionId)) records.Add(proof);
    }
    private AiCampaignWitnessOccurrence Occurrence(AutomatedSeatObservation observation, GameCommand command,
        string sourceId, string name, string executionId, long afterBoard, ImmutableArray<long> events, string consequence)
        => new(observation.State.Id, slots[observation.SeatId], observation.SeatId, sourceId, name,
            (command as DecisionCommand)?.DecisionId ?? "", command.GetType().Name, true, observation.State.Revision,
            observation.State.BoardRevision, afterBoard, executionId, events, consequence);
    private static bool Related(GameSnapshot state, string child, string parent)
    {
        var occurrence = ExecutionFrames.Find(state, child);
        var root = ExecutionFrames.Find(state, parent);
        return child == parent || occurrence != null && root != null && occurrence.BudgetRootId == root.BudgetRootId;
    }
}
