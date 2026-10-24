using Mana.Contracts;

namespace Mana.Client;

public enum DeckPersistenceStatus { Empty, Saved, Saving, Failed }
public sealed record DeckPersistenceStamp(DeckWorkspaceContext Context, long Generation);
public sealed record DeckPersistenceView(DeckWorkspaceContext? Context, long Generation, DeckPersistenceStatus Status, string Error);

/// <summary>Owns acknowledgment of the selected saved document. A library-list
/// observation is independent of accepting a completed document mutation.</summary>
public sealed class DeckPersistenceSession
{
    private bool closed;
    public DeckPersistenceView View { get; private set; } = new(null, 0, DeckPersistenceStatus.Empty, "");

    public void Observe(DeckDetails? deck)
    {
        if (closed) return;
        DeckWorkspaceContext? context = deck == null ? null : new(deck.Id, deck.Revision);
        string error = deck?.SaveError ?? "";
        var status = deck == null ? DeckPersistenceStatus.Empty : error.Length > 0 ? DeckPersistenceStatus.Failed : DeckPersistenceStatus.Saved;
        if (View.Context == context && View.Status == status && View.Error == error) return;
        View = new(context, View.Generation + 1, status, error);
    }

    public DeckPersistenceStamp? Begin(DeckWorkspaceContext context)
    {
        if (closed || context != View.Context || View.Status == DeckPersistenceStatus.Saving) return null;
        View = View with { Generation = View.Generation + 1, Status = DeckPersistenceStatus.Saving, Error = "" };
        return new(context, View.Generation);
    }

    public bool IsCurrent(DeckPersistenceStamp stamp) => !closed && View.Status == DeckPersistenceStatus.Saving
        && View.Context == stamp.Context && View.Generation == stamp.Generation;

    public bool Accept(DeckPersistenceStamp stamp, DeckDetails result, bool createsDocument = false)
    {
        if (!IsCurrent(stamp)) return false;
        if (string.IsNullOrWhiteSpace(result.Id) || result.Revision < 0
            || !createsDocument && (result.Id != stamp.Context.DeckId || result.Revision < stamp.Context.Revision)) {
            Fail(stamp, "The save returned a different document or an outdated revision. Reopen your deck before editing again.");
            return false;
        }
        View = new(new(result.Id, result.Revision), View.Generation + 1,
            result.SaveError.Length > 0 ? DeckPersistenceStatus.Failed : DeckPersistenceStatus.Saved, result.SaveError);
        return true;
    }

    public bool Fail(DeckPersistenceStamp stamp, string error)
    {
        if (!IsCurrent(stamp)) return false;
        View = View with { Generation = View.Generation + 1, Status = DeckPersistenceStatus.Failed,
            Error = string.IsNullOrWhiteSpace(error) ? "Your changes could not be saved." : error };
        return true;
    }

    public void Close() { closed = true; View = new(null, View.Generation + 1, DeckPersistenceStatus.Empty, ""); }
}
