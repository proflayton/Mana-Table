using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;
using Microsoft.Xna.Framework;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private bool analysisOpen { get => workshopTab == "analysis"; set { if (value) workshopTab = "analysis"; else if (analysisOpen) workshopTab = "build"; } }
    private string analysisDetailTitle = "", analysisDetailText = "";
    private string[] analysisDetailCards = [];
    private string analysisDetailBinding = "";
    private Card? analysisDetailSuggestion;
    private string[] analysisMissingCards = [], analysisPayoffCards = [];
    private string analysisVariant = "", analysisAdvancedText = "";
    private bool analysisAdvanced;
    private int analysisDetailIndex = -1;
    private AnalysisRecommendation? analysisDetailRecommendation;
    private int analysisEvidenceScroll, analysisEvidenceMaximum;
    private Rectangle analysisEvidenceBounds;
    private TuningQuestion? analysisDetailQuestion;
    private readonly HashSet<string> analysisRelatedCards = new(StringComparer.OrdinalIgnoreCase);
    private int analysisTab, analysisPage;
    private bool analysisShowReviewed;
    private AnalysisRecommendation[] VisibleRecommendations(DeckAnalysisState state) => state.Report.Recommendations
        .Where(r => Mana.Magic.DeckExperience.Reviewed(state, r.Id) == analysisShowReviewed).ToArray();
    private static readonly string[] AnalysisTabs = ["Checks", "Engines", "Swaps", "Roles", "History"];
    private static string AnalysisBasis(string value) => value switch {
        "text-estimate" => "Estimated from card text", "strategic-estimate" => "Strategy estimate",
        "curated-rules-line" => "Curated rules interaction", "bundled-rules" => "Bundled format snapshot",
        "unknown" => "Eligibility unknown", "restricted" => "Restricted in the bundled format snapshot",
        _ => value.Replace('-', ' ')
    };
    private string AnalysisScope => $"{analysisTab}:{analysisPage}:{analysisShowReviewed}:{deckWorkspace.View.Analysis?.MetadataRevision}:{analysisDetailTitle}:{analysisEvidenceScroll}:{analysisAdvanced}:{workspaceCardReading?.Identity}:{workspaceCardReading?.OtherFace}:{string.Join(',', analysisRelatedCards)}";
    private bool AnalysisCurrent => deckWorkspace.AnalysisCurrent;
    private void OpenDeckAnalysis()
    {
        if (busy || closing || loaded && ActivityOpen || selectedDeckDetails is not { } deck) return;
        DismissDeckWorkspaceReading();
        if (AnalysisCurrent || services.Analysis == null) {
            analysisOpen = true; workshopDialog = deckTextFocus = ""; textFocus = false; return;
        }
        bool sameDeck = deckWorkspace.View.Analysis?.Report.DeckId == deck.Id;
        var reviewing = deckWorkspace.ReviewAsync();
        Work(async () => {
            bool accepted = await reviewing;
            updates.Enqueue(() => {
                if (!accepted || !AnalysisCurrent) return;
                analysisOpen = true;
                if (!sameDeck) analysisTab = analysisPage = 0;
                workshopDialog = deckTextFocus = ""; textFocus = false;
            });
        }, "Reviewing your deck", "Checking its plan, resources, and engine dependencies.");
    }
    private void SaveAnalysisProfile(AnalysisProfile profile)
    {
        if (!AnalysisCurrent || services.Analysis is not { } service || deckWorkspace.View.Analysis is not { } current) return;
        var stamp = deckWorkspace.Capture();
        Work(async () => {
            var result = await service.SaveAnalysisProfileAsync(current.Report.DeckId, current.Report.Revision, current.MetadataRevision,
                current.Report.Fingerprint, profile);
            var shared = WorkshopEngine is { } builder ? await builder.WorkshopAsync(current.Report.DeckId, current.Report.Revision) : null;
            updates.Enqueue(() => deckWorkspace.AcceptAnalysis(stamp, result, shared));
        }, "Saving review preferences", "Updating the checks against your choices.", false);
    }
    private void ConsiderAnalysis(AnalysisRecommendation recommendation)
    {
        if (!AnalysisCurrent || services.Analysis is not { } service || deckWorkspace.View.Analysis is not { } current) return;
        var stamp = deckWorkspace.Capture();
        Work(async () => {
            var result = await service.ConsiderAnalysisAsync(current.Report.DeckId, current.Report.Revision, current.MetadataRevision,
                current.Report.Fingerprint, recommendation.Id, "");
            updates.Enqueue(() => { if (deckWorkspace.AcceptAnalysis(stamp, result.Analysis, result.Workshop)) message = recommendation.Add.Name + " saved to Considering. Review it in Build before swapping."; });
        }, "Saving this option", "Keeping your decision and candidate card together.", false);
    }
    private void RecordAnalysis(string id, string decision, string? suppliedNote = null)
    {
        if (!AnalysisCurrent || services.Analysis is not { } service || deckWorkspace.View.Analysis is not { } current) return;
        string? note = suppliedNote ?? Ask("Recommendation note", "Why this choice? (optional)", "");
        if (note == null) return;
        var stamp = deckWorkspace.Capture();
        Work(async () => {
            var result = await service.ReviewAnalysisAsync(current.Report.DeckId, current.Report.Revision, current.MetadataRevision,
                current.Report.Fingerprint, id, decision, note);
            var shared = WorkshopEngine is { } builder ? await builder.WorkshopAsync(current.Report.DeckId, current.Report.Revision) : null;
            updates.Enqueue(() => deckWorkspace.AcceptAnalysis(stamp, result, shared));
        }, "Saving your decision", "Keeping it with the playable version you reviewed.", false);
    }
    private void EditAnalysisText(string field)
    {
        if (!AnalysisCurrent || deckWorkspace.View.Analysis is not { } current) return;
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
    private string AnalysisDetailBinding => $"{selectedDeckDetails?.Id}:{selectedDeckDetails?.Revision}:{deckWorkspace.View.Analysis?.MetadataRevision}:{analysisTab}:{analysisPage}:{analysisShowReviewed}";
    private void ShowAnalysisDetails(string title, string text, string[]? cards = null, TuningQuestion? question = null, Card? suggestion = null, string[]? missing = null, string variant = "", int index = -1, string[]? payoffs = null)
    {
        analysisDetailTitle = title; analysisDetailText = text; analysisDetailCards = cards ?? [];
        analysisDetailQuestion = question; analysisMissingCards = missing ?? []; analysisPayoffCards = payoffs ?? []; analysisVariant = variant; analysisDetailIndex = index; analysisAdvanced = false;
        analysisDetailSuggestion = suggestion; analysisDetailBinding = AnalysisDetailBinding; analysisEvidenceScroll = 0;
        DismissDeckWorkspaceReading();
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
        if (deckWorkspace.View.Analysis is not { } state) {
            analysisAdvancedText = WorkshopResourceEvidence(); analysisDetailBinding = AnalysisDetailBinding; analysisAdvanced = true; analysisEvidenceScroll = 0; return;
        }
        var m = state.Report.Metrics;
        string model = m.LandProbability is { } probability ? $"{probability:P1} to see {m.LandGoal} lands in {m.CardsSeen} cards." : "No land-count estimate applies to this target.";
        string counts = $"{m.MainCards} main cards | {m.Lands} lands | {m.Creatures} creatures | {m.ExpensiveSpells} spells costing 5+\n{m.EarlyRamp} early ramp | {m.UnconditionalDraw} unconditional draw | {m.ConditionalDraw} conditional draw | {m.Interaction} interaction";
        string shared = WorkshopResourceEvidence();
        analysisAdvancedText = counts + "\n\n" + model + "\n\nLand-count model\n" + m.ProbabilityAssumptions
            + "\n\nMana curve\n" + string.Join("; ", m.Curve.Select((count, value) => (value == m.Curve.Length - 1 ? value + "+" : value.ToString()) + ": " + count))
            + "\n\nAnalysis limits\n" + string.Join("\n\n", state.Report.Limitations) + shared;
        analysisAdvanced = true; analysisEvidenceScroll = 0;
    }
    private string WorkshopResourceEvidence() => deckWorkspace.View.Workshop is not { } tools ? "" : "\n\nYour plan\n" + tools.Plan
        + "\n\nRoles (may overlap)\n" + string.Join("; ", tools.Insights.RoleCounts.Select(pair => pair.Key + ": " + pair.Value))
        + "\n\nPotential colored sources\n" + string.Join("; ", tools.Insights.ColoredSources.Select(pair => pair.Key + ": " + pair.Value))
        + "\n" + tools.Insights.Note;
    private void DrawDeckAnalysis()
    {
        if (services.Analysis == null) {
            DrawAnalysisWorkspaceOverview();
            MenuAction("analysis-model", "Advanced evidence", new(48, 910, 240, 36), ShowAnalysisModel, !busy, analysisAdvanced, .7f);
            if (analysisAdvanced) DrawAnalysisDetails();
            return;
        }
        if (!AnalysisCurrent || deckWorkspace.View.Analysis is not { } state) {
            canvas.Text(deckWorkspace.View.AnalysisError.Length == 0 ? "Updating your deck checks..." : deckWorkspace.View.AnalysisError, 64, 328, Ink, .95f, 1400, 6);
            if (deckWorkspace.View.AnalysisError.Length > 0) Button("analysis-refresh", "Retry", 64, 470, 180, OpenDeckAnalysis, !busy, height: 40);
            return;
        }
        var layout = PrepareAnalysisWorkspace()!;
        var entries = layout.Entries;
        var pages = layout.Pages;
        var visible = layout.Visible;
        int count = entries.Length;
        for (int i = 0; i < AnalysisTabs.Length; i++) {
            int tab = i;
            MenuAction("analysis-tab:" + i, AnalysisTabs[i], new(48 + i * 88, 244, 86, 38),
                () => { analysisTab = tab; analysisPage = 0; }, !busy, analysisTab == i, .65f);
        }
        int top = 300;
        foreach (int index in visible) {
            DrawAnalysisRow(entries[index], index, top); top += AnalysisRowHeight(entries[index]) + 12;
        }
        if (count == 0) canvas.Text(analysisTab == 1 ? "No curated lines found. Coverage is limited; this does not mean the deck lacks synergy."
            : analysisTab == 2 ? analysisShowReviewed ? "No reviewed swaps for this version." : "No supported undecided swaps for this version."
            : analysisTab == 4 ? "Your saved recommendation decisions appear here." : "No supported checks in this view.", 60, 326, Muted, .8f, 420, 10);
        if (pages.Count > 1) {
            MenuAction("analysis-prev", "Previous", new(48, 888, 122, 32), () => analysisPage--, !busy && analysisPage > 0, size: .68f);
            MenuAction("analysis-next", "Next", new(180, 888, 100, 32), () => analysisPage++, !busy && analysisPage + 1 < pages.Count, size: .68f);
        }
        if (analysisTab == 2) MenuAction("analysis-reviewed", analysisShowReviewed ? "Undecided swaps" : "Saved decisions", new(286, 888, 202, 32),
            () => { analysisShowReviewed = !analysisShowReviewed; analysisPage = 0; }, !busy, analysisShowReviewed, .68f);
        MenuAction("analysis-model", "Advanced evidence", new(48, 930, 240, 36), ShowAnalysisModel, !busy, analysisAdvanced, .7f);
        MenuAction("analysis-preferences", "Preferences", new(304, 930, 184, 36), () => workshopDialog = "analysis-preferences", !busy, size: .7f);
        canvas.Line(new(508, 250), new(508, 950), LobbyTrim * .6f);
        if (analysisDetailTitle.Length > 0 || analysisAdvanced) DrawAnalysisDetails();
        DrawWorkspaceCardReading(new(1110, 250, 442, 660));
        if (count == 0 && !analysisAdvanced) DrawAnalysisWorkspaceOverview();
    }
    private sealed record AnalysisWorkspaceLayout(AnalysisWorkspaceEntry[] Entries, List<List<int>> Pages, IReadOnlyList<int> Visible);
    // Resolve local selection and reader ownership before registering any controls.
    // A removed recommendation must not revoke the scope halfway through a frame.
    private AnalysisWorkspaceLayout? PrepareAnalysisWorkspace()
    {
        if (!analysisOpen || !AnalysisCurrent || deckWorkspace.View.Analysis is not { } state) return null;
        int count = analysisTab switch { 0 => state.Report.Findings.Length, 1 => state.Report.Engines.Length,
            2 => VisibleRecommendations(state).Length, 3 => state.Report.Cards.Length, _ => state.History.Length };
        var entries = Enumerable.Range(0, count).Select(index => AnalysisEntry(state, index)).ToArray();
        // Rows grow with the full card/variant names and the current text scale.
        // Paging is only for the list; the selected relationship never changes size.
        var pages = new List<List<int>>();
        var page = new List<int>(); int used = 0;
        for (int i = 0; i < entries.Length; i++) {
            int height = AnalysisRowHeight(entries[i]);
            if (page.Count > 0 && used + height > 586) { pages.Add(page); page = []; used = 0; }
            page.Add(i); used += height + 12;
        }
        if (page.Count > 0) pages.Add(page);
        analysisPage = Math.Clamp(analysisPage, 0, Math.Max(0, pages.Count - 1));
        IReadOnlyList<int> visible = pages.Count == 0 ? [] : pages[analysisPage];
        if (analysisDetailBinding != AnalysisDetailBinding || visible.Count > 0 && !visible.Contains(analysisDetailIndex)
            || visible.Count == 0 && analysisDetailIndex != -1) {
            if (visible.Count > 0) SelectAnalysisEntry(entries[visible[0]], visible[0]);
            else { ShowAnalysisDetails("", ""); analysisDetailRecommendation = null; }
        }
        if (workspaceCardReading is { } reading && reading.Scope != WorkspaceReadingScope) DismissDeckWorkspaceReading();
        return new(entries, pages, visible);
    }
    private void DrawAnalysisPreferences()
    {
        if (!AnalysisCurrent || deckWorkspace.View.Analysis is not { } state) return;
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
    private sealed record AnalysisWorkspaceEntry(string Title, string Details, string[] Cards, string[] Missing, string[] Payoffs, string Variant, TuningQuestion? Question, Card? Suggestion, AnalysisRecommendation? Recommendation);
    private AnalysisWorkspaceEntry AnalysisEntry(DeckAnalysisState state, int index)
    {
        var report = state.Report;
        string title, summary, details, variant = ""; string[] cards = [], missing = [], payoffs = [];
        Card? suggestion = null;
        TuningQuestion? question = null;
        AnalysisRecommendation? recommendation = null;
        if (analysisTab == 0) {
            var f = report.Findings[index]; title = f.Dimension + ": " + f.Title; summary = f.Evidence;
            details = f.Evidence + "\n\nSeverity: " + f.Severity + "\nBasis: " + AnalysisBasis(f.Confidence); cards = f.Cards;
            question = f.Severity == "review" ? DeckTuning.FromFinding(report, f) : null;
        } else if (analysisTab == 1) {
            var e = report.Engines[index]; title = e.Missing.Length == 0 ? e.Name : "Missing: " + string.Join(" + ", e.Missing);
            missing = e.Missing; payoffs = e.Payoffs; variant = e.Name;
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
            suggestion = new() { Name = r.Add.Name, ArtName = r.Add.Name, Type = r.Add.Type, Text = r.Add.OracleText };
            recommendation = r;
        } else if (analysisTab == 3) {
            var c = report.Cards[index];
            bool favorite = deckWorkspace.View.Workshop?.Roles.Any(r => r.CardId == c.CardId && r.Pinned) == true;
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
            if (report.Recommendations.FirstOrDefault(r => r.Id == h.RecommendationId) is { } earlier) details += "\n\n" + earlier.CutName + " -> " + earlier.Add.Name;
        }
        return new(title, details, cards, missing, payoffs, variant, question, suggestion, recommendation);
    }
    private void SelectAnalysisEntry(AnalysisWorkspaceEntry entry, int index)
    {
        ShowAnalysisDetails(entry.Title, entry.Details, entry.Cards, entry.Question, entry.Suggestion, entry.Missing, entry.Variant, index, entry.Payoffs);
        analysisDetailRecommendation = entry.Recommendation;
        if (AnalysisSourceFaces().FirstOrDefault() is { } source) ReadWorkspaceCard(source, "analysis:" + source.Name);
    }
    private static string AnalysisRowVariant(AnalysisWorkspaceEntry entry) => entry.Variant.Length == 0 ? ""
        : (entry.Variant == entry.Title ? "" : entry.Variant + "\n")
            + string.Join(" + ", entry.Cards.Where(name => !entry.Payoffs.Contains(name, StringComparer.OrdinalIgnoreCase)));
    private int AnalysisRowHeight(AnalysisWorkspaceEntry entry)
    {
        int titleHeight = canvas.TextSize(entry.Title, .75f, 326, 80, true).Y;
        string variant = AnalysisRowVariant(entry);
        int variantHeight = variant.Length == 0 ? 0 : canvas.TextSize(variant, .65f, 326, 80).Y + 10;
        return Math.Max(132, titleHeight + variantHeight + 32);
    }
    private void DrawAnalysisRow(AnalysisWorkspaceEntry entry, int index, int y)
    {
        bool selected = analysisDetailIndex == index && analysisDetailBinding == AnalysisDetailBinding;
        int height = AnalysisRowHeight(entry);
        var bounds = new Rectangle(48, y, 440, height);
        if (selected || IsControlHovered("analysis-details:" + index)) canvas.Rounded(bounds, selected ? new(30, 51, 61) : new(24, 37, 46), 5);
        if (selected) canvas.Fill(new(48, y + 4, 3, height - 8), Blue);
        Card? face = entry.Suggestion ?? selectedDeckDetails?.Entries.FirstOrDefault(card => entry.Cards.Contains(card.Card.Name, StringComparer.OrdinalIgnoreCase))?.Card;
        if (face != null) PaintCard(face, new Pose(new(98, y + 66), new(74, 104)), false, false);
        int titleX = 150;
        canvas.Text(entry.Title, titleX, y + 14, selected ? Ink : entry.Missing.Length > 0 ? Gold : Muted, .75f, 326, 80, true);
        string variant = AnalysisRowVariant(entry);
        if (variant.Length > 0) {
            int titleHeight = canvas.TextSize(entry.Title, .75f, 326, 80, true).Y;
            canvas.Text(variant, titleX, y + 24 + titleHeight, Muted, .65f, 326, 80);
        }
        hits.Add(new("analysis-details:" + index, Scope, bounds, () => SelectAnalysisEntry(entry, index)));
    }
    private void DrawAnalysisWorkspaceOverview()
    {
        if (selectedDeckDetails is not { } deck) return;
        DrawDeckComposition(deck, 760, 430, 764);
        if (deckWorkspace.View.Workshop is not { } tools) return;
        canvas.Text("Roles", 760, 552, Gold, .76f, 700, 1, true);
        int at = 0;
        foreach (var pair in tools.Insights.RoleCounts) {
            string role = pair.Key;
            Button("workshop-role-filter:" + role, role + "  " + pair.Value, 760 + at % 3 * 255, 592 + at / 3 * 42, 243, () => {
                workshopRoleFilter = role; workshopManaFilter = null; analysisRelatedCards.Clear();
                ShowWorkshopTab("build"); commanderCandidates = false; deckSection = "Main"; deckFilter = ""; deckCardsPage = 0; deckIssuesOnly = false;
            }, !busy, height: 32);
            at++;
            if (at == 12) break;
        }
        canvas.Text("Potential colored sources", 760, 784, Gold, .68f, 720, 1, true);
        canvas.Text(string.Join("    ", tools.Insights.ColoredSources.Select(pair => pair.Key + " " + pair.Value)), 760, 824, Ink, .76f, 740, 2);
    }
    private List<Card> AnalysisSourceFaces()
    {
        var cards = analysisDetailCards.Select(name => selectedDeckDetails?.Entries.FirstOrDefault(entry => entry.Card.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Card)
            .OfType<Card>().DistinctBy(card => card.Name).ToList();
        if (analysisDetailSuggestion is { } candidate && cards.All(card => card.Name != candidate.Name)) cards.Add(candidate);
        return cards;
    }
    private void DrawAnalysisDetails()
    {
        const int x = 532, width = 552;
        string heading = analysisAdvanced ? "Deck evidence" : analysisVariant.Length > 0 ? analysisVariant : analysisDetailTitle;
        int headingHeight = canvas.TextSize(heading, .85f, width, 80, true).Y;
        canvas.Text(heading, x, 250, Ink, .85f, width, 80, true);
        int top = 268 + headingHeight;
        string missing = analysisAdvanced ? "" : analysisMissingCards.Length > 0 ? "Missing from this deck: " + string.Join(" + ", analysisMissingCards)
            : analysisVariant.Length > 0 ? "All required cards are in this deck" : "";
        if (missing.Length > 0) {
            canvas.Text(missing, x, top, analysisMissingCards.Length > 0 ? Gold : Blue, .75f, width, 80);
            top += canvas.TextSize(missing, .75f, width, 80).Y + 18;
        }
        var faces = AnalysisSourceFaces();
        const int perRow = 4;
        int faceRowHeight = 176 + faces.Select(card => canvas.TextSize(card.Name, .65f, 128, 80).Y).DefaultIfEmpty(0).Max();
        int faceHeight = analysisAdvanced ? 0 : (faces.Count + perRow - 1) / perRow * faceRowHeight + (faces.Count > 0 ? 18 : 0);
        analysisEvidenceBounds = new(x, top, width, Math.Max(100, 878 - top));
        const float textSize = .75f;
        int lineHeight = canvas.TextSize("Ag", textSize, width - 14, 80).Y;
        string body = analysisAdvanced ? analysisAdvancedText : analysisDetailText;
        var lines = body.Replace("\r", "").Split('\n')
            .SelectMany(paragraph => paragraph.Length == 0 ? [""] : canvas.TextPages(paragraph, textSize, width - 14, lineHeight, 80)).ToArray();
        var roles = analysisAdvanced && deckWorkspace.View.Workshop != null ? deckWorkspace.View.Workshop.Insights.RoleCounts.ToArray() : [];
        int roleHeight = roles.Select(pair => canvas.TextSize(pair.Key + "  " + pair.Value, .72f, width - 16, 80).Y + 18).Sum();
        int contentHeight = faceHeight + lines.Length * lineHeight + roleHeight;
        analysisEvidenceMaximum = Math.Max(0, contentHeight - analysisEvidenceBounds.Height);
        analysisEvidenceScroll = Math.Clamp(analysisEvidenceScroll, 0, analysisEvidenceMaximum);
        if (!analysisAdvanced) for (int i = 0; i < faces.Count; i++) {
            int left = x + i % perRow * 138, y = top + i / perRow * faceRowHeight - analysisEvidenceScroll;
            if (y < top || y + faceRowHeight > analysisEvidenceBounds.Bottom) continue;
            var card = faces[i];
            string caption = analysisDetailRecommendation != null ? i == 0 ? "Current" : "Option"
                : analysisPayoffCards.Contains(card.Name, StringComparer.OrdinalIgnoreCase) ? "Payoff" : analysisVariant.Length > 0 ? "Required" : "";
            if (caption.Length > 0) canvas.Text(caption, left, y, Muted, .65f, 128, 1);
            var pose = new Pose(new(left + 62, y + 95), new(92, 129));
            PaintCard(card, pose, false, false);
            if (workspaceCardReading?.Identity == "analysis:" + card.Name) canvas.CardFrame(pose, Blue, 2);
            canvas.Text(card.Name, left, y + 164, Ink, .65f, 128, 80);
            hits.Add(new("analysis-card:" + i, Scope, new(left, y, 128, faceRowHeight), () => ReadWorkspaceCard(card, "analysis:" + card.Name)));
        }
        for (int i = 0; i < lines.Length; i++) {
            int y = top + faceHeight + i * lineHeight - analysisEvidenceScroll;
            if (y >= top && y + lineHeight <= analysisEvidenceBounds.Bottom)
                canvas.Text(lines[i], x, y, Ink, textSize, width - 14, 1);
        }
        int roleY = top + faceHeight + lines.Length * lineHeight - analysisEvidenceScroll;
        foreach (var pair in roles) {
            int height = canvas.TextSize(pair.Key + "  " + pair.Value, .72f, width - 16, 80).Y + 18;
            if (roleY >= top && roleY + height <= analysisEvidenceBounds.Bottom) {
                string role = pair.Key;
                canvas.Text(pair.Key + "  " + pair.Value, x + 8, roleY + 8, Blue, .72f, width - 16, 80);
                hits.Add(new("workshop-role-filter:" + role, Scope, new(x, roleY, width - 14, height), () => {
                    workshopRoleFilter = role; workshopManaFilter = null; analysisRelatedCards.Clear();
                    ShowWorkshopTab("build"); commanderCandidates = false; deckSection = "Main"; deckFilter = ""; deckCardsPage = 0; deckIssuesOnly = false;
                }));
            }
            roleY += height;
        }
        if (analysisEvidenceMaximum > 0) {
            int height = analysisEvidenceBounds.Height;
            int thumb = Math.Max(18, height * height / Math.Max(1, contentHeight));
            int y = top + (height - thumb) * analysisEvidenceScroll / analysisEvidenceMaximum;
            canvas.Rounded(new(1090, top, 3, height), Muted * .2f, 1);
            canvas.Rounded(new(1090, y, 3, thumb), Blue, 1);
        }
        if (analysisAdvanced) MenuAction("analysis-detail-close", "Back to this check", new(x, 922, 250, 36),
            () => { analysisAdvanced = false; analysisEvidenceScroll = 0; }, !busy, size: .75f);
        else if (analysisDetailRecommendation is { } offered) {
            int index = analysisDetailIndex;
            bool saved = deckWorkspace.View.Workshop?.Considering.Any(card => card.Card.Card.Name.Equals(offered.Add.Name, StringComparison.OrdinalIgnoreCase)) == true;
            Button("analysis-consider:" + index, saved ? "View option" : "Try this card", x, 902, 180,
                () => { if (saved) ShowAnalysisOption(offered); else ConsiderAnalysis(offered); }, !busy, true, 36);
            if (analysisShowReviewed) MenuAction("analysis-reopen:" + index, "Reopen this swap", new(732, 902, 352, 36), () => RecordAnalysis(offered.Id, "reopen", ""), !busy, size: .75f);
            else {
                MenuAction("analysis-keep:" + index, "Keep current", new(722, 902, 180, 36), () => RecordAnalysis(offered.Id, "keep", ""), !busy, size: .75f);
                MenuAction("analysis-dismiss:" + index, "Dismiss", new(912, 902, 172, 36), () => RecordAnalysis(offered.Id, "dismiss", ""), !busy, size: .75f);
            }
        } else if (analysisDetailQuestion is { } question && DeckTuning.Matches(question, deckWorkspace.View.Workshop))
            Button("analysis-test-check", "Test this check", x, 902, 250, () => TestAnalysisFinding(question), !busy && AnalysisCurrent && WorkshopCurrent, true, 40);
        if (faces.Any(card => selectedDeckDetails!.Entries.Any(entry => entry.Card.Name == card.Name)))
            MenuAction("analysis-show-cards", "Edit related cards", new(796, 952, 288, 36), ShowAnalysisCards, !busy, size: .75f);
    }
    private bool ScrollDeckWorkspace(int direction)
    {
        if (match != null || !deckEditor || !analysisOpen || workshopDialog.Length > 0) return false;
        if (analysisDetailTitle.Length == 0 || analysisDetailBinding != AnalysisDetailBinding || !analysisEvidenceBounds.Contains(pointer)) return true;
        analysisEvidenceScroll = Math.Clamp(analysisEvidenceScroll - direction * 36, 0, analysisEvidenceMaximum);
        return true;
    }
}
