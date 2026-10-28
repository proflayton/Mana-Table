using Mana.Contracts;

namespace Mana.Client;

// Saved-file identity is independent of the editor's revision, which may restart
// when that file is reopened. Empty tokens remain usable by older providers.
public sealed record LobbyDeckDocument(string TableId, string DeckId, string LibraryToken, long Revision)
{
    public static LobbyDeckDocument Saved(string tableId, DeckDetails deck) =>
        new(tableId, deck.Id, deck.LibraryToken, deck.LibraryToken.Length > 0 ? 0 : deck.Revision);
    public static LobbyDeckDocument Clear(string tableId) => new(tableId, "", "", 0);
    public LobbyDeckSelection Selection => new(TableId, DeckId, LibraryToken);
}

public enum LobbyDeckStatus { Empty, Pending, Publishing, Synchronized, Failed }
public sealed record LobbyDeckAttempt(LobbyDeckDocument Document, long Generation);
public sealed record LobbyDeckView(LobbyDeckDocument? Document, long Generation, LobbyDeckStatus Status,
    string SelectionId, string Error);

/// <summary>Tracks acknowledgment of the chosen saved list, not lobby transport
/// or public roster state. Only a captured current attempt can become Ready.</summary>
public sealed class LobbyDeckSession
{
    public LobbyDeckView View { get; private set; } = new(null, 0, LobbyDeckStatus.Empty, "", "");

    public void Observe(LobbyDeckDocument? document)
    {
        if (document == View.Document) return;
        View = new(document, View.Generation + 1, document == null ? LobbyDeckStatus.Empty : LobbyDeckStatus.Pending, "", "");
    }

    public LobbyDeckAttempt? TryBegin()
    {
        if (View.Status != LobbyDeckStatus.Pending || View.Document is not { } document) return null;
        View = View with { Status = LobbyDeckStatus.Publishing };
        return new(document, View.Generation);
    }

    public bool IsCurrent(LobbyDeckAttempt attempt) => View.Status == LobbyDeckStatus.Publishing
        && View.Generation == attempt.Generation && View.Document == attempt.Document;

    public bool Succeed(LobbyDeckAttempt attempt, Lobby receipt)
    {
        if (!IsCurrent(attempt)) return false;
        if (!Acknowledges(attempt.Document, receipt)) {
            Fail(attempt, "The table did not acknowledge your current deck. Try selecting it again.");
            return false;
        }
        View = View with { Status = LobbyDeckStatus.Synchronized, SelectionId = receipt.Seats.Single(seat => seat.Local).DeckPreview?.SelectionId ?? "", Error = "" };
        return true;
    }

    public static bool Acknowledges(LobbyDeckDocument document, Lobby receipt)
    {
        if (receipt.TableId != document.TableId || receipt.Mode == "idle") return false;
        var local = receipt.Seats.Where(seat => seat.Local).ToArray();
        return local.Length == 1 && (document.DeckId.Length == 0
            ? local[0].DeckPreview == null && local[0].Deck == null
            : !string.IsNullOrWhiteSpace(local[0].DeckPreview?.SelectionId));
    }

    public bool Fail(LobbyDeckAttempt attempt, string error)
    {
        if (!IsCurrent(attempt)) return false;
        View = View with { Status = LobbyDeckStatus.Failed,
            Error = string.IsNullOrWhiteSpace(error) ? "Your deck could not be shared with the table." : error };
        return true;
    }

    public bool Retry()
    {
        if (View.Status != LobbyDeckStatus.Failed) return false;
        View = View with { Generation = View.Generation + 1, Status = LobbyDeckStatus.Pending, Error = "" };
        return true;
    }

    public bool Matches(Lobby lobby)
    {
        if (View.Status != LobbyDeckStatus.Synchronized || View.Document?.TableId != lobby.TableId || lobby.Mode == "idle") return false;
        var local = lobby.Seats.Where(seat => seat.Local).ToArray();
        return local.Length == 1 && (View.Document.DeckId.Length == 0 ? local[0].DeckPreview == null && local[0].Deck == null
            : local[0].DeckPreview?.SelectionId == View.SelectionId);
    }

    // Call only with the session's ordered current observation. A revoked public
    // assignment must be republished before the private saved selection is Ready.
    public void Reconcile(Lobby lobby)
    {
        if (View.Status == LobbyDeckStatus.Synchronized && View.Document?.TableId == lobby.TableId && !Matches(lobby))
            View = View with { Generation = View.Generation + 1, Status = LobbyDeckStatus.Pending, SelectionId = "", Error = "" };
    }
}
