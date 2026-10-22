namespace Mana.Contracts;

// Optional library management. A saved-file token targets the listed deck even
// when another document is open; it is distinct from an editor revision.
public interface IDeckLibrary
{
    bool SupportsDeckLibrary => true;
    Task<IReadOnlyList<DeckLibraryDeletion>> DeletedDecksAsync();
    Task<DeckLibraryDeletion> DeleteDeckAsync(string deckId, string libraryToken);
    Task<DeckSummary> RestoreDeckAsync(string deckId, string restoreToken);
}

// Opaque restore handles remain usable after restart. Listing is newest first;
// restoring a saved deck does not open it or replace another current selection.
public sealed record DeckLibraryDeletion(string DeckId, string Name, string RestoreToken)
{
    public long DeletedAt { get; init; }
}
