using Mana.Client;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private readonly LobbyDeckSession lobbyDeck = new();

    private LobbyDeckDocument? DesiredLobbyDeck()
    {
        if (!Connected || match != null || lobby is not { TableId.Length: > 0 } current || LocalSeat == null) return null;
        // A saved draft is still a public selection, separate from legality and
        // Ready consent. Unacknowledged storage must withdraw the old assignment.
        return selectedDeckDetails is { SaveError.Length: 0 } deck
                && deckPersistence.View.Status != DeckPersistenceStatus.Failed
            ? LobbyDeckDocument.Saved(current.TableId, deck) : LobbyDeckDocument.Clear(current.TableId);
    }

    private void UpdateLobbyDeck()
    {
        lobbyDeck.Observe(DesiredLobbyDeck());
        if (lobby is { } current) lobbyDeck.Reconcile(current);
        if (closing || busy || polling || !loaded || lobbyDeck.View.Status != LobbyDeckStatus.Pending) return;
        if (lobbyDeck.TryBegin() is not { } attempt) return;
        // Sharing follows the acknowledged save silently; it must not clear a
        // storage error or start another foreground activity transition.
        session.Run(() => PublishLobbyDeck(attempt));
    }

    private async Task PublishLobbyDeck(LobbyDeckAttempt attempt)
    {
        try {
            var receipt = await tables.SelectDeckAsync(attempt.Document.Selection);
            if (!LobbyDeckSession.Acknowledges(attempt.Document, receipt))
                throw new InvalidOperationException("The table changed while sharing your deck. Choose Ready to try again.");
            updates.Enqueue(() => {
                if (!lobbyDeck.IsCurrent(attempt)) return;
                if (!session.AcceptLobby(receipt)) {
                    lobbyDeck.Fail(attempt, "The table changed while sharing your deck. Choose Ready to try again.");
                    return;
                }
                lobbyDeck.Succeed(attempt, receipt);
                if (lobbyDeck.View.Status == LobbyDeckStatus.Failed) error = lobbyDeck.View.Error;
            });
        } catch (Exception failure) {
            updates.Enqueue(() => lobbyDeck.Fail(attempt, failure.Message));
            throw;
        }
    }
}
