using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using System.Text.Json;
using Point = Microsoft.Xna.Framework.Point;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;

namespace Mana.Table;

internal sealed class AnalysisSmoke(TableGame.Probe view) : ITableAutomation
{
    private readonly DateTime deadline = DateTime.UtcNow.AddMinutes(3);
    private readonly List<string> checks = [];
    private int stage, mousePhase, next;
    private Point mouse;
    public string? ExpectedClick { get; private set; }
    public bool ClickAccepted { get; set; }
    public MouseState ReadMouse() {
        bool down = mousePhase == 1; if (mousePhase is 1 or 2) mousePhase++;
        return new(mouse.X, mouse.Y, 0, down ? ButtonState.Pressed : ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);
    }
    private void Click(string id, int target) {
        var hit = view.Hits.LastOrDefault(h => h.Id == id) ?? throw new InvalidOperationException("Missing analysis control: " + id);
        var p = hit.Bounds.Center; var viewport = view.GraphicsDevice.Viewport;
        float scale = Math.Min(viewport.Width / 1600f, viewport.Height / (float)view.DesignHeight);
        mouse = new((int)((viewport.Width - 1600 * scale) / 2 + p.X * scale), (int)((viewport.Height - view.DesignHeight * scale) / 2 + p.Y * scale));
        ExpectedClick = id; ClickAccepted = false; next = target; mousePhase = 1;
    }
    private void Check(bool condition, string text) { if (!condition) throw new InvalidOperationException(text); checks.Add(text); }
    private void Capture(string name) { using var file = File.Create(Path.Combine(view.Profile, name + ".png")); view.Surface.SaveAsPng(file, view.Surface.Width, view.Surface.Height); }
    public void Frame(Texture2D surface) {
        if (DateTime.UtcNow > deadline) throw new TimeoutException("Analysis smoke timeout at " + stage);
        if (view.Error.Length > 0) throw new InvalidOperationException(view.Error);
        if (mousePhase is 1 or 2) return;
        if (mousePhase == 3) { Check(ClickAccepted, "Accepted click " + ExpectedClick); mousePhase = 0; ExpectedClick = null; stage = next; }
        if (!view.Loaded || view.Busy || view.ActivityOpen) return;
        switch (stage) {
            case 0: view.ImportDeck("Native Toph analysis", AnalysisRegression.List); stage++; break;
            case 1:
                if (!view.DeckEditor) break;
                Click("deck-analysis", 2); break;
            case 2:
                if (!view.AnalysisOpen || view.Analysis == null) break;
                Check(view.Analysis.Report.Findings.Any(f => f.Id == "curio-lattice"), "Native review displays the shared dependency findings");
                Capture("analysis-findings"); Click("analysis-pace", 3); break;
            case 3:
                Check(view.Analysis!.Report.Profile.Pace == "fast", "Pace can be edited with a real native control");
                Click("analysis-tab:1", 4); break;
            case 4:
                Check(view.Analysis!.Report.Engines.Any(e => e.Missing.Length == 0), "Native engine tab has the actual Toph lines");
                Capture("analysis-engines"); Click("analysis-tab:2", 5); break;
            case 5:
                Capture("analysis-swaps"); Click("analysis-tab:3", 6); break;
            case 6:
                Capture("analysis-functions"); Click("analysis-tab:4", 7); break;
            case 7:
                Capture("analysis-history"); Click("analysis-back", 8); break;
            case 8:
                Check(!view.AnalysisOpen && view.DeckEditor, "Back restores the editable deck");
                Check(view.DeckDetails!.Entries.Sum(e => e.Quantity) == 100, "Review navigation leaves playable cards unchanged");
                File.WriteAllText(Path.Combine(view.Profile, "analysis-ui-results.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
                view.Exit(); stage++; break;
        }
    }
}
