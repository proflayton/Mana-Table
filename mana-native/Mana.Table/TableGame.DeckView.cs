using Mana.Contracts;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableGame
{
    private Rectangle? deckIssueBounds;
    private readonly HashSet<string> renderedDeckIssueCodes = [];
    private readonly HashSet<string> renderedDeckIssueCardIds = [];
    private string shownDeckIssueCard = "";
    private void DrawDeckEditor()
    {
        var deck = selectedDeckDetails!;
        deckIssueBounds = null; renderedDeckIssueCodes.Clear(); renderedDeckIssueCardIds.Clear();
        if (analysisOpen) { DrawDeckAnalysis(); return; }
        if (DeckSelection is { } selected) art.Get(selected.Card);
        canvas.Text(deck.Name, 48, 96, Ink, 1.3f, 700, 1, true);
        Button("deck-rename", "Rename", 764, 98, 108, () => { var name = Ask("Rename deck", "Deck name", deck.Name); if (name != null) RenameDeck(name); }, !busy, height: 36);
        Button("deck-duplicate", "Duplicate", 882, 98, 118, () => { var name = Ask("Copy deck", "Name this separate copy", deck.Name + " copy"); if (name != null) DuplicateDeck(name); }, !busy, height: 36);
        Button("deck-export", "Copy list", 1010, 98, 125, ExportDeck, !busy && DeckCanCopy, height: 36);
        Button("deck-new", "New deck", 1145, 98, 125, () => NewDeck(), !busy, height: 36);
        Button("deck-done", "Back to setup", 1280, 98, 272, CloseDeckEditor, !busy, height: 36);
        int main = deck.Entries.Where(e => e.Section == "Main").Sum(e => e.Quantity), leaders = deck.Entries.Where(e => e.Section == "Commander").Sum(e => e.Quantity);
        int side = deck.Entries.Where(e => e.Section == "Sideboard").Sum(e => e.Quantity);
        bool countIssue = DeckIssues.Any(issue => issue.Code is "MAIN_COUNT" or "COMMANDER_COUNT" or "MISSING_COMMANDER");
        canvas.Text($"{main + leaders} / 100 cards", 50, 153, countIssue ? Gold : Ink, .85f, 230, 1, countIssue);
        canvas.Text($"{main} main   ·   {leaders} commander{(leaders == 1 ? "" : "s")}   ·   {side} sideboard", 294, 153, Muted, .8f, 980, 1);
        Button("deck-undo", "Undo", 1320, 151, 110, () => EditDeck(d => engine.UndoDeckAsync(d.Id, d.Revision), "Edit undone."), !busy && deck.CanUndo, height: 32);
        Button("deck-redo", "Redo", 1440, 151, 110, () => EditDeck(d => engine.RedoDeckAsync(d.Id, d.Revision), "Edit restored."), !busy && deck.CanRedo, height: 32);
        var names = deck.Entries.Where(e => e.Section == "Commander").Select(e => e.Card.Name).ToArray();
        canvas.Text(names.Length == 0 ? "Choose your commander from the deck's Leaders tab, or find one in the library." : "COMMANDER  " + string.Join(" + ", names), 50, 194, names.Length == 0 ? Muted : Gold, .78f, 1480, 1);
        if (engine is IDeckAnalysis) Button("deck-analysis", "Analyze deck", 1372, 188, 180, OpenDeckAnalysis, !busy, height: 32);
        canvas.Panel(new(32, 236, 538, 712), new(21, 29, 39), new(54, 67, 79), 8);
        canvas.Panel(new(586, 236, 528, 712), new(21, 29, 39), new(54, 67, 79), 8);
        canvas.Panel(new(1130, 236, 438, 712), new(18, 25, 34), new(54, 67, 79), 8);
        DrawCardLibrary(); DrawEditableDeck(); DrawDeckCardPreview();
        string status = deck.SaveError.Length > 0 ? "Could not save: " + deck.SaveError
            : DeckIssues.Length > 0 ? "Changes saved. Deck checks update as you edit." : "All changes saved.";
        if (InlineActivityVisible) DrawInlineActivity();
        else canvas.Text(status, 46, 963, deck.SaveError.Length > 0 ? Red : Muted, .68f, 1250, 1);
        if (deck.SaveError.Length > 0) Button("deck-retry-save", "Retry save", 1340, 955, 214, () => EditDeck(d => engine.SaveDeckAsync(d.Id, d.Revision), "Deck saved."), !busy, height: 33);
        if (deckMenu.Length > 0) DrawDeckMenu();
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
        } else if (catalogError.Length > 0) canvas.Text(catalogError + " · Use Reset to retry.", 69, 470, Red, .85f, 465, 5);
        else {
            DrawLoadingIndicator(new(300, 506), 22, true, Blue);
            canvas.CenterText("Finding your cards", new(61, 553, 475, 42), Ink, .95f, true);
            canvas.CenterText("Searching the library with your filters.", new(61, 598, 475, 36), Muted, .72f);
        }
    }
    private void DrawEditableDeck()
    {
        var deck = selectedDeckDetails!;
        canvas.Text("YOUR DECK", 600, 252, Gold, .8f, 310, 1, true);
        int affected = deck.Entries.Count(entry => CardIssues(entry.Id, entry.Section).Length > 0);
        Button("deck-issues-filter", "Problems (" + affected + ")", 946, 247, 154, ToggleDeckIssues, affected > 0 || deckIssuesOnly, deckIssuesOnly, 29);
        DeckSearchBox("deck", deckFilter, "Find a card in this deck…", new(600, 283, 500, 38));
        var tabs = new[] { ("deck-all", "All", "", false), ("deck-main", "Main", "Main", false), ("deck-side", "Side", "Sideboard", false), ("deck-command", "Cmd", "Commander", false), ("deck-candidates", "Leaders", "", true) };
        for (int i = 0; i < tabs.Length; i++) {
            var tab = tabs[i];
            Button(tab.Item1, tab.Item2, 600 + i * 102, 331, 92, () => FilterDeck(tab.Item3, tab.Item4), true, !deckIssuesOnly && commanderCandidates == tab.Item4 && deckSection == tab.Item3, 32);
        }
        var rows = DeckRowsToShow();
        deckCardsPage = Math.Clamp(deckCardsPage, 0, Math.Max(0, (rows.Length - 1) / DeckRows));
        for (int i = 0; i < Math.Min(DeckRows, rows.Length - deckCardsPage * DeckRows); i++) {
            var entry = rows[deckCardsPage * DeckRows + i]; int y = 378 + i * 32;
            bool selected = librarySelection == null && deckCardId == entry.Id && deckCardSection == entry.Section;
            bool problem = CardIssues(entry.Id, entry.Section).Length > 0;
            Color fill = problem ? selected ? new(70, 53, 42) : new(48, 37, 34) : selected ? new(43, 65, 79) : new(29, 39, 50);
            canvas.Rounded(new(600, y, 500, 30), fill, 3);
            if (problem || entry.Section == "Commander") canvas.Fill(new(600, y, 3, 30), Gold);
            canvas.Text(entry.Quantity + "×", 610, y + 7, problem ? Gold : Muted, .64f, 38, 1, problem);
            canvas.Text(entry.Card.Name, 651, y + 6, problem || entry.Section == "Commander" ? Gold : Ink, .66f, 354, 1, selected);
            if (problem) { canvas.Text("!", 1016, y + 6, Gold, .7f, 18, 1, true); renderedDeckIssueCardIds.Add(entry.Id); }
            canvas.Text(entry.Section == "Commander" ? "CMD" : entry.Section == "Sideboard" ? "SIDE" : entry.ManaValue.ToString(), 1040, y + 9, Muted, .53f, 54, 1);
            hits.Add(new("deck-card:" + entry.Section + ":" + entry.Id, Scope, new(600, y, 500, 30), () => SelectDeckRow(entry)));
        }
        if (rows.Length == 0) canvas.Text(deckIssuesOnly ? "No cards need changes in this view. Turn off Problems to see your full list."
            : commanderCandidates ? "No leaders in this view. Find your commander in the library, add it, then set its role here." : "No cards in this view. Select a library card and use + to add it.", 620, 419, Muted, .82f, 450, 5);
        Button("deck-cards-prev", "Previous", 600, 710, 105, () => deckCardsPage--, deckCardsPage > 0, height: 30);
        Button("deck-cards-next", "Next", 715, 710, 90, () => deckCardsPage++, (deckCardsPage + 1) * DeckRows < rows.Length, height: 30);
        canvas.Text($"{rows.Length} entries · {deckCardsPage + 1}/{Math.Max(1, (rows.Length + DeckRows - 1) / DeckRows)}", 822, 719, Muted, .6f, 278, 1);
        DrawDeckIssues();
    }
    private void DrawDeckCardPreview()
    {
        var deck = selectedDeckDetails!;
        if (DeckSelection is not { } card) { canvas.Text("Choose a card to read it, add copies, or set its role.", 1170, 422, Muted, 1, 358, 4); return; }
        var preview = DeckCardFace(card.Card, deckBackId == card.Id);
        PaintCard(preview, new Pose(new(1349, 450), new(286, 400)), false, false);
        int copies = deck.Entries.Where(e => e.Card.Name == card.Card.Name).Sum(e => e.Quantity);
        canvas.Text(card.Edition + " · " + deckCardSection + " · " + copies + (copies == 1 ? " copy in list" : " copies in list"), 1150, 663, Muted, .63f, 398, 1);
        DrawSelectedDeckIssues(card);
        int count = DeckQuantity(); bool editable = deckCardSection is "Main" or "Sideboard";
        Button("deck-remove", "−", 1150, 784, 46, () => DeckQuantity(count - 1), !busy && editable && count > 0, height: 36);
        Button("deck-quantity", count + (count == 1 ? " copy" : " copies"), 1204, 784, 122, () => {
            var value = Ask("Card quantity", card.Card.Name + " in " + deckCardSection + " (0 removes it)", count.ToString());
            if (value != null) { if (int.TryParse(value, out int quantity)) DeckQuantity(quantity); else error = "Enter a whole-number quantity."; }
        }, !busy && editable, CardIssues(card.Id, deckCardSection).Any(issue => issue.Code == "CARD_COPY_LIMIT"), 36);
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
    private void DrawDeckIssues()
    {
        var deck = selectedDeckDetails!;
        var issues = DeckIssues;
        deckIssueBounds = new(600, 748, 500, 196);
        canvas.Line(new(600, 754), new(1100, 754), LobbyTrim);
        canvas.Text("DECK CHECKS", 600, 769, issues.Length > 0 ? Gold : ReadyColor, .7f, 292, 1, true);
        canvas.Text(issues.Length == 0 ? "Checks passed" : issues.Length + (issues.Length == 1 ? " issue" : " issues"), 903, 771, issues.Length > 0 ? Gold : ReadyColor, .65f, 197, 1);
        if (issues.Length == 0) { DrawDeckComposition(deck, 600, 840, 500); return; }
        var parts = IssueTextPages(issues, 90);
        deckIssuePage = Math.Clamp(deckIssuePage, 0, Math.Max(0, (parts.Length - 1) / 2));
        for (int i = 0; i < Math.Min(2, parts.Length - deckIssuePage * 2); i++) {
            int index = deckIssuePage * 2 + i, y = 798 + i * 56;
            var part = parts[index];
            renderedDeckIssueCodes.Add(part.Issue.Code);
            canvas.Text("!", 600, y, Gold, .72f, 16, 1, true);
            canvas.Text(part.Text, 619, y + 1, Ink, .64f, 456, 3);
            bool actionable = part.Issue.CardIds.Length > 0 || part.Issue.Code is "COMMANDER_COUNT" or "MISSING_COMMANDER";
            if (actionable) {
                canvas.Text(">", 1085, y + 2, Gold, .67f, 15, 1, true);
                hits.Add(new("deck-issue:" + index, Scope, new(600, y, 500, 54), () => ShowDeckIssue(part.Issue)));
            }
        }
        Button("deck-issues-prev", "Previous", 600, 910, 103, () => deckIssuePage--, deckIssuePage > 0, height: 30);
        Button("deck-issues-next", "Next", 713, 910, 88, () => deckIssuePage++, (deckIssuePage + 1) * 2 < parts.Length, height: 30);
        canvas.Text($"Page {deckIssuePage + 1} / {Math.Max(1, (parts.Length + 1) / 2)}", 820, 919, Muted, .6f, 280, 1);
    }
    private void DrawSelectedDeckIssues(CatalogCard card)
    {
        string selection = card.Id + ":" + deckCardSection;
        if (shownDeckIssueCard != selection) { shownDeckIssueCard = selection; deckCardIssuePage = 0; }
        var issues = CardIssues(card.Id, deckCardSection);
        var bounds = new Rectangle(1144, 687, 410, 89);
        if (issues.Length == 0) {
            canvas.Text("Color identity: " + DeckColorIdentity(card.ColorIdentity), 1152, 699, Muted, .67f, 394, 1);
            if (CommanderIdentity is { } colors) canvas.Text("Commander colors: " + DeckColorIdentity(colors), 1152, 732, Muted, .65f, 394, 1);
            return;
        }
        canvas.Panel(bounds, new(45, 35, 31), Gold * .55f, 5);
        var parts = IssueTextPages(issues, 72);
        deckCardIssuePage = Math.Clamp(deckCardIssuePage, 0, parts.Length - 1);
        var part = parts[deckCardIssuePage];
        renderedDeckIssueCodes.Add(part.Issue.Code);
        canvas.Text(part.Text, 1153, 693, Gold, .65f, 390, 3);
        if (parts.Length > 1) {
            canvas.Text($"Reason {deckCardIssuePage + 1} / {parts.Length}", 1154, 756, Muted, .53f, 263, 1);
            Button("deck-card-issue-prev", "<", 1456, 751, 38, () => deckCardIssuePage--, deckCardIssuePage > 0, height: 22);
            Button("deck-card-issue-next", ">", 1504, 751, 38, () => deckCardIssuePage++, deckCardIssuePage + 1 < parts.Length, height: 22);
        } else if (part.Issue.Code == "COLOR_IDENTITY" && CommanderIdentity is { } colors)
            canvas.Text("Card: " + DeckColorIdentity(card.ColorIdentity) + "   Commander: " + DeckColorIdentity(colors), 1153, 752, Muted, .57f, 390, 1);
    }
    private static string DeckColorIdentity(int colors) => colors == 0 ? "Colorless" : string.Join(" / ", new[] { (1, "W"), (2, "U"), (4, "B"), (8, "R"), (16, "G") }.Where(color => (colors & color.Item1) != 0).Select(color => color.Item2));
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
