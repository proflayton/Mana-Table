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
    ImmutableArray<long> ActivityIds, string Consequence)
{
    public ImmutableArray<string> CausalExecutionIds { get; init; } = [];
    public int? ObservedCounterDelta { get; init; }
    public ImmutableArray<long> HistorySequences { get; init; } = [];
    public ImmutableArray<AiCausalPaymentEvidence> Payments { get; init; } = [];
}
internal sealed record AiCampaignDeckWitness(string Id, string Status, string Detail,
    ImmutableArray<AiCampaignWitnessOccurrence> Evidence);
internal sealed record AiCampaignDeckQualification(AiCampaignDeckBinding Deck, string ConfigurationId,
    string ConfigurationSha256, int SlotIndex, int PlayerId, int TeamId, string GameId,
    string MechanicalIntegrity, string SampledNaturalGame, string HumanReview,
    ImmutableArray<AiCampaignDeckWitness> RequiredWitnesses)
{
    public ImmutableArray<AiCampaignWitnessOccurrence> IntegrityFailures { get; init; } = [];
    public string Interpretation => "Observed mechanics are scoped to this semantic deck/configuration/runtime evidence. "
        + "Missing defining branches remain unobserved. A smoke prefix, natural finish or legality is not strategic qualification. "
        + "Causal witnesses certify positive committed-event ancestry, not exhaustive activity history; activity has no exported lower-retention watermark.";
}
internal sealed record AiCampaignTableParticipant(int Index, int PlayerId, int TeamId);

internal static class AiCampaignQualification
{
    internal static ImmutableHashSet<string> SupportedWitnessIds { get; } = ImmutableHashSet.Create(StringComparer.Ordinal,
        "rhys-paid-tokens", "owned-equipment-attachment", "dual-commander-casts",
        "useful-opponent-removal", "source-cost-ownership",
        "loch-mare-counter-cost-draw", "pir-toothy-draw-counters",
        "historic-traxos-untap", "required-colorless-payment");
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
    private sealed record AcceptedTable(string GameId, ImmutableArray<AiCampaignTableParticipant> Participants,
        ImmutableArray<string> DeckHandles, System.Action<GameSnapshot> ValidateOwned, System.Action<GameSnapshot> ValidatePublic);
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
    private readonly AcceptedTable accepted;
    private readonly ImmutableArray<AiCampaignDeckBinding> decks;
    private readonly string configurationId, definitionSha256;
    private readonly Dictionary<int, int> slots;
    private readonly List<Action> actions = [];
    private readonly Dictionary<(int Slot, string Witness), List<AiCampaignWitnessOccurrence>> observed = [];
    private readonly Dictionary<int, List<AiCampaignWitnessOccurrence>> wrongSourceCosts = [];
    private readonly AiCausalMechanicEvidence causal = new();
    internal AiCausalMechanicEvidence CausalEvidence => causal;
    internal ImmutableArray<AiCampaignTableParticipant> Participants => accepted.Participants;
    private readonly Dictionary<int, Dictionary<string, AiCampaignWitnessOccurrence>> commanderCasts = [];
    private const int MaximumActions = 512, MaximumWitnesses = 8;

    internal AiCampaignQualificationTracker(ImmutableArray<AiCampaignDeckBinding> orderedBindings,
        ConfiguredSimulationStart accepted, string configurationId, string definitionSha256)
        : this(orderedBindings, FromConfigured(accepted), configurationId, definitionSha256) { }

