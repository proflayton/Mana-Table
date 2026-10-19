using Mana.Contracts;
using Microsoft.Xna.Framework;

namespace Mana.Table;

internal sealed partial class TableGame
{
    private DeckAnalysisState? analysisState;
    private bool analysisOpen;
    private int analysisTab, analysisPage;
    private static readonly string[] AnalysisTabs = ["Needs attention", "Engines", "Suggested swaps", "Card functions", "Review history"];
    private void OpenDeckAnalysis()
    {
        if (engine is not IDeckAnalysis service || selectedDeckDetails is not { } deck) return;
        Work(async () => {
            var result = await service.AnalyzeDeckAsync(deck.Id, deck.Revision);
            updates.Enqueue(() => {
                if (selectedDeckDetails?.Id != result.Report.DeckId || selectedDeckDetails.Revision != result.Report.Revision) return;
                analysisState = result; analysisOpen = true; analysisTab = analysisPage = 0;
            });
        }, "Reviewing your deck", "Checking its plan, resources, and engine dependencies.");
    }
    private void SaveAnalysisProfile(AnalysisProfile profile)
    {
        if (engine is not IDeckAnalysis service || analysisState is not { } current) return;
        Work(async () => {
            var result = await service.SaveAnalysisProfileAsync(current.Report.DeckId, current.Report.Revision, current.MetadataRevision, profile);
            updates.Enqueue(() => { if (selectedDeckDetails?.Id == result.Report.DeckId && selectedDeckDetails.Revision == result.Report.Revision) analysisState = result; });
        }, "Saving your deck goals", "Updating the review against your chosen plan.");
    }
    private void RecordAnalysis(string id, string decision)
    {
        if (engine is not IDeckAnalysis service || analysisState is not { } current) return;
        string? note = Ask(decision == "observation" ? "Playtest observation" : "Recommendation note",
            decision == "observation" ? "What happened, and under what game conditions?" : "Why this choice? (optional)", "");
        if (note == null || decision == "observation" && string.IsNullOrWhiteSpace(note)) return;
        Work(async () => {
            var result = await service.ReviewAnalysisAsync(current.Report.DeckId, current.Report.Revision, current.MetadataRevision,
                current.Report.Fingerprint, id, decision, note);
            updates.Enqueue(() => { if (selectedDeckDetails?.Id == result.Report.DeckId && selectedDeckDetails.Revision == result.Report.Revision) analysisState = result; });
        }, "Saving your review", "Keeping this decision with the version you reviewed.");
    }
    private void EditAnalysisText(string field)
    {
        if (analysisState is not { } current) return;
        var p = current.Report.Profile;
        string value = field switch { "plan" => p.Plan, "backup" => p.Backup, "protected" => string.Join("; ", p.ProtectedCards), "pool" => string.Join("; ", p.Pool), _ => p.TargetTurn.ToString() };
        string prompt = field switch { "plan" => "Describe how this deck intends to win", "backup" => "Describe its backup plan", "protected" => "Cards to preserve, separated by semicolons", "pool" => "Available pool card names, separated by semicolons", _ => "Land-drop target turn: 0 for commander mana value or no target" };
        var next = Ask("Deck goals", prompt, value); if (next == null) return;
        string[] Names() => next.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (field == "turn" && (!int.TryParse(next, out int turn) || turn < 0 || turn > 30)) { error = "Choose a turn from 0 to 30."; return; }
        SaveAnalysisProfile(field switch {
            "plan" => p with { Plan = next }, "backup" => p with { Backup = next }, "protected" => p with { ProtectedCards = Names() },
            "pool" => p with { Pool = Names() }, _ => p with { TargetTurn = int.Parse(next) }
        });
    }
    private void DrawDeckAnalysis()
    {
        if (analysisState is not { } state || selectedDeckDetails is not { } deck) { analysisOpen = false; return; }
        if (state.Report.DeckId != deck.Id || state.Report.Revision != deck.Revision) {
            canvas.Text("This deck has changed. Refresh its review.", 60, 200, Ink, 1, 1400, 2);
            Button("analysis-refresh", "Refresh review", 60, 290, 200, OpenDeckAnalysis, !busy);
            Button("analysis-back", "Back to deck", 280, 290, 200, () => analysisOpen = false, !busy); return;
        }
        var report = state.Report; var profile = report.Profile; var m = report.Metrics;
        canvas.Text("Strategy review - " + deck.Name, 48, 96, Ink, 1.15f, 1200, 1, true);
        Button("analysis-back", "Back to deck", 1330, 94, 220, () => analysisOpen = false, !busy, height: 38);
        string[] strategies = ["unspecified", "aggro", "midrange", "control", "combo", "landfall", "artifact-recursion"];
        Button("analysis-strategy", "Strategy: " + profile.Strategy, 48, 155, 284, () => SaveAnalysisProfile(profile with { Strategy = strategies[(Array.IndexOf(strategies, profile.Strategy) + 1) % strategies.Length] }), !busy, height: 34);
        string[] paces = ["relaxed", "balanced", "fast"];
        Button("analysis-pace", "Pace: " + profile.Pace, 344, 155, 210, () => SaveAnalysisProfile(profile with { Pace = paces[(Array.IndexOf(paces, profile.Pace) + 1) % paces.Length] }), !busy, height: 34);
        Button("analysis-plan", "Primary plan", 566, 155, 180, () => EditAnalysisText("plan"), !busy, height: 34);
        Button("analysis-backup", "Backup plan", 758, 155, 180, () => EditAnalysisText("backup"), !busy, height: 34);
        Button("analysis-protect", "Preserve cards", 950, 155, 180, () => EditAnalysisText("protected"), !busy, height: 34);
        Button("analysis-turn", "Target turn: " + (profile.TargetTurn == 0 ? "auto" : profile.TargetTurn), 1142, 155, 198, () => EditAnalysisText("turn"), !busy, height: 34);
        Button("analysis-observation", "Test note", 1352, 155, 198, () => RecordAnalysis("", "observation"), !busy, height: 34);
        canvas.Text(profile.Plan.Length == 0 ? "Set a primary plan so you can judge each card against its purpose." : profile.Plan, 50, 205, Muted, .76f, 1480, 2);
        string stats = $"{report.Format.Name} | {m.MainCards} main | {m.Lands} lands | {m.EarlyRamp} early ramp | {m.ConditionalDraw} conditional draw | {m.ExpensiveSpells} spells costing 5+";
        canvas.Text(stats, 50, 255, Ink, .72f, 1230, 1);
        if (m.LandProbability is { } probability) canvas.Text($"Land-count model: {probability:P0} to see {m.LandGoal} lands in {m.CardsSeen} cards. Seven-card keep; "
            + (report.Format.CommanderIdentity ? "draw on turn one; " : "on the play, no turn-one draw; ")
            + "no mulligans, ramp, color, or tapped-land modeling.", 50, 278, Muted, .56f, 1490, 1);
        if (report.Format.PoolRequired) Button("analysis-pool", "Set available pool", 1320, 245, 230, () => EditAnalysisText("pool"), !busy, height: 32);
        else if (!report.Format.CommanderIdentity) {
            string[] formats = ["Constructed", "Standard", "Pioneer", "Modern", "Legacy", "Vintage", "Pauper"];
            Button("analysis-format", report.Format.Name, 1320, 245, 230, () => SaveAnalysisProfile(profile with { RulesFormat = formats[(Array.IndexOf(formats, report.Format.Name) + 1) % formats.Length] }), !busy, height: 32);
        }
        for (int i = 0; i < AnalysisTabs.Length; i++) {
            int tab = i;
            Button("analysis-tab:" + i, AnalysisTabs[i], 48 + i * 302, 298, 290, () => { analysisTab = tab; analysisPage = 0; }, !busy, analysisTab == i, 36);
        }
        int count = analysisTab switch { 0 => report.Findings.Length, 1 => report.Engines.Length, 2 => report.Recommendations.Length, 3 => report.Cards.Length, _ => state.History.Length };
        analysisPage = Math.Clamp(analysisPage, 0, Math.Max(0, (count - 1) / 4));
        for (int i = 0; i < Math.Min(4, count - analysisPage * 4); i++) {
            int index = analysisPage * 4 + i, y = 356 + i * 137;
            canvas.Panel(new(48, y, 1502, 128), new(21, 29, 39), new(54, 67, 79), 5);
            string title, text;
            if (analysisTab == 0) { var f = report.Findings[index]; title = f.Dimension + ": " + f.Title; text = f.Evidence; }
            else if (analysisTab == 1) { var e = report.Engines[index]; title = e.Name + (e.Missing.Length == 0 ? " - pieces present" : " - missing: " + string.Join(", ", e.Missing)); text = "Pieces: " + string.Join(" + ", e.Cards) + (e.Payoffs.Length == 0 ? ". " : ". Payoffs present: " + string.Join(", ", e.Payoffs) + ". ") + string.Join(" ", e.Steps) + " Output: " + string.Join("; ", e.Outputs) + ". Requires: " + string.Join("; ", e.Requirements); }
            else if (analysisTab == 2) {
                var r = report.Recommendations[index];
                title = r.CutName + " -> " + r.Add.Name;
                var previous = state.History.LastOrDefault(h => h.Fingerprint == report.Fingerprint && h.RecommendationId == r.Id);
                text = r.Benefit + " Tradeoff: " + r.Tradeoff + " Mana value change: " + r.ManaValueDelta + ". "
                    + (r.Eligibility == "unknown" ? "Format eligibility unknown." : "Checked against bundled format rules.")
                    + (previous == null ? "" : " Your decision: " + previous.Decision);
                Button("analysis-consider:" + index, "Consider", 1360, y + 10, 170, () => RecordAnalysis(r.Id, "consider"), !busy, height: 30);
                Button("analysis-keep:" + index, "Keep current", 1360, y + 48, 170, () => RecordAnalysis(r.Id, "keep"), !busy, height: 30);
                Button("analysis-dismiss:" + index, "Dismiss", 1360, y + 86, 170, () => RecordAnalysis(r.Id, "dismiss"), !busy, height: 30);
            } else if (analysisTab == 3) {
                var c = report.Cards[index]; title = c.Quantity + "x " + c.Name + " (" + c.Section + ")" + (c.ProtectedCard ? " - preserved" : "");
                text = string.Join(" | ", c.Functions.Select(f => f.Role + " [" + f.Event + "]: " + f.Provides + (f.Requires.Length == 0 ? "" : " Needs " + string.Join("; ", f.Requires))));
            } else {
                var h = state.History[state.History.Length - 1 - index]; title = h.Decision + " - " + (h.Fingerprint == report.Fingerprint ? "current deck version" : "earlier deck version");
                text = h.RecommendationId + (h.RecommendationId.Length == 0 ? "" : ". ") + h.Note;
            }
            canvas.Text(title, 64, y + 12, Gold, .77f, analysisTab == 2 ? 1270 : 1470, 2, true);
            canvas.Text(text.Length == 0 ? "No function was confidently detected; inspect this card's rules." : text, 64, y + 46, Ink, .65f, analysisTab == 2 ? 1270 : 1470, 4);
        }
        if (count == 0) canvas.Text(analysisTab == 1 ? "No lines from the curated engine registry were found. This does not mean the deck lacks synergy."
            : analysisTab == 2 ? "No supported paired swaps match this list. Review the findings and card functions."
            : analysisTab == 4 ? "Record a recommendation decision or a playtest observation to start your history." : "Nothing to show in this view.", 72, 390, Muted, .9f, 1430, 4);
        Button("analysis-prev", "Previous", 48, 920, 150, () => analysisPage--, !busy && analysisPage > 0, height: 32);
        Button("analysis-next", "Next", 210, 920, 150, () => analysisPage++, !busy && (analysisPage + 1) * 4 < count, height: 32);
        canvas.Text($"Page {analysisPage + 1} / {Math.Max(1, (count + 3) / 4)} | Strategic estimates; curated engines are not simulator proofs.", 385, 929, Muted, .62f, 1160, 1);
    }
}
