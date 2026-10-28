using Mana.Contracts;

namespace Mana.Magic;

public static class TurnGuide
{
    public static readonly (string Key, string Label)[] Stops = [("UPKEEP", "Upkeep"), ("DRAW", "Draw"), ("MAIN1", "Main 1"), ("COMBAT_BEGIN", "Combat"), ("MAIN2", "Main 2"), ("END_OF_TURN", "End")];
    public static string Confirm(GameSnapshot state)
    {
        var d = state.Decision;
        if (d == null) return "Waiting";
        if (d.Payload is AttackPayload) return state.Combat?.Attackers.Length > 0 ? "Confirm attackers" : "No attacks";
        if (d.Payload is BlockPayload) return state.Combat?.Attackers.Any(a => a.BlockerIds.Any(id => state.Viewer?.Zone("Battlefield").Cards.Any(c => c.CombatId == id) == true)) == true ? "Confirm blocks" : "No blocks";
        if (d.Payload is not PriorityPayload) return d.Payload switch {
            OpeningHandPayload opening => opening.Keep.Label,
            PaymentPayload payment => payment.Pay.Label,
            TargetSelectionPayload target => target.Complete.Label,
            EntitySelectionPayload entity => entity.Complete.Label,
            ConfirmationPayload confirmation => confirmation.Accept.Label,
            OptionsPayload => "Confirm selection", OrderingPayload => "Confirm order", AbilityChoicePayload => "Choose an ability",
            RevealPayload => "Continue", NumberPayload or TextPayload => "Confirm", AllocationPayload => "Confirm assignment",
            _ => throw new InvalidOperationException("The decision payload has no confirmation label.")
        };
        if (state.Stack.Length > 0) return "Pass once";
        if (state.ActivePlayerId != state.ViewerId) return "Pass once";
        return state.PhaseKey switch { "MAIN1" => "Go to combat", "COMBAT_BEGIN" => "To attackers", "COMBAT_DECLARE_ATTACKERS" => "To blockers", "COMBAT_DECLARE_BLOCKERS" => "To damage", "COMBAT_FIRST_STRIKE_DAMAGE" => "Continue damage", "COMBAT_DAMAGE" => "Finish damage", "COMBAT_END" => "Second main", "MAIN2" => "Go to end step", "END_OF_TURN" => "End turn", _ => "Continue" };
    }
    public static string Instruction(GameSnapshot state) => state.Decision?.Payload switch {
        AttackPayload => "Choose a creature, then its defender. Dragging works too.",
        BlockPayload => "Choose your blocker, then an attacking creature.",
        PaymentPayload => state.Decision is { Context.Message.Length: > 0 } payment ? payment.Context.Message : "Pay the cost with your mana sources, or use Auto-pay.",
        PriorityPayload when state.Stack.Length > 0 => "Add a response, or pass priority.",
        PriorityPayload when state.ActivePlayerId == state.ViewerId && state.PhaseKey is "MAIN1" or "MAIN2" => "Click to read; use Play card or drag onto the table to play.",
        PriorityPayload => "Your chance to respond.",
        _ => state.Decision?.Context.Message ?? (state.Status == "starting" ? "Preparing your table…" : "The table is resolving.")
    };
    public static bool CanAutoPass(GameSnapshot state, bool automatic, bool held, IReadOnlySet<string> stops, bool overlayOpen) => automatic && !held && !overlayOpen
        && state.Status == "playing" && state.Turn > 0
        && state.Decision is { Payload: PriorityPayload { CanAutoPass: true, Pass.Enabled: true } }
        && !(state.ActivePlayerId == state.ViewerId && stops.Contains(state.PhaseKey));
}
