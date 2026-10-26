namespace Mana.Contracts;

/// <summary>A current player-zone projection, including a disclosed top or a masked face.</summary>
public readonly record struct ZoneCardOccurrence(int PlayerId, string Zone, Card Card);

/// <summary>The allowlisted card views received for this seat. Never retain a revoked object.</summary>
public static class CardViews
{
    // Stack and decision faces may describe a departed source. They are not
    // player-zone locations and cannot establish current source authority.
    public static IEnumerable<ZoneCardOccurrence> InZones(GameSnapshot state)
    {
        foreach (var player in state.Players)
            foreach (var zone in player.Zones) {
                foreach (var card in zone.Cards) yield return new(player.Id, zone.Name, card);
                if (zone.TopCard is { } top) yield return new(player.Id, zone.Name, top);
            }
    }

    public static ZoneCardOccurrence? FindZoneOccurrence(GameSnapshot state, string occurrenceId)
    {
        if (occurrenceId.Length == 0) return null;
        foreach (var occurrence in InZones(state))
            if (occurrence.Card.VisualId == occurrenceId || occurrence.Card.CombatId == occurrenceId) return occurrence;
        return null;
    }

    public static ZoneCardOccurrence? FindZoneActionSource(GameSnapshot state, string actionKey)
    {
        if (actionKey.Length == 0) return null;
        foreach (var occurrence in InZones(state))
            if (occurrence.Card.Key == actionKey) return occurrence;
        return null;
    }

    // Reconcile display only. Original choices, action handles and constraints
    // stay on their interaction objects. A masked current face replaces an old
    // revealed report; only the current snapshot's embedded face is a fallback.
    public static Card Current(GameSnapshot? state, Card reported, string occurrenceId = "")
        => CurrentFace(state, reported, occurrenceId) ?? reported;
    public static Card? Choice(GameSnapshot? state, Choice choice)
        => CurrentFace(state, choice.Card, choice.CardId) ?? choice.Card;
    public static Card? Recipient(GameSnapshot? state, AllocationRecipient recipient)
        => CurrentFace(state, recipient.Card, recipient.CardId) ?? recipient.Card;
    public static Card Disclosed(GameSnapshot? state, LibraryChoice face)
        => CurrentFace(state, face.Card, face.CardId) ?? face.Card;
    public static Card? Source(GameSnapshot? state, Decision decision)
        => decision.Context.Source is { } source ? CurrentFace(state, source.Card, source.CardId) ?? source.Card : null;

    private static Card? CurrentFace(GameSnapshot? state, Card? reported, string occurrenceId)
    {
        string id = occurrenceId.Length > 0 ? occurrenceId
            : !string.IsNullOrEmpty(reported?.VisualId) ? reported.VisualId : reported?.CombatId ?? "";
        return state == null ? null : FindZoneOccurrence(state, id)?.Card;
    }

    public static IEnumerable<(Card Card, string Zone)> Located(GameSnapshot state) => state.Players.SelectMany(p => p.Zones.SelectMany(z => z.Cards.Select(c => (Card: c, Zone: z.Name))))
        .Concat(state.Stack.Where(s => s.Card != null).Select(s => (Card: s.Card!, Zone: "Stack")));
    public static IEnumerable<Card> Visible(GameSnapshot state) => Located(state).Select(p => p.Card)
        .Concat(state.Players.SelectMany(p => p.Zones).Where(z => z.TopCard != null).Select(z => z.TopCard!))
        .Concat(DecisionFaces(state.Decision?.Payload))
        .Concat(state.Decision?.Context.Source?.Card is { } source ? [source] : []);
    private static IEnumerable<Card> DecisionFaces(DecisionPayload? payload) => payload switch {
        OptionsPayload options => ChoiceFaces(options.Selection),
        OrderingPayload order => ChoiceFaces(order.Selection),
        AbilityChoicePayload ability => ChoiceFaces(ability.Selection),
        RevealPayload reveal => reveal.Cards.Select(face => face.Card),
        AllocationPayload allocation => allocation.Recipients.Where(recipient => recipient.Card != null).Select(recipient => recipient.Card!),
        _ => []
    };
    private static IEnumerable<Card> ChoiceFaces(ChoiceSelection selection) => selection.Choices.Where(choice => choice.Card != null)
        .Select(choice => choice.Card!).Concat(selection.Disclosures.Select(face => face.Card));
    public static string Identity(Card card) => !string.IsNullOrEmpty(card.VisualId) ? card.VisualId : !string.IsNullOrEmpty(card.CombatId) ? card.CombatId : card.Key;
    public static bool Same(Card a, Card b) => !string.IsNullOrEmpty(Identity(a)) && Identity(a) == Identity(b);
}
