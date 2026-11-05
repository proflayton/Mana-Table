using Mana.Contracts;

namespace Mana.Magic;

// A legal manual attempt and a positive payment cue are different projections.
// Current offered actions remain usable even when payment knowledge is unknown.
public static class CardAffordability
{
    public static bool CanHighlight(GameSnapshot state, Card reported, string zone, bool interactionAvailable)
    {
        if (!interactionAvailable || state.Status != "playing" || state.Decision == null) return false;
        if (state.Decision?.Payload is not PriorityPayload || zone is "Battlefield" or "Stack") return true;
        if (state.DecisionOwnerId != state.ViewerId || state.PriorityPlayerId != state.ViewerId) return false;
        var current = CardViews.InZones(state).FirstOrDefault(occurrence => occurrence.Zone == zone
            && reported.Key.Length > 0 && occurrence.Card.Key == reported.Key && CardViews.Same(occurrence.Card, reported)).Card;
        return current is { FaceDown: false, PlayAffordability: ActionAffordability.Payable };
    }
}
