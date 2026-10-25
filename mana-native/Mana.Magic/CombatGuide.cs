using Mana.Contracts;

namespace Mana.Magic;

// Presentation of the engine's legal pairs; no combat rules are computed here.
public static class CombatGuide
{
    public static Card? Find(GameSnapshot state, string id) => state.Players.SelectMany(p => p.Zones).SelectMany(z => z.Cards).FirstOrDefault(c => c.CombatId == id);
    public static bool CanSelect(GameSnapshot state, Card card) => !string.IsNullOrEmpty(card.Key) && (state.Decision?.Task switch {
        DecisionTask.Attack => state.Combat?.AttackOptions.Any(a => a.CardId == card.CombatId && a.Defenders.Length > 0) == true,
        DecisionTask.Block => state.Combat?.Attackers.Any(a => a.EligibleBlockerIds.Contains(card.CombatId)) == true,
        _ => false
    });
    public static bool CanConfirm(GameSnapshot state) => state.Decision is { Task: DecisionTask.Priority or DecisionTask.OpeningHand or DecisionTask.Payment or DecisionTask.Attack or DecisionTask.Block or DecisionTask.TargetSelection or DecisionTask.EntitySelection or DecisionTask.Confirmation, OkEnabled: true }
        && !(state.Decision.Task == DecisionTask.Block && !string.IsNullOrWhiteSpace(state.Combat?.BlockProblem));
    public static DecisionCommand? Remove(GameSnapshot state, string source, string? attackerId = null)
    {
        var d = state.Decision; var card = Find(state, source);
        if (d == null || card == null || !CanSelect(state, card)) return null;
        if (d.Task == DecisionTask.Attack && state.Combat?.Attackers.FirstOrDefault(a => a.CardId == source) is { } attack) {
            if (attack.Defender is { Kind: "card" } defender) {
                var target = Find(state, defender.Id);
                return target != null && LegalTargets.CanAttackCard(state, source, defender.Id)
                    ? new ToggleAttackPermanent(state.Id, d.Id, card.Key, target.Key) : null;
            }
            return LegalTargets.CanAttack(state, source, attack.DefendingPlayerId)
                ? new ToggleAttackPlayer(state.Id, d.Id, card.Key, attack.DefendingPlayerId) : null;
        }
        if (d.Task == DecisionTask.Block && state.Combat?.Attackers.FirstOrDefault(a => a.BlockerIds.Contains(source) && (attackerId == null || a.CardId == attackerId)) is { } blocked) {
            var attacker = Find(state, blocked.CardId);
            return attacker != null && LegalTargets.CanBlock(state, source, blocked.CardId)
                ? new ToggleBlock(state.Id, d.Id, attacker.Key, card.Key) : null;
        }
        return null;
    }
}
