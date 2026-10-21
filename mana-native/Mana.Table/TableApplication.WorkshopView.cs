using Mana.Contracts;
using Mana.Renderer;
using Color = Microsoft.Xna.Framework.Color;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private void DrawWorkshopRolePills()
    {
        string[] roles = ["Lands", "Ramp", "Draw", "Interaction"];
        int[] positions = [600, 716, 828, 940], widths = [108, 104, 104, 160];
        for (int i = 0; i < roles.Length; i++) {
            string role = roles[i]; string count = workshop?.Insights.RoleCounts.GetValueOrDefault(role).ToString() ?? "...";
            Button("workshop-build-role:" + role, role + " " + count, positions[i], 714, widths[i], () => {
                workshopRoleFilter = workshopRoleFilter == role ? "" : role; workshopManaFilter = null; analysisRelatedCards.Clear();
                commanderCandidates = false; deckSection = "Main"; deckCardsPage = 0; deckIssuesOnly = false;
            }, !busy && WorkshopCurrent, workshopRoleFilter == role, 28);
        }
    }
    private void DrawWorkshopNavigation()
    {
        Button("workshop-build", "Build", 48, 190, 100, () => ShowWorkshopTab("build"), !busy, workshopTab == "build", 32);
        if (engine is IDeckAnalysis) Button("deck-analysis", "Analyze", 158, 190, 134, OpenDeckAnalysis, !busy && WorkshopCurrent, analysisOpen, 32);
        Button("workshop-test", "Test", 302, 190, 100, () => ShowWorkshopTab("test"), !busy, workshopTab == "test", 32);
        Button("workshop-results", "Results", 412, 190, 116, () => ShowWorkshopTab("results"), !busy, workshopTab == "results", 32);
        Button("workshop-insights", "Insights", 538, 190, 126, () => { workshopDialog = "insights"; WorkshopDialogPage = 0; }, !busy && WorkshopCurrent, height: 32);
        string version = workshop?.Versions.FirstOrDefault(v => v.Id == workshop.VersionId)?.Label ?? "";
        string info = workshopError.Length > 0 ? "Deck tools unavailable" : !WorkshopCurrent ? "Loading deck tools..." : version + (workshop!.Plan.Length == 0 ? "  ·  Add your deck's plan" : "  ·  " + workshop.Plan);
        if (WorkshopCurrent) Button("workshop-plan-summary", info + "  >", 676, 190, 478, () => EditWorkshopPlan(false), !busy, height: 32);
        else canvas.Text(info, 676, 199, workshopError.Length > 0 ? Red : Muted, .62f, 478, 1);
        Button("workshop-source", workshop?.Source == null ? "Link Archidekt" : "Linked source", 1174, 190, 184, () => { if (workshop?.Source == null) ImportArchidekt(); else { workshopDialog = "source"; WorkshopDialogPage = 0; } }, !busy && WorkshopCurrent, height: 32);
        Button("workshop-history", "Versions", 1368, 190, 184, () => { workshopDialog = "versions"; WorkshopDialogPage = 0; }, !busy && WorkshopCurrent, height: 32);
    }
    private void DrawWorkshopStatus()
    {
        if (workshopTab != "build" && InlineActivityVisible) DrawInlineActivity();
        if (workshopError.Length == 0) return;
        canvas.Text(workshopError, 48, 954, Red, .63f, 1240, 2);
        Button("workshop-retry", "Retry deck tools", 1338, 953, 214, () => RequestWorkshopRefresh(), !busy, height: 34);
    }
    private void DrawWorkshopTest()
    {
        canvas.Panel(new(32, 236, 1036, 712), new(21, 29, 39), LobbyTrim, 8);
        canvas.Panel(new(1084, 236, 484, 712), new(21, 29, 39), LobbyTrim, 8);
        canvas.Text("ASK SOMETHING OF YOUR DECK", 52, 255, Gold, .74f, 855, 1, true);
        canvas.Text(workshop?.Question is { Length: > 0 } question ? question : "For example: Can I make my first three land drops?", 54, 294, Ink, .85f, 980, 2);
        canvas.Text("Question outcomes are manual notes; aggregate counts do not measure land drops by turn.", 54, 332, Muted, .61f, 980, 1);
        Button("workshop-question", "Set test question", 54, 365, 224, () => EditWorkshopPlan(true), !busy && WorkshopCurrent, height: 33);
        Button("workshop-plan", "Deck plan", 288, 365, 180, () => EditWorkshopPlan(false), !busy && WorkshopCurrent, height: 33);
        canvas.Line(new(54, 409), new(1046, 409), LobbyTrim);
        canvas.Text("OPENING HAND SAMPLER", 54, 430, Gold, .77f, 860, 1, true);
        canvas.Text("Draw-only practice. Redraw shuffles a fresh seven; this does not enforce London mulligans.", 54, 465, Muted, .68f, 977, 2);
        if (workshopHand is { } hand) {
            int handPage = Math.Clamp(workshopPage, 0, Math.Max(0, (hand.Hand.Length - 1) / 7));
            for (int i = 0; i < Math.Min(7, hand.Hand.Length - handPage * 7); i++) {
                var card = hand.Hand[handPage * 7 + i]; int x = 58 + i * 141;
                PaintCard(card.Card, new Pose(new(x + 65, 619), new(126, 176)), false, false);
                canvas.Text(card.Card.Name, x, 720, Ink, .54f, 131, 2);
                hits.Add(new("workshop-hand-card:" + i, Scope, new(x, 531, 131, 225), () => { librarySelection = card; deckCardId = null; deckCardSection = "Main"; workshopTab = "build"; }));
            }
            canvas.Text($"{hand.Hand.Length} in hand  ·  {hand.LibraryCount} in library  ·  {hand.Draws} draws  ·  {hand.Redraws} redraws", 54, 775, Muted, .67f, 860, 1);
            if (hand.Hand.Length > 7) {
                Button("workshop-hand-prev", "<", 946, 766, 42, () => workshopPage--, handPage > 0, height: 28);
                Button("workshop-hand-next", ">", 998, 766, 42, () => workshopPage++, (handPage + 1) * 7 < hand.Hand.Length, height: 28);
            }
            bool active = hand.Status is "active" or "open" or "running";
            Button("workshop-hand-draw", "Draw a card", 54, 821, 178, () => PracticeWorkshop("draw"), !busy && active && hand.LibraryCount > 0 && WorkshopCurrent, height: 40);
            Button("workshop-hand-redraw", "Redraw seven", 242, 821, 192, () => PracticeWorkshop("mulligan"), !busy && active && WorkshopCurrent, height: 40);
            Button("workshop-hand-keep", "Record keep", 444, 821, 178, () => PracticeWorkshop("keep"), !busy && active && WorkshopCurrent, true, 40);
            Button("workshop-hand-new", "New sample", 632, 821, 186, () => PracticeWorkshop("new"), !busy && WorkshopCurrent, height: 40);
            canvas.Text(active ? "Record keep when you have learned what you need. Add a note in Results." : "Sample recorded for its exact deck version. Start a new sample or review it in Results.", 54, 883, Muted, .68f, 980, 2);
        } else {
            canvas.Text("See how your real library opens.", 70, 572, Ink, 1.14f, 910, 1, true);
            canvas.Text("Use draws to explore a hand, then record your decision and what you noticed.", 70, 627, Muted, .9f, 900, 3);
            Button("workshop-hand-new", "Draw a sample hand", 70, 745, 386, () => PracticeWorkshop("new"), !busy && WorkshopCurrent && selectedDeckDetails!.Entries.Any(e => e.Section == "Main"), true, 52);
        }
        DrawWorkshopTableSetup();
    }
    private void DrawWorkshopTableSetup()
    {
        canvas.Text("TEST AT A REAL TABLE", 1106, 255, Gold, .74f, 433, 1, true);
        canvas.Text("Forge rules. You play your deck; AI takes the other seats.", 1106, 294, Muted, .79f, 431, 3);
        int[] sizes = (workshopSetup?.MaxPlayers ?? (CommanderDeck ? 4 : 2)) <= 2 ? [2] : [2, 4];
        for (int i = 0; i < sizes.Length; i++) {
            int players = sizes[i];
            Button("workshop-players:" + players, players + " players", 1106 + i * 222, 392, 208, () => workshopPlayers = players, !busy, workshopPlayers == players, 36);
        }
        if (workshopSetup is { Opponents.Length: > 0 } setup) {
            for (int i = 0; i < workshopPlayers - 1; i++) {
                int seat = i, y = 451 + i * 57;
                var opponent = setup.Opponents[workshopOpponents[i]];
                canvas.Text("AI " + (i + 1), 1110, y + 2, Gold, .57f, 63, 1, true);
                Button("workshop-opponent-select:" + i, opponent.Name, 1172, y - 3, 273, () => { workshopOpponentSeat = seat; workshopOpponentSearch = ""; workshopDialog = "opponents"; WorkshopDialogPage = 0; }, !busy, height: 40);
                Button("workshop-opponent-prev:" + i, "<", 1452, y - 1, 38, () => workshopOpponents[seat] = (workshopOpponents[seat] + setup.Opponents.Length - 1) % setup.Opponents.Length, !busy, height: 29);
                Button("workshop-opponent-next:" + i, ">", 1500, y - 1, 38, () => workshopOpponents[seat] = (workshopOpponents[seat] + 1) % setup.Opponents.Length, !busy, height: 29);
            }
        } else Button("workshop-prepare", "Choose AI opponents", 1106, 455, 430, PrepareWorkshopTable, !busy && DeckReady && !Connected, height: 42);
        canvas.Text(Connected ? "Leave your friends table from setup to start an AI test. Hand sampling is still available." : DeckReady ? "This test saves its deck version before the game starts." : "Finish the live deck checks in Build to start a real game. Hand sampling still works for drafts.", 1106, 758, DeckReady && !Connected ? Muted : Gold, .75f, 429, 3);
        Button("workshop-start-game", "Start test game", 1106, 855, 430, StartWorkshopTable, !busy && !Connected && DeckReady && WorkshopCurrent && workshopSetup is { Opponents.Length: > 0 }, true, 52);
    }
    private string WorkshopVersionLabel(string id) => workshop?.Versions.FirstOrDefault(v => v.Id == id)?.Label ?? id[..Math.Min(8, id.Length)];
    private static string WorkshopTime(long timestamp) => DateTimeOffset.FromUnixTimeMilliseconds(timestamp).ToLocalTime().ToString("MMM d · h:mm tt");
    private void DrawWorkshopResults()
    {
        canvas.Panel(new(32, 236, 476, 712), new(21, 29, 39), LobbyTrim, 8);
        canvas.Panel(new(524, 236, 1044, 712), new(21, 29, 39), LobbyTrim, 8);
        canvas.Text("YOUR TESTS", 52, 255, Gold, .78f, 420, 1, true);
        int currentTests = workshopTests.Count(t => t.VersionId == workshop?.VersionId);
        canvas.Text(currentTests == 0 ? "Current version: no tests yet" : $"Current version: {currentTests} test{(currentTests == 1 ? "" : "s")}", 54, 294, currentTests == 0 ? Gold : Muted, .74f, 425, 2);
        workshopPage = Math.Clamp(workshopPage, 0, Math.Max(0, (workshopTests.Length - 1) / 6));
        for (int i = 0; i < Math.Min(6, workshopTests.Length - workshopPage * 6); i++) {
            var test = workshopTests[workshopPage * 6 + i]; int y = 355 + i * 79;
            canvas.Panel(new(50, y, 440, 68), workshopSelectedTest == test.Id ? new(37, 59, 75) : new(27, 38, 49), workshopSelectedTest == test.Id ? Blue : LobbyTrim, 5);
            canvas.Text((test.Kind == "hand" ? "Hand sample" : "AI game") + "  ·  " + WorkshopVersionLabel(test.VersionId), 64, y + 10, Ink, .72f, 410, 1, true);
            canvas.Text(WorkshopTime(test.Started) + "  ·  " + test.Status, 64, y + 39, Muted, .6f, 410, 1);
            hits.Add(new("workshop-test-record:" + test.Id, Scope, new(50, y, 440, 68), () => workshopSelectedTest = test.Id));
        }
        Button("workshop-tests-prev", "Previous", 54, 867, 150, () => workshopPage--, workshopPage > 0, height: 32);
        Button("workshop-tests-next", "Next", 214, 867, 120, () => workshopPage++, (workshopPage + 1) * 6 < workshopTests.Length, height: 32);
        if (workshopTests.FirstOrDefault(t => t.Id == workshopSelectedTest) is not { } selected) {
            canvas.Text("Start with a question.", 570, 365, Ink, 1.65f, 917, 1, true);
            canvas.Text("Sample a hand or play a real AI game. Your observations stay attached to the version you tested, even after editing or refreshing Archidekt.", 572, 440, Muted, 1, 882, 5);
            Button("workshop-results-test", "Choose a test", 574, 637, 400, () => ShowWorkshopTab("test"), !busy, true, 52); return;
        }
        canvas.Text(selected.Kind == "hand" ? "Opening hand sample" : "AI game observations", 550, 257, Ink, 1.16f, 970, 1, true);
        canvas.Text(Mana.Magic.DeckExperience.ResultLabel(selected), 552, 333, selected.Status is "conceded" or "interrupted" or "error" ? Gold : Ink, .82f, 950, 1, true);
        canvas.Text(WorkshopVersionLabel(selected.VersionId) + (selected.VersionId == workshop?.VersionId ? " | Current playable list" : " | Earlier playable list"), 552, 302, selected.VersionId == workshop?.VersionId ? Blue : Gold, .75f, 960, 1);
        canvas.Text(selected.Question.Length == 0 ? "No test question was set." : selected.Question, 552, 368, Ink, .88f, 949, 2, true);
        string opponents = selected.Opponents.Length == 0 ? "" : "AI decks: " + string.Join(", ", selected.Opponents.Select(id => workshopSetup?.Opponents.FirstOrDefault(o => o.Id == id)?.Name ?? presets.FirstOrDefault(p => "preset:" + p.Id == id)?.Name ?? (id == "green" ? "Verdant" : id == "red" ? "Cinder" : id)));
        canvas.Text("Question answer: not measured automatically. Record turn-by-turn evidence in your notes.", 552, 440, Gold, .64f, 950, 2);
        canvas.Text(opponents, 552, 487, Muted, .62f, 950, 1);
        if (selected.Kind == "hand" && selected.Hand.Length > 0) Button("workshop-review-saved-hand", "Review saved hand", 1128, 487, 406, () => { workshopDialog = "saved-hand"; WorkshopDialogPage = 0; }, !busy, height: 40);
        int observation = 0;
        foreach (var pair in selected.Observations.Take(9)) {
            int x = 552 + observation % 3 * 324, y = 538 + observation / 3 * 58;
            canvas.Text(pair.Value.ToString(), x, y, Ink, 1.1f, 76, 1, true);
            canvas.Text(ObservationLabel(pair.Key), x + 67, y + 7, Muted, .64f, 242, 2); observation++;
        }
        canvas.Text(selected.Note.Length == 0 ? "Your notes: What worked, what felt awkward, and what would you test next?" : "Your notes: " + selected.Note, 552, 735, selected.Note.Length == 0 ? Muted : Ink, .77f, 947, 4);
        Button("workshop-test-note", selected.Note.Length == 0 ? "Add a note" : "Edit note", 552, 868, 210, () => SaveWorkshopTestNote(selected), !busy, height: 40);
        Button("workshop-view-tested", "View tested deck / compare", 778, 868, 390, () => InspectWorkshopVersion(selected.Id, selected.VersionId), !busy && engine is IDeckVersionHistory, height: 40);
        Button("workshop-results-build", "Tune deck", 1180, 868, 156, () => ShowWorkshopTab("build"), !busy, height: 40);
        Button("workshop-results-again", "Test again", 1348, 868, 188, () => ShowWorkshopTab("test"), !busy, true, 40);
        canvas.Text("Observed counts and your notes describe this test. Small AI samples do not establish a human matchup win rate.", 552, 925, Muted, .57f, 968, 1);
    }
    private static string ObservationLabel(string key) => key switch {
        "ownTurns" => "own turns started", "cardsDrawn" => "cards drawn", "landsPlayed" => "lands played", "spellsCast" => "spells cast", "commanderCasts" => "commander spells cast", "mulligans" => "mulligans", "draws" => "sample draws", "redraws" => "sample redraws", "handLands" => "lands in saved hand", "handCards" or "handSize" => "cards in saved hand", _ => key
    };
    private void DrawWorkshopDialog()
    {
        if (workshopDialog.Length == 0) return;
        hits.Clear(); hovered = null;
        canvas.Fill(new(0, 64, 1600, 936), Color.Black * .82f);
        canvas.Panel(new(168, 142, 1264, 752), new(22, 32, 44), LobbyTrim, 16);
        string title = workshopDialog switch { "analysis-detail" => "Strategy review details", "analysis-preferences" => "Make this review yours (optional)", "insights" => "Understand your deck", "versions" => "Playable deck versions", "version-view" => "Saved deck — read only", "source-confirm" => "Confirm the whole-list outcome", "card" => "This card in your plan", "swap" => "Choose a card to swap out", "opponents" => "Choose AI opponent " + (workshopOpponentSeat + 1), "source" => "Your Archidekt connection", "source-details" => "Archidekt import details", "saved-hand" => "Your recorded sample hand", "saved-card" => "Inspect a recorded card", _ => "Review the Archidekt deck" };
        if (workshopDialog == "printings") title = "Choose a printing";
        canvas.Text(title, 202, 179, Ink, 1.35f, 1020, 1, true);
        Button("workshop-dialog-close", "Back", 1292, 160, 112, () => { if (workshopDialog == "printings") { ClosePrintings(); return; } workshopDialog = workshopDialog switch { "saved-card" => workshopSavedReturn, "version-view" => workshopVersionReturn, "source-confirm" or "source-details" => "source-preview", "swap" => "card", _ => "" }; }, !busy, height: 36);
        switch (workshopDialog) {
            case "printings": DrawPrintings(); break;
            case "analysis-detail": DrawAnalysisDetails(); break;
            case "analysis-preferences": DrawAnalysisPreferences(); break;
            case "insights": DrawWorkshopInsights(); break;
            case "versions": DrawWorkshopVersions(); break;
            case "card": DrawWorkshopCardRoles(); break;
            case "swap": DrawWorkshopSwap(); break;
            case "source": DrawWorkshopSource(); break;
            case "source-preview": DrawWorkshopSourcePreview(); break;
            case "source-details": DrawWorkshopSourceDetails(); break;
            case "source-confirm": DrawWorkshopSourceConfirmation(); break;
            case "version-view": DrawWorkshopVersionView(); break;
            case "saved-hand": DrawWorkshopSavedHand(); break;
            case "saved-card": DrawWorkshopSavedCard(); break;
            case "opponents": DrawWorkshopOpponents(); break;
        }
    }
    private void DrawWorkshopInsights()
    {
        if (workshop is not { } state || selectedDeckDetails is not { } deck) return;
        canvas.Text("YOUR PLAN", 206, 246, Gold, .72f, 1110, 1, true);
        canvas.Text(state.Plan.Length == 0 ? "Add a short reminder of what your deck is trying to do." : state.Plan, 208, 280, Ink, .91f, 915, 3);
        Button("workshop-edit-plan", "Edit plan", 1156, 272, 224, () => EditWorkshopPlan(false), !busy && WorkshopCurrent, height: 36);
        canvas.Text("ROLES  ·  Select one to see the cards", 208, 378, Gold, .74f, 1100, 1, true);
        var roles = state.Insights.RoleCounts.ToArray();
        for (int i = 0; i < Math.Min(12, roles.Length); i++) {
            string role = roles[i].Key;
            Button("workshop-role-filter:" + role, role + "  " + roles[i].Value, 208 + i % 4 * 294, 417 + i / 4 * 46, 278, () => {
                workshopRoleFilter = role; workshopManaFilter = null; analysisRelatedCards.Clear(); workshopTab = "build"; workshopDialog = ""; commanderCandidates = false; deckSection = "Main"; deckFilter = ""; deckCardsPage = 0; deckIssuesOnly = false;
            }, !busy, height: 36);
        }
        canvas.Text("Roles can overlap. Estimates are a starting point; select a card to change its categories.", 208, 569, Muted, .68f, 1160, 2);
        DrawDeckComposition(deck, 208, 623, 562);
        canvas.Text("POTENTIAL COLORED SOURCES", 838, 623, Gold, .67f, 540, 1, true);
        canvas.Text(string.Join("    ", state.Insights.ColoredSources.Select(p => p.Key + ": " + p.Value)), 838, 665, Ink, .85f, 535, 2);
        canvas.Text(state.Insights.Note, 838, 730, Muted, .64f, 535, 6);
        Button("workshop-insights-test", "Test a question", 208, 817, 346, () => ShowWorkshopTab("test"), !busy, true, 44);
        canvas.Text("Deck checks stay separate from tuning. A draft can still show its curve and roles.", 586, 832, Muted, .7f, 791, 2);
    }
    private void DrawWorkshopVersions()
    {
        if (workshop is not { } state) return;
        canvas.Text("Versions save playable content. Your notes, roles, and past test records stay with the deck.", 206, 244, Muted, .84f, 1165, 3);
        var versions = state.Versions.Reverse().ToArray();
        WorkshopDialogPage = Math.Clamp(WorkshopDialogPage, 0, Math.Max(0, (versions.Length - 1) / 7));
        for (int i = 0; i < Math.Min(7, versions.Length - WorkshopDialogPage * 7); i++) {
            var version = versions[WorkshopDialogPage * 7 + i]; int y = 336 + i * 59;
            canvas.Rounded(new(204, y, 1190, 50), new(28, 40, 53), 5);
            canvas.Text(version.Label + (version.Id == state.VersionId ? "  ·  Current" : ""), 219, y + 14, version.Id == state.VersionId ? Blue : Ink, .78f, 297, 1, true);
            canvas.Text(WorkshopTime(version.Created) + $"  ·  {version.MainCount} main + {version.CommanderCount} commander", 532, y + 16, Muted, .66f, 463, 1);
            Button("workshop-version-view:" + version.Id, "View / compare", 1010, y + 7, 186, () => InspectWorkshopVersion(null, version.Id), !busy && engine is IDeckVersionHistory, height: 36);
            Button("workshop-restore:" + version.Id, "Restore", 1210, y + 7, 166, () => RestoreWorkshopVersion(version), !busy && version.Id != state.VersionId, height: 36);
        }
        Button("workshop-versions-prev", "Previous", 206, 817, 160, () => WorkshopDialogPage--, WorkshopDialogPage > 0, height: 36);
        Button("workshop-versions-next", "Next", 378, 817, 126, () => WorkshopDialogPage++, (WorkshopDialogPage + 1) * 7 < versions.Length, height: 36);
    }
    private void DrawWorkshopCardRoles()
    {
        if (DeckSelection is not { } card || workshop is not { } state) return;
        var metadata = state.Roles.FirstOrDefault(r => r.CardId == card.Id);
        PaintCard(card.Card, new Pose(new(376, 495), new(270, 378)), false, false);
        canvas.Text(card.Card.Name, 572, 250, Ink, 1.16f, 790, 2, true);
        canvas.Text("Categories describe your plan. One card may have several roles.", 574, 324, Muted, .79f, 792, 3);
        string[] roles = ["Ramp", "Draw", "Interaction", "Lands", "Protection", "Win condition"];
        for (int i = 0; i < roles.Length; i++) {
            string role = roles[i]; bool selected = metadata?.Roles.Contains(role, StringComparer.OrdinalIgnoreCase) == true;
            Button("workshop-card-role:" + role, (selected ? "✓ " : "+ ") + role, 574 + i % 2 * 397, 419 + i / 2 * 52, 380, () => ToggleWorkshopRole(role), !busy && WorkshopCurrent, selected, 40);
        }
        Button("workshop-card-categories", "Edit all categories", 574, 595, 380, EditWorkshopRoles, !busy && WorkshopCurrent, height: 40);
        Button("workshop-card-pin", metadata?.Pinned == true ? "✓ Favorite to keep" : "Mark as favorite to keep", 971, 595, 380, ToggleWorkshopPin, !busy && WorkshopCurrent, metadata?.Pinned == true, 40);
        canvas.Text(metadata == null ? "Role estimates come from card text. You can override them here." : "Category source: " + metadata.Origin + ". Your edits take precedence.", 574, 657, Muted, .7f, 790, 3);
        bool considering = deckCardSection == "Considering";
        bool inMain = selectedDeckDetails!.Entries.Any(e => e.Section == "Main" && e.Id == card.Id);
        Button("workshop-card-consider", considering ? "Move into main deck" : inMain ? "Move to Considering" : "Save to Considering", 574, 753, 380, () => { workshopDialog = ""; if (considering || inMain) MoveConsideringCard(); else ConsideringQuantity(card, 1); }, !busy && WorkshopCurrent && deckCardSection is "Main" or "Considering", true, 44);
        if (considering) Button("workshop-card-swap", "Swap for a main-deck card", 971, 753, 380, () => { workshopDialog = "swap"; WorkshopDialogPage = 0; }, !busy && WorkshopCurrent, height: 44);
        else if (deckCardSection != "Main") Button("workshop-card-save-option", "Save to Considering", 971, 753, 380, () => ConsideringQuantity(card, 1), !busy && WorkshopCurrent, height: 44);
        canvas.Text("Considering is a place for options. Those cards never enter your games or deck checks.", 574, 819, Muted, .71f, 789, 2);
    }
    private void DrawWorkshopSwap()
    {
        if (DeckSelection is not { } candidate || workshop is not { } state || selectedDeckDetails is not { } deck) return;
        canvas.Text("Bring in one " + candidate.Card.Name + ". The selected cut moves to Considering in the same saved edit.", 206, 246, Muted, .86f, 1153, 3);
        var options = deck.Entries.Where(e => e.Section == "Main" && e.Quantity > 0)
            .OrderBy(e => state.Roles.FirstOrDefault(r => r.CardId == e.Id)?.Pinned == true).ThenBy(e => e.Card.Name).ToArray();
        WorkshopDialogPage = Math.Clamp(WorkshopDialogPage, 0, Math.Max(0, (options.Length - 1) / 7));
        for (int i = 0; i < Math.Min(7, options.Length - WorkshopDialogPage * 7); i++) {
            var entry = options[WorkshopDialogPage * 7 + i]; int y = 343 + i * 58;
            bool pinned = state.Roles.FirstOrDefault(r => r.CardId == entry.Id)?.Pinned == true;
            canvas.Rounded(new(206, y, 1176, 49), new(28, 40, 53), 5);
            canvas.Text(entry.Card.Name + (pinned ? "  ·  Favorite to keep" : ""), 220, y + 14, pinned ? Gold : Ink, .75f, 859, 1);
            Button("workshop-swap:" + entry.Id, pinned ? "Choose favorite..." : "Swap this card", 1100, y + 7, 264, () => {
                if (pinned && MessageBox.Show("You marked " + entry.Card.Name + " as a favorite to keep. Swap it out for this test?", "Swap a favorite", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                MoveConsideringCard(entry.Id);
            }, !busy && WorkshopCurrent, height: 35);
        }
        Button("workshop-swap-prev", "Previous", 206, 817, 160, () => WorkshopDialogPage--, WorkshopDialogPage > 0, height: 36);
        Button("workshop-swap-next", "Next", 378, 817, 126, () => WorkshopDialogPage++, (WorkshopDialogPage + 1) * 7 < options.Length, height: 36);
    }
    private void DrawWorkshopSource()
    {
        if (workshop?.Source is not { } source) return;
        canvas.Text(source.Name, 206, 250, Ink, 1.4f, 1160, 2, true);
        canvas.Text(source.Url, 208, 330, Blue, .82f, 1160, 2);
        canvas.Text("Last checked: " + WorkshopTime(source.LastChecked) + "  ·  " + source.Status, 208, 404, Muted, .8f, 1160, 2);
        canvas.Text("Check the source to review changes before applying them. Local edits and past tests are preserved until you choose how to handle the difference.", 208, 486, Ink, .95f, 1116, 4);
        canvas.Text("Keep building in Archidekt and test here. To bring card changes back, copy the playable list into Archidekt. This export includes commander, main deck and sideboard; Considering and categories stay here.", 208, 645, Muted, .84f, 1125, 4);
        Button("workshop-source-check", "Check for changes", 208, 811, 340, RefreshArchidekt, !busy && WorkshopCurrent, true, 48);
        Button("workshop-source-open", "Open in Archidekt", 564, 811, 310, OpenArchidekt, !busy, height: 48);
        Button("workshop-source-export", "Copy playable list", 890, 811, 492, ExportDeck, !busy && DeckCanCopy, height: 48);
    }
    private void DrawWorkshopSourcePreview()
    {
        if (workshopSourcePreview is not { } preview) return;
        bool importing = workshopSourceImport;
        int played = preview.Cards.Where(c => c.Included).Sum(c => c.Quantity), considering = preview.Cards.Where(c => !c.Included).Sum(c => c.Quantity);
        canvas.Text(preview.Name + "  ·  " + preview.Format, 206, 243, Ink, 1.05f, 1160, 2, true);
        canvas.Text($"{played} playable cards  ·  {considering} Considering  ·  Categories preserved", 208, 302, Muted, .75f, 1160, 1);
        var changes = preview.Changes;
        var rows = changes.Length > 0 ? changes.Select(c => (Label: c.Card + "  ·  " + c.Section, Detail: $"Base {c.Baseline}  ·  Here {c.Local}  ·  Source {c.Remote}", Conflict: c.Conflict)).ToArray()
            : preview.Cards.Select(c => (Label: c.Quantity + "× " + c.Card.Card.Name + "  ·  " + (c.Included ? c.Section : "Considering"), Detail: string.Join(", ", c.Categories), Conflict: false)).ToArray();
        WorkshopDialogPage = Math.Clamp(WorkshopDialogPage, 0, Math.Max(0, (rows.Length - 1) / 7));
        for (int i = 0; i < Math.Min(7, rows.Length - WorkshopDialogPage * 7); i++) {
            var row = rows[WorkshopDialogPage * 7 + i]; int y = 354 + i * 45;
            canvas.Rounded(new(206, y, 1176, 40), row.Conflict ? new(62, 45, 34) : new(28, 40, 53), 4);
            canvas.Text((row.Conflict ? "! " : "") + row.Label, 218, y + 11, row.Conflict ? Gold : Ink, .67f, 591, 1);
            canvas.Text(row.Detail, 827, y + 12, Muted, .6f, 541, 1);
        }
        Button("workshop-source-prev", "Previous", 206, 685, 144, () => WorkshopDialogPage--, WorkshopDialogPage > 0, height: 28);
        Button("workshop-source-next", "Next", 362, 685, 108, () => WorkshopDialogPage++, (WorkshopDialogPage + 1) * 7 < rows.Length, height: 28);
        if (preview.Problems.Length + preview.Warnings.Length > 0) Button("workshop-source-details", $"Import details ({preview.Problems.Length + preview.Warnings.Length})", 492, 685, 326, () => { workshopDialog = "source-details"; WorkshopDialogPage = 0; }, !busy, height: 28);
        Button("workshop-source-preview-open", "Open in Archidekt", 1062, 685, 320, OpenArchidektPreview, !busy, height: 28);
        string problem = string.Join(" ", preview.Problems.Concat(preview.Warnings));
        bool conflict = changes.Any(c => c.Conflict);
        string context = problem.Length > 0 ? problem : !importing && !preview.HasChanges ? "No source changes. Your current deck and version are preserved."
            : conflict ? "Both lists changed the same card. Keep your local list, use the source list, or save the source as a separate copy."
            : importing ? "Import creates a local linked deck. Cards in excluded categories stay in Considering." : "Independent edits can merge. Review the counts above before applying.";
        canvas.Text(context, 208, 734, preview.Problems.Length > 0 || conflict ? Gold : Muted, .73f, 1167, 3);
        if (importing) Button("workshop-source-import", "Import linked deck", 914, 817, 468, () => ApplyArchidekt("remote"), !busy && preview.Problems.Length == 0, true, 44);
        else {
            Button("workshop-source-keep-local", "Keep local", 208, 817, 238, () => ApplyArchidekt("local"), !busy && preview.Problems.Length == 0, height: 44);
            Button("workshop-source-use-remote", "Use source", 458, 817, 238, () => ApplyArchidekt("remote"), !busy && preview.Problems.Length == 0, height: 44);
            Button("workshop-source-copy", "Save source copy", 708, 817, 300, () => ApplyArchidekt("copy"), !busy && preview.Problems.Length == 0, height: 44);
            Button("workshop-source-merge", preview.HasChanges ? "Merge changes" : "Accept check", 1020, 817, 362, () => ApplyArchidekt("merge"), !busy && !conflict && preview.Problems.Length == 0, true, 44);
        }
    }
    private void DrawWorkshopSourceDetails()
    {
        if (workshopSourcePreview is not { } preview) return;
        canvas.Text("Every mapping problem must be resolved before import. Your existing deck is preserved.", 206, 245, Muted, .83f, 1160, 3);
        var parts = preview.Problems.Select(p => "Needs attention: " + p).Concat(preview.Warnings.Select(w => "Note: " + w)).SelectMany(p => TextPages(p, ReadingPageSize(180))).ToArray();
        WorkshopDialogPage = Math.Clamp(WorkshopDialogPage, 0, Math.Max(0, (parts.Length - 1) / 7));
        for (int i = 0; i < Math.Min(7, parts.Length - WorkshopDialogPage * 7); i++)
            canvas.Text(parts[WorkshopDialogPage * 7 + i], 208, 337 + i * 63, Ink, .73f, 1164, 3);
        Button("workshop-source-details-prev", "Previous", 208, 817, 160, () => WorkshopDialogPage--, WorkshopDialogPage > 0, height: 36);
        Button("workshop-source-details-next", "Next", 380, 817, 126, () => WorkshopDialogPage++, (WorkshopDialogPage + 1) * 7 < parts.Length, height: 36);
        Button("workshop-source-details-back", "Back to preview", 1034, 817, 348, () => { workshopDialog = "source-preview"; }, !busy, true, 36);
    }
    private void DrawWorkshopSavedHand()
    {
        if (workshopTests.FirstOrDefault(t => t.Id == workshopSelectedTest) is not { } test) return;
        canvas.Text(WorkshopVersionLabel(test.VersionId) + "  ·  " + WorkshopTime(test.Started) + "  ·  " + test.Status, 206, 244, Blue, .85f, 1160, 2);
        canvas.Text("These are the cards recorded with this sample. Select one to read it.", 208, 306, Muted, .82f, 1160, 2);
        WorkshopDialogPage = Math.Clamp(WorkshopDialogPage, 0, Math.Max(0, (test.Hand.Length - 1) / 7));
        for (int i = 0; i < Math.Min(7, test.Hand.Length - WorkshopDialogPage * 7); i++) {
            var card = test.Hand[WorkshopDialogPage * 7 + i]; int x = 210 + i * 168;
            PaintCard(card.Card, new Pose(new(x + 74, 504), new(144, 202)), false, false);
            canvas.Text(card.Card.Name, x, 621, Ink, .64f, 151, 3);
            hits.Add(new("workshop-saved-card:" + i, Scope, new(x, 403, 154, 292), () => { workshopSavedCard = card; workshopSavedBack = false; workshopSavedReturn = "saved-hand"; inspectPage = 0; workshopDialog = "saved-card"; }));
        }
        Button("workshop-saved-hand-prev", "Previous", 208, 817, 160, () => WorkshopDialogPage--, WorkshopDialogPage > 0, height: 36);
        Button("workshop-saved-hand-next", "Next", 380, 817, 126, () => WorkshopDialogPage++, (WorkshopDialogPage + 1) * 7 < test.Hand.Length, height: 36);
    }
    private void DrawWorkshopSavedCard()
    {
        if (workshopSavedCard is not { } entry) return;
        var card = DeckCardFace(entry.Card, workshopSavedBack);
        PaintCard(card, new Pose(new(382, 510), new(275, 385)), false, false);
        canvas.Text(card.Name, 578, 245, Ink, 1.18f, 790, 2, true);
        canvas.Text(card.Type + "  ·  " + card.ManaCost, 580, 320, Gold, .84f, 789, 3);
        var pages = TextPages(card.Text, ReadingPageSize(1150)); inspectPage = Math.Clamp(inspectPage, 0, pages.Length - 1);
        canvas.Text(pages[inspectPage], 580, 411, Ink, .83f, 780, 16);
        if (entry.Card.OtherFace != null) Button("workshop-saved-card-flip", workshopSavedBack ? "Front face" : "Other face", 242, 738, 280, () => { workshopSavedBack = !workshopSavedBack; inspectPage = 0; }, !busy, height: 38);
        Button("workshop-saved-card-prev", "Previous text", 580, 817, 216, () => inspectPage--, inspectPage > 0, height: 36);
        Button("workshop-saved-card-next", "More text", 808, 817, 216, () => inspectPage++, inspectPage + 1 < pages.Length, height: 36);
    }
    private void DrawWorkshopOpponents()
    {
        if (workshopSetup is not { } setup) return;
        canvas.Text(workshopOpponentSearch.Length == 0 ? "Choose an available engine deck for this seat." : "Showing decks matching: " + workshopOpponentSearch, 206, 246, Muted, .84f, 870, 2);
        Button("workshop-opponent-search", "Search decks", 1080, 241, 302, () => {
            string? query = Ask("Find an AI deck", "Search deck names and descriptions", workshopOpponentSearch);
            if (query != null) { workshopOpponentSearch = query.Trim(); WorkshopDialogPage = 0; }
        }, !busy, height: 36);
        var choices = setup.Opponents.Select((opponent, index) => (opponent, index)).Where(o => (o.opponent.Name + " " + o.opponent.Description).Contains(workshopOpponentSearch, StringComparison.OrdinalIgnoreCase)).ToArray();
        WorkshopDialogPage = Math.Clamp(WorkshopDialogPage, 0, Math.Max(0, (choices.Length - 1) / 6));
        for (int i = 0; i < Math.Min(6, choices.Length - WorkshopDialogPage * 6); i++) {
            var choice = choices[WorkshopDialogPage * 6 + i]; int y = 326 + i * 74;
            canvas.Panel(new(206, y, 1176, 63), new(28, 40, 53), LobbyTrim, 5);
            canvas.Text(choice.opponent.Name, 221, y + 10, Ink, .79f, 837, 1, true);
            canvas.Text(choice.opponent.Description, 223, y + 37, Muted, .62f, 834, 1);
            Button("workshop-opponent-deck:" + choice.opponent.Id, workshopOpponents[workshopOpponentSeat] == choice.index ? "Selected" : "Choose", 1102, y + 12, 264, () => { workshopOpponents[workshopOpponentSeat] = choice.index; workshopDialog = ""; }, !busy, workshopOpponents[workshopOpponentSeat] == choice.index, 38);
        }
        if (choices.Length == 0) canvas.Text("No available decks match. Try a different name or clear the search.", 226, 425, Muted, .93f, 1110, 3);
        Button("workshop-opponents-prev", "Previous", 208, 817, 160, () => WorkshopDialogPage--, WorkshopDialogPage > 0, height: 36);
        Button("workshop-opponents-next", "Next", 380, 817, 126, () => WorkshopDialogPage++, (WorkshopDialogPage + 1) * 6 < choices.Length, height: 36);
        canvas.Text(choices.Length + " available decks", 540, 829, Muted, .7f, 610, 1);
    }
}
