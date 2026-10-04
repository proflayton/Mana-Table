using System.Text.Json;
using Mana.Contracts;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Color = Microsoft.Xna.Framework.Color;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

// Deliberately feeds the frames the old client used to expose. The dock must remain
// visually identical even when a batch yields while waiting on another human.
internal sealed class AutoDockSmoke(TableGame.Probe view, InteractionScenarioEngine scenario) : ITableAutomation
{
    private int stage;
    private double changed;
    private readonly List<string> checks = [];
    private Color[] quiet = [];
    private MouseState mouse = new(0, 0, 0, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);
    public string? ExpectedClick => "hold";
    public bool ClickAccepted { get; set; }
    public MouseState ReadMouse() => mouse;
    private void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks.Add(message); }
    private void Publish(GameSnapshot state) { scenario.Present(state); view.Accept(scenario.State); }
    private void Next() { stage++; changed = view.Now; }
    private static Color[] Dock(Texture2D surface) {
        var area = new Rectangle(1266, 726, 296, 120); var pixels = new Color[area.Width * area.Height];
        surface.GetData(0, area, pixels, 0, pixels.Length); return pixels;
    }
    private void Capture(Texture2D surface, string name) {
        using var file = File.Create(Path.Combine(view.Profile, name + ".png")); surface.SaveAsPng(file, surface.Width, surface.Height);
    }
    private void MouseAt(Point point, ButtonState button) {
        var viewport = view.GraphicsDevice.Viewport; float scale = Math.Min(viewport.Width / 1600f, viewport.Height / 900f);
        mouse = new((int)(point.X * scale + (viewport.Width - 1600 * scale) / 2), (int)(point.Y * scale + (viewport.Height - 900 * scale) / 2), 0,
            button, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);
    }
    public void Frame(Texture2D surface)
    {
        if (view.Error.Length > 0) throw new InvalidOperationException(view.Error);
        if (view.Now > 20) throw new TimeoutException("Auto dock review at stage " + stage);
        if (view.Now - changed < (stage == 0 ? 2 : .08)) return;
        var state = scenario.State;
        switch (stage) {
            case 0: view.SetAutomatic(true); Next(); break;
            case 1:
                quiet = Dock(surface);
                Check(!view.Hits.Any(h => h.Id is "confirm" or "cancel"), "Empty automatic priority exposes no flashing action buttons");
                Capture(surface, "01-auto-following");
                Publish(state with { Decision = null, Status = "resolving" }); Next(); break;
            case 2:
                Check(quiet.SequenceEqual(Dock(surface)), "Resolving and empty priority render identical dock pixels");
                Publish(state with { Status = "playing", PhaseKey = "COMBAT_BEGIN", Decision = PresentationSmoke.Example().Decision }); Next(); break;
            case 3:
                Check(quiet.SequenceEqual(Dock(surface)), "A new empty phase leaves dock text and layout unchanged");
                Check(view.Hits.Any(h => h.Id == "auto") && view.Hits.Any(h => h.Id == "hold"), "Auto and Hold remain enabled between decisions");
                Publish(state with { Decision = state.Decision! with { CanAutoPass = false } }); Next(); break;
            case 4:
                Check(view.Hits.Any(h => h.Id == "confirm") && !quiet.SequenceEqual(Dock(surface)), "A playable response immediately restores the real action control");
                Capture(surface, "02-auto-response");
                Publish(state with { Decision = state.Decision! with { CanAutoPass = true } }); Next(); break;
            case 5:
                MouseAt(view.Hits.Single(h => h.Id == "hold").Bounds.Center, ButtonState.Pressed); Next(); break;
            case 6:
                Check(view.Pressed?.Id == "hold", "Pointer press lands on Hold while Auto is following");
                Publish(state with { Decision = null, Status = "resolving" });
                MouseAt(view.Pressed!.Bounds.Center, ButtonState.Released); Next(); break;
            case 7:
                Check(ClickAccepted && view.Held, "Hold click survives a decision change between pointer press and release");
                Publish(state with { Status = "playing", Decision = PresentationSmoke.Example().Decision }); Next(); break;
            case 8:
                Check(view.Hits.Any(h => h.Id == "confirm"), "Hold exposes manual controls even for an empty priority window");
                view.SetAutomatic(false); Next(); break;
            case 9:
                Check(view.Hits.Any(h => h.Id == "confirm"), "Full control retains every manual priority window");
                view.SetAutomatic(true);
                Publish(state with { Turn = state.Turn + 1, Decision = state.Decision! with { Intent = DecisionIntent.Block, CanAutoPass = false } }); Next(); break;
            case 10:
                Check(!view.Held && view.Hits.Any(h => h.Id == "confirm"), "Auto presents blocking as a required decision immediately after a turn change");
                Capture(surface, "03-auto-blockers");
                Check(scenario.Replies.Count == 0, "Dock rendering and playback controls never submit gameplay choices");
                File.WriteAllText(Path.Combine(view.Profile, "auto-dock-results.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
                view.Exit(); break;
        }
    }
}
