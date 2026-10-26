using Mana.Contracts;

namespace Mana.Magic;

// The engine supplies eligibility. Names, printed rules and card types never
// create an alternate action in the client.
public static class HandPlacements
{
    public static PriorityAbility[] AllOptions(GameSnapshot state) => (state.Viewer?.Zone("Hand").Cards ?? [])
        .SelectMany(card => Options(state, card)).DistinctBy(a => a.Id).ToArray();

    public static PriorityAbility[] Options(GameSnapshot state, Card clicked)
    {
        if (state.Status != "playing" || state.Decision is not { Payload: PriorityPayload priority } decision
            || decision.Id.Length == 0 || state.Viewer is not { } viewer) return [];
        var card = viewer.Zone("Hand").Cards.FirstOrDefault(c => CardViews.Same(c, clicked));
        if (card is not { FaceDown: false, Key.Length: > 0 }) return [];
        return priority.Abilities.Where(a => GameCommandPermissions.Allows(state,
                new PlaceFromHand(state.Id, decision.Id, card.Key, a.Id)))
            .DistinctBy(a => a.Id).ToArray();
    }

    public static PlaceFromHand? Start(GameSnapshot state, Card card, string abilityId)
    {
        var current = state.Viewer?.Zone("Hand").Cards.FirstOrDefault(c => CardViews.Same(c, card));
        return current != null && Options(state, current).Any(a => a.Id == abilityId)
            ? new(state.Id, state.Decision!.Id, current.Key, abilityId) : null;
    }

    public static string Availability(GameSnapshot state, Card card)
    {
        int count = Options(state, card).Sum(a => a.HandPlacementLimit);
        return count == 0 ? "" : count + (count == 1 ? " placement available" : " placements available");
    }
}
