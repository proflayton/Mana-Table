using Mana.Contracts;

namespace Mana.Table;

internal sealed partial class TableGame
{
    private DeckDetails? selectedDeckDetails;
    private bool deckEditor, commanderCandidates = true, deckReview;
    private int deckCardsPage, deckReviewPage;
    private string? deckCardId;
    private string deckSection = "", deckCardSection = "Main", deckFilter = "";
    private CatalogCard? librarySelection;
    private string? deckBackId;
    private const int DeckRows = 12;
    private bool DeckReady => selectedDeckDetails is { Problem.Length: 0, SaveError.Length: 0 } && selectedDeckDetails.CommanderIds.Length > 0;
    private string DeckScope => $"setup:{selectedDeckDetails?.Id}:{selectedDeckDetails?.Revision}:{deckEditor}:{commanderCandidates}:{deckSection}:{deckFilter}:{deckCardsPage}:{deckCardId}:{deckCardSection}:{librarySelection?.Id}:{catalogGeneration}:{deckReview}:{deckMenu}";
    private int? CommanderIdentity => selectedDeckDetails?.Entries.Where(e => e.Section == "Commander").ToArray() is { Length: > 0 } leaders
        ? leaders.Aggregate(0, (mask, e) => mask | e.ColorIdentity) : null;
    private CatalogCard? DeckSelection {
        get {
            if (librarySelection != null) return librarySelection;
            var entry = selectedDeckDetails?.Entries.FirstOrDefault(e => e.Id == deckCardId && e.Section == deckCardSection)
                ?? selectedDeckDetails?.Entries.FirstOrDefault(e => e.Id == deckCardId);
            return entry == null ? null : new(entry.Id, entry.Card, entry.ManaValue, entry.ColorIdentity, entry.Section, entry.Edition);
        }
    }
    private void ApplyDeck(DeckDetails details, bool open = false)
    {
        bool changed = selectedDeckDetails?.Id != details.Id;
        int? identity = CommanderIdentity;
        selectedDeck = new(details.Id, details.Name); selectedDeckDetails = details; InvalidateDeckConfirmation();
        if (changed) {
            commanderCandidates = details.CommanderIds.Length == 0 && details.Entries.Length > 0;
            deckSection = ""; deckFilter = ""; deckCardsPage = 0; deckReview = false;
            var first = details.Entries.FirstOrDefault(e => e.Section == "Commander") ?? details.Entries.FirstOrDefault();
            deckCardId = first?.Id; deckCardSection = first?.Section ?? "Main"; librarySelection = null;
            ResetCatalog();
        } else if (catalogCommanderColors && identity != CommanderIdentity) { if (CommanderIdentity == null) catalogCommanderColors = false; ChangeCatalog(); }
        string? selectedId = librarySelection?.Id ?? deckCardId;
        if (selectedId != null && !details.Entries.Any(e => e.Id == selectedId && e.Section == deckCardSection)
            && details.Entries.FirstOrDefault(e => e.Id == selectedId) is { } moved) deckCardSection = moved.Section;
        if (open) deckEditor = true;
    }
    private async Task ReadSelectedDeck(DeckSummary deck, bool edit = false)
    {
        var details = await engine.OpenDeckAsync(deck.Id);
        updates.Enqueue(() => {
            ApplyDeck(details); deckEditor = edit; setupModal = "";
            message = details.CommanderIds.Length == 0 ? "Choose a commander for your deck." : "Deck selected.";
        });
    }
    private void ImportDeck(Func<Task<DeckSummary>> import, bool edit)
    {
        Work(async () => {
            await UnreadyForDeckEdit();
            var deck = await import(); var saved = await engine.DecksAsync();
            updates.Enqueue(() => decks = saved);
            await ReadSelectedDeck(deck, edit);
            if (!edit) updates.Enqueue(ReviewDeck);
        });
    }
    private async Task UnreadyForDeckEdit()
    {
        updates.Enqueue(InvalidateDeckConfirmation);
        if (lobby is not { Mode: not "idle" }) return;
        await engine.SetReadyAsync(false);
        var state = await engine.LobbyAsync(); updates.Enqueue(() => lobby = state);
    }
    private void NewDeck(string name = "New Commander deck") => Work(async () => {
        await UnreadyForDeckEdit();
        var created = await engine.CreateDeckAsync(name); var saved = await engine.DecksAsync();
        updates.Enqueue(() => { decks = saved; setupModal = ""; ApplyDeck(created, true); message = "Search for your commander or start adding cards."; });
    });
    // Every mutation shares persistence, readiness invalidation and refresh.
    private void EditDeck(Func<DeckDetails, Task<DeckDetails>> edit, string success)
    {
        var current = selectedDeckDetails!;
        Work(async () => {
            await UnreadyForDeckEdit();
            var updated = await edit(current); var saved = await engine.DecksAsync();
            updates.Enqueue(() => {
                decks = saved; ApplyDeck(updated);
                message = updated.SaveError.Length > 0 ? "Could not save the deck. Retry below." : success;
            });
        });
    }
    private void SaveCommanders(string[] ids) => EditDeck(d => engine.SetCommandersAsync(d.Id, d.Revision, ids), "Commander saved.");
    private void DeckQuantity(int count)
    {
        if (DeckSelection is not { } card || deckCardSection == "Commander") return;
        if (count < 0 || count > 1000) { error = "Quantity must be between 0 and 1000."; return; }
        string section = deckCardSection;
        EditDeck(d => engine.EditDeckAsync(d.Id, d.Revision, [new(section, card.Id, count)]), "Deck saved.");
    }
    private int DeckQuantity() => DeckSelection is { } card ? selectedDeckDetails!.Entries.FirstOrDefault(e => e.Id == card.Id && e.Section == deckCardSection)?.Quantity ?? 0 : 0;
    private void MoveDeckCard()
    {
        if (DeckSelection is not { } card || deckCardSection is not ("Main" or "Sideboard")) return;
        string from = deckCardSection, to = from == "Main" ? "Sideboard" : "Main";
        var d = selectedDeckDetails!;
        int count = DeckQuantity(), existing = d.Entries.FirstOrDefault(e => e.Id == card.Id && e.Section == to)?.Quantity ?? 0;
        if (count == 0) { deckCardSection = to; return; }
        EditDeck(async current => {
            var updated = await engine.EditDeckAsync(current.Id, current.Revision, [new(from, card.Id, 0), new(to, card.Id, count + existing)]);
            updates.Enqueue(() => deckCardSection = to); return updated;
        }, "Cards moved. Deck saved.");
    }
    private void RenameDeck(string name) => EditDeck(d => engine.RenameDeckAsync(d.Id, d.Revision, name), "Deck renamed.");
    private void DuplicateDeck(string name) => EditDeck(d => engine.DuplicateDeckAsync(d.Id, d.Revision, name), "Copy saved as a separate deck.");
    private void ExportDeck()
    {
        var d = selectedDeckDetails!;
        Work(async () => {
            string text = await engine.ExportDeckAsync(d.Id, d.Revision);
            updates.Enqueue(() => { Clipboard.SetText(text); message = "Deck list copied, including commander and sideboard sections."; });
        });
    }
    private void CloseDeckEditor() { deckEditor = false; deckTextFocus = deckMenu = ""; textFocus = false; }
    private void OpenDeckEditor() => OpenDeckEditorAt(null);
    private void OpenDeckEditorAt(DeckEntry? entry) {
        if (selectedDeck is { } deck) Work(async () => {
            await UnreadyForDeckEdit(); await ReadSelectedDeck(deck, true);
            if (entry != null) updates.Enqueue(() => {
                SelectDeckRow(entry); deckFilter = ""; FilterDeck(entry.Section);
                deckCardsPage = Math.Max(0, Array.FindIndex(DeckRowsToShow(), e => e.Id == entry.Id) / DeckRows);
            });
        });
    }
    private void SelectDeckRow(DeckEntry entry) { deckCardId = entry.Id; deckCardSection = entry.Section; librarySelection = null; deckBackId = null; }
    private void FilterDeck(string section, bool leaders = false) { deckSection = section; commanderCandidates = leaders; deckCardsPage = 0; }
    private DeckEntry[] DeckRowsToShow() => selectedDeckDetails?.Entries
        .Where(e => (deckSection.Length == 0 || e.Section == deckSection)
            && (!commanderCandidates || selectedDeckDetails.CommanderOptions.Any(c => c.Id == e.Id))
            && (deckFilter.Length == 0 || (e.Card.Name + " " + e.Card.Type + " " + e.Card.Text).Contains(deckFilter, StringComparison.OrdinalIgnoreCase)))
        .OrderBy(e => e.Card.Name).ThenBy(e => e.Section).ToArray() ?? [];
}
