using Mana.Contracts;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private DeckDetails? selectedDeckDetails;
    private bool deckEditor, commanderCandidates = true, deckIssuesOnly;
    private int deckCardsPage, deckIssuePage, deckCardIssuePage;
    private string? deckCardId;
    private string deckSection = "", deckCardSection = "Main", deckFilter = "";
    private CatalogCard? librarySelection;
    private string? deckBackId;
    private int DeckRowHeight => Math.Max(32, DeckRowsToShow().Select(entry => canvas.TextSize(entry.Card.Name, .66f, 378, 80, true).Y + 12).DefaultIfEmpty(32).Max());
    private int DeckRows => Math.Max(1, 288 / DeckRowHeight);
    private bool CommanderDeck => selectedDeckDetails?.Format is null or "Commander";
    private bool DeckReady => selectedDeckDetails is { Problem.Length: 0, SaveError.Length: 0 } && (!CommanderDeck || selectedDeckDetails.CommanderIds.Length > 0);
    private bool DeckCanCopy => selectedDeckDetails?.Entries.Any(e => e.Quantity > 0) == true;
    private string DeckScope => $"setup:{selectedDeckDetails?.Id}:{selectedDeckDetails?.Revision}:{deckEditor}:{commanderCandidates}:{deckSection}:{deckFilter}:{deckCardsPage}:{deckCardId}:{deckCardSection}:{librarySelection?.Id}:{catalogRead.View.Generation}:{deckIssuesOnly}:{deckIssuePage}:{deckCardIssuePage}:{deckMenu}:{WorkshopScope}:{AnalysisScope}";
    private DeckIssue[] DeckIssues => selectedDeckDetails is not { } deck ? [] : deck.Issues.Length > 0 ? deck.Issues
        : deck.Problem.Length > 0 ? [new("LEGACY", deck.Problem, null, [])] : [];
    private DeckIssue[] CardIssues(string cardId, string section) => DeckIssues
        .Where(issue => (issue.Section == null || issue.Section == section) && issue.CardIds.Contains(cardId)).ToArray();
    private sealed record DeckIssueText(DeckIssue Issue, string Text);
    private DeckIssueText[] IssueTextPages(IEnumerable<DeckIssue> issues, int characters) => issues
        .SelectMany(issue => TextPages(issue.Message, Math.Max(28, (int)(characters / (preferences.TextScale * preferences.TextScale))))
            .Select(text => new DeckIssueText(issue, text))).ToArray();
    private int? CommanderIdentity => selectedDeckDetails?.Entries.Where(e => e.Section == "Commander").ToArray() is { Length: > 0 } leaders
        ? leaders.Aggregate(0, (mask, e) => mask | e.ColorIdentity) : null;
    private CatalogCard? DeckSelection {
        get {
            if (librarySelection != null) return librarySelection;
            if (deckCardSection == "Considering" && deckWorkspace.View.Workshop?.Considering.FirstOrDefault(c => c.CardId == deckCardId) is { } option) return option.Card;
            var entry = selectedDeckDetails?.Entries.FirstOrDefault(e => e.Id == deckCardId && e.Section == deckCardSection)
                ?? selectedDeckDetails?.Entries.FirstOrDefault(e => e.Id == deckCardId);
            return entry == null ? null : new(entry.Id, entry.Card, entry.ManaValue, entry.ColorIdentity, entry.Section, entry.Edition);
        }
    }
    private void ApplyDeck(DeckDetails details, bool open = false)
    {
        Interlocked.Increment(ref deckSelectionGeneration);
        bool changed = selectedDeckDetails?.Id != details.Id;
        string? previousFormat = selectedDeckDetails?.Format;
        int? identity = CommanderIdentity;
        selectedDeck = new(details.Id, details.Name) { LibraryToken = details.LibraryToken }; selectedDeckDetails = details; InvalidateDeckConfirmation();
        deckWorkspace.Observe(new(details.Id, details.Revision));
        if (previousFormat != details.Format) { workshopSetup = null; workshopPlayers = details.Format == "Commander" ? Math.Min(4, workshopPlayers) : 2; }
        RequestWorkshopRefresh(changed);
        deckIssuePage = deckCardIssuePage = 0;
        if (changed) {
            deckInteraction.Reset();
            analysisOpen = false;
            commanderCandidates = details.CommanderIds.Length == 0 && details.Entries.Length > 0;
            deckSection = ""; deckFilter = ""; deckCardsPage = 0; deckIssuesOnly = false;
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
        int generation = Interlocked.Increment(ref deckSelectionGeneration);
        var details = await catalog.OpenDeckAsync(deck.Id);
        updates.Enqueue(() => {
            if (generation != deckSelectionGeneration || match != null || deletingDeckId == deck.Id
                || deletedDecks.Any(removed => removed.DeckId == deck.Id)
                    && !decks.Any(current => current.Id == details.Id && current.LibraryToken == details.LibraryToken)) return;
            ApplyDeck(details); deckEditor = edit; setupModal = "";
            message = details.CommanderIds.Length == 0 ? "Choose a commander for your deck." : "Deck selected.";
        });
    }
    private void ImportDeck(Func<Task<DeckSummary>> import, bool edit, string title = "Importing your deck", string detail = "Reading the list and finding its cards.")
    {
        Work(async () => {
            await UnreadyForDeckEdit();
            var deck = await import(); var saved = await catalog.DecksAsync();
            updates.Enqueue(() => decks = saved);
            await ReadSelectedDeck(deck, edit);
        }, title, detail);
    }
    private async Task UnreadyForDeckEdit()
    {
        updates.Enqueue(InvalidateDeckConfirmation);
        if (lobby is not { Mode: not "idle" }) return;
        await tables.SetReadyAsync(false);
        var state = await tables.LobbyAsync(); updates.Enqueue(() => lobby = state);
    }
    private void NewDeck(string name = "New Commander deck") => Work(async () => {
        await UnreadyForDeckEdit();
        var created = await catalog.CreateDeckAsync(name); var saved = await catalog.DecksAsync();
        updates.Enqueue(() => { decks = saved; setupModal = ""; ApplyDeck(created, true); message = "Search for your commander or start adding cards."; });
    }, "Creating your deck", "Making a place for your next Commander list.");
    // Every mutation shares persistence, readiness invalidation and refresh.
    private void EditDeck(Func<DeckDetails, Task<DeckDetails>> edit, string success)
    {
        var current = selectedDeckDetails!;
        Work(async () => {
            await UnreadyForDeckEdit();
            var updated = await edit(current); var saved = await catalog.DecksAsync();
            updates.Enqueue(() => {
                decks = saved; ApplyDeck(updated);
                message = updated.SaveError.Length > 0 ? "Could not save the deck. Retry below." : success;
                if (updated.SaveError.Length > 0) { error = updated.SaveError; activityFailed = true; }
            });
        }, "Saving your deck", "Your changes save automatically.", false);
    }
    private void SaveCommanders(string[] ids) => EditDeck(d => catalog.SetCommandersAsync(d.Id, d.Revision, ids), "Commander saved.");
    private void DeckQuantity(int count)
    {
        if (DeckSelection is not { } card || deckCardSection == "Commander") return;
        if (count < 0 || count > 1000) { error = "Quantity must be between 0 and 1000."; return; }
        if (deckCardSection == "Considering") { ConsideringQuantity(card, count); return; }
        string section = deckCardSection;
        EditDeck(d => catalog.EditDeckAsync(d.Id, d.Revision, [new(section, card.Id, count)]), "Deck saved.");
    }
    private int DeckQuantity() => DeckSelection is not { } card ? 0 : deckCardSection == "Considering" ? deckWorkspace.View.Workshop?.Considering.FirstOrDefault(c => c.CardId == card.Id)?.Quantity ?? 0
        : selectedDeckDetails!.Entries.FirstOrDefault(e => e.Id == card.Id && e.Section == deckCardSection)?.Quantity ?? 0;
    private void MoveDeckCard()
    {
        if (DeckSelection is not { } card || deckCardSection is not ("Main" or "Sideboard")) return;
        string from = deckCardSection, to = from == "Main" ? "Sideboard" : "Main";
        var d = selectedDeckDetails!;
        int count = DeckQuantity(), existing = d.Entries.FirstOrDefault(e => e.Id == card.Id && e.Section == to)?.Quantity ?? 0;
        if (count == 0) { deckCardSection = to; return; }
        EditDeck(async current => {
            var updated = await catalog.EditDeckAsync(current.Id, current.Revision, [new(from, card.Id, 0), new(to, card.Id, count + existing)]);
            updates.Enqueue(() => deckCardSection = to); return updated;
        }, "Cards moved. Deck saved.");
    }
    private void RenameDeck(string name) => EditDeck(d => catalog.RenameDeckAsync(d.Id, d.Revision, name), "Deck renamed.");
    private void DuplicateDeck(string name) => EditDeck(d => catalog.DuplicateDeckAsync(d.Id, d.Revision, name), "Copy saved as a separate deck.");
    private void ExportDeck()
    {
        if (!DeckCanCopy) { message = "Add a card before copying your deck list."; return; }
        var d = selectedDeckDetails!;
        Work(async () => {
            string text = await catalog.ExportDeckAsync(d.Id, d.Revision);
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("There are no cards to copy. Add a card and try again.");
            updates.Enqueue(() => {
                try {
                    Clipboard.SetText(text);
                    message = "Deck list copied, including commander and sideboard sections.";
                } catch (System.Runtime.InteropServices.ExternalException) {
                    error = "The clipboard is unavailable. Try Copy list again.";
                    FailActivity();
                }
            });
        }, "Copying your deck list", "Preparing the commander, main deck, and sideboard for the clipboard.");
    }
    private void CloseDeckEditor() {
        analysisOpen = false; deckEditor = false; deckMenu = ""; DismissDeckWorkspaceReading();
        ClosePrintings();
    }
    private void OpenDeckEditor() => OpenDeckEditorAt(null);
    private void OpenDeckEditorAt(DeckEntry? entry) {
        if (selectedDeck is { } deck) Work(async () => {
            await UnreadyForDeckEdit(); await ReadSelectedDeck(deck, true);
            if (entry != null) updates.Enqueue(() => {
                SelectDeckRow(entry); deckFilter = ""; FilterDeck(entry.Section);
                deckCardsPage = Math.Max(0, Array.FindIndex(DeckRowsToShow(), e => e.Id == entry.Id) / DeckRows);
            });
        }, "Opening the deck builder", "Getting your saved list ready to edit.");
    }
    private void SelectDeckRow(DeckEntry entry) { deckCardId = entry.Id; deckCardSection = entry.Section; librarySelection = null; deckBackId = null; deckCardIssuePage = 0; }
    private void FilterDeck(string section, bool leaders = false) { deckSection = section; commanderCandidates = leaders; deckCardsPage = 0; deckIssuesOnly = false; workshopRoleFilter = ""; workshopManaFilter = null; deckInteraction.ClearRelated(); }
    private void ToggleDeckIssues()
    {
        deckIssuesOnly = !deckIssuesOnly; deckCardsPage = 0;
        workshopRoleFilter = ""; workshopManaFilter = null; deckInteraction.ClearRelated();
        if (deckIssuesOnly) { deckSection = deckFilter = ""; commanderCandidates = false; }
    }
    private void ShowDeckIssue(DeckIssue issue)
    {
        deckInteraction.ClearRelated();
        var entry = selectedDeckDetails?.Entries.FirstOrDefault(e => (issue.Section == null || issue.Section == e.Section) && issue.CardIds.Contains(e.Id));
        if (entry != null) {
            deckIssuesOnly = true; deckSection = deckFilter = ""; commanderCandidates = false;
            SelectDeckRow(entry);
            deckCardsPage = Math.Max(0, Array.FindIndex(DeckRowsToShow(), e => e.Id == entry.Id && e.Section == entry.Section) / DeckRows);
        } else if (issue.Code is "COMMANDER_COUNT" or "MISSING_COMMANDER") FilterDeck("", true);
    }
    private DeckEntry[] DeckRowsToShow() => (deckSection == "Considering" ? deckWorkspace.View.Workshop?.Considering.Select(c => new DeckEntry("Considering", c.CardId, c.Card.Card, c.Quantity, c.Card.ManaValue, c.Card.ColorIdentity, c.Card.Edition)) ?? [] : selectedDeckDetails?.Entries ?? [])
        .Where(e => (deckSection.Length == 0 || e.Section == deckSection)
            && (!commanderCandidates || selectedDeckDetails!.CommanderOptions.Any(c => c.Id == e.Id))
            && (!deckIssuesOnly || CardIssues(e.Id, e.Section).Length > 0)
            && (deckInteraction.RelatedCards.Count == 0 || deckInteraction.RelatedCards.Contains(e.Card.Name))
            && (workshopRoleFilter.Length == 0 || deckWorkspace.View.Workshop?.Roles.Any(r => r.CardId == e.Id && r.Roles.Contains(workshopRoleFilter, StringComparer.OrdinalIgnoreCase)) == true)
            && (workshopManaFilter == null || !e.Card.Type.Contains("Land") && Math.Min(6, e.ManaValue) == workshopManaFilter)
            && (deckFilter.Length == 0 || (e.Card.Name + " " + e.Card.Type + " " + e.Card.Text).Contains(deckFilter, StringComparison.OrdinalIgnoreCase)))
        .OrderBy(e => e.Card.Name).ThenBy(e => e.Section).ToArray();
}
