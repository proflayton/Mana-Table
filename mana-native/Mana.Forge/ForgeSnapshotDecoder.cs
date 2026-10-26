using System.Collections.Immutable;
using System.Text.Json;
using Mana.Contracts;

namespace Mana.Forge;

// The process envelope stays private. Only explicitly mapped, immutable game
// state crosses the provider boundary; a wire `decision` cannot supply authority.
internal static class ForgeSnapshotDecoder
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    internal static GameSnapshot? Decode(JsonElement raw)
    {
        if (raw.ValueKind == JsonValueKind.Null) return null;
        try {
            if (raw.ValueKind != JsonValueKind.Object) throw Invalid();
            var wire = raw.Deserialize<Snapshot>(Json) ?? throw Invalid();
            if (string.IsNullOrWhiteSpace(wire.Id) || wire.Status is not ("starting" or "playing" or "resolving" or "finished" or "error")
                || !raw.TryGetProperty("viewerId", out var viewer) || !viewer.TryGetInt32(out _)
                || wire.Players.IsDefault || wire.Stack.IsDefault || wire.Players.Any(player => player == null) || wire.Stack.Any(item => item == null)
                || wire.Players.Select(player => player.Id).Distinct().Count() != wire.Players.Length) throw Invalid();
            var players = wire.Players.Select(player => player.Id).ToImmutableHashSet();
            if (wire.Status != "starting" && !players.Contains(wire.ViewerId)) throw Invalid();
            var decision = wire.Prompt.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null
                : ForgeDecisionDecoder.Decode(wire.Prompt, players);
            if (wire.Status == "resolving" && decision != null
                || decision != null && wire.DecisionOwnerId is { } chooser && chooser != wire.ViewerId
                || decision?.Payload is PriorityPayload && wire.PriorityPlayerId is { } responder && responder != wire.ViewerId) throw Invalid();
            if (wire.States.IsDefault || wire.Activity.IsDefault || wire.Notices.IsDefault || wire.IgnoredPriorityAbilities.IsDefault) throw Invalid();
            ValidateAccess(wire, decision);
            return new(wire.Id, wire.Status, wire.Error, wire.Result, wire.ViewerId, wire.Turn,
                wire.Phase ?? "Starting game", wire.PhaseKey ?? "PREGAME", wire.ActivePlayerId, wire.Players,
                wire.Stack.Select(item => item.Native()).ToImmutableArray(), wire.Combat?.Native(), decision) {
                Revision = wire.Revision, BoardRevision = wire.BoardRevision,
                PriorityPlayerId = wire.PriorityPlayerId, DecisionOwnerId = wire.DecisionOwnerId,
                ActionClass = wire.ActionClass, States = wire.States, Activity = wire.Activity,
                Notices = wire.Notices, IgnoredPriorityAbilities = wire.IgnoredPriorityAbilities,
                PlayAccesses = wire.PlayAccesses, Tempo = wire.Tempo
            };
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
        public int? PriorityPlayerId { get; init; }
        public int? DecisionOwnerId { get; init; }
        public string ActionClass { get; init; } = "";
        public ImmutableArray<PublicState> States { get; init; } = [];
        public ImmutableArray<ActivityEntry> Activity { get; init; } = [];
        public ImmutableArray<string> Notices { get; init; } = [];
        public ImmutableArray<PriorityAbility> IgnoredPriorityAbilities { get; init; } = [];
        public ImmutableArray<PlayAccess> PlayAccesses { get; init; } = [];
        public DecisionTempoFrame? Tempo { get; init; }
    }

    private sealed record WireStackItem(string Name, string Text, bool Ability, Card? Card)
    {
        public JsonElement Id { get; init; }
        public int? ControllerId { get; init; }
        public ImmutableArray<StackTarget> Targets { get; init; } = [];
        public PendingEffect? Effect { get; init; }
        public StackItem Native() => new(Name, Text, Ability, Card) {
            Id = Identifier(Id), ControllerId = ControllerId,
            Targets = Targets.IsDefault ? throw Invalid() : Targets, Effect = Effect
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
        public WireDefender? Defender { get; init; }
        public bool Blocked { get; init; }
        public Attack Native() => new(CardId, DefendingPlayerId,
            BlockerIds.IsDefault ? throw Invalid() : BlockerIds, EligibleBlockerIds.IsDefault ? throw Invalid() : EligibleBlockerIds) {
            Defender = Defender?.Native(), Blocked = Blocked
        };
    }
    private sealed record WireCombat(ImmutableArray<WireAttack> Attackers, ImmutableArray<WireAttackOption> AttackOptions, string? BlockProblem)
    {
        public Combat Native() => Attackers.IsDefault || AttackOptions.IsDefault ? throw Invalid() : new(
            Attackers.Select(attack => attack?.Native() ?? throw Invalid()).ToImmutableArray(),
            AttackOptions.Select(option => option?.Native() ?? throw Invalid()).ToImmutableArray(), BlockProblem);
    }
}
