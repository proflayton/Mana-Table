using System.Text.Json.Nodes;
using Mana.Contracts;

namespace Mana.Forge;

// The provider classifies its live input once, on its owned rules/input side.
// Native code validates that meaning; it never guesses from Java class names.
internal static class ForgeDecisionDecoder
{
    internal static void Normalize(JsonNode prompt, JsonNode? players = null)
    {
        try {
        string task = Text(prompt, "task"), kind = Text(prompt, "kind"), purpose = Text(prompt, "purpose");
        bool ordered = Flag(prompt, "ordered"), numeric = Flag(prompt, "numeric");
        bool input = task is "priority" or "mana" or "attack" or "block" or "selection" or "openingHand" or "confirmation";
        bool dialog = task is "choice" or "reveal" or "number" or "text" or "allocate";
        if (!input && !dialog || input && kind != "input" || dialog && kind != task
            && !(task == "number" && kind == "text" && numeric) || ordered && task != "choice"
            || numeric && task != "number")
            throw new IOException("The rules provider published an unsupported or contradictory decision task.");
        if (purpose is not ("" or "openingHand" or "targets" or "abilities")
            || (task == "openingHand") != (purpose == "openingHand")
            || purpose == "targets" && task != "selection"
            || purpose == "abilities" && (task != "choice" || ordered))
            throw new IOException("The rules provider published a decision purpose that does not match its task.");
        DecisionTask semantic = task switch {
            "priority" => DecisionTask.Priority, "openingHand" => DecisionTask.OpeningHand,
            "mana" => DecisionTask.Payment, "attack" => DecisionTask.Attack, "block" => DecisionTask.Block,
            "selection" when purpose == "targets" => DecisionTask.TargetSelection,
            "selection" => DecisionTask.EntitySelection, "confirmation" => DecisionTask.Confirmation,
            "choice" when purpose == "abilities" => DecisionTask.AbilityChoice,
            "choice" when ordered => DecisionTask.Ordering, "choice" => DecisionTask.Options,
            "reveal" => DecisionTask.Reveal, "number" => DecisionTask.Number,
            "text" => DecisionTask.Text,
            "allocate" => DecisionTask.Allocation, _ => throw Invalid()
        };
        if (prompt["librarySearch"] is { } search) {
            if (semantic is not (DecisionTask.TargetSelection or DecisionTask.Confirmation) || search is not JsonObject) throw Invalid();
            string destination = Text(search, "destination", required: true);
            _ = Text(search, "cardName", required: true);
            if (destination.Length == 0 || !search.AsObject().ContainsKey("libraryOwnerId")) throw Invalid();
            if (search["libraryOwnerId"] is { } owner) {
                if (owner is not JsonValue value || !value.TryGetValue<int>(out int id)
                    || players is not JsonArray seats || !seats.Any(player => player?["id"]?.GetValue<int>() == id)) throw Invalid();
            } else if (semantic == DecisionTask.Confirmation) throw Invalid();
        }
        if (prompt["openingHand"] is { } opening) {
            if (semantic != DecisionTask.OpeningHand || opening is not JsonObject) throw Invalid();
            int hand = Integer(opening, "handSize"), next = Integer(opening, "nextHandSize"), bottom = Integer(opening, "cardsToReturn");
            bool free = Flag(opening, "freeMulligan", required: true);
            if (hand < 0 || next < 0 || bottom < 0 || next > hand || free && (bottom > 0 || next < hand)) throw Invalid();
        }
        if (semantic is DecisionTask.Options or DecisionTask.Ordering or DecisionTask.AbilityChoice or DecisionTask.Number) {
            int min = Integer(prompt, "min"), max = Integer(prompt, "max");
            if (min > max || semantic != DecisionTask.Number && min < 0) throw Invalid();
            if (semantic == DecisionTask.AbilityChoice && (min is < 0 or > 1 || max != 1 || prompt["choices"] is not JsonArray { Count: > 0 }
                || prompt["libraryCards"] is not null && prompt["libraryCards"] is not JsonArray { Count: 0 })) throw Invalid();
        }
        prompt["task"] = (int)semantic;
        prompt["priorityAlternative"] = (int)(Text(prompt, "priorityAlternative") switch {
            "undo" when semantic == DecisionTask.Priority => PriorityAlternative.UndoLastAction,
            "passTurn" when semantic == DecisionTask.Priority => PriorityAlternative.PassTurn,
            "none" or "" => PriorityAlternative.None,
            _ => throw new IOException("The rules provider published an invalid priority alternative.")
        });
        foreach (string field in new[] { "inputType", "context", "kind", "purpose", "intent", "ordered", "numeric" })
            prompt.AsObject().Remove(field);
        } catch (Exception error) when (error is InvalidOperationException or FormatException or OverflowException) {
            throw new IOException("The rules provider published an invalid decision schema.", error);
        }
    }
    private static string Text(JsonNode node, string key, bool required = false) => node[key] is JsonValue value && value.TryGetValue<string>(out var text)
        ? text : node[key] == null && !required ? "" : throw Invalid();
    private static bool Flag(JsonNode node, string key, bool required = false) => node[key] is JsonValue value && value.TryGetValue<bool>(out bool flag)
        ? flag : node[key] == null && !required ? false : throw Invalid();
    private static int Integer(JsonNode node, string key) => node[key] is JsonValue value && value.TryGetValue<int>(out int number) ? number : throw Invalid();
    private static IOException Invalid() => new("The rules provider published an unsupported or contradictory decision task or schema.");
}
