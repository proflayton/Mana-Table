using System.Text.Json;
using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Color = Microsoft.Xna.Framework.Color;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

// Control geometry survives an observer update or role change. Different public
// states retain their own explanation; unchanged observations retain their text.
internal sealed class AutoDockSmoke(TableWindow.Probe view, InteractionScenarioEngine scenario) : ITableAutomation
{
    private int stage;
    private double changed;
    private readonly List<string> checks = [];
    private Color[] quiet = [];
    private Rectangle autoBounds, holdBounds;
    private MouseState mouse = new(0, 0, 0, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);
    public string? ExpectedClick => "hold";
    public bool ClickAccepted { get; set; }
    public MouseState ReadMouse() => mouse;
    private void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks.Add(message); }
    private void Publish(GameSnapshot state) { scenario.Present(state); view.Accept(scenario.State); }
    private void Next() { stage++; changed = view.Now; }
    private static Color[] Dock(Texture2D surface) {
        // The status region excludes the deliberate running dots below it.
        var area = RenderViewport.ForSurface(new(surface.Width, surface.Height), 900).ToSurface(new(1266, 726, 296, 80));
        var pixels = new Color[area.Width * area.Height];
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
                Check(view.DecisionDock?.Status == DecisionDockStatus.AutoPassing && !view.Hits.Any(h => h.Id is "confirm" or "cancel"), "Engine-authorized empty automatic priority exposes no enabled gameplay action buttons");
                if (quiet.Length == 0) {
                    quiet = Dock(surface); autoBounds = view.Hits.Single(hit => hit.Id == "auto").Bounds;
                    holdBounds = view.Hits.Single(hit => hit.Id == "hold").Bounds;
                    Capture(surface, "01-auto-following"); view.Accept(state); changed = view.Now; break;
                }
                Check(quiet.SequenceEqual(Dock(surface)), "An unchanged observer snapshot preserves the actual priority status pixels");
                Publish(state with { Decision = null, Status = "resolving", ActionClass = "resolving", PriorityPlayerId = null, DecisionOwnerId = null }); Next(); break;
            case 2:
                Check(view.DecisionDock is { Status: DecisionDockStatus.Resolving, Headline: "Resolving", ActorId: null }
                    && !view.Hits.Any(hit => hit.Id is "confirm" or "cancel")
                    && view.Hits.Single(hit => hit.Id == "auto").Bounds == autoBounds && view.Hits.Single(hit => hit.Id == "hold").Bounds == holdBounds,
                    "An authoritative resolving state changes its explanation while retaining playback control positions and no gameplay action");
                Publish(state with { Status = "playing", ActionClass = "priority", PriorityPlayerId = 0, DecisionOwnerId = 0,
                    PhaseKey = "COMBAT_BEGIN", Decision = PresentationSmoke.Example().Decision }); Next(); break;
            case 3:
                Check(view.DecisionDock is { Status: DecisionDockStatus.AutoPassing, ActorId: 0 }
                    && !view.Hits.Any(hit => hit.Id is "confirm" or "cancel"), "A new engine-authorized empty phase restores the actual local Auto state");
                Check(view.Hits.Single(hit => hit.Id == "auto").Bounds == autoBounds && view.Hits.Single(hit => hit.Id == "hold").Bounds == holdBounds,
                    "Auto and Hold keep their positions between priority and processing states");
                Publish(state with { Decision = state.Decision! with { CanAutoPass = false } }); Next(); break;
            case 4:
                Check(view.DecisionDock?.Status == DecisionDockStatus.Priority && view.Hits.Any(h => h.Id == "confirm"), "A response requiring attention immediately restores the real action control");
                Capture(surface, "02-auto-response");
                Publish(state with { Decision = state.Decision! with { CanAutoPass = true } }); Next(); break;
            case 5:
                MouseAt(view.Hits.Single(h => h.Id == "hold").Bounds.Center, ButtonState.Pressed); Next(); break;
            case 6:
                Check(view.Pressed?.Id == "hold", "Pointer press lands on Hold while Auto is following");
                Publish(state with { Decision = null, Status = "resolving", ActionClass = "resolving", PriorityPlayerId = null, DecisionOwnerId = null });
                MouseAt(view.Pressed!.Bounds.Center, ButtonState.Released); Next(); break;
            case 7:
                Check(ClickAccepted && view.Held, "Hold click survives a decision change between pointer press and release");
                Publish(state with { Status = "playing", ActionClass = "priority", PriorityPlayerId = 0, DecisionOwnerId = 0, Decision = PresentationSmoke.Example().Decision }); Next(); break;
            case 8:
                Check(view.Hits.Any(h => h.Id == "confirm"), "Hold exposes manual controls even for an empty priority window");
                view.SetAutomatic(false); Next(); break;
            case 9:
                Check(view.Hits.Any(h => h.Id == "confirm"), "Full control retains every manual priority window");
                view.SetAutomatic(true);
                Publish(state with { Turn = state.Turn + 1, ActionClass = "block", PriorityPlayerId = null,
                    Decision = state.Decision! with { Intent = DecisionIntent.Block, CanAutoPass = false } }); Next(); break;
            case 10:
                Check(!view.Held && view.Hits.Any(h => h.Id == "confirm"), "Auto presents blocking as a required decision immediately after a turn change");
                Capture(surface, "03-auto-blockers");
                Check(scenario.Replies.Count == 0, "Dock rendering and playback controls never submit gameplay choices");
                File.WriteAllText(Path.Combine(view.Profile, "auto-dock-results.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
                view.Exit(); break;
        }
    }
}
