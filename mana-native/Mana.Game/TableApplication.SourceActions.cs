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
        if (!SourceActionsSupported || busy || session.NeedsRecovery || match is not { Decision: { } decision }
            || decision.Intent is not (DecisionIntent.Priority or DecisionIntent.Mana)) return null;
        var current = matchPresentation.Cards.Pinned is { ChoiceSlot: < 0, Zone: not "Stack" } entry && CardContext.CanRead(entry)
            ? CardContext.CurrentOccurrence(entry) : dragging && dragSource != null ? CurrentDragCard(dragSource) : null;
        return current is { FaceDown: false, Key.Length: > 0 } ? current : null;
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
            || sourceActions.TryCreateCommand(context, action.Id) is not { } reply) return;
        if (zoneName != "Battlefield") zoneName = null;
        Send(reply);
        CollapseCardReading(); ClearReaderActions();
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
