using Mana.Contracts;

namespace Mana.Magic;

/// <summary>Connects explicit option indices to already visible table occurrences.</summary>
public static class TableChoices
{
    public static bool IsDirect(GameSnapshot state)
    {
        var selection = state.Decision?.Payload switch { OptionsPayload options => options.Selection, OrderingPayload order => order.Selection, _ => null };
        if (selection is not { Choices.Length: > 0, Disclosures.Length: 0 }) return false;
        bool ordered = state.Decision!.Payload is OrderingPayload;
        if (ordered && selection.Choices.Length > 12) return false;
        var cards = state.Players.SelectMany(player => player.Zones.Where(zone => zone.Name == "Battlefield"
            || !ordered && (zone.Name == "Command" || zone.Name == "Hand" && player.Id == state.ViewerId)).SelectMany(zone => zone.Cards)).ToArray();
        return selection.Choices.All(choice => choice.Card != null && choice.CardId.Length > 0 && cards.Any(card => card.VisualId == choice.CardId))
            && selection.Choices.Select(choice => choice.CardId).Distinct().Count() == selection.Choices.Length;
    }
    public static int? Index(GameSnapshot state, Card card) => !IsDirect(state) ? null : state.Decision!.Payload switch {
        OptionsPayload options => options.Selection.Choices.FirstOrDefault(choice => choice.CardId == card.VisualId)?.Index,
        OrderingPayload order => order.Selection.Choices.FirstOrDefault(choice => choice.CardId == card.VisualId)?.Index,
        _ => null
    };
    public static bool CanConfirm(GameSnapshot state, IReadOnlyCollection<int> selected) => IsDirect(state) && DecisionCommands.Choose(state, selected) != null;
}
