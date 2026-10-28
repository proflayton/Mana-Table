using System.Text.Json;
using Mana.Renderer;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

// CI window captures of actual settings input and text geometry. Controlled
// frames keep this local preference journey independent of engine advancement.
internal sealed class AudioSettingsSmoke : ITableAutomation
{
    private static readonly string[] ControlIds = ["motion", "sound", "audio-volume", "audio-volume-down", "audio-volume-up", "text-size", "updates-settings", "close-overlay"];
    private readonly TableWindow.Probe view;
    private readonly InteractionScenarioEngine scenario;
    private readonly Queue<(double Delay, Action Action)> steps = [];
    private readonly List<string> checks = [];
    private readonly Dictionary<string, Rectangle> originalBounds = [];
    private MouseState mouse;
    private double lastStep;
    private BoardView camera;
    public string? ExpectedClick { get; private set; }
    public bool ClickAccepted { get; set; }
    public MouseState ReadMouse() => mouse;

    internal static void Run(string profile, Point? size)
    {
        new PlayPreferences { Automatic = false, ReducedMotion = true, TextScale = 1.3f }.Save(profile);
        var scenario = new InteractionScenarioEngine(); scenario.Present(PresentationSmoke.Example());
        using var game = new TableWindow(TestTableServices.For(scenario), profile, null, "controlled-frames", size,
            initialState: scenario.State, artSource: new CardReadingArtFixture(), enableLiveTableStudy: true);
        game.Automation = new AudioSettingsSmoke(game.TestPort, scenario);
        game.Run();
    }

