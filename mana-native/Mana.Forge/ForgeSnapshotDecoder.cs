using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mana.Contracts;
using Mana.Planning;

namespace Mana.Forge;

// The process envelope stays private. Only explicitly mapped, immutable game
// state crosses the provider boundary; a wire `decision` cannot supply authority.
internal static class ForgeSnapshotDecoder
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions OperationJson = new() { PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) } };

    internal static GameSnapshot? Decode(JsonElement raw, bool spectator = false, bool certifiedUndo = false)
    {
        if (raw.ValueKind == JsonValueKind.Null) return null;
        try {
            if (raw.ValueKind != JsonValueKind.Object) throw Invalid();
            var wire = raw.Deserialize<Snapshot>(Json) ?? throw Invalid();
            if (wire.Undo != null && !certifiedUndo) throw Invalid();
            if (string.IsNullOrWhiteSpace(wire.Id) || wire.Status is not ("starting" or "playing" or "resolving" or "finished" or "error")
                || !raw.TryGetProperty("viewerId", out var viewer) || !viewer.TryGetInt32(out _)
                || !TryUniqueProperty(raw, "players", out var participants) || participants.ValueKind != JsonValueKind.Array
                || participants.EnumerateArray().Any(player => !TryUniqueProperty(player, "id", out var id)
                    || id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out int number) || number < 0)
                || wire.Players.IsDefault || wire.Stack.IsDefault || wire.Players.Any(player => player == null) || wire.Stack.Any(item => item == null)
                || wire.Players.Select(player => player.Id).Distinct().Count() != wire.Players.Length
                || wire.Players.Any(player => player.TeamId is < 0)
                || wire.StackInterruptionVersion is < 0) throw Invalid();
            var players = wire.Players.Select(player => player.Id).ToImmutableHashSet();
            bool earlyFailure = wire.Status == "error" && wire.ViewerId >= 0 && wire.Players.IsEmpty && wire.Outcome == null
                && wire.Prompt.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;
            if (spectator ? wire.ViewerId != -1 || wire.Prompt.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                    : wire.Status != "starting" && !earlyFailure && !players.Contains(wire.ViewerId)) throw Invalid();
            var decision = wire.Prompt.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null
                : ForgeDecisionDecoder.Decode(wire.Prompt, players);
            if (wire.Status == "resolving" && decision != null
                || decision != null && wire.DecisionOwnerId is { } chooser && chooser != wire.ViewerId
                || decision?.Payload is PriorityPayload && wire.PriorityPlayerId is { } responder && responder != wire.ViewerId) throw Invalid();
            if (wire.States.IsDefault || wire.Activity.IsDefault || wire.Notices.IsDefault || wire.IgnoredPriorityAbilities.IsDefault) throw Invalid();
            if (spectator && (!wire.PlayAccesses.IsEmpty || !wire.Activity.IsEmpty || !wire.Notices.IsEmpty
                || wire.Players.Any(player => player.Zones.Any(zone => zone.TopCard is { } top
                    && top.PlayAffordability != ActionAffordability.Unknown))
                || wire.Players.Any(player => player.Zones.Any(zone => zone.Name is "Hand" or "Library"
                    && (!zone.Cards.IsEmpty || zone.TopCard != null)))
                || wire.Players.SelectMany(player => player.Zones).SelectMany(zone => zone.Cards)
                    .Concat(wire.Stack.Select(item => item.Card).OfType<Card>())
                    .Any(card => card.Key.Length > 0 || card.Selectable || card.Highlighted
                        || card.PlayAffordability != ActionAffordability.Unknown))) throw Invalid();
            ValidateAccess(wire, decision);
            var snapshot = new GameSnapshot(wire.Id, wire.Status, wire.Error, wire.Result, wire.ViewerId, wire.Turn,
                wire.Phase ?? "Starting game", wire.PhaseKey ?? "PREGAME", wire.ActivePlayerId, wire.Players,
                wire.Stack.Select(item => item.Native()).ToImmutableArray(), wire.Combat?.Native(), decision) {
                Revision = wire.Revision, BoardRevision = wire.BoardRevision, PublicInspection = wire.PublicInspection,
                StackInterruptionVersion = wire.StackInterruptionVersion,
                StackActionVersion = wire.StackActionVersion, ResolutionChoiceVersion = wire.ResolutionChoiceVersion,
                FiniteStackYield = wire.FiniteStackYield,
                PriorityPlayerId = wire.PriorityPlayerId, DecisionOwnerId = wire.DecisionOwnerId,
                ActionClass = wire.ActionClass, States = wire.States, Activity = wire.Activity,
                Notices = wire.Notices, IgnoredPriorityAbilities = wire.IgnoredPriorityAbilities,
                PlayAccesses = wire.PlayAccesses, Tempo = wire.Tempo, ActionSources = wire.ActionSources,
                Executions = wire.Executions, Undo = wire.Undo, Outcome = wire.Outcome,
                Encounter = wire.PartyId == null && wire.EncounterId == null ? null
                    : new EncounterIdentity(wire.PartyId ?? "", wire.EncounterId ?? "")
            };
            if (snapshot.PublicInspection is { } inspection && (string.IsNullOrWhiteSpace(inspection.Id) || inspection.Id.Length > 128
                || inspection.Id.Any(char.IsControl) || inspection.BoardRevision != snapshot.BoardRevision
                || !players.Contains(inspection.PlayerId) || snapshot.DecisionOwnerId != inspection.PlayerId
                || snapshot.PriorityPlayerId != inspection.PlayerId || snapshot.ActionClass != "priority" || snapshot.Status != "playing"
                || inspection.PlayerId == snapshot.ViewerId && snapshot.Decision?.Payload is not PriorityPayload)) throw Invalid();
            ValidateActionSources(snapshot, spectator);
            EncounterFacts.Validate(snapshot);
            MatchOutcomeFacts.Validate(snapshot);
            UndoFacts.Validate(snapshot);
            ExecutionFrames.Validate(snapshot);
            PublicActivityFacts.Validate(snapshot);
            BlockingCapacityFacts.Validate(snapshot);
            CombatRepairFacts.Validate(snapshot);
            FiniteResolutionFacts.Validate(snapshot);
            FiniteStackYieldFacts.Validate(snapshot);
            if (snapshot.Decision?.Payload is OptionsPayload { HandReduction: not null }
                && !HandReductionFacts.IsCurrent(snapshot)) throw Invalid();
            var operations = new ActionOperationValidation(snapshot);
            foreach (var item in snapshot.Stack) operations.ValidateCommitted(item);
            if (snapshot.Decision?.Payload is PaymentPayload payment) {
                if (payment.FundingContext is { } funding) ManaFundingFacts.Validate(snapshot, funding, payment.PayerId);
                var payer = snapshot.Players.FirstOrDefault(player => player.Id == (payment.PayerId ?? snapshot.ViewerId));
                if (payment.UsablePoolManaColors is { } colors && colors.Any(color => payer?.Mana.GetValueOrDefault(color) is not > 0)) throw Invalid();
            }
            return snapshot;
        } catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException or FormatException or OverflowException) {
            throw new IOException("The rules provider published an invalid match snapshot.", error);
        }
    }

    private static string Identifier(JsonElement value) => value.ValueKind switch {
        JsonValueKind.String when value.GetString() is { Length: > 0 } id => id,
        JsonValueKind.Number when value.TryGetInt64(out long number) => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => throw Invalid()
    };
    private static IOException Invalid() => new("The rules provider published an invalid match snapshot.");

    // Match the decoder's case-insensitive property names without permitting two
    // aliases to disagree about the participant identity that was deserialized.
    private static bool TryUniqueProperty(JsonElement node, string name, out JsonElement value)
    {
        value = default;
        if (node.ValueKind != JsonValueKind.Object) return false;
        bool found = false;
        foreach (var property in node.EnumerateObject()) {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            if (found) return false;
            found = true; value = property.Value;
        }
        return found;
    }

    private static void ValidateActionSources(GameSnapshot wire, bool spectator)
    {
        if (wire.ActionSources.IsDefault || wire.ActionSources.Any(source => source == null)) throw Invalid();
        if (wire.ActionSources.IsEmpty) return;
        if (spectator || wire.Status != "playing" || wire.Decision?.Payload is not (PriorityPayload or PaymentPayload)
            || wire.ActionSources.Select(source => source.Card?.Key).Distinct(StringComparer.Ordinal).Count() != wire.ActionSources.Length) throw Invalid();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in wire.ActionSources) {
            var card = source.Card;
            if (!CardViews.IsCurrentActionSource(wire, source) || !identities.Add(CardViews.Identity(card))
                || card.FaceDown && (card.Text.Length > 0 || card.ManaCost.Length > 0 || card.ArtName.Length > 0
                    || card.ArtSet.Length > 0 || card.CollectorNumber.Length > 0 || card.OtherFace != null
                    || card.Name is not ("Face-down card" or "Face-down or hidden card"))) throw Invalid();
        }
    }

    private static void ValidateAccess(Snapshot wire, Decision? decision)
    {
        if (wire.PlayAccesses.IsDefault || wire.PlayAccesses.Any(access => access == null)
            || wire.PlayAccesses.Select(access => access.CardId).Distinct(StringComparer.Ordinal).Count() != wire.PlayAccesses.Length)
            throw Invalid();
        bool localPriority = wire.Status == "playing" && decision?.Payload is PriorityPayload
            && (wire.DecisionOwnerId == null || wire.DecisionOwnerId == wire.ViewerId)
            && (wire.PriorityPlayerId == null || wire.PriorityPlayerId == wire.ViewerId);
        foreach (var access in wire.PlayAccesses) {
            if (access.Zone is "Hand" or "Battlefield" or "Stack" or "Command") throw Invalid();
            var zone = wire.Players.FirstOrDefault(player => player.Id == access.PlayerId)?.Zones
                .FirstOrDefault(candidate => candidate.Name == access.Zone);
            var card = zone?.Cards.Append(zone.TopCard).OfType<Card>()
                .FirstOrDefault(candidate => CardViews.Identity(candidate) == access.CardId);
            if (card == null || card.FaceDown || access.Routes.Any(route => route.CanStart && (!localPriority || card.Key.Length == 0)))
                throw Invalid();
        }
    }

    private sealed record Snapshot
    {
        public string Id { get; init; } = "";
        public string Status { get; init; } = "";
        public string? Error { get; init; }
        public string? Result { get; init; }
        public MatchOutcome? Outcome { get; init; }
        public string? PartyId { get; init; }
        public string? EncounterId { get; init; }
        public int ViewerId { get; init; }
        public int Turn { get; init; }
        public string? Phase { get; init; }
        public string? PhaseKey { get; init; }
        public int? ActivePlayerId { get; init; }
        public ImmutableArray<Player> Players { get; init; } = [];
        public ImmutableArray<WireStackItem> Stack { get; init; } = [];
        public WireCombat? Combat { get; init; }
        public JsonElement Prompt { get; init; }
        public long Revision { get; init; }
        public long BoardRevision { get; init; }
        public PublicInspectionContext? PublicInspection { get; init; }
        public long? StackInterruptionVersion { get; init; }
        public long? StackActionVersion { get; init; }
        public long? ResolutionChoiceVersion { get; init; }
        public FiniteStackYieldState? FiniteStackYield { get; init; }
        public int? PriorityPlayerId { get; init; }
        public int? DecisionOwnerId { get; init; }
        public string ActionClass { get; init; } = "";
        public ImmutableArray<PublicState> States { get; init; } = [];
        public ImmutableArray<ActivityEntry> Activity { get; init; } = [];
        public ImmutableArray<string> Notices { get; init; } = [];
        public ImmutableArray<PriorityAbility> IgnoredPriorityAbilities { get; init; } = [];
        public ImmutableArray<PlayAccess> PlayAccesses { get; init; } = [];
        public ImmutableArray<ActionSourceOccurrence> ActionSources { get; init; } = [];
        public DecisionTempoFrame? Tempo { get; init; }
        public ExecutionFrame? Executions { get; init; }
        public UndoState? Undo { get; init; }
    }

    private sealed record WireStackItem(string Name, string Text, bool Ability, Card? Card)
    {
        public string Kind { get; init; } = "";
        public JsonElement Id { get; init; }
        public int? ControllerId { get; init; }
        public ImmutableArray<StackTarget> Targets { get; init; } = [];
        public PendingEffect? Effect { get; init; }
        public JsonElement OperationGraph { get; init; }
        public string ExecutionId { get; init; } = "";
        public StackRepetition? Repetition { get; init; }
        private StackActionKind ActionKind() => (Kind, Ability) switch {
            ("spell" or "", false) => StackActionKind.Spell,
            ("activated", true) => StackActionKind.ActivatedAbility,
            ("triggered", true) => StackActionKind.TriggeredAbility,
            ("ability" or "", true) => StackActionKind.Ability,
            _ => throw Invalid()
        };
        public StackItem Native() => new(Name, Text, ActionKind(), Card) {
            Id = Identifier(Id), ControllerId = ControllerId, Repetition = Repetition,
            Targets = Targets.IsDefault ? throw Invalid() : Targets, Effect = Effect, ExecutionId = ExecutionId ?? "",
            OperationGraph = OperationGraph.ValueKind == JsonValueKind.Undefined ? ActionOperationGraph.Unknown
                : OperationGraph.Deserialize<ActionOperationGraph>(OperationJson) ?? throw Invalid()
        };
    }

    private sealed record WireDefender(string Kind, JsonElement Id, int PlayerId, string Name)
    {
        public Defender Native() => Kind is "player" or "card" ? new(Kind, Identifier(Id), PlayerId, Name) : throw Invalid();
    }
    private sealed record WireAttackOption(string CardId, ImmutableArray<WireDefender> Defenders)
    {
        public AttackOption Native() => new(CardId, Defenders.IsDefault ? throw Invalid() : Defenders.Select(defender => defender?.Native() ?? throw Invalid()).ToImmutableArray());
    }
    private sealed record WireAttack(string CardId, int DefendingPlayerId, ImmutableArray<string> BlockerIds, ImmutableArray<string> EligibleBlockerIds)
    {
        public bool HasDefender { get; init; } = true;
        public WireDefender? Defender { get; init; }
        public bool Blocked { get; init; }
        public Attack Native() => !HasDefender && Defender != null ? throw Invalid() : new(CardId, DefendingPlayerId,
            BlockerIds.IsDefault ? throw Invalid() : BlockerIds, EligibleBlockerIds.IsDefault ? throw Invalid() : EligibleBlockerIds) {
            HasDefender = HasDefender, Defender = Defender?.Native(), Blocked = Blocked
        };
    }
    private sealed record WireCombat(ImmutableArray<WireAttack> Attackers, ImmutableArray<WireAttackOption> AttackOptions, string? BlockProblem)
    {
        public bool AttackValid { get; init; } = true;
        public ImmutableArray<WireAttackAssignment> AttackRepair { get; init; } = [];
        public ImmutableArray<WireBlockAssignment>? BlockRepair { get; init; }
        public string DeclarationId { get; init; } = "";
        public Combat Native() => Attackers.IsDefault || AttackOptions.IsDefault ? throw Invalid() : new(
            Attackers.Select(attack => attack?.Native() ?? throw Invalid()).ToImmutableArray(),
            AttackOptions.Select(option => option?.Native() ?? throw Invalid()).ToImmutableArray(), BlockProblem) {
                AttackValid = AttackValid, DeclarationId = DeclarationId ?? throw Invalid(),
                AttackRepair = AttackRepair.IsDefault ? throw Invalid() : AttackRepair.Select(assignment => assignment?.Native() ?? throw Invalid()).ToImmutableArray(),
                BlockRepair = BlockRepair is { } repair ? repair.IsDefault ? throw Invalid()
                    : repair.Select(assignment => assignment?.Native() ?? throw Invalid()).ToImmutableArray() : null
            };
    }
    private sealed record WireAttackAssignment(string CardId, WireDefender Defender)
    {
        public AttackAssignment Native() => new(CardId, Defender?.Native() ?? throw Invalid());
    }
    private sealed record WireBlockAssignment(string AttackerId, string BlockerId)
    {
        public BlockAssignment Native() => new(AttackerId, BlockerId);
    }
}
