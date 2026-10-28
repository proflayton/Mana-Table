using System.Text.Json;
using Mana.Client;
using Mana.Contracts;
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
    private readonly ProjectionRecording recording;
    private readonly ReplayClock clock = new();
    private readonly ClientSession.ObservationIngress ingress;
    private readonly List<Acceptance> accepted = [];
    private const long FrameTicks = TimeSpan.TicksPerSecond / 60;
    private Point pointer = new(20, 60);
    internal sealed record Acceptance(GameSnapshot State, TimeSpan At, ProjectionPresentationSettings Settings);
    internal IReadOnlyList<Acceptance> Accepted => accepted;
    internal long ElapsedTicks => clock.Ticks;
    internal ProjectionPlayback Playback { get; }
    internal TableApplication.Probe View => app.TestPort;
    internal PresentationFrame Frame { get; private set; } = null!;
    internal ProjectionReplaySession(ProjectionRecording recording, string profile, Point size)
    {
        this.recording = recording;
        var settings = recording.Timeline?.Settings;
        if (settings != null && (size.X != settings.Width || size.Y != settings.Height))
            throw new ArgumentException("Timed playback preserves its captured viewport; use an authored scenario to test another size.");
        Size = size;
        Directory.CreateDirectory(profile);
        new PlayPreferences { Automatic = false, ReducedMotion = settings?.ReducedMotion ?? true,
            TextScale = settings?.TextScale ?? 1, Sounds = false }.Save(profile);
        Playback = new(recording);
        // These isolated metadata ports cannot load real decks or contact an
        // engine. Live source-action discovery is deliberately not composed.
        var metadata = new ExperienceLabEngine(new ExperienceLabSession());
        app = new(new(metadata, metadata, Playback, metadata.InitializeAsync), profile, initialState: Playback.Current,
            clock: clock, observationDelivery: ObservationDeliveryMode.External);
        app.Automated = true; app.Initialize(); View.SetTableStudy(settings?.TableStudy ?? true);
        app.ProjectionAccepted += OnAccepted;
        ingress = app.OpenObservationIngress(Playback.Current.Id, Playback.Current.ViewerId);
        QueueCurrent();
        if (recording.Timeline != null) { QueueDue(0); TickAt(0); }
        else { Tick(); Tick(); }
    }
    private void OnAccepted(GameSnapshot state, TimeSpan at, ProjectionPresentationSettings settings)
        => accepted.Add(new(state, at, settings));
    private Point Size { get; }
    internal PresentationFrame Tick()
        => recording.Timeline is { } timeline
            ? TickTo(Math.Min(timeline.EndTicks, clock.Ticks + FrameTicks))
            : TickAt(clock.Ticks + FrameTicks);
    private PresentationFrame TickAt(long ticks, Point? viewport = null)
    {
        long elapsed = ticks - clock.Ticks;
        if (elapsed < 0) throw new ArgumentOutOfRangeException(nameof(ticks));
        clock.Ticks = ticks;
        app.Update(new(TimeSpan.FromTicks(ticks), TimeSpan.FromTicks(elapsed)),
            new(pointer.X, pointer.Y, 0, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released),
            new(), viewport ?? Size);
        if (View.Error.Length > 0) throw new InvalidOperationException(View.Error);
        return Frame = app.ReadPresentation();
    }
    internal bool Advance()
    {
        if (Playback.Position + 1 >= recording.Observations.Length) return false;
        if (recording.Timeline is { } timeline) TickTo(timeline.AcceptedAtTicks[Playback.Position + 1]);
        else { Playback.Advance(); QueueCurrent(); Tick(); Tick(); }
        return true;
    }
    internal PresentationFrame TickTo(long targetTicks)
    {
        var timeline = recording.Timeline ?? throw new InvalidOperationException("This recording has no captured timing.");
        if (targetTicks < clock.Ticks || targetTicks > timeline.EndTicks) throw new ArgumentOutOfRangeException(nameof(targetTicks));
        while (clock.Ticks < targetTicks) {
            long next = Math.Min(targetTicks, clock.Ticks + FrameTicks);
            if (Playback.Position + 1 < recording.Observations.Length)
                next = Math.Min(next, timeline.AcceptedAtTicks[Playback.Position + 1]);
            QueueDue(next); TickAt(next);
        }
        return Frame;
    }
    private void QueueDue(long ticks)
    {
        var timeline = recording.Timeline!;
        while (Playback.Position + 1 < recording.Observations.Length && timeline.AcceptedAtTicks[Playback.Position + 1] <= ticks) {
            Playback.Advance(); QueueCurrent();
        }
    }
    private void QueueCurrent()
    {
        if (!ingress.TryPost(Playback.Current)) throw new InvalidOperationException("The recorded observation exceeded its current ingress scope or bound.");
    }
    internal PresentationFrame Finish() => recording.Timeline is { } timeline ? TickTo(timeline.EndTicks) : Frame;
    internal void Capture(ProjectionCapture? capture) => app.CaptureProjection(capture);
    internal PresentationFrame UpdateViewport(Point viewport) => TickAt(clock.Ticks, viewport);
    internal PresentationFrame Hover(Point position) { pointer = position; Tick(); return Tick(); }
    public void Dispose() { app.ProjectionAccepted -= OnAccepted; ingress.Dispose(); app.Dispose(); }

    private sealed class ReplayClock : TimeProvider
    {
        internal long Ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(Ticks);
        public override long GetTimestamp() => Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }

    internal static void RunFile(string path, string profile)
    {
        var recording = ProjectionRecording.Read(path);
        var settings = recording.Timeline?.Settings;
        using var run = new ProjectionReplaySession(recording, profile,
            settings == null ? new(1600, 900) : new(settings.Width, settings.Height));
        var frames = new List<object>();
        do {
            frames.Add(new { observation = run.Playback.Position, stateRevision = run.Playback.Current.Revision,
                frame = run.Frame.Observe() });
        } while (run.Advance());
        if (recording.Timeline is { } timeline && run.ElapsedTicks < timeline.EndTicks) {
            run.Finish();
            frames.Add(new { observation = run.Playback.Position, stateRevision = run.Playback.Current.Revision,
                frame = run.Frame.Observe() });
        }
        File.WriteAllText(Path.Combine(profile, "projection-replay.json"), JsonSerializer.Serialize(new {
            formatVersion = 1, audience = "viewer-private", recording.ProviderId, recording.SourceRevision,
            scope = "Recorded observation playback through production presentation; no rules simulation or human UX approval.",
            timing = recording.Timeline == null
                ? "Timing unknown: synthetic 60 Hz updates, two per observation."
                : "Captured acceptance offsets and final dwell, resampled at 60 Hz plus exact acceptance boundaries; baseline settings retained.",
            limitations = "Pointer/camera input, pre-window animations and asset-loading timing were not recorded. No human UX approval.",
            commandRejections = run.Playback.RejectedCommands, observationRequests = run.Playback.ObservationRequests,
            accepted = run.Accepted.Select((entry, ordinal) => new { ordinal, ticks = entry.At.Ticks, entry.State.Revision }), frames
        }));
    }
}
