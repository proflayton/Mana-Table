using Mana.Contracts;

namespace Mana.AI;

// Bounded final-duel and explicit free-for-all player-elimination forecasts.
// This is ordinary public combat arithmetic,
// not a new legality solver or a prediction of hidden responses/triggered effects.
// Each actual assignment still uses a current engine-offered attack command.
internal static class AiSwarmCombat
{
    private const int MaxCandidates = 128, MaxCohort = 32, MaxPermanents = 256, MultiplayerWork = 384;
    private static readonly HashSet<string> OrdinaryKeywords = new([
        "vigilance", "trample", "haste", "flying", "reach", "deathtouch", "indestructible", "first strike", "double strike",
        "first-strike", "double-strike", "hexproof", "ward", "shroud", "protection", "defender", "flash", "menace", "partner", "partner with",
        "evolve", "evoke", "adapt", "modular", "sunburst"
    ], StringComparer.OrdinalIgnoreCase);

    internal static DecisionCommand? TryFinish(AiDecisionRequest request, AttackPayload payload, int mechanicalReserve)
    {
        var state = request.Frame.State; var combat = state.Combat!;
        // Team combat can permit additional defenders/blockers. Only actual
        // opponent relations may enter either forecast; current offers remain
        // the authority.
        if (state.Players.Any(player => player.Id != state.ViewerId && !player.Eliminated
            && !AiPlayerRelations.Opponent(state, player.Id))) return null;
        var opponents = state.Players.Where(player => AiPlayerRelations.Opponent(state, player.Id) && !player.Eliminated).ToArray();
        if (opponents.Length > 1) return TryEliminateOpponent(request, payload, opponents, mechanicalReserve);
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
        var reserved = ReservedResources(request.Plan);
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

    private sealed record Elimination(Player Opponent, List<Card> Cohort);

    private static DecisionCommand? TryEliminateOpponent(AiDecisionRequest request, AttackPayload payload,
        Player[] opponents, int mechanicalReserve)
    {
        var state = request.Frame.State; var combat = state.Combat!; var actor = state.Viewer!;
        var alive = state.Players.Where(player => !player.Eliminated).ToArray();
        // Unlike the duel forecast, this path must leave a viable public return
        // position. Explicit distinct teams exclude shared life/blocking; absent
        // legacy relationships cannot establish this narrower FFA model.
        if (alive.Length is not (3 or 4) || !payload.Complete.Enabled || !state.States.IsEmpty
            || alive.Any(player => player.TeamId is not >= 0 || player.Life <= 0 || player.States.IsDefault
                || !player.States.IsEmpty || player.Counters.Any(counter => counter.Value != 0))
            || alive.Select(player => player.TeamId).Distinct().Count() != alive.Length
            || combat.AttackOptions.Length > MaxCandidates) return null;
        var fields = alive.ToDictionary(player => player.Id, player => player.Zone("Battlefield").Cards);
        if (fields.Values.Any(cards => cards.Length > MaxPermanents)) return null;
        var creatures = fields.ToDictionary(pair => pair.Key, pair => pair.Value.Where(CasualTableAssessment.IsCreature).ToArray());
        var work = request.Work.CreateSlice(MultiplayerWork, mechanicalReserve);
        if (!work.TrySpend(Math.Max(1, creatures.Values.Sum(cards => cards.Length)))) return null;
        var byId = creatures[actor.Id].Where(card => CardViews.Identity(card).Length > 0)
            .ToDictionary(CardViews.Identity, StringComparer.Ordinal);
        var reserved = ReservedResources(request.Plan);
        var assigned = new HashSet<string>(StringComparer.Ordinal);
        var staged = new List<Card>(); int? defender = null;
        foreach (var attack in combat.Attackers) {
            if (!work.TrySpend() || !attack.HasDefender || attack.Defender is { Kind: not "player" }
                || defender is { } prior && prior != attack.DefendingPlayerId
                || !opponents.Any(player => player.Id == attack.DefendingPlayerId)
                || !byId.TryGetValue(attack.CardId, out var card) || reserved.Contains(attack.CardId) || !Ordinary(card)) return null;
            defender = attack.DefendingPlayerId; assigned.Add(attack.CardId); staged.Add(card);
        }
        if (staged.Count > MaxCohort) return null;
        // Treat all nominal return damage as coming from the most advanced
        // commander. This needs no printed-name/source matching. Missing owner
        // coverage is not evidence that previous commander damage was zero.
        long? commanderRoom = null;
        if (!actor.CommanderDamage.IsDefaultOrEmpty && actor.CommanderDamage.Length <= MaxCandidates
            && actor.CommanderDamage.All(row => row is { Damage: >= 0 and < 21 })
            && alive.All(player => actor.CommanderDamage.Any(row => row.OwnerId == player.Id))) {
            if (!work.TrySpend(actor.CommanderDamage.Length)) return null;
            commanderRoom = 21L - actor.CommanderDamage.Max(row => row.Damage);
        }
        var plans = new List<Elimination>();
        foreach (var enemy in opponents.OrderBy(player => player.Id)) {
            if (defender is { } locked && enemy.Id != locked) continue;
            long returning = 0; bool knownReturn = true;
            foreach (var other in opponents.Where(player => player.Id != enemy.Id))
            foreach (var card in creatures[other.Id]) {
                // Count tapped/sick and phased-out bodies too: they may untap
                // or phase in before a later combat. Assume no own blockers.
                if (!work.TrySpend() || !Ordinary(card)) { knownReturn = false; break; }
                returning += CasualTableAssessment.UnblockedDamage(card);
            }
            if (!knownReturn || returning >= actor.Life
                || returning > 0 && (commanderRoom == null || returning >= commanderRoom.Value)) continue;
            int capacity = 0; bool knownBlocks = true;
            foreach (var card in creatures[enemy.Id]) {
                if (card.PhasedOut || card.Tapped) continue;
                if (!work.TrySpend() || !Ordinary(card)
                    || card.BlockingCapacity is not { Unlimited: false, Maximum: >= 0 } blocks) { knownBlocks = false; break; }
                capacity = (int)Math.Min(MaxCohort, (long)capacity + blocks.Maximum!.Value);
            }
            if (!knownBlocks || capacity >= MaxCohort) continue;
            var cohort = new List<Card>(staged);
            if (Lethal(cohort, capacity, enemy.Life)) {
                plans.Add(new(enemy, cohort)); continue;
            }
            var candidates = new List<Card>();
            foreach (var option in combat.AttackOptions) {
                if (!work.TrySpend()) return null;
                if (assigned.Contains(option.CardId) || reserved.Contains(option.CardId)
                    || !option.Defenders.Any(target => target.Kind == "player" && target.PlayerId == enemy.Id)
                    || !byId.TryGetValue(option.CardId, out var card) || card.Key.Length == 0 || card.PhasedOut || !Ordinary(card)) continue;
                var offered = new ToggleAttackPlayer(state.Id, state.Decision!.Id, card.Key, enemy.Id);
                if (request.Frame.Allows(offered)) candidates.Add(card);
            }
            foreach (var card in candidates.OrderByDescending(CasualTableAssessment.UnblockedDamage).ThenBy(CardViews.Identity, StringComparer.Ordinal)) {
                if (cohort.Count >= MaxCohort) break;
                if (!work.TrySpend()) return null;
                cohort.Add(card);
                if (!Lethal(cohort, capacity, enemy.Life)) continue;
                plans.Add(new(enemy, cohort)); break;
            }
        }
        var plan = plans.OrderBy(candidate => candidate.Cohort.Count).ThenBy(candidate => candidate.Opponent.Id).FirstOrDefault();
        if (plan == null) return null;
        int remaining = plan.Cohort.Count - staged.Count;
        if (remaining == 0) return new CompleteAttackers(state.Id, state.Decision!.Id);
        // Each later stage recomputes from fresh state, within the same capped
        // optional search. Reserve its worst-case work before starting a group.
        if (request.Work.Remaining < (long)remaining * MultiplayerWork + mechanicalReserve) return null;
        var next = plan.Cohort.First(card => !assigned.Contains(CardViews.Identity(card)));
        var command = new ToggleAttackPlayer(state.Id, state.Decision!.Id, next.Key, plan.Opponent.Id);
        return request.Frame.Allows(command) ? command : null;
    }

    private static HashSet<string> ReservedResources(AiTurnPlan? plan)
    {
        var reserved = plan?.RetainedBlockerIds.ToHashSet(StringComparer.Ordinal) ?? [];
        if (plan != null) foreach (var reservation in plan.Reservations) {
            reserved.Add(reservation.SourceCardId);
            reserved.UnionWith(reservation.ManaSourceIds);
        }
        return reserved;
    }

    private static bool Lethal(List<Card> cohort, int capacity, int life)
        => cohort.Count > capacity && cohort.Select(CasualTableAssessment.UnblockedDamage).OrderDescending().Skip(capacity).Sum() >= life;

    private static bool Ordinary(Card card) => !card.FaceDown && card.Power is >= 0 && card.Toughness is > 0
        && card.States is { } states && states.Where(state => state.Kind == "ability").All(state => OrdinaryKeywords.Contains(state.Key))
        && card.CombatKeywords.All(OrdinaryKeywords.Contains);
}