    private AiCampaignQualificationTracker(ImmutableArray<AiCampaignDeckBinding> orderedBindings,
        AcceptedTable accepted, string configurationId, string definitionSha256)
    {
        if (orderedBindings.IsDefault || orderedBindings.Length != accepted.Participants.Length
            || !AiCampaignQualification.Hash(definitionSha256) || string.IsNullOrWhiteSpace(configurationId))
            throw new InvalidDataException("Invalid qualification configuration identity.");
        foreach (var deck in orderedBindings) {
            var manifest = AiCampaignDecks.All.SingleOrDefault(item => item.Id == deck.CandidateId);
            if (manifest == null || deck.SourceSha256 != manifest.Hash || !AiCampaignQualification.Hash(deck.SemanticVersionId)
                || !deck.RequiredWitnessIds.SequenceEqual(manifest.RequiredWitnessIds))
                throw new InvalidDataException("Unknown or changed campaign qualification content.");
        }
        if (!orderedBindings.Select(deck => deck.Handle).SequenceEqual(accepted.DeckHandles, StringComparer.Ordinal))
            throw new InvalidDataException("Accepted table decks differ from the pinned semantic bindings.");
        this.accepted = accepted; decks = orderedBindings; this.configurationId = configurationId; this.definitionSha256 = definitionSha256;
        slots = accepted.Participants.ToDictionary(item => item.PlayerId, item => item.Index);
    }

    private static AcceptedTable FromConfigured(ConfiguredSimulationStart accepted)
    {
        ConfiguredSimulationFacts.ValidateState(accepted, accepted.State);
        return new(accepted.State.Id, accepted.Participants.Select(participant => new AiCampaignTableParticipant(
            participant.Index, participant.PlayerId, accepted.Setup.Specification.Seats.Single(seat => seat.Index == participant.Index).TeamId))
            .ToImmutableArray(), accepted.Setup.Decks,
            state => ConfiguredSimulationFacts.ValidateOwnedState(accepted, state),
            state => ConfiguredSimulationFacts.ValidateState(accepted, state));
    }

