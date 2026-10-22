using Mana.Contracts;
using Mana.Magic;
using Mana.Client;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private readonly SourceActionSession sourceActions;
    private bool SourceActionsSupported => sourceActions.Supported;

    private Card? ReaderActionSource()
    {
        if (!SourceActionsSupported || busy || session.NeedsRecovery || match is not { Decision: { } decision } state
            || decision.Intent is not (DecisionIntent.Priority or DecisionIntent.Mana)
            || cardPresentation.Pinned is not { Card.FaceDown: false, ChoiceSlot: < 0 } entry || !CardContext.CanRead(entry)
            || entry.Zone is not ("Hand" or "Battlefield" or "Command" or "Graveyard" or "Exile")) return null;
        return state.Players.SelectMany(player => player.Zones).Where(zone => zone.Name == entry.Zone)
            .SelectMany(zone => zone.Cards).FirstOrDefault(card => !card.FaceDown && card.Key.Length > 0 && SameVisibleCard(card, entry.Card));
    }

    private SourceActionContext? ReaderActionContext(Card card) => match is not { Decision: { } decision } state ? null
        : new(state.Id, decision.Id, state.Revision, state.ViewerId, card.Key, CardViews.Identity(card));

    private bool ReaderActionsMatch(Card card) => sourceActions.View.Status == SourceActionStatus.Ready
        && sourceActions.View.Context is { } current && current == ReaderActionContext(card);

    private void ClearReaderActions() => sourceActions.Clear();

    private void UpdateReaderActions()
    {
        var card = ReaderActionSource();
        sourceActions.Observe(card == null ? null : ReaderActionContext(card));
    }

    private void RetryReaderActions()
    {
        UpdateReaderActions(); sourceActions.Retry();
    }

    private void UseReaderAction(SourceAction action)
    {
        var card = ReaderActionSource();
        if (card == null || ReaderActionContext(card) is not { } context
            || sourceActions.TryCreateReply(context, action.Id) is not { } reply) return;
        CollapseCardReading(); ClearReaderActions();
        if (zoneName != "Battlefield") zoneName = null;
        Send(reply);
    }

    private void UseReaderPlacement(PriorityAbility placement)
    {
        var card = ReaderActionSource();
        if (card == null || !ReaderActionsMatch(card) || match is not { } state
            || !HandPlacements.Options(state, card).Any(current => current.Id == placement.Id)
            || HandPlacements.Start(state, card, placement.Id) is not { } reply) return;
        CollapseCardReading(); ClearReaderActions();
        if (zoneName != "Battlefield") zoneName = null;
        Send(reply);
    }
}
