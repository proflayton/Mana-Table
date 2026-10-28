using Mana.Contracts;

namespace Mana.AI;

// One bounded final-duel forecast. This is ordinary public combat arithmetic,
// not a new legality solver or a prediction of hidden responses/triggered effects.
// Each actual assignment still uses a current engine-offered attack command.
internal static class AiSwarmCombat
{
    private const int MaxCandidates = 128, MaxCohort = 32, MaxPermanents = 256;
    private static readonly HashSet<string> OrdinaryKeywords = new([
        "vigilance", "trample", "haste", "flying", "reach", "deathtouch", "indestructible", "first strike", "double strike",
        "first-strike", "double-strike", "hexproof", "ward", "shroud", "protection", "defender", "flash", "menace", "partner", "partner with",
        "evolve", "evoke", "adapt", "modular", "sunburst"
    ], StringComparer.OrdinalIgnoreCase);

    internal static DecisionCommand? TryFinish(AiDecisionRequest request, AttackPayload payload, int mechanicalReserve)
    {
        var state = request.Frame.State; var combat = state.Combat!;
        var opponents = state.Players.Where(player => player.Id != state.ViewerId && !player.Eliminated).ToArray();
        if (opponents.Length != 1 || !payload.Complete.Enabled || !state.States.IsEmpty || !opponents[0].States.IsEmpty
            || combat.AttackOptions.Length > MaxCandidates) return null;
        var enemy = opponents[0]; var own = state.Viewer!.Zone("Battlefield").Cards; var opposing = enemy.Zone("Battlefield").Cards;
        if (own.Length > MaxPermanents || opposing.Length > MaxPermanents) return null;
        var work = request.Work.CreateSlice(384, mechanicalReserve);
        var ownCreatures = own.Where(CasualTableAssessment.IsCreature).ToArray();
        var opposingCreatures = opposing.Where(CasualTableAssessment.IsCreature).ToArray();
        // Lands and noncreature artifacts do not participate in this forecast.
        // Charge each actual creature considered, not an extra combat search
        // for every unrelated permanent in the bounded source arrays.
        if (!work.TrySpend(Math.Max(1, ownCreatures.Length + opposingCreatures.Length))) return null;
        var byId = ownCreatures.Where(card => CardViews.Identity(card).Length > 0).ToDictionary(CardViews.Identity, StringComparer.Ordinal);
        var reserved = request.Plan?.RetainedBlockerIds.ToHashSet(StringComparer.Ordinal) ?? [];
        reserved.UnionWith(request.Plan?.Reservations.SelectMany(reservation => reservation.ManaSourceIds) ?? []);
        int capacity = 0;
        foreach (var creature in opposingCreatures) {
            if (creature.PhasedOut || creature.Tapped) continue;
            if (!Ordinary(creature)) return null;
            if (creature.BlockingCapacity is not { Unlimited: false, Maximum: >= 0 } blocks) return null;
            capacity = (int)Math.Min(MaxCohort, (long)capacity + blocks.Maximum!.Value);
        }
        if (capacity >= MaxCohort) return null;
        var selected = new List<Card>(); var assigned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attack in combat.Attackers) {
            if (!work.TrySpend() || attack.DefendingPlayerId != enemy.Id || !attack.HasDefender
                || attack.Defender is { Kind: not "player" } || !byId.TryGetValue(attack.CardId, out var card)
                || reserved.Contains(attack.CardId) || !Ordinary(card)) return null;
            assigned.Add(attack.CardId); selected.Add(card);
        }
        if (selected.Count > MaxCohort) return null;
        if (Lethal(selected, capacity, enemy.Life))
            return new CompleteAttackers(state.Id, state.Decision!.Id);

        var candidates = new List<Card>();
        foreach (var option in combat.AttackOptions) {
            if (!work.TrySpend()) return null;
            if (assigned.Contains(option.CardId) || reserved.Contains(option.CardId)
                || !option.Defenders.Any(defender => defender.Kind == "player" && defender.PlayerId == enemy.Id)
                || !byId.TryGetValue(option.CardId, out var card) || card.Key.Length == 0 || card.PhasedOut || !Ordinary(card)) continue;
            candidates.Add(card);
        }
        foreach (var card in candidates.OrderByDescending(CasualTableAssessment.UnblockedDamage).ThenBy(CardViews.Identity, StringComparer.Ordinal)) {
            if (selected.Count >= MaxCohort || !work.TrySpend()) return null;
            selected.Add(card);
            if (!Lethal(selected, capacity, enemy.Life)) continue;
            int remaining = selected.Count - assigned.Count;
            // The shared input allowance also funds later staging and completion.
            // Do not start a cohort which this bounded implementation cannot finish.
            long expansion = (long)ownCreatures.Length + opposingCreatures.Length + combat.Attackers.Length + combat.AttackOptions.Length + remaining + 1;
            long completion = (long)ownCreatures.Length + opposingCreatures.Length + selected.Count + 1;
            if (request.Work.Remaining < expansion * Math.Max(0, remaining - 1) + completion + mechanicalReserve) return null;
            var next = selected.First(item => !assigned.Contains(CardViews.Identity(item)));
            var command = new ToggleAttackPlayer(state.Id, state.Decision!.Id, next.Key, enemy.Id);
            return request.Frame.Allows(command) ? command : null;
        }
        return null;
    }

    private static bool Lethal(List<Card> cohort, int capacity, int life)
        => cohort.Count > capacity && cohort.Select(CasualTableAssessment.UnblockedDamage).OrderDescending().Skip(capacity).Sum() >= life;

    private static bool Ordinary(Card card) => !card.FaceDown && card.Power is >= 0 && card.Toughness is > 0
        && card.States is { } states && states.Where(state => state.Kind == "ability").All(state => OrdinaryKeywords.Contains(state.Key))
        && card.CombatKeywords.All(OrdinaryKeywords.Contains);
}
