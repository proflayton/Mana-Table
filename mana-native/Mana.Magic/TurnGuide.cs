using Mana.Contracts;

namespace Mana.Magic;

public static class TurnGuide
{
    public static readonly (string Key, string Label)[] Stops = [("UPKEEP", "Upkeep"), ("DRAW", "Draw"), ("MAIN1", "Main 1"), ("COMBAT_BEGIN", "Combat"), ("MAIN2", "Main 2"), ("END_OF_TURN", "End")];
    public static string Confirm(GameSnapshot state)
    {
        var d = state.Decision;
        if (d == null) return "Waiting";
        if (d.Task == DecisionTask.Attack) return state.Combat?.Attackers.Length > 0 ? "Confirm attackers" : "No attacks";
        if (d.Task == DecisionTask.Block) return state.Combat?.Attackers.Any(a => a.BlockerIds.Any(id => state.Viewer?.Zone("Battlefield").Cards.Any(c => c.CombatId == id) == true)) == true ? "Confirm blocks" : "No blocks";
        if (d.Task != DecisionTask.Priority) return d.Ok;
        if (state.Stack.Length > 0) return "Pass priority";
        if (state.ActivePlayerId != state.ViewerId) return "Pass priority";
        return state.PhaseKey switch { "MAIN1" => "Go to combat", "COMBAT_BEGIN" => "To attackers", "COMBAT_DECLARE_ATTACKERS" => "To blockers", "COMBAT_DECLARE_BLOCKERS" => "To damage", "COMBAT_FIRST_STRIKE_DAMAGE" => "Continue damage", "COMBAT_DAMAGE" => "Finish damage", "COMBAT_END" => "Second main", "MAIN2" => "Go to end step", "END_OF_TURN" => "End turn", _ => "Continue" };
    }
    public static string Instruction(GameSnapshot state) => state.Decision?.Task switch {
        DecisionTask.Attack => "Choose a creature, then its defender. Dragging works too.",
        DecisionTask.Block => "Choose your blocker, then an attacking creature.",
        DecisionTask.Payment => state.Decision is { Message.Length: > 0 } payment ? payment.Message : "Pay the cost with your mana sources, or use Auto-pay.",
        DecisionTask.Priority when state.Stack.Length > 0 => "Add a response, or pass priority.",
        DecisionTask.Priority when state.ActivePlayerId == state.ViewerId && state.PhaseKey is "MAIN1" or "MAIN2" => "Click to read; use Play card or drag onto the table to play.",
        DecisionTask.Priority => "Your chance to respond.",
        _ => state.Decision?.Message ?? (state.Status == "starting" ? "Preparing your table…" : "The table is resolving.")
    };
    public static bool CanAutoPass(GameSnapshot state, bool automatic, bool held, IReadOnlySet<string> stops, bool overlayOpen) => automatic && !held && !overlayOpen
        && state.Status == "playing" && state.Turn > 0
        && state.Decision is { Task: DecisionTask.Priority, CanAutoPass: true, OkEnabled: true }
        && !(state.ActivePlayerId == state.ViewerId && stops.Contains(state.PhaseKey));
}
