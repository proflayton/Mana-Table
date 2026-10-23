using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.Forge;

public sealed partial class ForgeEngine : IDeckLibrary
{
    public bool SupportsDeckLibrary => current is { DeckLibraryAvailable: true, Failure: null } owner
        && owner.Ready.Task.IsCompletedSuccessfully && !disposed;

    private void RequireDeckLibrary()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!SupportsDeckLibrary) throw new NotSupportedException("This rules adapter does not expose saved deck deletion and restore.");
    }

    public async Task<ImmutableArray<DeckLibraryDeletion>> DeletedDecksAsync()
    {
        RequireDeckLibrary();
        return Read<ImmutableArray<DeckLibraryDeletion>>(await Request("deletedDecks"));
    }

    public async Task<DeckLibraryDeletion> DeleteDeckAsync(string deckId, string libraryToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deckId);
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryToken);
        RequireDeckLibrary();
        var deletion = Read<DeckLibraryDeletion>(await Request("deleteDeck", new { deckId, libraryToken }));
        if (!string.Equals(deletion.DeckId, deckId, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(deletion.RestoreToken))
            throw new IOException("The rules adapter returned an invalid deck deletion receipt.");
        if (activeDeckId == deckId) activeDeckId = null;
        return deletion;
    }

    public async Task<DeckSummary> RestoreDeckAsync(string deckId, string restoreToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deckId);
        ArgumentException.ThrowIfNullOrWhiteSpace(restoreToken);
        RequireDeckLibrary();
        // Deck()/MapDeck() also select the editor. A library restore must retain
        // an unrelated open document, so deserialize this summary directly.
        var restored = Read<DeckSummary>(await Request("restoreDeck", new { deckId, restoreToken }));
        if (!string.Equals(restored.Id, deckId, StringComparison.Ordinal))
            throw new IOException("The rules adapter returned a different restored deck.");
        return restored;
    }
}
