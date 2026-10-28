using System.Text.Json;
using Mana.Scenarios;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

// The shipping application prepares every frame. This runner only supplies
// recorded observations and a clock; its connection cannot send live commands.
internal sealed class ProjectionReplaySession : IDisposable
{
    private readonly TableApplication app;
    private double seconds;
    private Point pointer = new(20, 60);
    internal ProjectionPlayback Playback { get; }
    internal TableApplication.Probe View => app.TestPort;
    internal PresentationFrame Frame { get; private set; } = null!;
    internal ProjectionReplaySession(ProjectionRecording recording, string profile, Point size)
    {
        Directory.CreateDirectory(profile);
        new PlayPreferences { Automatic = false, ReducedMotion = true, Sounds = false }.Save(profile);
        Playback = new(recording);
        // These isolated metadata ports cannot load real decks or contact an
        // engine. Live source-action discovery is deliberately not composed.
        var metadata = new ExperienceLabEngine(new ExperienceLabSession());
        app = new(new(metadata, metadata, Playback, metadata.InitializeAsync), profile, initialState: Playback.Current);
        app.Automated = true; app.Initialize(); View.SetTableStudy(true);
        Size = size; Tick(); Tick();
    }
    private Point Size { get; }
    internal PresentationFrame Tick()
    {
        seconds += 1.0 / 60;
        app.Update(new(TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(1.0 / 60)),
            new(pointer.X, pointer.Y, 0, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released),
            new(), Size);
        if (View.Error.Length > 0) throw new InvalidOperationException(View.Error);
        return Frame = app.ReadPresentation();
    }
    internal bool Advance()
    {
        if (!Playback.Advance()) return false;
        View.Accept(Playback.Current); Tick(); Tick(); return true;
    }
    internal PresentationFrame Hover(Point position) { pointer = position; Tick(); return Tick(); }
    public void Dispose() => app.Dispose();

    internal static void RunFile(string path, string profile)
    {
        var recording = ProjectionRecording.Read(path);
        using var run = new ProjectionReplaySession(recording, profile, new(1600, 900));
        var frames = new List<object>();
        do {
            frames.Add(new { observation = run.Playback.Position, stateRevision = run.Playback.Current.Revision,
                frame = run.Frame.Observe() });
        } while (run.Advance());
        File.WriteAllText(Path.Combine(profile, "projection-replay.json"), JsonSerializer.Serialize(new {
            formatVersion = 1, audience = "viewer-private", recording.ProviderId, recording.SourceRevision,
            scope = "Recorded observation playback through production presentation; no rules simulation or human UX approval.",
            timing = "Synthetic 60 Hz updates, two per observation; original dwell, pointer and camera timing were not recorded.",
            commandRejections = run.Playback.RejectedCommands, frames
        }));
    }
}
