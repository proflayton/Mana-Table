using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.Client;

// A one-to-one arrangement requested by the player. Published pair eligibility
// establishes each edge, not the legality or tactical merit of the whole plan.
public sealed record CombatAssignmentPreview(ImmutableArray<BlockAssignment> Proposed,
    ImmutableArray<BlockAssignment> Desired, int SelectedBlockers, int SelectedAttackers)
{
    public int UnpairedBlockers => SelectedBlockers - Proposed.Length;
    public int UnpairedAttackers => SelectedAttackers - Proposed.Length;

    public static CombatAssignmentPreview? PairOneEach(GameSnapshot state, IEnumerable<string> blockerIds, IEnumerable<string> attackerIds)
    {
        if (!CombatAssignmentIntent.OwnDeclaration(state)) return null;
        var blockers = blockerIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var targets = attackerIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (blockers.Length is 0 or > 512 || targets.Length is 0 or > 512) return null;
        var cards = CardViews.InZones(state).Where(item => item.Zone == "Battlefield" && item.Card.CombatId.Length > 0)
            .GroupBy(item => item.Card.CombatId).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var attacks = state.Combat!.Attackers.ToDictionary(attack => attack.CardId, StringComparer.Ordinal);
        if (blockers.Any(id => !cards.TryGetValue(id, out var card) || card.PlayerId != state.ViewerId)
            || targets.Any(id => !cards.ContainsKey(id) || !attacks.TryGetValue(id, out var attack) || attack.DefendingPlayerId != state.ViewerId)) return null;
        var existing = CombatAssignmentIntent.OwnAssignments(state);
        var assigned = existing.Select(pair => pair.BlockerId).ToHashSet(StringComparer.Ordinal);
        var edges = blockers.Select(id => assigned.Contains(id) ? [] : targets.Select((target, index) => (target, index))
            .Where(item => attacks[item.target].BlockerIds.IsEmpty && attacks[item.target].EligibleBlockerIds.Contains(id)
                && GameCommandPermissions.Allows(state, new ToggleBlock(state.Id, state.Decision!.Id,
                    cards[item.target].Card.Key, cards[id].Card.Key)))
            .Select(item => item.index).ToArray()).ToArray();
        // Layered augmenting paths find a maximum distinct pairing without
        // enumerating permutations or confusing a flying-only edge with a free slot.
        int[] left = Enumerable.Repeat(-1, blockers.Length).ToArray(), right = Enumerable.Repeat(-1, targets.Length).ToArray();
        var distance = new int[blockers.Length];
        while (Layer())
            for (int index = 0; index < left.Length; index++) if (left[index] < 0) Augment(index);
        var proposed = left.Select((target, index) => (target, index)).Where(pair => pair.target >= 0)
            .Select(pair => new BlockAssignment(targets[pair.target], blockers[pair.index])).ToImmutableArray();
        return new(proposed, existing.Concat(proposed).ToImmutableArray(), blockers.Length, targets.Length);

        bool Layer()
        {
            var queue = new Queue<int>(); bool free = false;
            for (int index = 0; index < left.Length; index++) {
                distance[index] = left[index] < 0 ? 0 : -1;
                if (left[index] < 0) queue.Enqueue(index);
            }
            while (queue.TryDequeue(out int source)) foreach (int target in edges[source]) {
                int paired = right[target];
                if (paired < 0) free = true;
                else if (distance[paired] < 0) { distance[paired] = distance[source] + 1; queue.Enqueue(paired); }
            }
            return free;
        }
        bool Augment(int source)
        {
            foreach (int target in edges[source]) {
                int paired = right[target];
                if (paired >= 0 && (distance[paired] != distance[source] + 1 || !Augment(paired))) continue;
                left[source] = target; right[target] = source; return true;
            }
            distance[source] = -1; return false;
        }
    }
}

/// <summary>Apply an explicitly reviewed finite declaration using fresh ordinary
/// block toggles. Reaching the desired assignment never confirms combat.</summary>
public sealed class CombatAssignmentIntent : IFiniteDecisionPolicy
{
    private readonly object gate = new();
    private readonly string game, declaration, phase;
    private readonly int viewer, turn;
    private readonly int? activePlayer;
    private readonly ImmutableHashSet<BlockAssignment> desired;
    private readonly ImmutableDictionary<string, (int Player, string? Kind, string? Id)> attackers;
    private readonly int changes;
    private ImmutableHashSet<BlockAssignment> expected;
    private long revision;
    private bool active = true, paused, completed;
    private string stopReason = "";
    private (BlockAssignment Pair, bool Present, long Revision)? pending;

    private CombatAssignmentIntent(GameSnapshot state, ImmutableArray<BlockAssignment> target)
    {
        game = state.Id; viewer = state.ViewerId; turn = state.Turn; phase = state.PhaseKey; activePlayer = state.ActivePlayerId;
        declaration = state.Combat!.DeclarationId; revision = state.Revision; desired = target.ToImmutableHashSet();
        attackers = state.Combat.Attackers.Where(attack => attack.DefendingPlayerId == viewer).ToImmutableDictionary(
            attack => attack.CardId, attack => (attack.DefendingPlayerId, attack.Defender?.Kind, attack.Defender?.Id), StringComparer.Ordinal);
        var current = OwnAssignments(state);
        expected = current;
        changes = current.Except(desired).Count() + desired.Except(current).Count();
        Remaining = changes;
    }

