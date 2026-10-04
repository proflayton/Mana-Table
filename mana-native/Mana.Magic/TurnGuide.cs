using Mana.Contracts;

namespace Mana.Magic;

public static class TurnGuide
{
    public static readonly (string Key, string Label)[] Stops = [("UPKEEP", "Upkeep"), ("DRAW", "Draw"), ("MAIN1", "Main 1"), ("COMBAT_BEGIN", "Combat"), ("MAIN2", "Main 2"), ("END_OF_TURN", "End")];
    public static string Confirm(GameSnapshot state)
    {
        var d = state.Decision;
        if (d == null) return "Waiting";
        if (d.Intent == DecisionIntent.Attack) return state.Combat?.Attackers.Length > 0 ? "Confirm attackers" : "No attacks";
        if (d.Intent == DecisionIntent.Block) return state.Combat?.Attackers.Any(a => a.BlockerIds.Any(id => state.Viewer?.Zone("Battlefield").Cards.Any(c => c.CombatId == id) == true)) == true ? "Confirm blocks" : "No blocks";
        if (d.Intent != DecisionIntent.Priority) return d.Ok;
        if (state.Stack.Length > 0) return "Let it resolve";
        return state.PhaseKey switch { "MAIN1" => "Go to combat", "COMBAT_BEGIN" => "To attackers", "COMBAT_DECLARE_ATTACKERS" => "To blockers", "COMBAT_DECLARE_BLOCKERS" => "To damage", "COMBAT_FIRST_STRIKE_DAMAGE" => "Continue damage", "COMBAT_DAMAGE" => "Finish damage", "COMBAT_END" => "Second main", "MAIN2" => "Go to end step", "END_OF_TURN" => "End turn", _ => "Continue" };
    }
    public static string Instruction(GameSnapshot state) => state.Decision?.Intent switch {
        DecisionIntent.Attack => "Choose a creature, then its defender. Dragging works too.",
        DecisionIntent.Block => "Choose your blocker, then an attacking creature.",
        DecisionIntent.Mana => "Pay the cost with your mana sources, or use Auto-pay.",
        DecisionIntent.Priority when state.Stack.Length > 0 => "Respond with a card or ability, or let the stack resolve.",
        DecisionIntent.Priority when state.ActivePlayerId == state.ViewerId && state.PhaseKey is "MAIN1" or "MAIN2" => "Double-click or drag a card to play. Hover to read.",
        DecisionIntent.Priority => "Your chance to respond.",
        _ => state.Decision?.Message ?? (state.Status == "starting" ? "Preparing your table…" : "The table is resolving.")
    };
    public static bool CanAutoPass(GameSnapshot state, bool automatic, bool held, IReadOnlySet<string> stops, bool overlayOpen) => automatic && !held && !overlayOpen
        && state.Status == "playing" && state.Turn > 0
        && state.Decision is { Kind: "input", Intent: DecisionIntent.Priority, CanAutoPass: true, OkEnabled: true }
        && !(state.ActivePlayerId == state.ViewerId && stops.Contains(state.PhaseKey));
}
