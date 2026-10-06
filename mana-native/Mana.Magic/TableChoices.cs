using Mana.Contracts;

namespace Mana.Magic;

/// <summary>Connects authoritative choice indices to existing table occurrences.</summary>
public static class TableChoices
{
    public static bool IsDirect(GameSnapshot state)
    {
        if (state.Decision is not { Kind: "choice", Choices.Length: > 0, LibraryCards.Length: 0 } d) return false;
        var cards = state.Players.SelectMany(p => p.Zones.Where(z => z.Name is "Battlefield" or "Command"
            || z.Name == "Hand" && p.Id == state.ViewerId).SelectMany(z => z.Cards)).ToArray();
        return d.Choices.All(c => c.Card != null && !string.IsNullOrEmpty(c.CardId) && cards.Any(card => card.VisualId == c.CardId))
            && d.Choices.Select(c => c.CardId).Distinct().Count() == d.Choices.Length;
    }
    public static int? Index(GameSnapshot state, Card card) => IsDirect(state)
        ? state.Decision!.Choices.FirstOrDefault(c => c.CardId == card.VisualId)?.Index : null;
    public static bool CanConfirm(GameSnapshot state, IReadOnlyCollection<int> selected) => IsDirect(state)
        && selected.Count >= state.Decision!.Min && selected.Count <= state.Decision.Max
        && selected.Distinct().Count() == selected.Count && selected.All(i => state.Decision.Choices.Any(c => c.Index == i));
}
