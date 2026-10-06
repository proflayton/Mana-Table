using Mana.Contracts;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableGame
{
    private string pickerSearch = "", reviewSearch = "", reviewSection = "";
    private int pickerSeat = -1, reviewPage;
    private bool reviewList, reviewBack;
    private DeckEntry? reviewCard;
    private const int ReviewRowsPerPage = 10;
    private sealed record DeckChoice(string Id, string Name, string Description, bool Current);
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
        if (!busy) hits.Add(new(id, Scope, bounds, () => { textFocus = true; setupSelectAll = false; }));
        if (value.Length > 0) Button(id + "-clear", "×", bounds.Right - 42, bounds.Y + 4, 36,
            () => { SetSetupInput(""); textFocus = true; setupSelectAll = false; }, !busy, height: bounds.Height - 8);
    }
    private DeckChoice[] PickerChoices()
    {
        IEnumerable<DeckChoice> choices = pickerSeat >= 0 && soloSetup is { } setup
            ? setup.Opponents.Select((p, i) => new DeckChoice(p.Id, p.Name, p.Description, soloChoices[pickerSeat] == i))
            : pickerPresets ? presets.Select(p => new DeckChoice(p.Id, p.Name, p.Description, false))
            : decks.Select(d => new DeckChoice(d.Id, d.Name, selectedDeck?.Id == d.Id ? "Your current deck" : "Saved Commander deck", selectedDeck?.Id == d.Id));
        return choices.Where(d => (d.Name + " " + d.Description).Contains(pickerSearch.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
    }
    private void OpenOpponentPicker(int seat)
    {
        if (soloSetup == null || busy) return;
        pickerSeat = seat; pickerSearch = ""; deckPage = 0; setupModal = "decks"; textFocus = false;
    }
    private void ChoosePickerDeck(string id)
    {
        if (pickerSeat >= 0 && soloSetup is { } setup) {
            int index = Array.FindIndex(setup.Opponents, p => p.Id == id);
            if (index < 0) return;
            soloChoices[pickerSeat] = index; CloseSetupModal();
            message = $"AI opponent {pickerSeat + 1} will play {setup.Opponents[index].Name}.";
        } else if (pickerPresets) ChoosePreset(id);
        else if (decks.FirstOrDefault(d => d.Id == id) is { } deck) ChooseDeck(deck);
    }
    private void DrawDeckPicker()
    {
        bool opponent = pickerSeat >= 0;
        canvas.Text(opponent ? $"Choose AI opponent {pickerSeat + 1}'s deck" : "Find your next deck", 202, 179, Ink, 1.4f, 1060, 1, true);
        canvas.Text(opponent ? "Build the matchup you want. Each AI seat has its own deck choice." : "Choose a list to review, or head into the builder to make it your own.", 204, 234, Muted, .82f, 1110, 1);
        if (!opponent) {
            Button("picker-saved", $"Your decks ({decks.Count})", 204, 278, 255, () => { pickerPresets = false; deckPage = 0; }, !busy, !pickerPresets, 40);
            Button("picker-presets", "Try a precon", 471, 278, 220, () => { pickerPresets = true; deckPage = 0; }, !busy, pickerPresets, 40);
        } else LobbyPill("AI OPPONENT " + (pickerSeat + 1), 204, 284, 224, Blue);
        SetupSearchBox("picker-search", "Search decks…", new(810, 278, 584, 40));
        var choices = PickerChoices(); int count = choices.Length;
        deckPage = Math.Clamp(deckPage, 0, Math.Max(0, (count - 1) / 6));
        for (int i = 0; i < Math.Min(6, count - deckPage * 6); i++) {
            int at = deckPage * 6 + i, x = 204 + i % 2 * 602, y = 342 + i / 2 * 128;
            var choice = choices[at];
            canvas.Panel(new(x, y, 586, 113), choice.Current ? new(37, 59, 75) : new(29, 42, 56), choice.Current ? Blue : LobbyTrim, 10);
            canvas.Text(choice.Name, x + 18, y + 15, Ink, .96f, 475, 1, true);
            canvas.Text(choice.Description, x + 18, y + 52, Muted, .7f, 467, 2);
            Button((opponent ? "opponent:" : pickerPresets ? "preset:" : "deck:") + choice.Id, choice.Current ? "✓" : ">", x + 514, y + 36, 52, () => ChoosePickerDeck(choice.Id), !busy, choice.Current, 42);
            if (!busy) hits.Insert(Math.Max(0, hits.Count - 1), new("pick-tile:" + at, Scope, new(x, y, 586, 113), () => ChoosePickerDeck(choice.Id)));
        }
        if (count == 0) {
            canvas.Text(pickerSearch.Length > 0 ? "No decks match your search." : "Your next deck starts here.", 280, 456, Ink, 1.2f, 1020, 1, true);
            canvas.Text(pickerSearch.Length > 0 ? "Try a different name or clear the search to see every deck." : "Import a list, try a precon, or build around your favorite commander.", 280, 511, Muted, .9f, 1020, 3);
        }
        Button("decks-prev", "Previous", 204, 740, 135, () => deckPage--, !busy && deckPage > 0, height: 34);
        Button("decks-next", "Next", 351, 740, 108, () => deckPage++, !busy && (deckPage + 1) * 6 < count, height: 34);
        canvas.Text($"{count} {(count == 1 ? "deck" : "decks")}  ·  Page {deckPage + 1} / {Math.Max(1, (count + 5) / 6)}", 484, 749, Muted, .67f, 620, 1);
        canvas.Line(new(204, 795), new(1394, 795), LobbyTrim);
        if (opponent) canvas.Text("Your deck stays confirmed while you choose your opponents.", 208, 835, Muted, .82f, 1120, 1);
        else {
            Button("import", "Import deck file", 204, 820, 247, ImportFile, !busy, height: 44);
            Button("paste", "Paste deck list", 463, 820, 247, ImportClipboard, !busy, height: 44);
            Button("picker-new", "+ Build a new deck", 1078, 820, 316, () => NewDeck(), !busy, true, 44);
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
            if (entry.Card.OtherFace != null) Button("review-flip", reviewBack ? "Front face" : "Other face", 234, 773, 336, () => reviewBack = !reviewBack, !busy, height: 28);
        } else canvas.Text("This deck has no cards yet. Open the builder to get started.", 243, 444, Muted, 1, 320, 4);
        SetupSearchBox("review-search", "Find a card by name, type, or rules…", new(626, 284, 768, 40));
        var sections = new[] { ("all", "All cards", ""), ("main", "Main", "Main"), ("command", "Commander", "Commander"), ("side", "Sideboard", "Sideboard") };
        for (int i = 0; i < sections.Length; i++) {
            var (id, label, section) = sections[i];
            int total = deck.Entries.Where(e => section.Length == 0 || e.Section == section).Sum(e => e.Quantity);
            Button("review-section:" + id, label + " (" + total + ")", 626 + i * 194, 337, 184, () => { reviewSection = section; reviewPage = 0; }, !busy, reviewSection == section, 34);
        }
        var rows = ReviewRows();
        reviewPage = Math.Clamp(reviewPage, 0, Math.Max(0, (rows.Length - 1) / ReviewRowsPerPage));
        for (int i = 0; i < Math.Min(ReviewRowsPerPage, rows.Length - reviewPage * ReviewRowsPerPage); i++) {
            var row = rows[reviewPage * ReviewRowsPerPage + i]; int y = 384 + i * 35;
            bool selected = reviewCard?.Id == row.Id && reviewCard?.Section == row.Section;
            canvas.Rounded(new(626, y, 768, 32), selected ? new(43, 65, 79) : new(29, 39, 50), 4);
            canvas.Text(row.Quantity + "×", 638, y + 8, Muted, .66f, 50, 1);
            canvas.Text(row.Card.Name, 686, y + 7, row.Section == "Commander" ? Gold : Ink, .75f, 523, 1, selected);
            canvas.Text(row.Section == "Commander" ? "COMMANDER" : row.Section == "Sideboard" ? "SIDEBOARD" : "MV " + row.ManaValue, 1252, y + 10, Muted, .52f, 132, 1);
            if (!busy) hits.Add(new("review-card:" + row.Section + ":" + row.Id, Scope, new(626, y, 768, 32), () => { reviewCard = row; reviewBack = false; textFocus = false; }));
        }
        if (rows.Length == 0) canvas.Text(reviewSearch.Length > 0 ? "No cards match. Try a different search or clear it." : "There are no cards in this section.", 650, 451, Muted, .92f, 708, 3);
        Button("review-prev", "Previous", 626, 745, 131, () => reviewPage--, !busy && reviewPage > 0, height: 32);
        Button("review-next", "Next", 769, 745, 104, () => reviewPage++, !busy && (reviewPage + 1) * ReviewRowsPerPage < rows.Length, height: 32);
        int totalCards = rows.Sum(e => e.Quantity);
        canvas.Text($"{totalCards} {(totalCards == 1 ? "card" : "cards")}  ·  {reviewPage + 1}/{Math.Max(1, (rows.Length + ReviewRowsPerPage - 1) / ReviewRowsPerPage)}", 904, 755, Muted, .66f, 480, 1);
    }
}
