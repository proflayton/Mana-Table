using Mana.Contracts;
using Mana.Magic;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private SourceActionSet? readerActions;
    private bool readerActionsLoading, readerActionsInFlight;
    private string readerActionsError = "", readerActionsBinding = "";
    private long readerActionsGeneration;
    private bool SourceActionsSupported => engine is ISourceActions { SupportsSourceActions: true };

    private Card? ReaderActionSource()
    {
        if (!SourceActionsSupported || busy || session.NeedsRecovery || match is not { Decision: { } decision } state
            || decision.Intent is not (DecisionIntent.Priority or DecisionIntent.Mana)
            || cardPresentation.Pinned is not { Card.FaceDown: false, ChoiceSlot: < 0 } entry || !CardContext.CanRead(entry)
            || entry.Zone is not ("Hand" or "Battlefield" or "Command" or "Graveyard" or "Exile")) return null;
        return state.Players.SelectMany(player => player.Zones).Where(zone => zone.Name == entry.Zone)
            .SelectMany(zone => zone.Cards).FirstOrDefault(card => !card.FaceDown && card.Key.Length > 0 && SameVisibleCard(card, entry.Card));
    }

    private string ReaderActionBinding(Card card) => match is not { Decision: { } decision } state ? ""
        : string.Join("\n", state.Id, decision.Id, state.Revision, card.Key, CardViews.Identity(card));

    private bool ReaderActionsMatch(Card card) => readerActions is { } actions && match is { Decision: { } decision } state
        && readerActionsBinding == ReaderActionBinding(card)
        && actions.GameId == state.Id && actions.DecisionId == decision.Id && actions.SourceCardKey == card.Key
        && (string.IsNullOrEmpty(actions.SourceCardId) || actions.SourceCardId == CardViews.Identity(card));

    private void ClearReaderActions()
    {
        if (readerActionsBinding.Length > 0) readerActionsGeneration++;
        readerActionsBinding = ""; readerActions = null; readerActionsLoading = false; readerActionsError = "";
    }

    private void UpdateReaderActions()
    {
        var card = ReaderActionSource();
        if (card == null) { ClearReaderActions(); return; }
        string binding = ReaderActionBinding(card);
        if (binding != readerActionsBinding) {
            readerActionsGeneration++; readerActionsBinding = binding;
            readerActions = null; readerActionsError = ""; readerActionsLoading = true;
        }
        // Coalesce rapid card changes. An obsolete read can finish, but never
        // publish actions for another source or queue a chain of old selections.
        if (!readerActionsLoading || readerActionsInFlight) return;
        readerActionsInFlight = true;
        long generation = readerActionsGeneration;
        var state = match!; string decision = state.Decision!.Id;
        var source = (ISourceActions)engine;
        _ = Task.Run(async () => {
            try {
                var actions = await source.SourceActionsAsync(state.Id, decision, card.Key).WaitAsync(TimeSpan.FromSeconds(5));
                session.Post(() => {
                    if (generation != readerActionsGeneration || binding != readerActionsBinding) return;
                    if (actions.GameId != state.Id || actions.DecisionId != decision || actions.SourceCardKey != card.Key
                        || !string.IsNullOrEmpty(actions.SourceCardId) && actions.SourceCardId != CardViews.Identity(card)) {
                        readerActionsError = "These actions changed. Try again.";
                    } else readerActions = actions;
                    readerActionsLoading = false;
                });
            } catch (Exception) {
                session.Post(() => {
                    if (generation != readerActionsGeneration || binding != readerActionsBinding) return;
                    readerActionsError = "Couldn't load this card's actions."; readerActionsLoading = false;
                });
            } finally { session.Post(() => readerActionsInFlight = false); }
        });
    }

    private void RetryReaderActions()
    {
        ClearReaderActions(); UpdateReaderActions();
    }

    private void UseReaderAction(SourceAction action)
    {
        var card = ReaderActionSource();
        if (card == null || !ReaderActionsMatch(card) || readerActions!.Actions.All(current => current.Id != action.Id)) return;
        var state = match!;
        var reply = new DecisionReply(state.Id, state.Decision!.Id, ReplyAction.UseSourceAction, Card: card.Key, AbilityId: action.Id);
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