    private AudioSettingsSmoke(TableWindow.Probe view, InteractionScenarioEngine scenario)
    {
        this.view = view; this.scenario = scenario;
        Step(.5, () => {
            Check(view.TextScale == 1.3f && !view.Automatic && scenario.Replies.Count == 0,
                "The controlled native settings journey starts with 130% text and no automatic engine action");
        });
        Click("settings");
        Step(.15, () => {
            Check(view.Overlay == "settings" && PlayPreferences.Load(view.Profile).Audio.Master is { Volume: 1, Muted: false },
                "Settings opens with the default audible master volume and a separate mute control");
            CheckText(1, "Mute");
            foreach (string id in ControlIds) originalBounds[id] = Bounds(id);
            camera = view.BoardView;
            CheckControls(); Capture("01-audio-default");
            Move(TrackPoint(.9f), ButtonState.Pressed);
        });
        Step(.1, () => {
            Check(PlayPreferences.Load(view.Profile).Audio.Master.Volume == 1,
                "The volume press starts a live adjustment without saving before release");
            Move(TrackPoint(.4f), ButtonState.Pressed);
        });
        Step(.1, () => {
            scenario.Present(scenario.State with { StackInterruptionVersion = scenario.State.StackInterruptionVersion + 1 });
            view.Accept(scenario.State);
            Move(TrackPoint(.4f));
        });
        Step(.15, () => {
            Check(PlayPreferences.Load(view.Profile).Audio.Master is { Muted: false } level && Near(level.Volume, .4f),
                "Releasing the actual slider at 40% persists its level across a fresh decision publication");
            CheckText(.4f, "Mute"); CheckControls(); Capture("02-audio-volume-40");
        });
        Click("sound");
        Step(.15, () => {
            Check(PlayPreferences.Load(view.Profile).Audio.Master is { Muted: true } level && Near(level.Volume, .4f),
                "The separate mute action retains the chosen 40% level");
            CheckText(.4f, "Unmute"); CheckControls(); Capture("03-audio-muted-40");
        });
        Step(.1, () => {
            Check(scenario.Replies.Count == 0 && view.Match?.Decision?.Id == scenario.State.Decision!.Id && view.BoardView == camera,
                "The full settings journey leaves the current engine decision, camera and gameplay untouched");
            Check(new[] { "01-audio-default", "02-audio-volume-40", "03-audio-muted-40" }.All(name =>
                    new[] { ".png", "-window.png", "-state.json" }.All(suffix => File.Exists(Path.Combine(view.Profile, name + suffix)))),
                "All three settings states retain fitted-surface captures, physical-window captures and matching frame observations");
            File.WriteAllText(Path.Combine(view.Profile, "audio-settings-results.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            view.Exit();
        });
    }

    private Rectangle Bounds(string id) => view.Hits.Last(hit => hit.Id == id).Bounds;
    private Point TrackPoint(float volume)
    {
        var bounds = Bounds("audio-volume");
        return new(bounds.Left + (int)MathF.Round((bounds.Width - 1) * volume), bounds.Center.Y);
    }
    private void CheckControls()
    {
        var controls = ControlIds.Select(Bounds).ToArray();
        Check(ControlIds.All(id => Bounds(id) == originalBounds[id] && view.HitAt(Bounds(id).Center)?.Id == id)
                && controls.All(bounds => new Rectangle(0, 0, 1600, view.DesignHeight).Contains(bounds))
                && controls.SelectMany((first, index) => controls.Skip(index + 1).Select(second => first.Intersects(second))).All(intersects => !intersects),
            "At 130% text, all audio and neighboring settings controls remain stable, in bounds, separate and reachable");
    }
    private void CheckText(float volume, string muteLabel)
    {
        string[] text = view.Frame!.Drawing.Commands.Select(command => command switch {
            DrawCommand.Text item => item.Value, DrawCommand.CenterText item => item.Value, _ => ""
        }).ToArray();
        Check(text.Contains("Master volume") && text.Contains(volume.ToString("P0")) && text.Contains(muteLabel),
            "The rendered settings state includes its master-volume name, retained percentage and " + muteLabel + " action");
    }
    private static bool Near(float actual, float expected) => Math.Abs(actual - expected) < .006f;
    private void Move(Point logical, ButtonState left = ButtonState.Released)
    {
        var viewport = view.GraphicsDevice.Viewport;
        var point = RenderViewport.Fit(new(viewport.Width, viewport.Height), view.DesignHeight).ToPhysical(logical.ToVector2());
        mouse = new((int)MathF.Ceiling(point.X), (int)MathF.Ceiling(point.Y), 0, left, default, default, default, default);
    }
    private void Click(string id)
    {
        Step(.06, () => {
            var point = Bounds(id).Center;
            Check(view.HitAt(point)?.Id == id, "The actual pointer target is reachable: " + id);
            ExpectedClick = id; ClickAccepted = false; Move(point, ButtonState.Pressed);
        });
        Step(.06, () => Move(Bounds(id).Center));
        Step(.06, () => { Check(ClickAccepted, "Production accepts the settings pointer gesture: " + id); ExpectedClick = null; });
    }
    private void Step(double delay, Action action) => steps.Enqueue((delay, action));
    private void Check(bool passed, string description)
    {
        if (!passed) throw new InvalidOperationException(description);
        checks.Add(description);
    }
    private void Capture(string name)
    {
        using (var output = File.Create(Path.Combine(view.Profile, name + ".png"))) view.Surface.SaveAsPng(output, view.Surface.Width, view.Surface.Height);
        view.CaptureWindow(Path.Combine(view.Profile, name + "-window.png"));
        File.WriteAllText(Path.Combine(view.Profile, name + "-state.json"), JsonSerializer.Serialize(new {
            Frame = view.Frame!.Observe(), Preferences = PlayPreferences.Load(view.Profile), EngineReplies = scenario.Replies.Count
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    public void Frame(Texture2D surface)
    {
        if (view.Error.Length > 0) throw new InvalidOperationException(view.Error);
        if (view.Now > 25) throw new TimeoutException("Audio settings capture journey did not finish.");
        if (!view.Loaded || view.Busy || view.ActivityOpen) return;
        try {
            if (steps.TryPeek(out var step) && view.Now - lastStep >= step.Delay) {
                steps.Dequeue(); step.Action(); lastStep = view.Now;
            }
        } catch {
            Capture("audio-settings-failure");
            throw;
        }
    }
}
