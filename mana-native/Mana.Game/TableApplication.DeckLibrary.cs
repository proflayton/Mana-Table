using Mana.Contracts;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private IReadOnlyList<DeckLibraryDeletion> deletedDecks = [];
    private DeckLibraryDeletion? deckDeletionNotice;
    private string? deletingDeckId;
    private bool pickerDeleted;
    private int deckLibraryGeneration, deckLibraryReadGeneration, deckSelectionGeneration;
    private IDeckLibrary? DeckLibrary => services.Library is { SupportsDeckLibrary: true } service ? service : null;
    private string DeckLibraryScope => $"{pickerDeleted}:{deckLibraryGeneration}:{deletingDeckId}:{deckDeletionNotice?.RestoreToken}:{string.Join(';', deletedDecks.Select(deck => deck.RestoreToken))}";
    private bool CanManageDeckLibrary => DeckLibrary != null && match == null && !Connected && !busy && !ActivityOpen;

    private async Task ReadDeletedDecks(int? requestedGeneration = null)
    {
        if (DeckLibrary is not { } service) return;
        int generation = requestedGeneration ?? deckLibraryGeneration;
        if (generation != deckLibraryGeneration) return;
        int read = Interlocked.Increment(ref deckLibraryReadGeneration);
        var removed = await service.DeletedDecksAsync();
        updates.Enqueue(() => { if (generation == deckLibraryGeneration && read == deckLibraryReadGeneration) deletedDecks = removed; });
    }
    private void RefreshDeletedDecks()
    {
        if (DeckLibrary == null) return;
        int generation = deckLibraryGeneration;
        _ = Task.Run(async () => {
            try { await ReadDeletedDecks(generation); }
            catch (Exception ex) { updates.Enqueue(() => { if (generation == deckLibraryGeneration) error = "Could not read deleted decks. " + ex.Message; }); }
        });
    }
    private void DeleteSavedDeck(string id, string token, int expectedGeneration)
    {
        if (!CanManageDeckLibrary || expectedGeneration != deckLibraryGeneration || token.Length == 0 || !decks.Any(deck => deck.Id == id && deck.LibraryToken == token)
            && !(selectedDeckDetails is { SaveError.Length: 0 } current && current.Id == id && current.LibraryToken == token)) return;
        if (selectedDeckDetails is { } selected && selected.Id == id && selected.SaveError.Length > 0) return;
        var service = DeckLibrary!;
        int generation = Interlocked.Increment(ref deckLibraryGeneration);
        deletingDeckId = id;
        Work(async () => {
            try {
                var removed = await service.DeleteDeckAsync(id, token);
                updates.Enqueue(() => {
                    if (generation != deckLibraryGeneration) return;
                    Interlocked.Increment(ref deckLibraryReadGeneration);
                    decks = decks.Where(deck => deck.Id != removed.DeckId).ToArray();
                    deletedDecks = new[] { removed }.Concat(deletedDecks.Where(deck => deck.RestoreToken != removed.RestoreToken)).ToArray();
                    deckDeletionNotice = removed;
                    if (match == null && selectedDeck?.Id == id) {
                        ClearDeletedDeckSelection();
                        setupModal = "decks"; pickerSeat = -1;
                    }
                    if (match == null && setupModal == "decks") {
                        pickerPresets = pickerDeleted = false; pickerSearch = ""; deckPage = 0;
                    }
                    message = removed.Name + " deleted. Undo is available in Your decks.";
                });
            } finally { updates.Enqueue(() => { if (generation == deckLibraryGeneration) deletingDeckId = null; }); }
        }, "Deleting saved deck", "Keeping its cards and deck history available for Undo.", false);
    }
    private void RestoreSavedDeck(string id, string token)
    {
        if (!CanManageDeckLibrary || !deletedDecks.Any(deck => deck.DeckId == id && deck.RestoreToken == token)) return;
        var service = DeckLibrary!;
        int generation = Interlocked.Increment(ref deckLibraryGeneration);
        Work(async () => {
            var restored = await service.RestoreDeckAsync(id, token);
            updates.Enqueue(() => {
                if (generation != deckLibraryGeneration) return;
                Interlocked.Increment(ref deckLibraryReadGeneration);
                decks = decks.Where(deck => deck.Id != restored.Id).Append(restored).OrderBy(deck => deck.Name, StringComparer.OrdinalIgnoreCase).ToArray();
                deletedDecks = deletedDecks.Where(deck => deck.RestoreToken != token).ToArray();
                if (deckDeletionNotice?.RestoreToken == token) deckDeletionNotice = null;
                if (match == null && setupModal == "decks") {
                    pickerPresets = pickerDeleted = false; pickerSearch = "";
                    deckPage = Math.Max(0, Array.FindIndex(PickerChoices(), choice => choice.Id == restored.Id) / PickerPageSize);
                }
                message = restored.Name + " restored. Your selected deck is unchanged.";
            });
        }, "Restoring saved deck", "Returning its cards and saved deck history to Your decks.", false);
    }
    private void ClearDeletedDeckSelection()
    {
        Interlocked.Increment(ref deckSelectionGeneration);
        selectedDeck = null; selectedDeckDetails = null; InvalidateDeckConfirmation();
        soloSetup = null; CloseDeckEditor(); reviewCard = null; reviewList = false;
        deckCardId = deckBackId = null; librarySelection = null; catalogRead.Clear();
        deckWorkspace.Clear(); deckInteraction.Reset(); workshopSetup = null;
        workshopVersionView = null; workshopSourcePreview = null; analysisOpen = false;
        printingRead.Clear(); printingSelection = null;
    }
}
