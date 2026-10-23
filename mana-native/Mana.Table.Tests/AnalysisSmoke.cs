using Mana.Renderer;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using System.Text.Json;
using Point = Microsoft.Xna.Framework.Point;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;

namespace Mana.Table;

internal sealed class AnalysisSmoke(TableWindow.Probe view) : ITableAutomation
{
    private readonly DateTime deadline = DateTime.UtcNow.AddMinutes(5);
    private readonly List<string> checks = [];
    private int stage, mousePhase, next, wheel, evidenceFrames;
    private readonly List<string> renderedEvidence = [];
    private Point mouse;
    private string version = "", candidate = "";
    private long revision;
    private string[] relatedNames = [];
    private bool RelationshipNamesVisible() => relatedNames.All(name => view.Frame!.Drawing.Commands.OfType<DrawCommand.Text>()
        .Any(text => text.X >= 532 && text.X < 1090 && text.Value == name));
    public string? ExpectedClick { get; private set; }
    public bool ClickAccepted { get; set; }
    public MouseState ReadMouse() {
        bool down = mousePhase == 1; if (mousePhase is 1 or 2) mousePhase++;
        return new(mouse.X, mouse.Y, wheel, down ? ButtonState.Pressed : ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);
    }
    private void Click(string id, int target) {
        var hit = view.Hits.LastOrDefault(h => h.Id == id) ?? throw new InvalidOperationException("Missing analysis control: " + id);
        mouse = SmokePointer.Hit(view, hit);
        ExpectedClick = id; ClickAccepted = false; next = target; mousePhase = 1;
    }
    private void Check(bool condition, string text) { if (!condition) { Capture("analysis-failure"); throw new InvalidOperationException("Analysis stage " + stage + ": " + text); } checks.Add(text); File.AppendAllText(Path.Combine(view.Profile, "analysis-progress.log"), text + "\n"); }
    private void Capture(string name) {
        using var file = File.Create(Path.Combine(view.Profile, name + ".png"));
        view.Surface.SaveAsPng(file, view.Surface.Width, view.Surface.Height);
        view.CaptureWindow(Path.Combine(view.Profile, name + "-window.png"));
    }
    public void Frame(Texture2D surface) {
        if (DateTime.UtcNow > deadline) { Capture("analysis-failure"); throw new TimeoutException("Analysis smoke timeout at " + stage); }
        if (view.Error.Length > 0) throw new InvalidOperationException(view.Error);
        if (mousePhase is 1 or 2) return;
        if (mousePhase == 3) { Check(ClickAccepted, "Accepted click " + ExpectedClick); mousePhase = 0; ExpectedClick = null; stage = next; }
        if (!view.Loaded || view.Busy || view.ActivityOpen) return;
        if (view.DeckEditor && !view.WorkshopCurrent || view.AnalysisOpen && !view.AnalysisCurrent) return;
        switch (stage) {
            case 0: view.ImportDeck("Native Toph analysis", AnalysisRegression.List); stage++; break;
            case 1:
                if (!view.DeckEditor) break;
                Click("deck-search-deck", 24); break;
            case 24: view.TypeDeckText("Forest"); stage = 25; break;
            case 25:
                Check(view.DeckFilter == "Forest", "A real deck search precedes analysis navigation");
                Click("deck-analysis", 2); break;
            case 2:
                if (!view.AnalysisOpen || view.Analysis == null) break;
                Check(view.Analysis.Report.Findings.Any(f => f.Id == "curio-lattice"), "Native review displays the shared dependency findings");
                version = view.Workshop!.VersionId; revision = view.DeckDetails!.Revision;
                Check(view.Analysis.Report.Fingerprint == version, "Analysis shares the version used by tests and restores");
                string[] navigation = ["workshop-build", "workshop-test", "workshop-results", "deck-analysis", "workshop-plan-summary", "workshop-source", "workshop-history"];
                var nav = navigation.Select(id => view.Hits.Single(h => h.Id == id)).ToArray();
                Check(nav.SelectMany((hit, i) => nav.Skip(i + 1).Select(other => !hit.Bounds.Intersects(other.Bounds))).All(value => value), "Build, Test, Results, analysis, shared plan, source and versions remain visible without overlap");
                Check(view.Analysis.Report.Profile.Plan.Length == 0 && view.Analysis.Report.Profile.TargetTurn == 0
                    && view.Analysis.Report.Metrics.LandProbability != null && view.Analysis.Report.Findings.All(f => f.Id != "choose-plan"), "The first review works without any goals or manual target");
                Check(view.Hits.Any(h => h.Id == "analysis-preferences") && view.Hits.All(h => h.Id != "analysis-pace"), "Optional settings do not occupy the review overview");
                Capture("analysis-findings"); Click("analysis-preferences", 26); break;
            case 26:
                Check(view.WorkshopDialog == "analysis-preferences" && new[] { "analysis-pace:relaxed", "analysis-pace:balanced", "analysis-pace:fast", "analysis-strategy:unspecified", "analysis-turn:0" }.All(id => view.Hits.Any(h => h.Id == id)), "Preferences offer visible answers and automatic defaults instead of cycling values");
                Capture("analysis-preferences"); Click("analysis-turn:5", 28); break;
            case 28:
                Check(view.Analysis!.Report.Profile.TargetTurn == 5 && view.Analysis.Report.Metrics.LandGoal == 5, "A direct turn answer overrides the automatic land target");
                Click("analysis-turn:0", 29); break;
            case 29:
                Check(view.Analysis!.Report.Profile.TargetTurn == 0 && view.Analysis.Report.Metrics.LandGoal == 4, "Automatic restores the commander's land target without saving an inferred choice");
                Click("analysis-pace:fast", 3); break;
            case 3:
                Check(view.Analysis!.Report.Profile.Pace == "fast", "Pace can be edited with a real native control");
                Check(view.Analysis.MetadataRevision == view.Workshop!.MetadataRevision && view.Workshop.VersionId == version, "Saving preferences refreshes shared metadata without changing the played list");
                Click("workshop-dialog-close", 27); break;
            case 27: Click("analysis-model", 4); break;
            case 4:
                Check(view.WorkshopDialog.Length == 0 && view.AnalysisOpen && view.Hits.Any(hit => hit.Id == "analysis-detail-close")
                    && view.Hits.All(hit => hit.Id is not ("analysis-detail-prev" or "analysis-detail-next")),
                    "Probability assumptions and limitations share Tune's evidence pane without a modal or detail pages");
                Capture("analysis-model"); renderedEvidence.Clear(); evidenceFrames = 0; stage = 40; break;
            case 40:
                foreach (var text in view.Frame!.Drawing.Commands.OfType<DrawCommand.Text>().Where(text => text.X == 532 && text.Y >= 250 && text.Y < 878 && text.Value != null))
                    if (!renderedEvidence.Contains(text.Value!)) renderedEvidence.Add(text.Value!);
                string Normalize(string value) => string.Concat(value.Where(character => !char.IsWhiteSpace(character)));
                string observed = Normalize(string.Join(" ", renderedEvidence));
                string[] facts = [view.Analysis!.Report.Metrics.ProbabilityAssumptions, .. view.Analysis.Report.Limitations, view.Workshop!.Insights.Note];
                if (facts.Where(fact => fact.Length > 0).All(fact => observed.Contains(Normalize(fact), StringComparison.Ordinal))) {
                    Check(evidenceFrames > 0 && view.DeckDetails!.Revision == revision && view.Workshop.VersionId == version,
                        "Real wheel input reaches every engine-authored probability assumption, limitation and source note without changing the played list");
                    Capture("analysis-model-scrolled"); Click("analysis-detail-close", 5); break;
                }
                if (++evidenceFrames > 150) Check(false, "Scrolling must reach the complete engine-authored probability, limitations and colored-source evidence");
                mouse = SmokePointer.Screen(view, new(1000, 700)); wheel -= 120; break;
            case 5: Click("analysis-tab:1", 6); break;
            case 6:
                Check(view.Analysis!.Report.Engines.Any(e => e.Missing.Length == 0), "Native engine tab has the actual Toph lines");
                foreach (var hit in view.Hits.Where(hit => hit.Id.StartsWith("analysis-details:", StringComparison.Ordinal))) {
                    var line = view.Analysis.Report.Engines[int.Parse(hit.Id.Split(':')[1])];
                    if (line.Missing.Length > 0) Check(view.Frame!.Drawing.Commands.OfType<DrawCommand.Text>()
                        .Any(text => text.Value == "Missing: " + string.Join(" + ", line.Missing)),
                        "A missing engine variant names its actual absent card identities in the list");
                }
                Capture("analysis-engines"); Click("analysis-details:0", 7); break;
            case 7:
                Check(view.WorkshopDialog.Length == 0 && view.Hits.Any(hit => hit.Id == "analysis-card:0"),
                    "An engine's actual source cards sit beside its conditions and steps in Tune");
                relatedNames = view.Analysis!.Report.Engines[0].Cards.Concat(view.Analysis.Report.Engines[0].Payoffs)
                    .Where(name => view.DeckDetails!.Entries.Any(entry => entry.Card.Name == name)).Distinct().ToArray();
                Check(RelationshipNamesVisible(), "The selected engine presents its actual related card identities together before reading");
                Capture("analysis-engine-details"); Click("analysis-card:1", 70); break;
            case 70:
                Check(view.AnalysisOpen && view.WorkshopDialog.Length == 0 && view.DeckDetails!.Revision == revision
                    && view.Workshop!.VersionId == version && view.Hits.Any(hit => hit.Id == "workspace-card-reading") && RelationshipNamesVisible(),
                    "Reading a related card retains its partner card identities and selected relationship beside the reader, with no deck/version mutation");
                Click("analysis-card:0", 72); break;
            case 72:
                Check(RelationshipNamesVisible() && view.Hits.Any(hit => hit.Id == "analysis-card:1"),
                    "Reading Ashaya leaves Earthbending Student and the other relationship cards visible and independently reachable");
                Capture("analysis-related-reader"); Click("workspace-card-reading", 71); break;
            case 71: Click("analysis-show-cards", 8); break;
            case 8:
                Check(!view.AnalysisOpen && view.DeckEditor && view.DeckFilter.Length == 0 && view.Hits.Any(h => h.Id == "analysis-clear-cards")
                    && view.Hits.Any(h => h.Id.StartsWith("deck-card:")), "Engine details clear an unrelated search and reveal related editable cards");
                Capture("analysis-related-cards"); Click("analysis-clear-cards", 9); break;
            case 9:
                view.SetWorkshopPlan("Turn artifacts into a recurring sacrifice finish.", "Does the engine recover after removal?"); stage++; break;
            case 10: Click("deck-analysis", 11); break;
            case 11:
                Check(view.Analysis!.Report.Profile.Plan == view.Workshop!.Plan && view.Workshop.Plan.StartsWith("Turn artifacts"), "A builder plan edit updates the strategy review");
                Click("analysis-tab:2", 12); break;
            case 12:
                Check(view.Analysis!.Report.Recommendations.Length > 0, "Shared findings produce supported paired recommendations");
                candidate = view.Analysis.Report.Recommendations[0].Add.Name;
                Capture("analysis-swaps"); Click("analysis-details:0", 13); break;
            case 13:
                Check(view.WorkshopDialog.Length == 0 && view.Hits.Any(hit => hit.Id == "analysis-consider:0"),
                    "Swap evidence keeps its conditions, tradeoffs and suggested rules in the existing Tune pane");
                Capture("analysis-swap-details"); stage = 14; break;
            case 14: Click("analysis-consider:0", 15); break;
            case 15:
                Check(view.Workshop!.Considering.Any(c => c.Card.Card.Name == candidate), "Save to Considering persists the actual candidate card");
                Check(view.Analysis!.History.Any(h => h.Decision == "consider" && h.Fingerprint == version), "The candidate and exact-version decision save together");
                Check(view.Analysis.MetadataRevision == view.Workshop.MetadataRevision, "The analysis action returns fresh workshop state");
                view.SetAnalysisProfile(view.Analysis.Report.Profile with { ProtectedCards = ["Lifecrafter's Bestiary"] }); stage++; break;
            case 16:
                var favorite = view.DeckDetails!.Entries.Single(e => e.Card.Name == "Lifecrafter's Bestiary");
                Check(view.Workshop!.Roles.Any(r => r.CardId == favorite.Id && r.Pinned), "Analysis favorites share Build's keep markers");
                Check(view.Analysis!.Report.Recommendations.All(r => r.CutName != "Lifecrafter's Bestiary"), "Favorite cards are excluded from recommended cuts");
                Click("analysis-tab:3", 17); break;
            case 17: Capture("analysis-functions"); Click("analysis-tab:4", 18); break;
            case 18:
                Capture("analysis-decisions"); Click("workshop-history", 19); break;
            case 19:
                Check(view.WorkshopDialog == "versions", "Analysis uses the workshop's existing version history");
                Click("workshop-dialog-close", 20); break;
            case 20: Click("workshop-test", 21); break;
            case 21:
                Check(view.WorkshopTab == "test" && view.Hits.Any(h => h.Id == "workshop-hand-new"), "Analysis moves into the existing sampler and real AI test setup");
                Check(view.Workshop!.Question == "Does the engine recover after removal?", "The test keeps its shared question");
                Click("workshop-results", 22); break;
            case 22:
                Check(view.WorkshopTab == "results" && view.Hits.Any(h => h.Id == "workshop-results-test"), "Recorded test results stay in their existing workspace");
                Click("workshop-build", 23); break;
            case 23:
                Check(!view.AnalysisOpen && view.DeckEditor, "Build restores the editable deck");
                Check(view.DeckDetails!.Entries.Sum(e => e.Quantity) == 100, "Review navigation leaves playable cards unchanged");
                Check(view.DeckDetails.Revision == revision && view.Workshop!.VersionId == version, "Analysis and test navigation preserve the editor revision and playable version");
                File.WriteAllText(Path.Combine(view.Profile, "analysis-ui-results.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
                view.Exit(); stage++; break;
        }
    }
}
