namespace Mana.Contracts;

// A full-hand complement is meaningful only for this exact owned occurrence
// set. It supplies no new visibility, card handles or rules permission.
public static class HandReductionFacts
{
    public static bool IsCurrent(GameSnapshot state)
    {
        if (state.Status != "playing" || state.Decision?.Payload is not OptionsPayload { HandReduction: { } reduction } options
            || reduction.PlayerId != state.ViewerId || state.DecisionOwnerId is { } owner && owner != state.ViewerId
            || state.Viewer is not { Eliminated: false } viewer || viewer.MaxHandSize != reduction.MaximumHandSize) return false;
        var hand = viewer.Zone("Hand");
        if (hand.Count != reduction.HandSize || hand.Cards.IsDefault || hand.Cards.Length != hand.Count
            || hand.Cards.Any(card => card == null)) return false;
        var identities = hand.Cards.Select(CardViews.Identity).ToHashSet(StringComparer.Ordinal);
        return identities.Count == hand.Count && !identities.Contains("")
            && identities.SetEquals(options.Selection.Choices.Select(choice => choice.CardId));
    }
}
