namespace Mana.Contracts;

/// <summary>The allowlisted card views received for this seat. Never retain a revoked object.</summary>
public static class CardViews
{
    public static IEnumerable<(Card Card, string Zone)> Located(GameSnapshot state) => state.Players.SelectMany(p => p.Zones.SelectMany(z => z.Cards.Select(c => (Card: c, Zone: z.Name))))
        .Concat(state.Stack.Where(s => s.Card != null).Select(s => (Card: s.Card!, Zone: "Stack")));
    public static IEnumerable<Card> Visible(GameSnapshot state) => Located(state).Select(p => p.Card)
        .Concat(state.Players.SelectMany(p => p.Zones).Where(z => z.TopCard != null).Select(z => z.TopCard!))
        .Concat(state.Decision?.Choices.Where(c => c.Card != null).Select(c => c.Card!) ?? [])
        .Concat(state.Decision?.LibraryCards.Select(c => c.Card) ?? [])
        .Concat(state.Decision?.SourceCard is { } source ? [source] : []);
    public static string Identity(Card card) => !string.IsNullOrEmpty(card.VisualId) ? card.VisualId : !string.IsNullOrEmpty(card.CombatId) ? card.CombatId : card.Key;
    public static bool Same(Card a, Card b) => !string.IsNullOrEmpty(Identity(a)) && Identity(a) == Identity(b);
}
