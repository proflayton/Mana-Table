using Mana.Contracts;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private string pickerSearch = "", reviewSearch = "", reviewSection = "";
    private int pickerSeat = -1, reviewPage;
    private bool reviewList, reviewBack;
    private DeckEntry? reviewCard;
    private const int ReviewRowsPerPage = 10;
    private sealed record DeckChoice(string Id, string Name, string Description, bool Current, string LibraryToken = "", DeckLibraryDeletion? Deleted = null);
    private int PickerTileHeight => Math.Max(113, PickerChoices().Select(choice => canvas.TextSize(choice.Name, .96f, 440, 80, true).Y + 50).DefaultIfEmpty(113).Max());
    private int PickerPageSize => 2 * Math.Clamp(385 / (PickerTileHeight + 15), 1, 3);
    private bool SetupHasInput => setupModal is "join" or "decks" or "profile" || setupModal == "confirm" && reviewList;
    private string SetupInput => setupModal == "profile" ? nameDraft : setupModal == "join" ? joinAddress : setupModal == "decks" ? pickerSearch : reviewSearch;
    private void SetSetupInput(string value)
    {
        value = value[..Math.Min(value.Length, setupModal == "profile" ? 24 : 220)];
        if (setupModal == "profile") nameDraft = value;
        else if (setupModal == "join") joinAddress = value;
        else if (setupModal == "decks") { pickerSearch = value; deckPage = 0; }
        else { reviewSearch = value; reviewPage = 0; }
    }
    private void SetupSearchBox(string id, string placeholder, Rectangle bounds)
    {
        canvas.Panel(bounds, new(14, 22, 32), textFocus ? Blue : LobbyTrim, 7);
        string value = SetupInput;
        canvas.Text(value.Length == 0 ? placeholder : value + (textFocus && (int)(now * 2) % 2 == 0 ? "|" : ""),
            bounds.X + 14, bounds.Y + 12, value.Length == 0 ? Muted : textFocus && setupSelectAll ? Gold : Ink, .72f, bounds.Width - 65, 1);
        if (!busy) hits.Add(ShellHit(id, bounds, new ShellIntent.FocusSetupInput()));
        if (value.Length > 0) Button(id + "-clear", "×", bounds.Right - 42, bounds.Y + 4, 36,
            new ShellIntent.FocusSetupInput(Clear: true), !busy, height: bounds.Height - 8);
    }
    private DeckChoice[] PickerChoices()
    {
        IEnumerable<DeckChoice> choices = pickerSeat >= 0 && soloSetup is { } setup
            ? setup.Opponents.Select((p, i) => new DeckChoice(p.Id, p.Name, p.Description, soloChoices[pickerSeat] == i))
            : pickerDeleted ? deletedDecks.Select(d => new DeckChoice(d.DeckId, d.Name, "Deleted", false, Deleted: d))
            : pickerPresets ? presets.Select(p => new DeckChoice(p.Id, p.Name, p.Description, false))
            : (deckDeletionNotice is { } removed && deletedDecks.Any(d => d.RestoreToken == removed.RestoreToken)
                ? new[] { new DeckChoice(removed.DeckId, removed.Name, "Deleted", false, Deleted: removed) } : [])
                .Concat(decks.Select(d => new DeckChoice(d.Id, d.Name, selectedDeck?.Id == d.Id ? "Your current deck" : "Saved deck", selectedDeck?.Id == d.Id, d.LibraryToken)));
        return choices.Where(d => (d.Name + " " + d.Description).Contains(pickerSearch.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
    }
    private void OpenOpponentPicker(int seat)
    {
        if (busy || Connected || !DeckReady || selectedDeckDetails is not { } deck || seat < 0 || seat >= (CommanderDeck ? 3 : 1)) return;
        ClearExperienceInput();
        if (soloSetup?.DeckId != deck.Id) {
            int generation = deckSelectionGeneration;
            string[] previous = CurrentSoloOpponents();
            Work(async () => {
                var prepared = await PrepareSoloDeck(deck.Id, previous);
                updates.Enqueue(() => {
                    if (generation != deckSelectionGeneration) return;
                    ApplySoloPreparation(prepared); confirmedDeckId = deck.Id;
                    pickerSeat = seat; pickerDeleted = false; pickerSearch = ""; deckPage = 0; setupModal = "decks"; textFocus = false;
                });
            }, "Choosing opponents", "Finding compatible AI decks.");
            return;
        }
        pickerSeat = seat; pickerDeleted = false; pickerSearch = ""; deckPage = 0; setupModal = "decks"; textFocus = false;
    }
    private void ChoosePickerDeck(string id)
    {
        if (pickerSeat >= 0 && soloSetup is { } setup) {
            int index = Array.FindIndex(setup.Opponents, p => p.Id == id);
            if (index < 0) return;
            soloChoices[pickerSeat] = index; CloseSetupModal();
            message = $"AI opponent {pickerSeat + 1} will play {setup.Opponents[index].Name}.";
        } else if (pickerDeleted) return;
        else if (pickerPresets) ChoosePreset(id);
        else if (decks.FirstOrDefault(d => d.Id == id) is { } deck) ChooseDeck(deck);
    }
    private void DrawDeckPicker()
    {
        bool opponent = pickerSeat >= 0;
        canvas.Text(opponent ? $"Choose AI opponent {pickerSeat + 1}'s deck" : "Find your next deck", 202, 179, Ink, 1.4f, 1060, 1, true);
        string pickerHint = opponent ? "Choose this opponent's deck. The other seats stay the same."
            : Connected && DeckLibrary != null ? "Leave the lobby to delete or restore saved decks."
            : pickerDeleted ? "Deleted lists keep their cards and deck history. Undo returns a list to Your decks."
            : decks.Count == 0 ? "New here? Try a precon for a complete Commander deck."
            : pickerPresets ? "Choose a precon to play or edit."
            : "Choose a saved list, or try a precon for a ready-made deck.";
        canvas.Text(pickerHint, 204, 234, Muted, .82f, 1110, 1);
        if (!opponent) {
            Button("picker-saved", $"Your decks ({decks.Count})", 204, 278, 255, new ShellIntent.SelectDeckCollection(DeckCollection.Saved), !busy, !pickerPresets && !pickerDeleted, 40);
            Button("picker-presets", "Try a precon", 471, 278, 220, new ShellIntent.SelectDeckCollection(DeckCollection.Presets), !busy, pickerPresets && !pickerDeleted, 40);
            if (DeckLibrary != null) Button("picker-deleted", "Deleted (" + deletedDecks.Count + ")", 703, 278, 202,
                new ShellIntent.SelectDeckCollection(DeckCollection.Deleted), !busy, pickerDeleted, 40);
        } else LobbyPill("AI OPPONENT " + (pickerSeat + 1), 204, 284, 224, Blue);
        int searchX = !opponent && DeckLibrary != null ? 925 : 810;
        SetupSearchBox("picker-search", "Search decks…", new(searchX, 278, 1394 - searchX, 40));
        var choices = PickerChoices(); int count = choices.Length;
        int pageSize = PickerPageSize, tileHeight = PickerTileHeight;
        for (int i = 0; i < Math.Min(pageSize, count - deckPage * pageSize); i++) {
            int at = deckPage * pageSize + i, x = 204 + i % 2 * 602, y = 342 + i / 2 * (tileHeight + 15);
            var choice = choices[at];
            canvas.Panel(new(x, y, 586, tileHeight), choice.Deleted != null ? new(21, 31, 39) : choice.Current ? new(37, 59, 75) : new(29, 42, 56), choice.Current ? Blue : LobbyTrim, 10);
            int titleHeight = canvas.TextSize(choice.Name, .96f, 440, 80, true).Y;
            canvas.Text(choice.Name, x + 18, y + 15, choice.Deleted == null ? Ink : Muted, .96f, 440, 80, true);
            bool saveFailed = selectedDeckDetails is { SaveError.Length: > 0 } selected && selected.Id == choice.Id;
            canvas.Text(deletingDeckId == choice.Id ? "Removing…" : saveFailed ? "Save failed; retry in builder." : choice.Description, x + 18, y + 20 + titleHeight, Muted, .7f, 440, 2);
            if (choice.Deleted is { } removed) {
                Button("deck-restore:" + removed.RestoreToken, "Undo", x + 462, y + tileHeight - 42, 104,
                    new DeckIntent.RestoreSavedDeck(removed.DeckId, removed.RestoreToken), CanManageDeckLibrary, height: 32);
                continue;
            }
            Button((opponent ? "opponent:" : pickerPresets ? "preset:" : "deck:") + choice.Id, choice.Current ? "✓" : ">", x + 514, y + 16, 52, new ShellIntent.ChoosePickerDeck(choice.Id), !busy, choice.Current, 42);
            if (!busy) hits.Insert(Math.Max(0, hits.Count - 1), ShellHit("pick-tile:" + at, new(x, y, 586, tileHeight), new ShellIntent.ChoosePickerDeck(choice.Id)));
            if (!opponent && !pickerPresets && DeckLibrary != null && choice.LibraryToken.Length > 0)
            {
                int libraryGeneration = deckLibraryGeneration;
                Button("deck-delete:" + choice.Id, "Delete", x + 470, y + tileHeight - 37, 96,
                    new DeckIntent.Delete(choice.Id, choice.LibraryToken, libraryGeneration), CanManageDeckLibrary && !saveFailed, height: 28);
            }
        }
        if (count == 0) {
            canvas.Text(pickerSearch.Length > 0 ? "No decks match your search." : pickerDeleted ? "No deleted decks." : "Your next deck starts here.", 280, 456, Ink, 1.2f, 1020, 1, true);
            canvas.Text(pickerSearch.Length > 0 ? "Try a different name or clear the search to see every deck." : pickerDeleted ? "Deleted saved lists appear here for Undo." : "Import a list, try a precon, or build around your favorite commander.", 280, 511, Muted, .9f, 1020, 3);
        }
        Button("decks-prev", "Previous", 204, 740, 135, new ShellIntent.Page(ShellPage.DeckPicker, -1), !busy && deckPage > 0, height: 34);
        Button("decks-next", "Next", 351, 740, 108, new ShellIntent.Page(ShellPage.DeckPicker, 1), !busy && (deckPage + 1) * pageSize < count, height: 34);
        canvas.Text($"{count} {(count == 1 ? "list" : "lists")}  ·  Page {deckPage + 1} / {Math.Max(1, (count + pageSize - 1) / pageSize)}", 484, 749, Muted, .67f, 620, 1);
        canvas.Line(new(204, 795), new(1394, 795), LobbyTrim);
        if (!opponent) {
            Button("import", "Import deck file", 204, 820, 247, new ShellIntent.ImportFile(), !busy, height: 44);
            Button("paste", "Paste deck list", 463, 820, 247, new ShellIntent.ImportClipboard(), !busy, height: 44);
            if (WorkshopEngine != null) Button("picker-archidekt", "Link Archidekt", 722, 820, 310, new DeckIntent.ImportArchidekt(), !busy, height: 44);
            Button("picker-new", "+ Build a new deck", 1078, 820, 316, new DeckIntent.NewDeck(), !busy, true, 44);
            canvas.Text("A deck file from your computer.", 209, 874, Muted, .59f, 240, 1);
            canvas.Text("Use a copied deck list.", 468, 874, Muted, .59f, 240, 1);
            if (WorkshopEngine != null) canvas.Text("Import and safely refresh a public deck.", 727, 874, Muted, .59f, 300, 1);
            canvas.Text("Start with an empty deck.", 1083, 874, Muted, .59f, 306, 1);
        }
    }
    private DeckEntry[] ReviewRows() => selectedDeckDetails?.Entries.Where(e => (reviewSection.Length == 0 || e.Section == reviewSection)
        && (e.Card.Name + " " + e.Card.Type + " " + e.Card.Text).Contains(reviewSearch.Trim(), StringComparison.OrdinalIgnoreCase))
        .OrderBy(e => e.Section == "Commander" ? 0 : e.Section == "Main" ? 1 : 2).ThenBy(e => e.Card.Name).ThenBy(e => e.Edition).ToArray() ?? [];
    private void DrawLobbyDeckList()
    {
        var deck = selectedDeckDetails!;
        if (reviewCard is { } entry) {
            PaintCard(DeckCardFace(entry.Card, reviewBack), new Pose(new(403, 512), new(316, 442)), false, false);
            canvas.Text($"{entry.Quantity}×  ·  {entry.Edition}  ·  {entry.Section}", 232, 746, Muted, .69f, 348, 1);
            if (entry.Card.OtherFace != null) Button("review-flip", reviewBack ? "Front face" : "Other face", 234, 773, 336, new ShellIntent.FlipReviewCard(), !busy, height: 28);
        } else canvas.Text("This deck has no cards yet. Open the builder to get started.", 243, 444, Muted, 1, 320, 4);
        SetupSearchBox("review-search", "Find a card by name, type, or rules…", new(626, 284, 768, 40));
        var sections = new[] { ("all", "All cards", ""), ("main", "Main", "Main"), ("command", "Commander", "Commander"), ("side", "Sideboard", "Sideboard") };
        for (int i = 0; i < sections.Length; i++) {
            var (id, label, section) = sections[i];
            int total = deck.Entries.Where(e => section.Length == 0 || e.Section == section).Sum(e => e.Quantity);
            Button("review-section:" + id, label + " (" + total + ")", 626 + i * 194, 337, 184, new ShellIntent.SelectReviewSection(section), !busy, reviewSection == section, 34);
        }
        var rows = ReviewRows();
        for (int i = 0; i < Math.Min(ReviewRowsPerPage, rows.Length - reviewPage * ReviewRowsPerPage); i++) {
            var row = rows[reviewPage * ReviewRowsPerPage + i]; int y = 384 + i * 35;
            bool selected = reviewCard?.Id == row.Id && reviewCard?.Section == row.Section;
            canvas.Rounded(new(626, y, 768, 32), selected ? new(43, 65, 79) : new(29, 39, 50), 4);
            canvas.Text(row.Quantity + "×", 638, y + 8, Muted, .66f, 50, 1);
            canvas.Text(row.Card.Name, 686, y + 7, row.Section == "Commander" ? Gold : Ink, .75f, 523, 1, selected);
            canvas.Text(row.Section == "Commander" ? "COMMANDER" : row.Section == "Sideboard" ? "SIDEBOARD" : "MV " + row.ManaValue, 1252, y + 10, Muted, .52f, 132, 1);
            if (!busy) hits.Add(ShellHit("review-card:" + row.Section + ":" + row.Id, new(626, y, 768, 32), new ShellIntent.SelectReviewCard(row.Id, row.Section)));
        }
        if (rows.Length == 0) canvas.Text(reviewSearch.Length > 0 ? "No cards match. Try a different search or clear it." : "There are no cards in this section.", 650, 451, Muted, .92f, 708, 3);
        Button("review-prev", "Previous", 626, 745, 131, new ShellIntent.Page(ShellPage.Review, -1), !busy && reviewPage > 0, height: 32);
        Button("review-next", "Next", 769, 745, 104, new ShellIntent.Page(ShellPage.Review, 1), !busy && (reviewPage + 1) * ReviewRowsPerPage < rows.Length, height: 32);
        int totalCards = rows.Sum(e => e.Quantity);
        canvas.Text($"{totalCards} {(totalCards == 1 ? "card" : "cards")}  ·  {reviewPage + 1}/{Math.Max(1, (rows.Length + ReviewRowsPerPage - 1) / ReviewRowsPerPage)}", 904, 755, Muted, .66f, 480, 1);
    }
}
