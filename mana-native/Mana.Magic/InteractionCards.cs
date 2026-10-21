using Mana.Contracts;

namespace Mana.Magic;

/// <summary>
/// Binds an interaction's reported face to a currently disclosed occurrence for
/// display. Choice indexes, action handles and engine allocation metadata stay
/// on the original interaction objects.
/// </summary>
public static class InteractionCards
{
    public static Card Current(GameSnapshot? state, Card reported, string occurrenceId = "")
        => Find(state, reported, occurrenceId) ?? reported;

    public static Card? Choice(GameSnapshot? state, Choice choice)
        => Find(state, choice.Card, choice.CardId) ?? choice.Card;

    public static Card? Source(GameSnapshot? state, Decision decision)
        => Find(state, decision.SourceCard, decision.SourceCardId) ?? decision.SourceCard;

    private static Card? Find(GameSnapshot? state, Card? reported, string occurrenceId)
    {
        if (state == null) return null;
        // A supplied occurrence link is authoritative. Printed names, prompt
        // array positions and old selection keys never identify a current face.
        string id = occurrenceId.Length > 0 ? occurrenceId
            : !string.IsNullOrEmpty(reported?.VisualId) ? reported.VisualId : reported?.CombatId ?? "";
        if (id.Length == 0) return null;
        foreach (var zone in state.Players.SelectMany(player => player.Zones)) {
            foreach (var card in zone.Cards)
                if (card.VisualId == id || card.CombatId == id) return card;
            if (zone.TopCard is { } top && (top.VisualId == id || top.CombatId == id)) return top;
        }
        // An ability can legitimately retain a disclosed source face after that
        // source left the battlefield. Only its current snapshot's embedded
        // view is the fallback; nothing is retained from previous snapshots.
        return null;
    }
}
