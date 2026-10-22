using System.Text.Json.Nodes;
using Mana.Contracts;

namespace Mana.Forge;

// The provider classifies its live input once, on its owned rules/input side.
// Native code validates that meaning; it never guesses from Java class names.
internal static class ForgeDecisionDecoder
{
    internal static void Normalize(JsonNode prompt)
    {
        string task = prompt["task"]?.GetValue<string>() ?? "";
        string kind = prompt["kind"]?.GetValue<string>() ?? "";
        bool input = task is "priority" or "mana" or "attack" or "block" or "selection" or "openingHand" or "confirmation";
        bool dialog = task is "choice" or "reveal" or "number" or "text" or "allocate";
        if (!input && !dialog || input && kind != "input" || dialog && kind != task
            && !(task == "number" && kind == "text" && prompt["numeric"]?.GetValue<bool>() == true))
            throw new IOException("The rules provider published an unsupported or contradictory decision task.");
        var intent = task switch {
            "priority" => DecisionIntent.Priority, "mana" => DecisionIntent.Mana,
            "attack" => DecisionIntent.Attack, "block" => DecisionIntent.Block,
            "selection" => DecisionIntent.Selection, _ => DecisionIntent.General
        };
        var purpose = prompt["purpose"]?.GetValue<string>() switch {
            "openingHand" => DecisionPurpose.OpeningHand, "targets" => DecisionPurpose.TargetSelection,
            "abilities" => DecisionPurpose.AbilitySelection, "" or null => DecisionPurpose.Unspecified,
            _ => throw new IOException("The rules provider published an unsupported decision purpose.")
        };
        if ((task == "openingHand") != (purpose == DecisionPurpose.OpeningHand)
            || purpose == DecisionPurpose.TargetSelection && task != "selection"
            || purpose == DecisionPurpose.AbilitySelection && task != "choice")
            throw new IOException("The rules provider published a decision purpose that does not match its task.");
        prompt["intent"] = (int)intent;
        prompt["purpose"] = (int)purpose;
        prompt["priorityAlternative"] = (int)(prompt["priorityAlternative"]?.GetValue<string>() switch {
            "undo" when task == "priority" => PriorityAlternative.UndoLastAction,
            "passTurn" when task == "priority" => PriorityAlternative.PassTurn,
            "none" or "" or null => PriorityAlternative.None,
            _ => throw new IOException("The rules provider published an invalid priority alternative.")
        });
        var fields = prompt.AsObject();
        fields.Remove("inputType");
        fields.Remove("context");
        fields.Remove("task");
    }
}
