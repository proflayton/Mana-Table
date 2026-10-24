using System.Collections.Immutable;
using Mana.Contracts;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private Rectangle? deckIssueBounds;
    private readonly HashSet<string> renderedDeckIssueCodes = [];
    private readonly HashSet<string> renderedDeckIssueCardIds = [];
    private string shownDeckIssueCard = "";
    private void DrawDeckEditor()
    {
        var deck = selectedDeckDetails!;
        deckIssueBounds = null; renderedDeckIssueCodes.Clear(); renderedDeckIssueCardIds.Clear();
        if (DeckSelection is { } selected) resources.Card(selected.Card);
        canvas.Text(deck.Name, 48, 84, Ink, 1.3f, 700, 1, true);
        MenuAction("deck-options", "Deck options ▾", new(770, 98, 180, 36), new DeckIntent.OpenDeckOptions(), !busy, size: .7f);
        MenuAction("deck-export", "Copy list", new(966, 98, 125, 36), new DeckIntent.Export(), !busy && DeckCanCopy, size: .7f);
        DrawDeckSaveStatus(1100, 98, 170);
        MenuAction("deck-done", "Back to play", new(1280, 98, 272, 36), new DeckIntent.CloseEditor(), !busy, size: .8f);
        int main = deck.Entries.Where(e => e.Section == "Main").Sum(e => e.Quantity), leaders = deck.Entries.Where(e => e.Section == "Commander").Sum(e => e.Quantity);
        int side = deck.Entries.Where(e => e.Section == "Sideboard").Sum(e => e.Quantity);
        bool countIssue = DeckIssues.Any(issue => issue.Code is "MAIN_COUNT" or "COMMANDER_COUNT" or "MISSING_COMMANDER");
        if (CommanderDeck) DrawDeckCommanders();
        if (workshopTab != "analysis" || !analysisOpen) {
            canvas.Text(CommanderDeck ? $"{main + leaders} / 100 cards" : $"{main} cards · {deck.Format}", 50, 153, countIssue ? Gold : Ink, .85f, 230, 1, countIssue);
            if (!CommanderDeck) canvas.Text($"{main} main   ·   {side} sideboard", 294, 153, Muted, .8f, 980, 1);
            Button("deck-undo", "Undo", 1320, 151, 110, new DeckIntent.Undo(), !busy && deck.CanUndo, height: 32);
            Button("deck-redo", "Redo", 1440, 151, 110, new DeckIntent.Redo(), !busy && deck.CanRedo, height: 32);
        }
        var names = deck.Entries.Where(e => e.Section == "Commander").Select(e => e.Card.Name).ToArray();
        if (WorkshopEngine != null) {
            DrawWorkshopNavigation();
            if (workshopTab != "build") {
                if (workshopTab == "tokens") DrawDeckTokens(); else if (workshopTab == "test") DrawWorkshopTest(); else if (workshopTab == "analysis") DrawDeckAnalysis(); else DrawWorkshopResults();
                DrawWorkshopStatus(); DrawWorkshopDialog(); return;
            }
        } else {
            canvas.Text(names.Length == 0 ? "Choose your commander from the deck's Leaders tab, or find one in the library." : "COMMANDER  " + string.Join(" + ", names), 50, 194, names.Length == 0 ? Muted : Gold, .78f, TokenEngine != null ? 1100 : 1480, 1);
            if (TokenEngine != null) Button("workshop-tokens", "Tokens", 1372, 188, 180, new DeckIntent.ShowWorkshopTab(WorkshopTab.Tokens), !busy, height: 32);
        }
        if (WorkshopEngine == null && workshopTab == "tokens" && TokenEngine != null) { DrawDeckTokens(); DrawWorkshopDialog(); return; }
        if (WorkshopEngine == null && services.Analysis != null) {
            Button("deck-analysis", "Analyze deck", TokenEngine != null ? 1184 : 1372, 188, 180, new DeckIntent.OpenAnalysis(), !busy, height: 32);
            if (analysisOpen) { DrawDeckAnalysis(); DrawWorkshopDialog(); return; }
        }
        canvas.Fill(new(32, 236, 1536, 712), new Color(10, 19, 25) * .7f);
        canvas.Line(new(578, 246), new(578, 936), LobbyTrim * .65f, 1);
        canvas.Line(new(1122, 246), new(1122, 936), LobbyTrim * .65f, 1);
        DrawCardLibrary(); DrawEditableDeck(); DrawDeckCardPreview();
        string status = DeckSaveProblem.Length > 0 ? "Could not save: " + DeckSaveProblem
            : "Changes save automatically. Deck checks update as you edit.";
        if (InlineActivityVisible) DrawInlineActivity();
        else canvas.Text(status, 46, 963, DeckSaveProblem.Length > 0 ? Red : Muted, .68f, 1250, 1);
        if (DeckSaveProblem.Length > 0) Button("deck-retry-save", "Retry save", 1340, 955, 214, new DeckIntent.Save(), !busy, height: 33);
        if (deckMenu.Length > 0) DrawDeckMenu();
        DrawWorkshopStatus(); DrawWorkshopDialog();
    }
    private void DrawDeckSaveStatus(int x, int y, int width)
    {
        string label = DeckSaveLabel;
        bool pending = label == "Saving…", failed = DeckSaveProblem.Length > 0;
        Color color = failed ? Red : pending ? Gold : ReadyColor;
        if (pending) DrawLoadingIndicator(new(x + 9, y + 18), 6, true, color);
        else if (!failed) { canvas.Line(new(x + 2, y + 18), new(x + 7, y + 23), color, 2); canvas.Line(new(x + 7, y + 23), new(x + 16, y + 12), color, 2); }
        else canvas.Text("!", x + 5, y + 8, color, .75f, 20, 1, true);
        canvas.Text(label, x + 24, y + 6, color, .78f, width - 24, 1, true);
    }
    private void DrawDeckCommanders()
    {
        var deck = selectedDeckDetails!;
        var leaders = deck.Entries.Where(entry => entry.Section == "Commander").ToArray();
        if (leaders.Length == 0) {
            MenuAction("deck-choose-commander", "Choose your commander", new(294, 142, 980, 43), new DeckIntent.ChooseCommander(), !busy, true, .82f);
            return;
        }
        canvas.Text(leaders.Length == 1 ? "COMMANDER" : "PAIRED COMMANDERS", 50, 133, Gold, .52f, 230, 1, true);
        int nameLines = (int)Math.Ceiling(2 * preferences.TextScale);
        for (int i = 0; i < Math.Min(2, leaders.Length); i++) {
            var entry = leaders[i]; int x = 294 + i * 488;
            canvas.Text(entry.Card.Name, x, 134, Ink, .78f, 470, nameLines, true);
            if (!busy) hits.Add(new("deck-leader:" + entry.Id, Scope, new(x, 132, 470, 56), new DeckIntent.SelectRow(entry)));
        }
        if (leaders.Length == 1) {
            if (CanFindPartner)
                MenuAction("deck-choose-partner", CompatiblePartnerIds.Length > 0 ? "+ Choose a partner" : "+ Find a partner", new(782, 145, 470, 38), new DeckIntent.ChooseCommander(true), !busy, size: .72f);
            else canvas.Text("Select another eligible card to change commander.", 782, 156, Muted, .57f, 470, 2);
        }
    }
    private void DeckSearchBox(DeckTextField field, string value, string placeholder, Rectangle bounds)
    {
        string fieldName = field.ToString().ToLowerInvariant();
        bool focus = deckTextFocus == fieldName;
        canvas.Panel(bounds, focus ? new(36, 52, 64) : new(14, 22, 30), focus ? Gold : Muted * .4f, 5);
        canvas.Text(value.Length == 0 ? placeholder : value + (focus && (int)(now * 2) % 2 == 0 ? "|" : ""), bounds.X + 10, bounds.Y + 10, value.Length == 0 ? Muted : focus && deckSelectAll ? Gold : Ink, .7f, bounds.Width - 53, 1);
        hits.Add(new("deck-search-" + fieldName, Scope, bounds, new DeckIntent.FocusText(field)));
        if (value.Length > 0) Button("deck-clear-" + fieldName, "×", bounds.Right - 35, bounds.Y + 3, 31, new DeckIntent.FocusText(field, true), height: bounds.Height - 6);
    }
    private void DrawCardLibrary()
    {
        canvas.Text(partnerCandidates ? "FIND A PARTNER" : "CARD LIBRARY", 46, 252, Gold, .8f, 260, 1, true);
        canvas.Text(catalogRead.View.Value is { } page ? $"{page.Total:N0} matches" : catalogRead.View.Error.Length > 0 ? "Search unavailable" : "Searching…", 310, 253, Muted, .65f, 245, 1);
        DeckSearchBox(DeckTextField.Library, catalogText, "Search names, rules, or creature types…", new(46, 283, 510, 38));
        Button("catalog-type", catalogType == 0 ? "Any type ▾" : CatalogTypes[catalogType] + " ▾", 46, 331, 163, new DeckIntent.OpenCatalogMenu(CatalogFilterMenu.Type), height: 32);
        Button("catalog-mana", catalogMana == 0 ? "MV: Any" : "MV ≤ " + (catalogMana - 1), 217, 331, 96, new DeckIntent.OpenCatalogMenu(CatalogFilterMenu.Mana), height: 32);
        Button("catalog-sort", catalogManaSort ? "Sort: Mana" : "Sort: Name", 321, 331, 133, new DeckIntent.OpenCatalogMenu(CatalogFilterMenu.Sort), height: 32);
        Button("catalog-reset", "Reset", 462, 331, 94, new DeckIntent.ResetCatalog(), height: 32);
        Button("catalog-identity", "Leader colors", 46, 373, 158, new DeckIntent.ToggleCommanderColors(), CommanderIdentity != null, catalogCommanderColors, 30);
        for (int i = 0; i < 7; i++) {
            int color = i, bit = i is >= 1 and <= 5 ? 1 << (i - 1) : 0;
            string label = new[] { "All", "W", "U", "B", "R", "G", "C" }[i];
            bool selected = !catalogCommanderColors && (i == 0 ? catalogColors == null : i == 6 ? catalogColors == 0 : (catalogColors.GetValueOrDefault() & bit) != 0);
            Button("catalog-color:" + label, label, 214 + i * 49, 373, 44, new DeckIntent.ToggleCatalogColor(color), accent: selected, height: 30);
        }
        if (catalogRead.View.Value is { } results) {
            for (int i = 0; i < results.Cards.Length; i++) {
                var card = results.Cards[i]; int x = 50 + i % 2 * 258, y = 419 + i / 2 * 150;
                var pose = new Pose(new(x + 45, y + 64), new(86, 120));
                PaintCard(card.Card, pose, librarySelection?.Id == card.Id, false);
                hits.Add(new("catalog-card:" + card.Id, Scope, new(x, y, 250, 144), new DeckIntent.SelectCatalogCard(card)));
                int copies = selectedDeckDetails!.Entries.Where(e => e.Card.Name == card.Card.Name).Sum(e => e.Quantity);
                canvas.Text(card.Card.Name, x + 100, y + 8, Ink, .65f, 145, 80);
                if (copies > 0) canvas.Text(copies + "×", x + 2, y + 125, Gold, .62f, 45, 1);
                Button("catalog-add:" + card.Id, "+", x + 55, y + 115, 32, new DeckIntent.SelectCatalogCard(card, true), !busy, height: 28);
            }
            if (results.Total == 0) canvas.Text("No cards match. Try fewer filters or a different name.", 69, 470, Muted, .9f, 465, 4);
            Button("catalog-prev", "Previous", 46, 896, 112, new DeckIntent.PageCatalog(-1), results.Offset > 0, height: 32);
            Button("catalog-next", "Next", 166, 896, 94, new DeckIntent.PageCatalog(1), results.Offset + results.Cards.Length < results.Total, height: 32);
            canvas.Text($"{(results.Total == 0 ? 0 : results.Offset + 1)}–{results.Offset + results.Cards.Length} of {results.Total:N0}", 282, 906, Muted, .63f, 272, 1);
        } else if (catalogRead.View.Error.Length > 0) canvas.Text(catalogRead.View.Error + " · Use Reset to retry.", 69, 470, Red, .85f, 465, 5);
        else {
            DrawLoadingIndicator(new(300, 506), 22, true, Blue);
            canvas.CenterText("Finding your cards", new(61, 553, 475, 42), Ink, .95f, true);
            canvas.CenterText("Searching the library with your filters.", new(61, 598, 475, 36), Muted, .72f);
        }
    }
    private void DrawEditableDeck()
    {
        var deck = selectedDeckDetails!;
        canvas.Text(partnerCandidates ? "CHOOSE A PARTNER" : commanderCandidates ? "CHOOSE A COMMANDER" : "YOUR DECK", 600, 252, Gold, .8f, 340, 1, true);
        if (workshopRoleFilter.Length > 0) Button("workshop-clear-role", workshopRoleFilter + " ×", 755, 248, 182, new DeckIntent.ClearRoleFilter(), !busy, true, 28);
        else if (workshopManaFilter is { } manaFilter) Button("workshop-clear-mana", "MV " + (manaFilter == 6 ? "6+" : manaFilter) + " ×", 755, 248, 182, new DeckIntent.ClearManaFilter(), !busy, true, 28);
        else if (deckInteraction.RelatedCards.Count > 0) Button("analysis-clear-cards", "Related cards ×", 755, 248, 182, new DeckIntent.ClearRelatedCards(), !busy, true, 28);
        int affected = deck.Entries.Count(entry => CardIssues(entry.Id, entry.Section).Length > 0);
        Button("deck-issues-filter", "Problems (" + affected + ")", 946, 247, 154, new DeckIntent.ToggleIssues(), affected > 0 || deckIssuesOnly, deckIssuesOnly, 29);
        DeckSearchBox(DeckTextField.Deck, deckFilter, "Find a card in this deck…", new(600, 283, 500, 38));
        var tabs = new List<(string, string, string, bool)> { ("deck-all", "All", "", false), ("deck-main", "Main", "Main", false), ("deck-side", "Side", "Sideboard", false), ("deck-candidates", "Commanders", "", true) };
        if (WorkshopEngine != null) tabs.Add(("deck-considering", "Options", "Considering", false));
        int tabX = 600;
        int[] tabWidths = tabs.Count == 5 ? [58, 75, 70, 158, 123] : [94, 94, 94, 188];
        for (int i = 0; i < tabs.Count; i++) {
            var tab = tabs[i];
            Button(tab.Item1, tab.Item2, tabX, 331, tabWidths[i], new DeckIntent.FilterList(tab.Item3, tab.Item4), true, !deckIssuesOnly && commanderCandidates == tab.Item4 && deckSection == tab.Item3, 32);
            tabX += tabWidths[i] + 4;
        }
        var rows = DeckRowsToShow();
        int rowHeight = DeckRowHeight;
        int rowsPerPage = Math.Max(1, 288 / rowHeight);
        for (int i = 0; i < Math.Min(rowsPerPage, rows.Length - deckCardsPage * rowsPerPage); i++) {
            var entry = rows[deckCardsPage * rowsPerPage + i]; int y = 378 + i * rowHeight;
            bool selected = librarySelection == null && deckCardId == entry.Id && deckCardSection == entry.Section;
            bool problem = CardIssues(entry.Id, entry.Section).Length > 0;
            Color fill = problem ? selected ? new(70, 53, 42) : new(48, 37, 34) : selected ? new(43, 65, 79) : new(29, 39, 50);
            canvas.Rounded(new(600, y, 500, rowHeight - 2), fill, 3);
            if (problem || entry.Section == "Commander") canvas.Fill(new(600, y, 3, rowHeight - 2), Gold);
            canvas.Text(entry.Quantity + "×", 610, y + 7, problem ? Gold : Muted, .64f, 38, 1, problem);
            canvas.Text(entry.Card.Name, 651, y + 6, problem || entry.Section == "Commander" ? Gold : Ink, .66f, 378, 80, selected);
            if (problem) { canvas.Text("!", 1016, y + 6, Gold, .7f, 18, 1, true); renderedDeckIssueCardIds.Add(entry.Id); }
            canvas.Text(entry.Section == "Commander" ? "CMD" : entry.Section == "Sideboard" ? "SIDE" : entry.Section == "Considering" ? "OPT" : entry.ManaValue.ToString(), 1040, y + 9, Muted, .53f, 54, 1);
            hits.Add(new("deck-card:" + entry.Section + ":" + entry.Id, Scope, new(600, y, 500, rowHeight - 2), new DeckIntent.SelectRow(entry)));
        }
        if (rows.Length == 0) canvas.Text(deckIssuesOnly ? "No cards need changes in this view. Turn off Problems to see your full list."
            : partnerCandidates ? "No compatible partner in your list. Find a card in the library and add it; compatibility is checked by the game."
            : commanderCandidates ? "Find your commander in the library, add it, then choose it here." : "No cards in this view. Select a library card and use + to add it.", 620, 419, Muted, .82f, 450, 5);
        Button("deck-cards-prev", "Previous", 600, 674, 105, new DeckIntent.PageDeck(-1), deckCardsPage > 0, height: 30);
        Button("deck-cards-next", "Next", 715, 674, 90, new DeckIntent.PageDeck(1), (deckCardsPage + 1) * rowsPerPage < rows.Length, height: 30);
        canvas.Text($"{rows.Length} entries · {deckCardsPage + 1}/{Math.Max(1, (rows.Length + rowsPerPage - 1) / rowsPerPage)}", 822, 683, Muted, .6f, 278, 1);
        if (WorkshopEngine != null) DrawWorkshopRolePills();
        DrawDeckIssues();
    }
    private void DrawDeckCardPreview()
    {
        var deck = selectedDeckDetails!;
        if (DeckSelection is not { } card) { canvas.Text("Choose a card to read it, add copies, or set its role.", 1170, 422, Muted, 1, 358, 4); return; }
        var preview = DeckCardFace(card.Card, deckBackId == card.Id);
        PaintCard(preview, new Pose(new(1349, 450), new(286, 400)), false, false);
        int copies = deck.Entries.Where(e => e.Card.Name == card.Card.Name).Sum(e => e.Quantity);
        if (services.Printings != null && deckCardSection != "Considering")
            Button("deck-printings", PrintingLabel(card.Card) + " ▾", 1150, 660, WorkshopEngine != null ? 238 : 398, new DeckIntent.OpenPrintings(), !busy, height: 28);
        if (WorkshopEngine != null) {
            var role = deckWorkspace.View.Workshop?.Roles.FirstOrDefault(r => r.CardId == card.Id);
            string label = (role?.Pinned == true ? "★ " : "") + (role?.Roles.Length > 0 ? string.Join(" · ", role.Roles) : "Set categories / keep / Considering") + "  >";
            bool printings = services.Printings != null && deckCardSection != "Considering";
            Button("workshop-card-tools", printings ? "Card plan >" : label, printings ? 1396 : 1150, 660, printings ? 152 : 398, new DeckIntent.OpenCardTools(), !busy && WorkshopCurrent, height: 28);
        } else if (services.Printings == null) canvas.Text(card.Edition + " · " + deckCardSection + " · " + copies + (copies == 1 ? " copy in list" : " copies in list"), 1150, 663, Muted, .63f, 398, 1);
        DrawSelectedDeckIssues(card);
        int count = DeckQuantity(); bool editable = deckCardSection is "Main" or "Sideboard" or "Considering";
        Button("deck-remove", "−", 1150, 784, 46, new DeckIntent.ChangeQuantity(-1), !busy && editable && count > 0, height: 36);
        Button("deck-quantity", count + (count == 1 ? " copy" : " copies"), 1204, 784, 122, new DeckIntent.EnterQuantity(), !busy && editable, CardIssues(card.Id, deckCardSection).Any(issue => issue.Code == "CARD_COPY_LIMIT"), 36);
        Button("deck-add", "+", 1334, 784, 46, new DeckIntent.ChangeQuantity(1), !busy && editable && count < 1000, true, 36);
        Button("deck-move", deckCardSection == "Considering" ? "To main" : deckCardSection == "Sideboard" ? "To main" : "To side", 1388, 784, 160, new DeckIntent.MoveCard(), !busy && editable, height: 36);
        if (card.Card.OtherFace != null) Button("deck-flip", deckBackId == card.Id ? "Front face" : "Other face", 1364, 826, 184, new DeckIntent.FlipCard(), height: 26);
        canvas.Text(card.Card.OtherFace != null ? "Click count to set quantity" : editable ? "+ adds one · Click the count to enter a quantity" : "Commanders count toward your 100-card deck.", 1150, 830, Muted, .59f, card.Card.OtherFace != null ? 204 : 408, 1);
        if (!CommanderDeck) { canvas.Text("Commander controls apply to Commander decks.", 1150, 876, Muted, .66f, 398, 2); return; }
        var ids = deck.CommanderIds;
        bool eligible = deck.CommanderOptions.Any(c => c.Id == card.Id);
        bool partner = ids.Length == 1 && deck.CommanderOptions.FirstOrDefault(c => c.Id == ids[0])?.Partners.Contains(card.Id) == true;
        if (partner && !ids.Contains(card.Id)) {
            Button("deck-add-partner", "Pair with your commander", 1150, 860, 398, new DeckIntent.SetCommanders([.. ids, card.Id]), !busy, true, 38);
            MenuAction("deck-set-commander", "Use as sole commander instead", new(1150, 907, 398, 30), new DeckIntent.SetCommanders([card.Id]), !busy && eligible, size: .64f);
        } else if (ids.Contains(card.Id)) {
            canvas.Text(ids.Length == 2 ? "One of your paired commanders" : "Your commander", 1150, 868, Gold, .72f, 398, 1, true);
            if (ids.Length == 2) MenuAction("deck-set-commander", "Use as sole commander", new(1150, 902, 218, 34), new DeckIntent.SetCommanders([card.Id]), !busy && eligible, size: .61f);
            MenuAction("deck-remove-commander", "To main deck", new(ids.Length == 2 ? 1378 : 1150, 902, ids.Length == 2 ? 170 : 398, 34), new DeckIntent.SetCommanders(ids.Where(id => id != card.Id).ToImmutableArray()), !busy, size: .61f);
        } else if (eligible) Button("deck-set-commander", ids.Length == 0 ? "Choose as commander" : "Replace commander", 1150, 860, 398, new DeckIntent.SetCommanders([card.Id]), !busy, true, 38);
    }
    private void DrawDeckIssues()
    {
        var deck = selectedDeckDetails!;
        var issues = DeckIssues;
        deckIssueBounds = new(600, 748, 500, 196);
        canvas.Line(new(600, 754), new(1100, 754), LobbyTrim);
        canvas.Text("DECK CHECKS", 600, 769, issues.Length > 0 ? Gold : ReadyColor, .7f, 292, 1, true);
        canvas.Text(issues.Length == 0 ? "Structure checked" : issues.Length + (issues.Length == 1 ? " issue" : " issues"), 903, 771, issues.Length > 0 ? Gold : ReadyColor, .62f, 197, 1);
        if (issues.Length == 0) {
            canvas.Text(Mana.Magic.DeckExperience.EligibilityLabel(deck), 600, 808, deck.Eligibility == "bundled-rules" ? Muted : Gold, .65f, 500, 1);
            canvas.Text("Offline snapshot; no current online ban-list check.", 600, 837, Muted, .52f, 500, 1);
            DrawDeckComposition(deck, 600, 860, 500); return;
        }
        var parts = IssueTextPages(issues, 90);
        for (int i = 0; i < Math.Min(1, parts.Length - deckIssuePage); i++) {
            int index = deckIssuePage + i, y = 798;
            var part = parts[index];
            renderedDeckIssueCodes.Add(part.Issue.Code);
            canvas.Text("!", 600, y, Gold, .72f, 16, 1, true);
            canvas.Text(part.Text, 619, y + 1, Ink, .61f, 456, 3);
            bool actionable = part.Issue.CardIds.Length > 0 || part.Issue.Code is "COMMANDER_COUNT" or "MISSING_COMMANDER";
            if (actionable) {
                canvas.Text(">", 1085, y + 2, Gold, .67f, 15, 1, true);
                hits.Add(new("deck-issue:" + index, Scope, new(600, y, 500, 54), new DeckIntent.ShowIssue(part.Issue)));
            }
        }
        Button("deck-issues-prev", "<", 1008, 764, 40, new DeckIntent.PageIssues(-1), deckIssuePage > 0, height: 26);
        Button("deck-issues-next", ">", 1060, 764, 40, new DeckIntent.PageIssues(1), deckIssuePage + 1 < parts.Length, height: 26);
        DrawDeckComposition(deck, 600, 852, 500);
    }
    private void DrawSelectedDeckIssues(CatalogCard card)
    {
        var issues = CardIssues(card.Id, deckCardSection);
        var bounds = new Rectangle(1144, 687, 410, 89);
        if (issues.Length == 0) {
            canvas.Text("Color identity: " + DeckColorIdentity(card.ColorIdentity), 1152, 699, Muted, .67f, 394, 1);
            if (CommanderIdentity is { } colors) canvas.Text("Commander colors: " + DeckColorIdentity(colors), 1152, 732, Muted, .65f, 394, 1);
            return;
        }
        canvas.Panel(bounds, new(45, 35, 31), Gold * .55f, 5);
        var parts = IssueTextPages(issues, 72);
        var part = parts[deckCardIssuePage];
        renderedDeckIssueCodes.Add(part.Issue.Code);
        canvas.Text(part.Text, 1153, 693, Gold, .65f, 390, 3);
        if (parts.Length > 1) {
            canvas.Text($"Reason {deckCardIssuePage + 1} / {parts.Length}", 1154, 756, Muted, .53f, 263, 1);
            Button("deck-card-issue-prev", "<", 1456, 751, 38, new DeckIntent.PageIssues(-1, true), deckCardIssuePage > 0, height: 22);
            Button("deck-card-issue-next", ">", 1504, 751, 38, new DeckIntent.PageIssues(1, true), deckCardIssuePage + 1 < parts.Length, height: 22);
        } else if (part.Issue.Code == "COLOR_IDENTITY" && CommanderIdentity is { } colors)
            canvas.Text("Card: " + DeckColorIdentity(card.ColorIdentity) + "   Commander: " + DeckColorIdentity(colors), 1153, 752, Muted, .57f, 390, 1);
    }
    private static string DeckColorIdentity(int colors) => colors == 0 ? "Colorless" : string.Join(" / ", new[] { (1, "W"), (2, "U"), (4, "B"), (8, "R"), (16, "G") }.Where(color => (colors & color.Item1) != 0).Select(color => color.Item2));
    private void DrawDeckMenu()
    {
        string kind = deckMenu;
        if (kind == "deck-actions") {
            hits.Clear(); hits.Add(new("deck-menu-dismiss", Scope, new(0, 64, 1600, 936), new DeckIntent.CloseCatalogMenu()));
            canvas.Panel(new(768, 140, 328, 150), new(16, 24, 34), Gold, 6);
            MenuAction("deck-rename", "Rename deck", new(780, 149, 304, 36), new DeckIntent.Rename(), !busy, size: .75f);
            MenuAction("deck-duplicate", "Duplicate deck", new(780, 190, 304, 36), new DeckIntent.Duplicate(), !busy, size: .75f);
            if (selectedDeckDetails is { LibraryToken.Length: > 0 } deck && DeckLibrary != null)
                MenuAction("deck-delete:" + deck.Id, "Delete deck", new(780, 231, 304, 36), new DeckIntent.Delete(deck.Id, deck.LibraryToken, deckLibraryGeneration), CanManageDeckLibrary && DeckSaveProblem.Length == 0, size: .75f);
            return;
        }
        string[] options = kind == "type" ? CatalogTypes.Select(t => t.Length == 0 ? "Any type" : t).ToArray()
            : kind == "mana" ? Enumerable.Range(0, 10).Select(i => i == 0 ? "Any mana value" : "Mana value ≤ " + (i - 1)).ToArray() : ["Name", "Mana value"];
        int x = kind == "type" ? 46 : kind == "mana" ? 217 : 321;
        hits.Clear(); hits.Add(new("deck-menu-dismiss", Scope, new(0, 64, 1600, 936), new DeckIntent.CloseCatalogMenu()));
        canvas.Panel(new(x, 367, 238, options.Length * 36 + 12), new(16, 24, 34), Gold, 6);
        for (int i = 0; i < options.Length; i++) {
            int index = i;
            Button("catalog-option:" + kind + ":" + i, options[i], x + 6, 373 + i * 36, 226, new DeckIntent.SelectCatalogFilter(Enum.Parse<CatalogFilterMenu>(kind, true), index), accent: kind == "type" ? i == catalogType : kind == "mana" ? i == catalogMana : (i == 1) == catalogManaSort, height: 32);
        }
    }
}
