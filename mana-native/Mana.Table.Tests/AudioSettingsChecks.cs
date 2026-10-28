using System.Text.Json;
using Mana.Audio;
using Mana.Contracts;
using Mana.Renderer;
using Mana.Scenarios;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Keys = Microsoft.Xna.Framework.Input.Keys;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal static class AudioSettingsChecks
{
    internal static void Run(string profile, Action<bool, string> check)
    {
        Persistence(profile, check);
        LiveVolume(profile, check);
        Keyboard(profile, check);
        LostCapture(profile, check);
        SilentReminder(profile, check);
        Geometry(profile, check);
    }

    private static void Persistence(string profile, Action<bool, string> check)
    {
        string path = Path.Combine(profile, "audio-settings-storage"); Directory.CreateDirectory(path);
        string file = Path.Combine(path, "play-preferences.json");
        foreach (bool sounds in new[] { true, false }) {
            File.WriteAllText(file, JsonSerializer.Serialize(new { Sounds = sounds, TextScale = 1.15f, PlayerName = "Audio player" }));
            var migrated = PlayPreferences.Load(path);
            check(migrated.Audio.Master.Volume == 1 && migrated.Audio.Master.Muted == !sounds
                    && migrated.Sounds == sounds && migrated.TextScale == 1.15f && migrated.PlayerName == "Audio player",
                "Legacy Sounds=" + sounds + " migrates to the master mute without losing prior preferences or reducing the retained volume");
        }
        var mix = new AudioMix {
            Master = new(.65f, true), Effects = new(.35f), Notifications = new(.8f, true), Music = new(.2f)
        };
        new PlayPreferences { Audio = mix, PlayerName = "Mixer player" }.Save(path);
        using (var document = JsonDocument.Parse(File.ReadAllText(file))) {
            check(PlayPreferences.Load(path).Audio == mix && !document.RootElement.TryGetProperty("Sounds", out _)
                    && document.RootElement.TryGetProperty("Audio", out _),
                "Saving and reloading retains every channel level and independent mute with one persisted audio model");
        }
        File.WriteAllText(file, "{\"Sounds\":false,\"Audio\":{\"Master\":{\"Volume\":0.4,\"Muted\":false},\"Effects\":null,\"Notifications\":{\"Volume\":-2},\"Music\":{\"Volume\":8}}}");
        var sanitized = PlayPreferences.Load(path);
        check(sanitized.Sounds && sanitized.Audio.Master.Volume == .4f && sanitized.Audio.Effects == new AudioLevel()
                && sanitized.Audio.Notifications.Volume == 0 && sanitized.Audio.Music.Volume == 1,
            "The current mix takes precedence over legacy sound state and normalizes missing channel levels and out-of-range gain");
        File.WriteAllText(file, "{\"Audio\":null}");
        check(PlayPreferences.Load(path).Audio == new AudioMix(), "A null persisted mix recovers to the audible defaults without crashing startup");
    }

    private static void LiveVolume(string profile, Action<bool, string> check)
    {
        var mix = new AudioMix { Master = new(.8f), Effects = new(.45f), Notifications = new(.5f), Music = new(.25f, true) };
        using var run = new AudioRun(profile, "audio-settings-live", mix);
        run.Tick(elapsed: 2.1);
        var voice = run.Output.Voices.Single(voice => voice.Key == "your-priority");
        check(GainNear(voice.Volume, .24f * .8f * .5f),
            "A production priority cue reaches the device voice through its authored gain, notification bus and master level exactly once");
        run.Click("settings");
        var camera = run.View.BoardView;
        var track = run.Bounds("audio-volume");
        run.Tick(point: Position(track, .25f), left: ButtonState.Pressed);
        check(GainNear(voice.Volume, .24f * .25f * .5f) && PlayPreferences.Load(run.Profile).Audio.Master.Volume == .8f,
            "Pressing the actual volume track adjusts an already-playing voice immediately while leaving the saved level untouched during the gesture");
        run.Tick(point: Position(track, .6f), left: ButtonState.Pressed);
        // A new engine decision arriving while adjusting local audio must not
        // invalidate a captured settings gesture or authorize a game action.
        run.Present(run.Engine.State with { StackInterruptionVersion = 2 });
        run.Tick(point: Position(track, .6f));
        var saved = PlayPreferences.Load(run.Profile).Audio;
        check(Near(saved.Master.Volume, .6f) && GainNear(voice.Volume, .24f * saved.Master.Volume * .5f)
                && saved.Effects == mix.Effects && saved.Notifications == mix.Notifications && saved.Music == mix.Music
                && run.View.Overlay == "settings" && run.View.BoardView == camera && run.Engine.Replies.Count == 0,
            "Releasing the volume drag saves its live level across an incoming decision without changing buses, the camera or engine authority");
        run.Click("sound");
        check(voice.Volume == 0 && PlayPreferences.Load(run.Profile).Audio.Master is { Muted: true } muted && Near(muted.Volume, .6f),
            "Muting from actual Settings silences the active voice and retains the chosen volume");
        run.Click("audio-volume-down");
        check(voice.Volume == 0 && PlayPreferences.Load(run.Profile).Audio.Master is { Muted: true } lowered && Near(lowered.Volume, .55f),
            "The volume step remains adjustable while muted without implicitly enabling sound");
        run.Click("sound"); run.Click("audio-volume-up");
        check(PlayPreferences.Load(run.Profile).Audio.Master is { Muted: false } restored && Near(restored.Volume, .6f)
                && GainNear(voice.Volume, .24f * restored.Volume * .5f),
            "Unmuting restores the retained volume and the separate five-percent increase updates the same live voice");
        track = run.Bounds("audio-volume");
        run.Tick(point: Position(track, .5f), left: ButtonState.Pressed);
        run.Tick(point: new(track.Right + 120, track.Center.Y), left: ButtonState.Pressed);
        run.Tick(point: new(track.Right + 120, track.Center.Y));
        check(PlayPreferences.Load(run.Profile).Audio.Master.Volume == 1 && run.Engine.Replies.Count == 0,
            "A captured slider drag released beyond its right edge clamps to full volume and cannot click the battlefield behind Settings");
        run.Tick(point: Position(track, .5f), left: ButtonState.Pressed);
        run.Tick(point: new(track.Left - 120, track.Center.Y), left: ButtonState.Pressed);
        run.Tick(point: new(track.Left - 120, track.Center.Y));
        check(PlayPreferences.Load(run.Profile).Audio.Master is { Volume: 0, Muted: false } && voice.Volume == 0,
            "Dragging beyond the left edge reaches true silence and saves zero without changing the separate mute preference");
    }

    private static void Keyboard(string profile, Action<bool, string> check)
    {
        using var run = new AudioRun(profile, "audio-settings-keyboard", new() { Master = new(.5f) });
        run.Click("settings");
        var at = run.Bounds("audio-volume").Center;
        var camera = run.View.BoardView;
        run.Press(Keys.Right, at);
        check(Near(PlayPreferences.Load(run.Profile).Audio.Master.Volume, .55f),
            "The focused volume track accepts a five-percent keyboard increase and persists it");
        run.Press(Keys.Left, at); run.Press(Keys.Home, at);
        check(PlayPreferences.Load(run.Profile).Audio.Master.Volume == 0,
            "The volume track accepts keyboard decrease and Home reaches silence");
        run.Press(Keys.End, at);
        check(PlayPreferences.Load(run.Profile).Audio.Master.Volume == 1 && run.View.BoardView == camera
                && run.View.Overlay == "settings" && run.Engine.Replies.Count == 0,
            "End reaches full volume while Settings retains the keyboard gesture and the game camera and decision stay unchanged");
    }

    private static void LostCapture(string profile, Action<bool, string> check)
    {
        using var run = new AudioRun(profile, "audio-settings-foreground", new() { Master = new(.8f) });
        run.Click("settings");
        var track = run.Bounds("audio-volume");
        run.Tick(point: Position(track, .3f), left: ButtonState.Pressed);
        run.Deactivate();
        float retained = PlayPreferences.Load(run.Profile).Audio.Master.Volume;
        run.Tick(point: Position(track, .9f), left: ButtonState.Pressed, active: false);
        run.Tick(point: Position(track, .9f), active: false);
        run.Tick(point: Position(track, .9f), left: ButtonState.Pressed);
        run.Tick(point: Position(track, .9f));
        check(Near(retained, .3f) && PlayPreferences.Load(run.Profile).Audio.Master.Volume == retained
                && run.Engine.Replies.Count == 0 && run.View.Overlay == "settings",
            "Losing foreground finalizes the current volume, cancels capture and rejects background movement and the held activation click");
        run.Click("audio-volume-up");
        check(Near(PlayPreferences.Load(run.Profile).Audio.Master.Volume, retained + .05f),
            "After neutral foreground input, a fresh volume action works normally");
        run.Tick(point: Position(track, .4f), left: ButtonState.Pressed);
        run.Tick(new(Keys.Escape), point: Position(track, .9f), left: ButtonState.Pressed);
        run.Tick(point: Position(track, .9f));
        check(Near(PlayPreferences.Load(run.Profile).Audio.Master.Volume, .4f) && run.View.Overlay.Length == 0
                && run.Engine.Replies.Count == 0 && !run.Frame.Cards.Any(card => card.Focused),
            "Escape during a volume drag saves the last preview, closes Settings and cancels the gesture without reading or playing a card underneath");
    }

    private static void SilentReminder(string profile, Action<bool, string> check)
    {
        using var run = new AudioRun(profile, "audio-settings-silent-reminder", new() { Master = new(0) });
        run.Tick(elapsed: 3);
        check(run.Output.Voices.Count == 0 && !run.Frame.Hits.Any(hit => hit.Id == "priority-alerts"),
            "Zero master gain suppresses reminder voice creation and presents the priority bell as inaudible");
        run.Click("settings"); run.Click("audio-volume-up"); run.Click("close-overlay"); run.Tick(elapsed: 3);
        check(run.Output.Voices.Count == 0,
            "Raising volume does not replay an old response first seen while the notification route was silent");
        run.Present(run.Engine.State with { StackInterruptionVersion = 2 }); run.Tick(); run.Tick(elapsed: 2.1);
        check(run.Output.Voices is [{ Key: "your-priority" }] && GainNear(run.Output.Voices[0].Volume, .24f * .05f)
                && run.Engine.Replies.Count == 0,
            "A fresh priority response after volume restoration plays once at the selected master level without sending a command");
    }

    private static void Geometry(string profile, Action<bool, string> check)
    {
        foreach (var pixels in new[] { new Point(1000, 740), new Point(1540, 980) }) {
            using var run = new AudioRun(profile, "audio-settings-geometry-" + pixels.X, new(), pixels, 1.3f);
            run.Click("settings");
            var ids = new[] { "motion", "sound", "audio-volume", "audio-volume-down", "audio-volume-up", "text-size", "updates-settings", "close-overlay" };
            var controls = ids.Select(run.Bounds).ToArray();
            check(controls.All(bounds => new Rectangle(0, 0, 1600, run.Frame.DesignHeight).Contains(bounds))
                    && controls.SelectMany((first, index) => controls.Skip(index + 1).Select(second => first.Intersects(second))).All(intersects => !intersects)
                    && ids.All(id => run.View.HitAt(run.At(id))?.Id == id),
                "At " + pixels.X + "x" + pixels.Y + " and 130% text, the Settings volume, mute, text and footer controls fit the native drawer, remain distinct and are reachable");
            run.Click("audio-volume-down");
            check(Near(PlayPreferences.Load(run.Profile).Audio.Master.Volume, .95f),
                "A real fitted-window volume click uses the displayed target at " + pixels.X + "x" + pixels.Y);
        }
    }

    private static Point Position(Rectangle track, float level) => new(track.Left + (int)MathF.Round((track.Width - 1) * level), track.Center.Y);
    private static bool Near(float actual, float expected) => Math.Abs(actual - expected) <= .011f;
    private static bool GainNear(float actual, float expected) => Math.Abs(actual - expected) <= .0002f;

    private sealed class AudioRun : IDisposable
    {
        private readonly TableApplication app;
        private readonly Point pixels;
        private double seconds;
        internal string Profile { get; }
        internal InteractionScenarioEngine Engine { get; } = new();
        internal RecordingAudioOutput Output { get; } = new();
        internal TableApplication.Probe View => app.TestPort;
        internal PresentationFrame Frame { get; private set; } = null!;
        internal AudioRun(string profile, string name, AudioMix mix, Point? pixels = null, float textScale = 1)
        {
            this.pixels = pixels ?? new(1600, 900);
            Profile = Path.Combine(profile, name); Directory.CreateDirectory(Profile);
            new PlayPreferences { Automatic = false, ReducedMotion = true, Audio = mix, TextScale = textScale }.Save(Profile);
            Engine.Present(FourPlayerTableScenario.Create()[0].Snapshot with {
                Id = name, PriorityPlayerId = 0, DecisionOwnerId = 0, ActionClass = "priority", StackInterruptionVersion = 1,
                Decision = new("audio-prompt", new(), new PriorityPayload(new("Pass priority", true)))
            });
            app = new(TestTableServices.For(Engine), Profile, initialState: Engine.State,
                resources: new() { AudioOutput = Output }) { Automated = true };
            app.Initialize(); app.EnableLiveTableStudy();
            View.PresentLobby(new("local", "Audio settings fixture", null, false, null, true, "", null, [], []), true);
            Tick();
        }
        internal Rectangle Bounds(string id) => Frame.Hits.Last(hit => hit.Id == id).Bounds;
        internal Point At(string id)
        {
            var bounds = Bounds(id);
            if (View.HitAt(bounds.Center)?.Id == id) return bounds.Center;
            for (int y = bounds.Top + 2; y < bounds.Bottom - 2; y += 3)
                for (int x = bounds.Left + 2; x < bounds.Right - 2; x += 3)
                    if (View.HitAt(new(x, y))?.Id == id) return new(x, y);
            throw new InvalidOperationException("Covered audio control: " + id);
        }
        internal void Click(string id)
        {
            var at = At(id); Tick(point: at, left: ButtonState.Pressed); Tick(point: at);
            if (app.AcceptedHit != id) throw new InvalidOperationException("Audio settings click was not accepted: " + id);
        }
        internal void Press(Keys key, Point at) { Tick(new(key), point: at); Tick(point: at); }
        internal void Present(GameSnapshot state) { Engine.Present(state); View.Accept(Engine.State); }
        internal void Deactivate() => app.DeactivateInput();
        internal void Tick(KeyboardState keys = default, double elapsed = .05, Point? point = null,
            ButtonState left = ButtonState.Released, bool active = true)
        {
            seconds += elapsed;
            var physical = RenderViewport.Fit(pixels, View.DesignHeight).ToPhysical((point ?? new Point(20, 30)).ToVector2());
            app.Update(new(TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(elapsed)),
                new((int)MathF.Ceiling(physical.X), (int)MathF.Ceiling(physical.Y), 0, left, default, default, default, default),
                keys, pixels, inputActive: active);
            Frame = app.ReadPresentation();
            if (View.Error.Length > 0) throw new InvalidOperationException(View.Error);
        }
        public void Dispose() => app.Dispose();
    }
}

// A device-free voice stays alive until disposed so integration checks can
// observe a production mixer changing playback that has already started.
internal sealed class RecordingAudioOutput(Action<string, float>? played = null) : IAudioOutput
{
    internal List<Voice> Voices { get; } = [];
    public IAudioVoice CreateVoice(string key)
    {
        var voice = new Voice(key, played); Voices.Add(voice); return voice;
    }
    internal sealed class Voice(string key, Action<string, float>? played) : IAudioVoice
    {
        public string Key { get; } = key;
        public float Volume { get; set; }
        public bool IsPlaying { get; private set; }
        public bool Play() { IsPlaying = true; played?.Invoke(Key, Volume); return true; }
        public void Dispose() => IsPlaying = false;
    }
}
