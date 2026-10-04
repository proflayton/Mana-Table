using Mana.Contracts;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableGame
{
    private void DrawDeckEditor()
    {
        var deck = selectedDeckDetails!;
        if (DeckSelection is { } selected) art.Get(selected.Card);
        canvas.Text(deck.Name, 48, 96, Ink, 1.3f, 700, 1, true);
        Button("deck-rename", "Rename", 764, 98, 108, () => { var name = Ask("Rename deck", "Deck name", deck.Name); if (name != null) RenameDeck(name); }, !busy, height: 36);
        Button("deck-duplicate", "Duplicate", 882, 98, 118, () => { var name = Ask("Copy deck", "Name this separate copy", deck.Name + " copy"); if (name != null) DuplicateDeck(name); }, !busy, height: 36);
        Button("deck-export", "Copy list", 1010, 98, 125, ExportDeck, !busy, height: 36);
        Button("deck-new", "New deck", 1145, 98, 125, () => NewDeck(), !busy, height: 36);
        Button("deck-done", "Review for table", 1280, 98, 272, ReviewBuiltDeck, !busy, height: 36);
        int main = deck.Entries.Where(e => e.Section == "Main").Sum(e => e.Quantity), leaders = deck.Entries.Where(e => e.Section == "Commander").Sum(e => e.Quantity);
        int side = deck.Entries.Where(e => e.Section == "Sideboard").Sum(e => e.Quantity);
        canvas.Text($"{main + leaders} / 100 cards   ·   {main} main   ·   {leaders} commander{(leaders == 1 ? "" : "s")}   ·   {side} sideboard", 50, 153, Ink, .85f, 1080, 1);
        Button("deck-undo", "Undo", 1320, 151, 110, () => EditDeck(d => engine.UndoDeckAsync(d.Id, d.Revision), "Edit undone."), !busy && deck.CanUndo, height: 32);
        Button("deck-redo", "Redo", 1440, 151, 110, () => EditDeck(d => engine.RedoDeckAsync(d.Id, d.Revision), "Edit restored."), !busy && deck.CanRedo, height: 32);
        var names = deck.Entries.Where(e => e.Section == "Commander").Select(e => e.Card.Name).ToArray();
        canvas.Text(names.Length == 0 ? "Choose your commander from the deck's Leaders tab, or find one in the library." : "COMMANDER  " + string.Join(" + ", names), 50, 194, names.Length == 0 ? Muted : Gold, .78f, 1480, 1);
        canvas.Panel(new(32, 236, 538, 712), new(21, 29, 39), new(54, 67, 79), 8);
        canvas.Panel(new(586, 236, 528, 712), new(21, 29, 39), new(54, 67, 79), 8);
        canvas.Panel(new(1130, 236, 438, 712), new(18, 25, 34), new(54, 67, 79), 8);
        DrawCardLibrary(); DrawEditableDeck(); DrawDeckCardPreview();
        string status = deck.SaveError.Length > 0 ? "Could not save: " + deck.SaveError : deck.Problem.Length > 0 ? "Deck needs changes: " + deck.Problem.Replace('\n', ' ') : "Ready to play · All changes saved";
        canvas.Text(status, 46, 963, deck.Problem.Length > 0 || deck.SaveError.Length > 0 ? Gold : Ink, .68f, 1250, 1);
        if (deck.SaveError.Length > 0) Button("deck-retry-save", "Retry save", 1340, 955, 214, () => EditDeck(d => engine.SaveDeckAsync(d.Id, d.Revision), "Deck saved."), !busy, height: 33);
        else Button("deck-review", "Review deck", 1340, 955, 214, () => { deckReview = true; deckReviewPage = 0; }, height: 33);
        if (deckMenu.Length > 0) DrawDeckMenu();
        if (deckReview) DrawDeckReview();
    }
    private void DeckSearchBox(string field, string value, string placeholder, Rectangle bounds)
    {
        bool focus = deckTextFocus == field;
        canvas.Panel(bounds, focus ? new(36, 52, 64) : new(14, 22, 30), focus ? Gold : Muted * .4f, 5);
        canvas.Text(value.Length == 0 ? placeholder : value + (focus && (int)(now * 2) % 2 == 0 ? "|" : ""), bounds.X + 10, bounds.Y + 10, value.Length == 0 ? Muted : focus && deckSelectAll ? Gold : Ink, .7f, bounds.Width - 53, 1);
        hits.Add(new("deck-search-" + field, Scope, bounds, () => FocusDeckText(field)));
        if (value.Length > 0) Button("deck-clear-" + field, "×", bounds.Right - 35, bounds.Y + 3, 31, () => { FocusDeckText(field); SetDeckText(""); }, height: bounds.Height - 6);
    }
    private void DrawCardLibrary()
    {
        canvas.Text("CARD LIBRARY", 46, 252, Gold, .8f, 220, 1, true);
        canvas.Text(catalogPage is { } page ? $"{page.Total:N0} matches" : catalogError.Length > 0 ? "Search unavailable" : "Searching…", 310, 253, Muted, .65f, 245, 1);
        DeckSearchBox("library", catalogText, "Search names, rules, or creature types…", new(46, 283, 510, 38));
        Button("catalog-type", catalogType == 0 ? "Any type ▾" : CatalogTypes[catalogType] + " ▾", 46, 331, 163, () => deckMenu = "type", height: 32);
        Button("catalog-mana", catalogMana == 0 ? "MV: Any" : "MV ≤ " + (catalogMana - 1), 217, 331, 96, () => deckMenu = "mana", height: 32);
        Button("catalog-sort", catalogManaSort ? "Sort: Mana" : "Sort: Name", 321, 331, 133, () => deckMenu = "sort", height: 32);
        Button("catalog-reset", "Reset", 462, 331, 94, ResetCatalog, height: 32);
        Button("catalog-identity", "Leader colors", 46, 373, 158, () => { catalogCommanderColors = !catalogCommanderColors; ChangeCatalog(); }, CommanderIdentity != null, catalogCommanderColors, 30);
        for (int i = 0; i < 7; i++) {
            int color = i, bit = i is >= 1 and <= 5 ? 1 << (i - 1) : 0;
            string label = new[] { "All", "W", "U", "B", "R", "G", "C" }[i];
            bool selected = !catalogCommanderColors && (i == 0 ? catalogColors == null : i == 6 ? catalogColors == 0 : (catalogColors.GetValueOrDefault() & bit) != 0);
            Button("catalog-color:" + label, label, 214 + i * 49, 373, 44, () => {
                catalogCommanderColors = false;
                catalogColors = color == 0 ? null : color == 6 ? 0 : catalogColors.GetValueOrDefault() ^ bit;
                ChangeCatalog();
            }, accent: selected, height: 30);
        }
        if (catalogPage is { } results) {
            for (int i = 0; i < results.Cards.Length; i++) {
                var card = results.Cards[i]; int x = 50 + i % 3 * 172, y = 419 + i / 3 * 233;
                var pose = new Pose(new(x + 76, y + 102), new(146, 204));
                PaintCard(card.Card, pose, librarySelection?.Id == card.Id, false);
                hits.Add(new("catalog-card:" + card.Id, Scope, new(x, y, 152, 227), () => { librarySelection = card; deckCardId = deckBackId = null; deckCardSection = "Main"; }));
                int copies = selectedDeckDetails!.Entries.Where(e => e.Card.Name == card.Card.Name).Sum(e => e.Quantity);
                canvas.Text(copies > 0 ? $"{copies} in list" : card.Card.Name, x, y + 207, copies > 0 ? Gold : Muted, .51f, 115, 1);
                Button("catalog-add:" + card.Id, "+", x + 122, y + 202, 32, () => {
                    librarySelection = card; deckCardId = deckBackId = null; deckCardSection = "Main"; DeckQuantity(DeckQuantity() + 1);
                }, !busy, height: 28);
            }
            if (results.Total == 0) canvas.Text("No cards match. Try fewer filters or a different name.", 69, 470, Muted, .9f, 465, 4);
            Button("catalog-prev", "Previous", 46, 896, 112, () => { catalogOffset = Math.Max(0, catalogOffset - 6); ChangeCatalog(true); }, results.Offset > 0, height: 32);
            Button("catalog-next", "Next", 166, 896, 94, () => { catalogOffset += 6; ChangeCatalog(true); }, results.Offset + results.Cards.Length < results.Total, height: 32);
            canvas.Text($"{(results.Total == 0 ? 0 : results.Offset + 1)}–{results.Offset + results.Cards.Length} of {results.Total:N0}", 282, 906, Muted, .63f, 272, 1);
        } else canvas.Text(catalogError.Length > 0 ? catalogError + " · Use Reset to retry." : "Searching the card library…", 69, 470, Muted, .85f, 465, 5);
    }
    private void DrawEditableDeck()
    {
        var deck = selectedDeckDetails!;
        canvas.Text("YOUR DECK", 600, 252, Gold, .8f, 310, 1, true);
        DeckSearchBox("deck", deckFilter, "Find a card in this deck…", new(600, 283, 500, 38));
        var tabs = new[] { ("deck-all", "All", "", false), ("deck-main", "Main", "Main", false), ("deck-side", "Side", "Sideboard", false), ("deck-command", "Cmd", "Commander", false), ("deck-candidates", "Leaders", "", true) };
        for (int i = 0; i < tabs.Length; i++) {
            var tab = tabs[i];
            Button(tab.Item1, tab.Item2, 600 + i * 102, 331, 92, () => FilterDeck(tab.Item3, tab.Item4), !busy, commanderCandidates == tab.Item4 && deckSection == tab.Item3, 32);
        }
        var rows = DeckRowsToShow();
        deckCardsPage = Math.Clamp(deckCardsPage, 0, Math.Max(0, (rows.Length - 1) / DeckRows));
        for (int i = 0; i < Math.Min(DeckRows, rows.Length - deckCardsPage * DeckRows); i++) {
            var entry = rows[deckCardsPage * DeckRows + i]; int y = 378 + i * 35;
            bool selected = librarySelection == null && deckCardId == entry.Id && deckCardSection == entry.Section;
            canvas.Rounded(new(600, y, 500, 32), selected ? new(43, 65, 79) : new(29, 39, 50), 3);
            if (entry.Section == "Commander") canvas.Fill(new(600, y, 3, 32), Gold);
            canvas.Text(entry.Quantity + "×", 610, y + 8, Muted, .64f, 38, 1);
            canvas.Text(entry.Card.Name, 651, y + 7, entry.Section == "Commander" ? Gold : Ink, .66f, 354, 1, selected);
            canvas.Text(entry.Section == "Commander" ? "CMD" : entry.Section == "Sideboard" ? "SIDE" : entry.ManaValue.ToString(), 1040, y + 9, Muted, .53f, 54, 1);
            if (!busy) hits.Add(new("deck-card:" + entry.Section + ":" + entry.Id, Scope, new(600, y, 500, 32), () => SelectDeckRow(entry)));
        }
        if (rows.Length == 0) canvas.Text(commanderCandidates ? "No leaders in this view. Find your commander in the library, add it, then set its role here." : "No cards in this view. Select a library card and use + to add it.", 620, 419, Muted, .82f, 450, 5);
        Button("deck-cards-prev", "Previous", 600, 805, 105, () => deckCardsPage--, !busy && deckCardsPage > 0, height: 30);
        Button("deck-cards-next", "Next", 715, 805, 90, () => deckCardsPage++, !busy && (deckCardsPage + 1) * DeckRows < rows.Length, height: 30);
        canvas.Text($"{rows.Length} entries · {deckCardsPage + 1}/{Math.Max(1, (rows.Length + DeckRows - 1) / DeckRows)}", 822, 814, Muted, .6f, 278, 1);
        DrawDeckComposition(deck, 600, 848, 500);
    }
    private void DrawDeckCardPreview()
    {
        var deck = selectedDeckDetails!;
        if (DeckSelection is not { } card) { canvas.Text("Choose a card to read it, add copies, or set its role.", 1170, 422, Muted, 1, 358, 4); return; }
        var preview = DeckCardFace(card.Card, deckBackId == card.Id);
        PaintCard(preview, new Pose(new(1349, 494), new(350, 490)), false, false);
        int copies = deck.Entries.Where(e => e.Card.Name == card.Card.Name).Sum(e => e.Quantity);
        canvas.Text(card.Edition + " · " + deckCardSection + " · " + copies + (copies == 1 ? " copy in list" : " copies in list"), 1150, 752, Muted, .63f, 398, 1);
        int count = DeckQuantity(); bool editable = deckCardSection is "Main" or "Sideboard";
        Button("deck-remove", "−", 1150, 784, 46, () => DeckQuantity(count - 1), !busy && editable && count > 0, height: 36);
        Button("deck-quantity", count + (count == 1 ? " copy" : " copies"), 1204, 784, 122, () => {
            var value = Ask("Card quantity", card.Card.Name + " in " + deckCardSection + " (0 removes it)", count.ToString());
            if (value != null) { if (int.TryParse(value, out int quantity)) DeckQuantity(quantity); else error = "Enter a whole-number quantity."; }
        }, !busy && editable, height: 36);
        Button("deck-add", "+", 1334, 784, 46, () => DeckQuantity(count + 1), !busy && editable && count < 1000, true, 36);
        Button("deck-move", deckCardSection == "Sideboard" ? "To main" : "To side", 1388, 784, 160, MoveDeckCard, !busy && editable, height: 36);
        if (card.Card.OtherFace != null) Button("deck-flip", deckBackId == card.Id ? "Front face" : "Other face", 1364, 826, 184, () => deckBackId = deckBackId == card.Id ? null : card.Id, height: 26);
        canvas.Text(card.Card.OtherFace != null ? "Click count to set quantity" : editable ? "+ adds one · Click the count to enter a quantity" : "Commanders count toward your 100-card deck.", 1150, 830, Muted, .59f, card.Card.OtherFace != null ? 204 : 408, 1);
        var ids = deck.CommanderIds;
        bool eligible = deck.CommanderOptions.Any(c => c.Id == card.Id);
        bool partner = ids.Length == 1 && deck.CommanderOptions.FirstOrDefault(c => c.Id == ids[0])?.Partners.Contains(card.Id) == true;
        Button("deck-set-commander", ids.Length == 2 ? "Use as sole commander" : "Set as commander", 1150, 860, 398, () => SaveCommanders([card.Id]), !busy && eligible && !(ids.Length == 1 && ids[0] == card.Id), true, 34);
        if (ids.Contains(card.Id)) Button("deck-remove-commander", "Return to main deck", 1150, 903, 398, () => SaveCommanders(ids.Where(id => id != card.Id).ToArray()), !busy, height: 34);
        else Button("deck-add-partner", "Add as partner / Background", 1150, 903, 398, () => SaveCommanders([.. ids, card.Id]), !busy && partner, height: 34);
    }
    private void DrawDeckReview()
    {
        hits.Clear(); canvas.Fill(new(0, 64, 1600, 936), Color.Black * .8f);
        canvas.Panel(new(180, 180, 1240, 645), new(23, 33, 45), Gold, 10);
        canvas.Text("DECK REVIEW", 215, 207, Gold, 1.1f, 900, 1, true);
        var deck = selectedDeckDetails!;
        string[] lines = (deck.SaveError.Length > 0 ? "Save failed: " + deck.SaveError : deck.Problem.Length > 0 ? deck.Problem : "This deck passes the engine's structural checks and is ready for a game.").Split('\n');
        deckReviewPage = Math.Clamp(deckReviewPage, 0, Math.Max(0, (lines.Length - 1) / 14));
        for (int i = 0; i < Math.Min(14, lines.Length - deckReviewPage * 14); i++) canvas.Text(lines[deckReviewPage * 14 + i], 215, 266 + i * 30, Ink, .82f, 1160, 1);
        canvas.Text("Checks include card count, commander pairing, color identity and duplicate limits.", 215, 711, Muted, .7f, 1150, 2);
        Button("deck-review-prev", "Previous", 215, 766, 135, () => deckReviewPage--, deckReviewPage > 0);
        Button("deck-review-next", "Next", 362, 766, 135, () => deckReviewPage++, (deckReviewPage + 1) * 14 < lines.Length);
        Button("deck-review-close", "Back to building", 1100, 766, 276, () => deckReview = false, true, true);
    }
    private void DrawDeckMenu()
    {
        string kind = deckMenu;
        string[] options = kind == "type" ? CatalogTypes.Select(t => t.Length == 0 ? "Any type" : t).ToArray()
            : kind == "mana" ? Enumerable.Range(0, 10).Select(i => i == 0 ? "Any mana value" : "Mana value ≤ " + (i - 1)).ToArray() : ["Name", "Mana value"];
        int x = kind == "type" ? 46 : kind == "mana" ? 217 : 321;
        hits.Clear(); hits.Add(new("deck-menu-dismiss", Scope, new(0, 64, 1600, 936), () => deckMenu = ""));
        canvas.Panel(new(x, 367, 238, options.Length * 36 + 12), new(16, 24, 34), Gold, 6);
        for (int i = 0; i < options.Length; i++) {
            int index = i;
            Button("catalog-option:" + kind + ":" + i, options[i], x + 6, 373 + i * 36, 226, () => {
                if (kind == "type") catalogType = index;
                else if (kind == "mana") catalogMana = index;
                else catalogManaSort = index == 1;
                deckMenu = ""; ChangeCatalog();
            }, accent: kind == "type" ? i == catalogType : kind == "mana" ? i == catalogMana : (i == 1) == catalogManaSort, height: 32);
        }
    }
}