    // These are actual ready/selection receipts and owned connection rows. A
    // configured-simulation receipt is never manufactured for an ordinary table.
    internal static AiCampaignQualificationTracker ForMixedTable(ImmutableArray<AiCampaignDeckBinding> orderedBindings,
        Lobby acceptedLobby, ImmutableArray<OwnedDecisionObservation> ownedRows,
        ImmutableArray<string> selectionIdsInSlotOrder, string configurationId, string definitionSha256)
    {
        if (acceptedLobby == null || acceptedLobby.Mode != "hosting" || !string.IsNullOrEmpty(acceptedLobby.Error)
            || !acceptedLobby.CanStart || acceptedLobby.MatchActive || string.IsNullOrWhiteSpace(acceptedLobby.TableId)
            || acceptedLobby.Seats.IsDefault || acceptedLobby.Seats.Length != 4
            || acceptedLobby.Seats.Any(seat => seat == null)
            || !acceptedLobby.Seats.Select(seat => seat.Index).Order().SequenceEqual([0, 1, 2, 3])
            || acceptedLobby.Seats.Any(seat => !seat.Ready || string.IsNullOrWhiteSpace(seat.ParticipantId)
                || seat.DeckPreview == null || seat.DeckPreview.Problem != null)
            || acceptedLobby.Seats.Select(seat => seat.ParticipantId).Distinct(StringComparer.Ordinal).Count() != 4
            || acceptedLobby.Seats.Count(seat => seat.Type == "LOCAL") != 1
            || acceptedLobby.Seats.Count(seat => seat.Type == "REMOTE") != 1
            || acceptedLobby.Seats.Count(seat => seat.Type == "AI") != 2
            || selectionIdsInSlotOrder.IsDefault || selectionIdsInSlotOrder.Length != 4
            || selectionIdsInSlotOrder.Any(string.IsNullOrWhiteSpace)
            || selectionIdsInSlotOrder.Distinct(StringComparer.Ordinal).Count() != 4
            || orderedBindings.IsDefault || orderedBindings.Length != 4
            || ownedRows.IsDefault || ownedRows.Length != 4 || ownedRows.Any(row => row?.State == null)
            || ownedRows.Select(row => row.State.ViewerId).Distinct().Count() != 4)
            throw new InvalidDataException("Mixed qualification requires four actual ready assignments and owned audiences.");
        string gameId = ownedRows[0].State.Id;
        var participants = ImmutableArray.CreateBuilder<AiCampaignTableParticipant>();
        var commanderSets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var seat in acceptedLobby.Seats.OrderBy(seat => seat.Index)) {
            var deck = orderedBindings[seat.Index];
            var manifest = AiCampaignDecks.All.SingleOrDefault(candidate => candidate.Id == deck.CandidateId)
                ?? throw new InvalidDataException("Unknown mixed qualification deck.");
            string commanders = CommanderSet(manifest.Commanders);
            if (!commanderSets.Add(commanders) || seat.DeckPreview!.SelectionId != selectionIdsInSlotOrder[seat.Index]
                || CommanderSet(seat.DeckPreview.Commanders.Select(card => card.Name)) != commanders
                || seat.Type == "AI" && seat.OpponentId != deck.Handle)
                throw new InvalidDataException("The mixed ready assignments changed their pinned deck/selection identity.");
            var matches = ownedRows.Where(row => row.State.Viewer is { } viewer
                && CommanderSet(viewer.Zone("Command").Cards.Select(card => card.Name)) == commanders).ToArray();
            if (matches.Length != 1 || matches[0].State.Viewer!.TeamId is not int team || team < 0
                || seat.TeamId is int expectedTeam && team != expectedTeam)
                throw new InvalidDataException("Initial owned commanders cannot prove a unique actual mixed slot/player binding.");
            participants.Add(new(seat.Index, matches[0].State.ViewerId, team));
        }
        var actual = participants.ToImmutable();
        if (actual.Select(participant => participant.PlayerId).Distinct().Count() != 4
            || actual.Select(participant => participant.TeamId).Distinct().Count() != 4)
            throw new InvalidDataException("The ordinary mixed qualification needs four distinct free-for-all players and teams.");
        foreach (var row in ownedRows) ValidateMixedFrame(row.State, gameId, actual, true);
        var accepted = new AcceptedTable(gameId, actual, orderedBindings.Select(deck => deck.Handle).ToImmutableArray(),
            state => ValidateMixedFrame(state, gameId, actual, true), state => ValidateMixedFrame(state, gameId, actual, false));
        return new(orderedBindings, accepted, configurationId, definitionSha256);
    }

    private static string CommanderSet(IEnumerable<string> names) => string.Join("\n", names.Order(StringComparer.Ordinal));

    private static void ValidateMixedFrame(GameSnapshot state, string gameId,
        ImmutableArray<AiCampaignTableParticipant> participants, bool owned)
    {
        if (state == null || string.IsNullOrWhiteSpace(gameId) || state.Id != gameId || state.Error != null || state.Encounter != null
            || state.Status is not ("playing" or "resolving" or "finished")
            || state.Players.IsDefault || state.Players.Length != 4 || state.Players.Any(player => player == null)
            || state.Players.Select(player => player.Id).Distinct().Count() != 4
            || owned && !participants.Any(participant => participant.PlayerId == state.ViewerId))
            throw new InvalidDataException("Mixed qualification changed its accepted game or audience.");
        foreach (var participant in participants)
            if (state.Players.SingleOrDefault(player => player.Id == participant.PlayerId)?.TeamId != participant.TeamId)
                throw new InvalidDataException("Mixed qualification changed an actual player/team binding.");
        MatchOutcomeFacts.Validate(state);
    }

    internal void Receipt(AutomatedSeatObservation? observation, GameCommand command, bool wasAccepted, string? error)
    {
        if (observation == null || !wasAccepted) return;
        Validate(observation);
        var state = observation.State;
        if (command.GameId != state.Id || command is not DecisionCommand decision || decision.DecisionId != state.Decision?.Id
            || !GameCommandPermissions.Allows(state, command)) throw new InvalidDataException("Qualification receipt has stale or foreign command scope.");
        causal.Receipt(observation, command, true);
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
        causal.ObserveOwned(observation);
        ImportCausalWitnesses();
        var state = observation.State;
        Dictionary<string, ExecutionOccurrence>? occurrences = null;
        Dictionary<string, long>? transitionSequences = null;
        bool occurrencesPrepared = false, transitionsPrepared = false;
        ExecutionOccurrence? Find(string? id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (!occurrencesPrepared) {
                occurrencesPrepared = true;
                if (state.Executions is { Occurrences.IsDefault: false } frame
                    && frame.Occurrences.All(item => item != null && item.Id != null)) {
                    occurrences = new(StringComparer.Ordinal);
                    foreach (var item in frame.Occurrences) occurrences.TryAdd(item.Id, item);
                }
            }
            // Preserve the existing lazy lookup/error behavior for malformed
            // direct collector inputs; admission remains the adapter's job.
            return occurrences != null ? occurrences.GetValueOrDefault(id) : ExecutionFrames.Find(state, id);
        }
        bool Fresh(string id, long prior)
        {
            if (!transitionsPrepared) {
                transitionsPrepared = true;
                if (state.Executions is { Transitions.IsDefault: false } frame
                    && frame.Transitions.All(item => item != null && item.ExecutionId != null)) {
                    transitionSequences = new(StringComparer.Ordinal);
                    foreach (var item in frame.Transitions)
                        if (!transitionSequences.TryGetValue(item.ExecutionId, out long previous) || item.Sequence > previous)
                            transitionSequences[item.ExecutionId] = item.Sequence;
                }
            }
            return transitionSequences != null && id != null
                ? transitionSequences.TryGetValue(id, out long sequence) && sequence > prior
                : state.Executions?.Transitions.Any(item => item.ExecutionId == id && item.Sequence > prior) == true;
        }
        bool Related(string child, string parent)
        {
            var occurrence = Find(child);
            var root = Find(parent);
            return child == parent || occurrence != null && root != null && occurrence.BudgetRootId == root.BudgetRootId;
        }
        foreach (var action in actions.Where(item => item.Before.SeatId == observation.SeatId && item.Before.State.Id == observation.State.Id)) {
            if (action.ExecutionId.Length == 0 && state.Executions is { } frame) {
                var execution = frame.Occurrences.FirstOrDefault(item => item.ActorPlayerId == observation.SeatId
                    && item.SourceCardId == action.SourceId && Fresh(item.Id, action.PriorExecutionSequence));
                if (execution != null) action.ExecutionId = execution.Id;
            }
            foreach (var entry in state.Activity) {
                if (entry.Id <= action.PriorActivity || entry.ExecutionId.Length == 0 || action.ExecutionId.Length == 0
                    || !Related(entry.ExecutionId, action.ExecutionId) || !action.Effects.Add(entry.Id)) continue;
                if (entry.Kind == "resolved" && entry.Outcome == "resolved" && entry.CardId == action.SourceId) action.Resolved = true;
                // Event-time controller and exact producing occurrence must
                // agree; a teammate's identical card cannot supply this credit.
                if (entry.Cause?.CardId == action.SourceId && entry.Cause.ControllerId == observation.SeatId)
                    action.Changes.Add(entry);
            }
            if (Find(action.ExecutionId) is { Step: ExecutionStep.Complete, Outcome: ExecutionOutcome.Resolved }) action.Resolved = true;
        }
    }

    internal long HistoryCursor(string gameId, int playerId) => causal.HistoryCursor(gameId, playerId);
    internal void HistoryUnavailable(string gameId, int playerId) => causal.HistoryUnavailable(gameId, playerId);
    internal void ObserveHistory(HistoryBatch batch)
    {
        if (batch.GameId != accepted.GameId || !slots.ContainsKey(batch.ViewerId))
            throw new InvalidDataException("Qualification history changed its accepted game or owned audience.");
        causal.ObserveHistory(batch);
        ImportCausalWitnesses();
    }

    internal void ObservePublic(GameSnapshot state)
    {
        accepted.ValidatePublic(state);
        causal.ObservePublic(state);
        ImportCausalWitnesses();
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
    {
        ImportCausalWitnesses();
        return accepted.Participants.OrderBy(item => item.Index).Select(participant => {
            bool incomplete = mechanicalFailure != null || stopReason is "time-budget" or "mechanical-failure"
                || causal.IsIncomplete(accepted.GameId, participant.PlayerId);
            var witnesses = decks[participant.Index].RequiredWitnessIds.Select(id => {
                var proof = observed.GetValueOrDefault((participant.Index, id))?.ToImmutableArray() ?? [];
                bool failed = id == "source-cost-ownership" && wrongSourceCosts.TryGetValue(participant.PlayerId, out var errors) && errors.Count > 0;
                bool implemented = AiCampaignQualification.SupportedWitnessIds.Contains(id);
                bool unavailable = id == "required-colorless-payment" && causal.IsHistoryUnavailable(accepted.GameId, participant.PlayerId);
                return new AiCampaignDeckWitness(id, failed ? "failed" : !proof.IsEmpty ? "observed"
                        : !implemented ? "not-instrumented" : incomplete || unavailable ? "inconclusive" : "not-observed",
                    failed ? "An accepted from-source cost selected a different actual occurrence."
                        : !proof.IsEmpty ? "Accepted command and authoritative attributed consequence recorded for this exact deck/player."
                        : !implemented ? "This defining-mechanic verifier is not implemented in this slice. It remains an explicit coverage gap, regardless of sampled game completion."
                        : unavailable ? "This provider does not expose owned automated payment history; no actual explicit-colorless payment proof can be claimed."
                        : "This defining branch was not proven. Offers, selected routes, source resolution alone and another deck's effects do not count.",
                    failed ? wrongSourceCosts[participant.PlayerId].ToImmutableArray() : proof);
            }).ToImmutableArray();
            return new AiCampaignDeckQualification(decks[participant.Index], configurationId, definitionSha256, participant.Index,
                participant.PlayerId, participant.TeamId,
                accepted.GameId, mechanicalFailure != null || witnesses.Any(item => item.Status == "failed")
                    || wrongSourceCosts.TryGetValue(participant.PlayerId, out var costFailures) && costFailures.Count > 0 ? "failed" : "no-observed-integrity-failure",
                verifiedNaturalOutcome ? "observed-natural-outcome" : "inconclusive", "pending", witnesses) {
                    IntegrityFailures = wrongSourceCosts.GetValueOrDefault(participant.PlayerId)?.ToImmutableArray() ?? []
                };
        }).ToImmutableArray();
    }

    private void Validate(AutomatedSeatObservation observation)
    {
        if (observation.SeatId != observation.State.ViewerId || !slots.ContainsKey(observation.SeatId))
            throw new InvalidDataException("Unknown qualification observation audience.");
        accepted.ValidateOwned(observation.State);
    }
    private void ImportCausalWitnesses()
    {
        // A later authoritative payment-restoration record can revoke this
        // particular payment witness. Reconcile bounded current evidence rather
        // than letting an append-only import retain invalidated credit.
        foreach (var key in observed.Keys.Where(key => key.Witness is "source-cost-ownership"
            or "loch-mare-counter-cost-draw" or "pir-toothy-draw-counters"
            or "historic-traxos-untap" or "required-colorless-payment").ToArray()) observed.Remove(key);
        wrongSourceCosts.Clear();
        var records = causal.Witnesses;
        foreach (var record in records) {
            var proof = Occurrence(record.Before, record.Command, record.SourceCardId, record.SourceName,
                record.ExecutionId, record.AfterBoardRevision, record.ActivityIds, record.Consequence) with {
                    CausalExecutionIds = record.CausalExecutionIds, ObservedCounterDelta = record.ObservedCounterDelta,
                    HistorySequences = record.HistorySequences, Payments = record.Payments
                };
            if (record.IntegrityFailure) {
                if (!wrongSourceCosts.TryGetValue(record.Before.SeatId, out var failures))
                    wrongSourceCosts[record.Before.SeatId] = failures = [];
                if (failures.Count < MaximumWitnesses) failures.Add(proof);
            } else Credit(record.Before.SeatId, record.WitnessId, proof);
        }
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
}