    public static CombatAssignmentIntent? TryArm(GameSnapshot state, IEnumerable<BlockAssignment> target)
    {
        ArgumentNullException.ThrowIfNull(state); ArgumentNullException.ThrowIfNull(target);
        var desired = target.ToImmutableArray();
        if (!OwnDeclaration(state) || desired.Length > 512 || desired.Any(pair => pair == null)
            || desired.Distinct().Count() != desired.Length) return null;
        foreach (var pair in desired) {
            var blocker = CardViews.FindZoneOccurrence(state, pair.BlockerId);
            if (blocker is not { Zone: "Battlefield" } || blocker.Value.PlayerId != state.ViewerId
                || !state.Combat!.Attackers.Any(attack => attack.CardId == pair.AttackerId && attack.DefendingPlayerId == state.ViewerId)) return null;
        }
        var intent = new CombatAssignmentIntent(state, desired);
        return intent.changes > 0 ? intent : null;
    }

    public bool IsActive { get { lock (gate) return active; } }
    public bool IsPaused { get { lock (gate) return paused; } }
    public bool Completed { get { lock (gate) return completed; } }
    public int Total => changes;
    public int Remaining { get; private set; }
    public string StopReason { get { lock (gate) return stopReason; } }
    public void Pause() { lock (gate) paused = true; }
    public void Resume() { lock (gate) paused = false; }
    public void Stop() { lock (gate) End("stopped"); }
    public bool Advance(ClientSession session, Func<GameSnapshot, GameSnapshot, bool> shouldPresent)
        => session.AdvanceFiniteCore(this, shouldPresent);

    public bool CanContinue(GameSnapshot state)
    {
        lock (gate) {
            if (!active) return false;
            if (state.Id != game || state.ViewerId != viewer || state.Turn != turn || state.PhaseKey != phase
                || state.ActivePlayerId != activePlayer || state.Status is not ("playing" or "resolving")
                || state.Viewer is not { Eliminated: false } || state.Combat?.DeclarationId != declaration
                || state.Decision != null && state.Decision.Payload is not BlockPayload) return End("declaration-changed");
            if (state.Revision < revision) return true;
            revision = state.Revision;
            var currentAttackers = state.Combat.Attackers.Where(attack => attack.DefendingPlayerId == viewer).ToArray();
            if (currentAttackers.Length != attackers.Count || currentAttackers.Any(attack => !attackers.TryGetValue(attack.CardId, out var original)
                    || original != (attack.DefendingPlayerId, attack.Defender?.Kind, attack.Defender?.Id))) return End("attackers-changed");
            var current = OwnAssignments(state);
            if (pending is { } sent) {
                var applied = sent.Present ? expected.Add(sent.Pair) : expected.Remove(sent.Pair);
                if (current.SetEquals(applied)) { pending = null; expected = applied; }
                else if (state.Decision?.Payload is BlockPayload && state.Revision > sent.Revision) return End("assignment-not-applied");
            } else if (!current.SetEquals(expected)) return End("assignments-changed");
            Remaining = current.Except(desired).Count() + desired.Except(current).Count();
            if (Remaining == 0 && pending == null) { completed = true; return End(""); }
            return true;
        }
    }

    DecisionCommand? IFiniteDecisionPolicy.NextCommand(GameSnapshot state)
    {
        lock (gate) {
            if (paused || !CanContinue(state) || state.Revision < revision || pending != null || state.Decision?.Payload is not BlockPayload) return null;
            var current = OwnAssignments(state);
            var next = current.Except(desired).OrderBy(pair => pair.AttackerId, StringComparer.Ordinal).ThenBy(pair => pair.BlockerId, StringComparer.Ordinal).FirstOrDefault()
                ?? desired.Except(current).OrderBy(pair => pair.AttackerId, StringComparer.Ordinal).ThenBy(pair => pair.BlockerId, StringComparer.Ordinal).FirstOrDefault();
            if (next == null) { End("no-current-assignment"); return null; }
            var attacker = CardViews.FindZoneOccurrence(state, next.AttackerId)?.Card;
            var blocker = CardViews.FindZoneOccurrence(state, next.BlockerId)?.Card;
            if (attacker == null || blocker == null) { End("occurrence-left"); return null; }
            var command = new ToggleBlock(state.Id, state.Decision.Id, attacker.Key, blocker.Key);
            if (GameCommandPermissions.Allows(state, command)) return command;
            End("assignment-needs-review"); return null;
        }
    }

    void IFiniteDecisionPolicy.Submitted(GameSnapshot state, DecisionCommand command)
    {
        lock (gate) {
            if (command is not ToggleBlock toggle) { End("unexpected-command"); return; }
            var attacker = CardViews.FindZoneActionSource(state, toggle.Attacker)?.Card;
            var blocker = CardViews.FindZoneActionSource(state, toggle.Blocker)?.Card;
            if (attacker == null || blocker == null) { End("occurrence-left"); return; }
            var pair = new BlockAssignment(attacker.CombatId, blocker.CombatId);
            pending = (pair, !OwnAssignments(state).Contains(pair), state.Revision);
        }
    }

    internal static bool OwnDeclaration(GameSnapshot state) => state.Status == "playing" && state.Viewer is { Eliminated: false }
        && state.Decision?.Payload is BlockPayload
        && (state.DecisionOwnerId == null || state.DecisionOwnerId == state.ViewerId)
        && state.Combat is { DeclarationId.Length: > 0 };

    internal static ImmutableHashSet<BlockAssignment> OwnAssignments(GameSnapshot state) => state.Combat?.Attackers.SelectMany(attack => attack.BlockerIds
        .Where(id => CardViews.FindZoneOccurrence(state, id) is { Zone: "Battlefield" } blocker && blocker.PlayerId == state.ViewerId)
        .Select(id => new BlockAssignment(attack.CardId, id))).ToImmutableHashSet() ?? [];

    private bool End(string reason) { active = false; stopReason = reason; return false; }
}
