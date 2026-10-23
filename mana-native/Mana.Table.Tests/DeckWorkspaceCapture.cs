using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using System.Text.Json;
using Point = Microsoft.Xna.Framework.Point;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;

namespace Mana.Table;

// A visual-review journey through a real saved document. This makes no rules or
// visual-quality claim; reviewers must inspect the captured native windows.
internal sealed class DeckWorkspaceCapture(TableWindow.Probe view) : ITableAutomation
{
    private readonly DateTime deadline = DateTime.UtcNow.AddSeconds(120);
    private readonly List<string> captures = [];
    private int stage, next, mousePhase;
    private Point mouse;
    private double settleUntil;
    public string? ExpectedClick { get; private set; }
    public bool ClickAccepted { get; set; }

    private void Capture(string name)
    {
        string prefix = Path.Combine(view.Profile, "workspace-" + name);
        view.CaptureWindow(prefix + "-window.png");
        using (var stream = File.Create(prefix + ".png")) view.Surface.SaveAsPng(stream, view.Surface.Width, view.Surface.Height);
        File.WriteAllText(prefix + ".json", JsonSerializer.Serialize(new {
            view.SelectedDeck, view.DeckDetails, view.DeckEditor, view.Analysis,
            view.AnalysisOpen, view.AnalysisCurrent, view.Workshop, view.WorkshopHand,
            view.WorkshopTab, view.WorkshopDialog, view.SetupModal, view.TextScale,
            view.Loaded, view.Busy, view.ActivityOpen, view.CatalogBusy, view.WorkshopCurrent,
            Controls = view.Hits.Select(hit => hit.Id).ToArray()
        }, new JsonSerializerOptions { WriteIndented = true }));
        captures.Add(name);
    }

    private void Click(string id, int target)
    {
        var hit = view.Hits.LastOrDefault(hit => hit.Id == id)
            ?? throw new InvalidOperationException("Workspace capture missing " + id + " at stage " + stage);
        mouse = SmokePointer.Hit(view, hit); ExpectedClick = id; ClickAccepted = false;
        mousePhase = 1; next = target;
    }

    private void Advance(int target) { stage = target; settleUntil = view.Now + .9; }

    public MouseState ReadMouse()
    {
        bool down = mousePhase == 1;
        if (mousePhase is 1 or 2) mousePhase++;
        return new(mouse.X, mouse.Y, 0, down ? ButtonState.Pressed : ButtonState.Released,
            ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);
    }

    public void Frame(Texture2D surface)
    {
        if (DateTime.UtcNow > deadline) { Capture("failure"); throw new TimeoutException("Workspace capture timed out at stage " + stage); }
        if (view.Error.Length > 0 || view.WorkshopError.Length > 0) {
            Capture("failure"); throw new InvalidOperationException(view.Error + " " + view.WorkshopError);
        }
        if (mousePhase is 1 or 2) return;
        if (mousePhase == 3) {
            if (!ClickAccepted) { Capture("failure"); throw new InvalidOperationException("Workspace capture rejected " + ExpectedClick); }
            mousePhase = 0; ExpectedClick = null; Advance(next);
        }
        // Catalog search may be pending at home, where the editor deliberately
        // does not run it. Only Build captures depend on that search settling.
        if (!view.Loaded || view.Busy || view.ActivityOpen
            || view.DeckEditor && view.WorkshopTab == "build" && view.CatalogBusy
            || view.DeckEditor && !view.WorkshopCurrent || view.AnalysisOpen && !view.AnalysisCurrent) {
            settleUntil = view.Now + .9; return;
        }
        if (view.Now < settleUntil) return;
        switch (stage) {
            case 0:
                if (view.SelectedDeck == null || view.DeckDetails == null)
                    throw new InvalidOperationException("Seed a saved selected deck before --capture-deck-workspace.");
                Capture("00-home"); Click("edit-deck", 1); break;
            case 1:
                Capture("01-build"); Click("deck-analysis", 2); break;
            case 2:
                Capture("02-tune"); Click("analysis-tab:1", 12);
                break;
            case 12:
                if (view.Hits.FirstOrDefault(hit => hit.Id.StartsWith("analysis-details:", StringComparison.Ordinal)) is { } engineEntry)
                    Click(engineEntry.Id, 3);
                else Click("analysis-tab:0", 13);
                break;
            case 13:
                var findingEntry = view.Hits.FirstOrDefault(hit => hit.Id.StartsWith("analysis-details:", StringComparison.Ordinal)
                    && int.TryParse(hit.Id.AsSpan("analysis-details:".Length), out int index)
                    && view.Analysis is { } analysis && index < analysis.Report.Findings.Length
                    && analysis.Report.Findings[index].Cards.Any(name => view.DeckDetails!.Entries.Any(entry => entry.Card.Name.Equals(name, StringComparison.OrdinalIgnoreCase))));
                if (findingEntry != null) Click(findingEntry.Id, 3);
                else if (view.Hits.Any(hit => hit.Id == "analysis-next")) Click("analysis-next", 13);
                else Advance(3);
                break;
            case 3:
                Capture("03-tune-context");
                if (view.Hits.Any(hit => hit.Id == "workspace-card-reading")) Advance(4);
                else if (view.Hits.FirstOrDefault(hit => hit.Id.StartsWith("analysis-card:", StringComparison.Ordinal)) is { } card)
                    Click(card.Id, 4);
                else Advance(5);
                break;
            case 4:
                Capture("04-tune-card"); Click("workspace-card-reading", 5); break;
            case 5:
                Click("workshop-test", 6); break;
            case 6:
                Capture("05-test"); Click("workshop-hand-new", 7); break;
            case 7:
                Capture("06-test-sample"); Click("workshop-hand-card:0", 8); break;
            case 8:
                Capture("07-test-card"); Click("workspace-card-reading", 9); break;
            case 9:
                Click("deck-done", 10); break;
            case 10:
                Capture("08-home-return"); Advance(11); break;
            case 11:
                File.WriteAllText(Path.Combine(view.Profile, "workspace-capture-results.json"), JsonSerializer.Serialize(new {
                    Purpose = "Native visual review of the current saved deck; sample hand is draw-only practice, not a rules-game result.",
                    DeckId = view.SelectedDeck?.Id, Captures = captures
                }, new JsonSerializerOptions { WriteIndented = true }));
                view.Exit(); break;
        }
    }
}
