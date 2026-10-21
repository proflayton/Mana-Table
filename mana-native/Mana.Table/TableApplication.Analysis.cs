using Mana.Contracts;
using Mana.Magic;
using Microsoft.Xna.Framework;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private DeckAnalysisState? analysisState;
    private bool analysisOpen { get => workshopTab == "analysis"; set { if (value) workshopTab = "analysis"; else if (analysisOpen) workshopTab = "build"; } }
    private bool analysisInFlight;
    private string analysisError = "", analysisDetailTitle = "", analysisDetailText = "";
    private string[] analysisDetailCards = [];
    private string[] analysisDetailPages = [""];
    private TuningQuestion? analysisDetailQuestion;
    private readonly HashSet<string> analysisRelatedCards = new(StringComparer.OrdinalIgnoreCase);
    private int analysisTab, analysisPage, analysisDetailPage;
    private bool analysisShowReviewed;
    private AnalysisRecommendation[] VisibleRecommendations(DeckAnalysisState state) => state.Report.Recommendations
        .Where(r => Mana.Magic.DeckExperience.Reviewed(state, r.Id) == analysisShowReviewed).ToArray();
    private static readonly string[] AnalysisTabs = ["What to check", "How cards work together", "Cards to try", "What each card does", "Your decisions"];
    private static string AnalysisBasis(string value) => value switch {
        "text-estimate" => "Estimated from card text", "strategic-estimate" => "Strategy estimate",
        "curated-rules-line" => "Curated rules interaction", "bundled-rules" => "Bundled format snapshot",
        "unknown" => "Eligibility unknown", "restricted" => "Restricted in the bundled format snapshot",
        _ => value.Replace('-', ' ')
    };
    private string AnalysisScope => $"{analysisTab}:{analysisPage}:{analysisShowReviewed}:{analysisState?.MetadataRevision}:{analysisDetailTitle}:{analysisDetailPage}:{string.Join(',', analysisRelatedCards)}";
    private bool AnalysisCurrent => analysisState is { } state && state.Report.DeckId == selectedDeckDetails?.Id
        && state.Report.Revision == selectedDeckDetails.Revision && (WorkshopEngine == null || WorkshopCurrent
            && workshop!.MetadataRevision == state.MetadataRevision && workshop.VersionId == state.Report.Fingerprint);

    private void AcceptAnalysis(DeckAnalysisState state, WorkshopState? shared = null)
    {
        if (selectedDeckDetails?.Id != state.Report.DeckId || selectedDeckDetails.Revision != state.Report.Revision) return;
        analysisState = state; analysisError = "";
        if (shared != null) { workshop = shared; workshopGeneration++; workshopPending = false; }
    }
    private void OpenDeckAnalysis()
    {
        if (engine is not IDeckAnalysis service || selectedDeckDetails is not { } deck) return;
        Work(async () => {
            var result = await service.AnalyzeDeckAsync(deck.Id, deck.Revision);
            var shared = WorkshopEngine is { } builder ? await builder.WorkshopAsync(deck.Id, deck.Revision) : null;
            updates.Enqueue(() => {
                if (selectedDeckDetails?.Id != deck.Id || selectedDeckDetails.Revision != deck.Revision) return;
                AcceptAnalysis(result, shared); analysisOpen = true; analysisTab = analysisPage = 0;
                workshopDialog = deckTextFocus = ""; textFocus = false;
            });
        }, "Reviewing your deck", "Checking its plan, resources, and engine dependencies.");
    }
    private void UpdateDeckAnalysis()
    {
        if (!analysisOpen || AnalysisCurrent || analysisInFlight || analysisError.Length > 0 || busy || !loaded
            || selectedDeckDetails is not { } deck || engine is not IDeckAnalysis service || WorkshopEngine != null && !WorkshopCurrent) return;
        analysisInFlight = true;
        _ = Task.Run(async () => {
            try {
                var result = await service.AnalyzeDeckAsync(deck.Id, deck.Revision);
                updates.Enqueue(() => AcceptAnalysis(result));
            } catch (Exception ex) {
                updates.Enqueue(() => { if (selectedDeckDetails?.Id == deck.Id && selectedDeckDetails.Revision == deck.Revision) analysisError = ex.Message; });
            } finally { updates.Enqueue(() => analysisInFlight = false); }
        });
    }
    private void SaveAnalysisProfile(AnalysisProfile profile)
    {
        if (!AnalysisCurrent || engine is not IDeckAnalysis service || analysisState is not { } current) return;
        Work(async () => {
            var result = await service.SaveAnalysisProfileAsync(current.Report.DeckId, current.Report.Revision, current.MetadataRevision,
                current.Report.Fingerprint, profile);
            var shared = WorkshopEngine is { } builder ? await builder.WorkshopAsync(current.Report.DeckId, current.Report.Revision) : null;
            updates.Enqueue(() => AcceptAnalysis(result, shared));
        }, "Saving review preferences", "Updating the checks against your choices.", false);
    }
    private void ConsiderAnalysis(AnalysisRecommendation recommendation)
    {
        if (!AnalysisCurrent || engine is not IDeckAnalysis service || analysisState is not { } current) return;
        Work(async () => {
            var result = await service.ConsiderAnalysisAsync(current.Report.DeckId, current.Report.Revision, current.MetadataRevision,
                current.Report.Fingerprint, recommendation.Id, "");
            updates.Enqueue(() => { AcceptAnalysis(result.Analysis, result.Workshop); message = recommendation.Add.Name + " saved to Considering. Review it in Build before swapping."; });
        }, "Saving this option", "Keeping your decision and candidate card together.", false);
    }
    private void RecordAnalysis(string id, string decision, string? suppliedNote = null)
    {
        if (!AnalysisCurrent || engine is not IDeckAnalysis service || analysisState is not { } current) return;
        string? note = suppliedNote ?? Ask("Recommendation note", "Why this choice? (optional)", "");
        if (note == null) return;
        Work(async () => {
            var result = await service.ReviewAnalysisAsync(current.Report.DeckId, current.Report.Revision, current.MetadataRevision,
                current.Report.Fingerprint, id, decision, note);
            var shared = WorkshopEngine is { } builder ? await builder.WorkshopAsync(current.Report.DeckId, current.Report.Revision) : null;
            updates.Enqueue(() => AcceptAnalysis(result, shared));
        }, "Saving your decision", "Keeping it with the playable version you reviewed.", false);
    }
    private void EditAnalysisText(string field)
    {
        if (!AnalysisCurrent || analysisState is not { } current) return;
        var p = current.Report.Profile;
        string value = field switch { "backup" => p.Backup, "protected" => string.Join("; ", p.ProtectedCards), "pool" => string.Join("; ", p.Pool), _ => p.TargetTurn.ToString() };
        string prompt = field switch { "backup" => "How else could this deck win? (optional note)", "protected" => "Which cards should we keep? Separate card names with semicolons, or mark favorites in Build.", "pool" => "Which cards are available from your draft or sealed pool? Separate card names with semicolons.", _ => "Which turn should the land check use? 0 uses your commander's mana value, or turn 3." };
        var next = Ask("Review preferences", prompt, value); if (next == null) return;
        string[] Names() => next.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (field == "turn" && (!int.TryParse(next, out int turn) || turn < 0 || turn > 30)) { error = "Choose a turn from 0 to 30."; return; }
        SaveAnalysisProfile(field switch {
            "backup" => p with { Backup = next }, "protected" => p with { ProtectedCards = Names() },
            "pool" => p with { Pool = Names() }, _ => p with { TargetTurn = int.Parse(next) }
        });
    }
    private void ShowAnalysisDetails(string title, string text, string[]? cards = null, TuningQuestion? question = null)
    {
        analysisDetailTitle = title; analysisDetailText = text; analysisDetailCards = cards ?? [];
        analysisDetailQuestion = question;
        analysisDetailPages = AnalysisDetailPages(text);
        analysisDetailPage = 0; workshopDialog = "analysis-detail";
    }
    private void ShowAnalysisCards()
    {
        FilterDeck(""); deckFilter = ""; analysisRelatedCards.UnionWith(analysisDetailCards);
        if (DeckRowsToShow().FirstOrDefault() is { } card) SelectDeckRow(card);
        ShowWorkshopTab("build");
    }
    private void ShowAnalysisOption(AnalysisRecommendation recommendation)
    {
        FilterDeck("Considering"); deckFilter = "";
        if (DeckRowsToShow().FirstOrDefault(e => e.Card.Name.Equals(recommendation.Add.Name, StringComparison.OrdinalIgnoreCase)) is { } card) SelectDeckRow(card);
        ShowWorkshopTab("build");
    }
    private void ShowAnalysisModel()
    {
        if (analysisState is not { } state) return;
        var m = state.Report.Metrics;
        string model = m.LandProbability is { } probability ? $"{probability:P1} to see {m.LandGoal} lands in {m.CardsSeen} cards." : "No land-count estimate applies to this target.";
        string counts = $"{m.MainCards} main cards | {m.Lands} lands | {m.Creatures} creatures | {m.ExpensiveSpells} spells costing 5+\n{m.EarlyRamp} early ramp | {m.UnconditionalDraw} unconditional draw | {m.ConditionalDraw} conditional draw | {m.Interaction} interaction";
        ShowAnalysisDetails("What this review can tell you", counts + "\n\n" + model + "\n\nLand-count model\n" + m.ProbabilityAssumptions
            + "\n\nAnalysis limits\n" + string.Join("\n\n", state.Report.Limitations));
    }
    private void DrawDeckAnalysis()
    {
        if (!AnalysisCurrent || analysisState is not { } state) {
            canvas.Panel(new(32, 236, 1536, 712), new(21, 29, 39), LobbyTrim, 8);
            canvas.Text(analysisError.Length == 0 ? "Updating your strategy review..." : analysisError, 82, 428, Ink, 1.12f, 1420, 4);
            canvas.Text("The review follows the current playable list, plan, and favorites.", 84, 584, Muted, .86f, 1400, 2);
            if (analysisError.Length > 0) Button("analysis-refresh", "Retry analysis", 84, 690, 284, OpenDeckAnalysis, !busy, height: 42);
            return;
        }
        var report = state.Report; var m = report.Metrics;
        canvas.Panel(new(32, 236, 1536, 180), new(21, 29, 39), LobbyTrim, 8);
        canvas.Text("YOUR DECK AT A GLANCE", 50, 252, Gold, .78f, 710, 1, true);
        canvas.Text(WorkshopVersionLabel(report.Fingerprint) + " | Current playable list", 798, 255, Muted, .65f, 736, 1);
        canvas.Text($"{report.Format.Name} | {m.MainCards} main cards | {m.Lands} lands | {m.EarlyRamp} early ramp | {m.UnconditionalDraw + m.ConditionalDraw} draw | {m.Interaction} interaction", 50, 290, Ink, .73f, 1222, 1);
        canvas.Text($"{report.Findings.Count(f => f.Severity == "review")} checks to review | {report.Recommendations.Length} swap ideas. Read a check, then try one change in Test.", 50, 324, Ink, .69f, 1222, 1);
        Button("analysis-preferences", "Review preferences", 1292, 284, 260, () => workshopDialog = "analysis-preferences", !busy, height: 34);
        canvas.Text(m.LandProbability is { } p ? $"Land check: {p:P0} chance to see {m.LandGoal} lands by turn {m.LandGoal}. Counts lands only; see how this works."
            : "Finish the main deck to see a land-drop estimate. Other supported checks are available now.", 50, 358, Muted, .65f, 1222, 1);
        Button("analysis-model", "How this works", 1292, 349, 260, ShowAnalysisModel, !busy, height: 30);
        bool needsPool = report.Findings.Any(f => f.Id == "missing-pool");
        bool needsFormat = report.Format.Name == "Constructed" && report.Profile.RulesFormat.Length == 0;
        canvas.Text(needsPool ? "Which cards can you use? Add your draft or sealed pool to get swap ideas."
            : needsFormat ? "Which format do you play? Using casual Constructed until you choose one."
            : "Ready without setup. Preferences are optional; your plan and favorites are shared with Build.", 50, 391, Muted, .63f, 1222, 1);
        if (needsPool || needsFormat) Button("analysis-question", needsPool ? "Add available cards" : "Choose format", 1292, 385, 260,
            () => { if (needsPool) EditAnalysisText("pool"); else workshopDialog = "analysis-preferences"; }, !busy, height: 28);
        for (int i = 0; i < AnalysisTabs.Length; i++) {
            int tab = i;
            Button("analysis-tab:" + i, AnalysisTabs[i], 48 + i * 302, 432, 290, () => { analysisTab = tab; analysisPage = 0; }, !busy, analysisTab == i, 36);
        }
        int count = analysisTab switch { 0 => report.Findings.Length, 1 => report.Engines.Length, 2 => VisibleRecommendations(state).Length, 3 => report.Cards.Length, _ => state.History.Length };
        analysisPage = Math.Clamp(analysisPage, 0, Math.Max(0, (count - 1) / 3));
        for (int i = 0; i < Math.Min(3, count - analysisPage * 3); i++) DrawAnalysisRow(state, analysisPage * 3 + i, 486 + i * 142);
        if (count == 0) canvas.Text(analysisTab == 1 ? "No lines from the curated engine registry were found. This does not mean the deck lacks synergy."
            : analysisTab == 2 ? analysisShowReviewed ? "No reviewed swaps for this playable version." : "No undecided supported swaps. Review saved decisions or the findings and card functions."
            : analysisTab == 4 ? "Save a recommendation decision here. Keep observations from hands and real games in Results."
            : analysisTab == 0 ? "No specific issues found by the supported checks. Try an opening hand in Test; this review does not cover every interaction." : "Nothing to show in this view.", 72, 540, Muted, .9f, 1430, 4);
        Button("analysis-prev", "Previous", 48, 920, 150, () => analysisPage--, !busy && analysisPage > 0, height: 32);
        Button("analysis-next", "Next", 210, 920, 150, () => analysisPage++, !busy && (analysisPage + 1) * 3 < count, height: 32);
        if (analysisTab == 2) Button("analysis-reviewed", analysisShowReviewed ? "Show undecided swaps" : "Review decisions (" + report.Recommendations.Count(r => Mana.Magic.DeckExperience.Reviewed(state, r.Id)) + ")", 385, 918, 452, () => { analysisShowReviewed = !analysisShowReviewed; analysisPage = 0; }, !busy, height: 34);
        else canvas.Text($"Page {analysisPage + 1} / {Math.Max(1, (count + 2) / 3)} | Strategic estimates. Test changes at the table.", 385, 929, Muted, .62f, 850, 1);
        Button("analysis-back", "Build", 1260, 918, 122, () => ShowWorkshopTab("build"), !busy, height: 34);
        Button("analysis-test", "Test deck", 1394, 918, 158, () => ShowWorkshopTab("test"), !busy, true, 34);
    }
    private void DrawAnalysisPreferences()
    {
        if (!AnalysisCurrent || analysisState is not { } state) return;
        var p = state.Report.Profile;
        canvas.Text("Your deck is reviewed automatically. Answer only what matters to you; choices save immediately.", 208, 240, Muted, .77f, 1164, 2);
        canvas.Text("How quickly do you want the deck to get going?", 208, 292, Gold, .82f, 1164, 1, true);
        string[] paces = ["relaxed", "balanced", "fast"], paceLabels = ["Take time to set up", "Balanced (default)", "Get going quickly"];
        for (int i = 0; i < paces.Length; i++) {
            string pace = paces[i];
            Button("analysis-pace:" + pace, paceLabels[i], 208 + i * 388, 326, 374, () => SaveAnalysisProfile(p with { Pace = pace }), !busy, p.Pace == pace, 36);
        }
        canvas.Text("Should swap ideas favor a particular plan? (optional)", 208, 382, Gold, .82f, 1164, 1, true);
        string[] strategies = ["unspecified", "aggro", "midrange", "control", "combo", "landfall", "artifact-recursion"];
        string[] labels = ["Use card interactions", "Attack early", "Build a strong board", "Control the game", "Assemble a combo", "Trigger landfall", "Reuse artifacts"];
        for (int i = 0; i < strategies.Length; i++) {
            string strategy = strategies[i];
            Button("analysis-strategy:" + strategy, labels[i], 208 + i % 4 * 291, 416 + i / 4 * 42, 278,
                () => SaveAnalysisProfile(p with { Strategy = strategy }), !busy, p.Strategy == strategy, 34);
        }
        if (!state.Report.Format.CommanderIdentity && !state.Report.Format.PoolRequired) {
            canvas.Text("Which format do you play? This filters the cards we suggest.", 208, 514, Gold, .8f, 1164, 1, true);
            string[] formats = ["", "Constructed", "Standard", "Pioneer", "Modern", "Legacy", "Vintage", "Pauper"];
            for (int i = 0; i < formats.Length; i++) {
                string format = formats[i];
                Button("analysis-format:" + format, i == 0 ? "Automatic" : i == 1 ? "Casual / any" : format, 208 + i * 146, 548, 136,
                    () => SaveAnalysisProfile(p with { RulesFormat = format }), !busy, p.RulesFormat == format, 34);
            }
        } else canvas.Text(state.Report.Format.Name + " comes from your deck. Its structure is kept automatically.", 208, 536, Muted, .77f, 1164, 2);
        canvas.Text("When should we check your land drops?", 208, 604, Gold, .82f, 1164, 1, true);
        int[] turns = [0, 3, 4, 5];
        for (int i = 0; i < turns.Length; i++) {
            int turn = turns[i];
            Button("analysis-turn:" + turn, turn == 0 ? "Automatic (default)" : "Turn " + turn, 208 + i * 239, 638, 226,
                () => SaveAnalysisProfile(p with { TargetTurn = turn }), !busy, p.TargetTurn == turn, 36);
        }
        Button("analysis-turn", p.TargetTurn is > 0 and not (3 or 4 or 5) ? "Turn " + p.TargetTurn : "Another turn", 1164, 638, 208, () => EditAnalysisText("turn"), !busy, height: 36);
        canvas.Text("Automatic uses your commander's mana value, or the first three land drops. It does not model colors or ramp.", 208, 686, Muted, .69f, 1164, 2);
        Button("analysis-protect", "Which cards should we keep?", 208, 746, 380, () => EditAnalysisText("protected"), !busy, height: 36);
        Button("analysis-backup", "Add a backup-plan note", 606, 746, 374, () => EditAnalysisText("backup"), !busy, height: 36);
        if (state.Report.Format.PoolRequired) Button("analysis-pool", "Add available pool cards", 998, 746, 374, () => EditAnalysisText("pool"), !busy, height: 36);
        canvas.Text("Favorites also live in Build. Plans are your notes; the review does not interpret free text as rules.", 208, 807, Muted, .69f, 1164, 2);
    }
    private void DrawAnalysisRow(DeckAnalysisState state, int index, int y)
    {
        var report = state.Report;
        canvas.Panel(new(48, y, 1504, 132), new(21, 29, 39), new(54, 67, 79), 5);
        string title, summary, details; string[] cards = [];
        TuningQuestion? question = null;
        if (analysisTab == 0) {
            var f = report.Findings[index]; title = f.Dimension + ": " + f.Title; summary = f.Evidence;
            details = f.Evidence + "\n\nSeverity: " + f.Severity + "\nBasis: " + AnalysisBasis(f.Confidence); cards = f.Cards;
            question = f.Severity == "review" ? DeckTuning.FromFinding(report, f) : null;
        } else if (analysisTab == 1) {
            var e = report.Engines[index]; title = e.Name + (e.Missing.Length == 0 ? " - pieces present" : " - pieces missing");
            summary = "Pieces: " + string.Join(" + ", e.Cards) + ". " + string.Join(" ", e.Steps);
            details = "Pieces: " + string.Join(" + ", e.Cards) + "\nPayoffs present: " + string.Join(", ", e.Payoffs)
                + "\nMissing: " + (e.Missing.Length == 0 ? "None in this line" : string.Join(", ", e.Missing))
                + "\n\n" + string.Join("\n\n", e.Steps) + "\n\nOutputs: " + string.Join("; ", e.Outputs)
                + "\nRequirements: " + string.Join("; ", e.Requirements) + "\nBasis: " + AnalysisBasis(e.Confidence);
            cards = e.Cards.Concat(e.Payoffs).ToArray();
        } else if (analysisTab == 2) {
            var r = VisibleRecommendations(state)[index]; title = r.CutName + " -> " + r.Add.Name;
            var decision = Mana.Magic.DeckExperience.LatestDecision(state, r.Id);
            if (decision != null) title += " | " + (decision.Decision == "keep" ? "Keeping current" : decision.Decision == "dismiss" ? "Dismissed" : decision.Decision);
            summary = r.Benefit + " Tradeoff: " + r.Tradeoff + (r.Eligibility == "unknown" ? " Format eligibility is unverified." : "");
            details = r.Benefit + "\n\nTradeoff: " + r.Tradeoff + "\nMana value change: " + r.ManaValueDelta
                + "\nRole changes: " + string.Join(", ", r.RoleDelta.Select(p => p.Key + " " + p.Value.ToString("+0;-0;0")))
                + "\nEligibility: " + AnalysisBasis(r.Eligibility) + "\nBasis: " + AnalysisBasis(r.Confidence) + "\n\n" + r.Add.Name + "\n" + r.Add.Type + "\n" + r.Add.OracleText;
            cards = [r.CutName];
            bool saved = workshop?.Considering.Any(c => c.Card.Card.Name.Equals(r.Add.Name, StringComparison.OrdinalIgnoreCase)) == true;
            Button("analysis-consider:" + index, saved ? "View option" : "Save to Considering", 1264, y + 9, 270, () => { if (saved) ShowAnalysisOption(r); else ConsiderAnalysis(r); }, !busy, height: 32);
            if (analysisShowReviewed) Button("analysis-reopen:" + index, "Reopen this swap", 1264, y + 50, 270, () => RecordAnalysis(r.Id, "reopen", ""), !busy, height: 30);
            else {
                Button("analysis-keep:" + index, "Keep current", 1264, y + 50, 130, () => RecordAnalysis(r.Id, "keep", ""), !busy, height: 30);
                Button("analysis-dismiss:" + index, "Dismiss", 1404, y + 50, 130, () => RecordAnalysis(r.Id, "dismiss", ""), !busy, height: 30);
            }
        } else if (analysisTab == 3) {
            var c = report.Cards[index];
            bool favorite = workshop?.Roles.Any(r => r.CardId == c.CardId && r.Pinned) == true;
            bool enginePiece = report.Engines.Any(e => e.Missing.Length == 0 && e.Cards.Contains(c.Name, StringComparer.OrdinalIgnoreCase));
            title = c.Quantity + "x " + c.Name + " (" + c.Section + ")" + (favorite ? " - favorite" : c.ProtectedCard ? enginePiece ? " - engine piece" : " - preserved preference" : "");
            summary = string.Join(" | ", c.Functions.Select(f => f.Role + " [" + f.Event + "]: " + f.Provides));
            details = c.Functions.Length == 0 ? "No function was confidently detected. Inspect the card's rules before deciding its role." : string.Join("\n\n", c.Functions.Select(f => f.Role + " | " + f.Event + "\n" + f.Provides + "\nRequires: " + (f.Requires.Length == 0 ? "No additional condition detected" : string.Join("; ", f.Requires)) + "\nBasis: " + AnalysisBasis(f.Confidence)));
            if (c.ProtectedCard) details = (favorite ? "A favorite you chose to keep. Change its marker in Build or Review preferences."
                : enginePiece ? "This card is part of a detected complete engine, so this review preserves it when suggesting cuts. It is not marked as a favorite."
                : "An existing analysis preference preserves this card from suggested cuts. Change it in Review preferences.") + "\n\n" + details;
            cards = [c.Name];
        } else {
            var h = state.History[state.History.Length - 1 - index]; title = h.Decision + " | " + WorkshopVersionLabel(h.Fingerprint);
            summary = WorkshopTime(h.Created) + (h.Note.Length == 0 ? " | No note added." : " | " + h.Note);
            details = summary + "\n\nPlayable version: " + WorkshopVersionLabel(h.Fingerprint) + (h.Fingerprint == report.Fingerprint ? " (current)" : " (earlier list)");
            if (report.Recommendations.FirstOrDefault(r => r.Id == h.RecommendationId) is { } recommendation) details += "\n\n" + recommendation.CutName + " -> " + recommendation.Add.Name;
        }
        canvas.Text(title, 64, y + 12, Gold, .77f, 1184, 1, true);
        canvas.Text(summary.Length == 0 ? "No function was confidently detected; inspect this card's rules." : summary, 64, y + 47, Ink, .68f, 1180, 3);
        Button("analysis-details:" + index, "Read details", 1264, y + (analysisTab == 2 ? 91 : 48), 270, () => ShowAnalysisDetails(title, details, cards, question), !busy, height: 30);
    }
    private void DrawAnalysisDetails()
    {
        canvas.Text(analysisDetailTitle, 206, 244, Gold, 1, 1166, 2, true);
        var pages = analysisDetailPages; analysisDetailPage = Math.Clamp(analysisDetailPage, 0, pages.Length - 1);
        canvas.Text(pages[analysisDetailPage], 208, 325, Ink, .83f, 1164, 22);
        Button("analysis-detail-prev", "Previous", 208, 817, 160, () => analysisDetailPage--, analysisDetailPage > 0, height: 36);
        Button("analysis-detail-next", "More", 380, 817, 126, () => analysisDetailPage++, analysisDetailPage + 1 < pages.Length, height: 36);
        canvas.Text($"{analysisDetailPage + 1} / {pages.Length}", 526, 827, Muted, .7f, 220, 1);
        if (analysisDetailQuestion is { } question && DeckTuning.Matches(question, workshop))
            Button("analysis-test-check", "Test this check", 742, 817, 226, () => TestAnalysisFinding(question), !busy && AnalysisCurrent && WorkshopCurrent, true, 36);
        bool hasCards = selectedDeckDetails?.Entries.Any(e => analysisDetailCards.Contains(e.Card.Name, StringComparer.OrdinalIgnoreCase)) == true;
        if (hasCards) Button("analysis-show-cards", "Show related cards in Build", 980, 817, 402, ShowAnalysisCards, !busy, true, 36);
    }
    private string[] AnalysisDetailPages(string value)
    {
        using var bitmap = new System.Drawing.Bitmap(1, 1);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        int pixels = Math.Max(9, (int)Math.Round(22 * .83f * preferences.TextScale));
        using var font = new System.Drawing.Font("Segoe UI", pixels, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Pixel);
        using var format = new System.Drawing.StringFormat(System.Drawing.StringFormat.GenericTypographic);
        var pages = new List<string>(); string rest = value.Replace("\r", "");
        do {
            graphics.MeasureString(rest, font, new System.Drawing.SizeF(1164, 430), format, out int fitted, out _);
            fitted = Math.Clamp(fitted, 1, Math.Max(1, rest.Length));
            pages.Add(rest.Length == 0 ? "" : rest[..fitted]); rest = rest.Length == 0 ? "" : rest[fitted..].TrimStart();
        } while (rest.Length > 0);
        return pages.ToArray();
    }
}
