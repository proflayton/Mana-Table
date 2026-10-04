using Mana.Contracts;

namespace Mana.Magic;

public static class LegalTargets
{
    public static bool CanAttack(GameSnapshot state, string attacker, int playerId) => state.Decision?.Intent == DecisionIntent.Attack
        && state.Combat?.AttackOptions.Any(a => a.CardId == attacker && a.Defenders.Any(d => d.Kind == "player" && d.PlayerId == playerId)) == true;
    public static bool CanAttackCard(GameSnapshot state, string attacker, string defender) => state.Decision?.Intent == DecisionIntent.Attack
        && state.Combat?.AttackOptions.Any(a => a.CardId == attacker && a.Defenders.Any(d => d.Kind != "player" && d.Id == defender)) == true;
    public static bool CanBlock(GameSnapshot state, string blocker, string attacker) => state.Decision?.Intent == DecisionIntent.Block
        && state.Combat?.Attackers.Any(a => a.CardId == attacker && a.EligibleBlockerIds.Contains(blocker)) == true;
}
