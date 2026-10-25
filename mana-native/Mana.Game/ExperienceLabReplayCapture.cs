using System.Diagnostics;
using System.Text.Json;
using Mana.Scenarios;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;

namespace Mana.Table;

// Human review tooling: publishes the same authored continuation as the lab's
// Next control, then saves real GPU keyframes. It makes no acceptance assertions.
internal sealed class ExperienceLabReplayCapture : ITableAutomation
{
    private static readonly (string Name, double Seconds)[] Keyframes = [
        ("source", .10), ("travel", .5), ("landing", .95), ("settled", 2.1)
    ];
    private readonly TableWindow.Probe view;
    private readonly ExperienceLabSession session;
    private readonly string directory;
    private readonly int finalStage;
    private readonly Stopwatch wall = Stopwatch.StartNew();
    private readonly List<CaptureRecord> captures = [];
    private double? started, transitionStarted;
    private int keyframe;
    private bool baselineCaptured, finished;
    public string? ExpectedClick => null;
    public bool ClickAccepted { get; set; }

    internal ExperienceLabReplayCapture(TableWindow window, ExperienceLabSession session, string directory, int finalStage)
    {
        if (session.StageIndex != 0) throw new ArgumentException("Capture replay starts at authored stage 0.", nameof(session));
        if (finalStage < 0 || finalStage >= session.Stages.Count) throw new ArgumentOutOfRangeException(nameof(finalStage));
        this.view = window.TestPort; this.session = session;
        this.directory = Path.GetFullPath(directory); this.finalStage = finalStage;
        Directory.CreateDirectory(this.directory);
    }

    public MouseState ReadMouse() => new(-100, -100, 0, ButtonState.Released, ButtonState.Released,
        ButtonState.Released, ButtonState.Released, ButtonState.Released);
    public KeyboardState? ReadKeyboard() => new KeyboardState();

    public void Frame(Texture2D surface)
    {
        if (finished) return;
        try {
            if (view.Error.Length > 0) throw new InvalidOperationException(view.Error);
            if (!view.Loaded || view.StartupVisible || view.Frame == null) return;
            started ??= view.Now;
            if (!baselineCaptured) {
                if (view.Now - started.Value < .6) return;
                Capture(surface, "baseline", null, null); baselineCaptured = true;
                return;
            }
            if (transitionStarted is not { } transition) {
                if (view.Now - started.Value < .7) return;
                if (session.StageIndex >= finalStage) { Finish(); return; }
                Continue(); return;
            }
            double elapsed = view.Now - transition;
            // At most one scheduled window capture per rendered frame: the host
            // writes that backbuffer after this callback returns.
            if (keyframe < Keyframes.Length && elapsed >= Keyframes[keyframe].Seconds) {
                var requested = Keyframes[keyframe++];
                Capture(surface, requested.Name, requested.Seconds, elapsed); return;
            }
            if (keyframe < Keyframes.Length || elapsed < 2.5) return;
            if (session.StageIndex >= finalStage) { Finish(); return; }
            Continue();
        } catch (Exception error) {
            finished = true; Save(error.ToString()); view.Exit(); throw;
        }
    }

    private void Continue()
    {
        // Same game epoch and a fresh publication, with no action sent for any seat.
        view.Accept(session.AdvanceTo(session.StageIndex + 1));
        transitionStarted = view.Now; keyframe = 0;
        Save(null);
    }

    private void Capture(Texture2D surface, string label, double? requestedOffset, double? actualOffset)
    {
        string name = $"stage-{session.StageIndex:00}-{label}";
        string surfacePath = Path.Combine(directory, name + ".png");
        string windowPath = Path.Combine(directory, name + "-window.png");
        string framePath = Path.Combine(directory, name + ".frame.json");
        using (var output = File.Create(surfacePath)) surface.SaveAsPng(output, surface.Width, surface.Height);
        view.CaptureWindow(windowPath);
        File.WriteAllText(framePath, JsonSerializer.Serialize(view.Frame!.Observe(), new JsonSerializerOptions { WriteIndented = true }));
        var state = view.Match;
        captures.Add(new(session.StageIndex, session.Stage.Name, label, requestedOffset, actualOffset, view.Now, wall.Elapsed.TotalSeconds,
            Path.GetFileName(surfacePath), Path.GetFileName(windowPath), Path.GetFileName(framePath), state?.Id, state?.Revision ?? 0,
            state?.Turn ?? 0, state?.ActivePlayerId, state?.ActionClass,
            state?.PriorityPlayerId, state?.DecisionOwnerId, state?.Decision?.Task.ToString(),
            view.BoardView.Offset.X, view.BoardView.Offset.Y, view.FollowTurnEnabled, view.BoardViewLimitedByLayout,
            state?.Stack.Select(item => new StackRecord(item.Id, item.Name, item.ControllerId)).ToArray() ?? []));
        Save(null);
    }

    private void Finish()
    {
        finished = true; Save(null); view.Exit();
    }
    private void Save(string? error)
    {
        var viewport = view.GraphicsDevice.Viewport;
        var manifest = new {
            scope = "Human review keyframes over authored snapshots. Labels identify scheduled capture offsets; no usability, rules or animation acceptance assertions.",
            scenarioId = session.Scenario.Id, scenarioName = session.Scenario.Name, finalStage, currentStage = session.StageIndex,
            finished, error, viewport = new { viewport.Width, viewport.Height },
            captures = captures.Select(capture => new { capture, windowWritten = File.Exists(Path.Combine(directory, capture.WindowFile)) }).ToArray()
        };
        File.WriteAllText(Path.Combine(directory, "replay-capture.json"), JsonSerializer.Serialize(manifest,
            new JsonSerializerOptions { WriteIndented = true }));
    }
    private sealed record StackRecord(string Id, string Name, int? ControllerId);
    private sealed record CaptureRecord(int Stage, string StageName, string Label, double? RequestedOffset, double? ActualOffset,
        double GameSeconds, double WallSeconds, string SurfaceFile, string WindowFile, string FrameFile, string? GameId, long Revision,
        int Turn, int? ActivePlayerId, string? ActionClass, int? PriorityPlayerId, int? DecisionOwnerId, string? DecisionKind,
        float ViewX, float ViewY, bool FollowTurnEnabled, bool LimitedByLayout, StackRecord[] Stack);
}
