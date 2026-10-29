using Mana.Client;
using Mana.Contracts;
using Mana.Scenarios;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

// One production application and completion pump, with caller-authored devices
// and time. Preferences, fixture rules, gestures and settling remain with callers.
internal sealed class PresentationRun : IDisposable
{
    private readonly TableApplication application;
    private readonly ControlledClock clock = new();
    private readonly ClientSession.ObservationIngress? ingress;
    private readonly bool allowErrors;
    private PresentationFrame? frame;
    private bool disposed;

    internal PresentationRun(TableServices services, string profile, GameSnapshot initial, Point viewport,
        ObservationDeliveryMode delivery = ObservationDeliveryMode.External, bool allowErrors = false)
    {
        this.allowErrors = allowErrors;
        Viewport = viewport;
        Delivery = delivery;
        application = new(services, profile, initialState: initial, clock: clock, observationDelivery: delivery) { Automated = true };
        try {
            application.Initialize();
            if (delivery == ObservationDeliveryMode.External) {
                ingress = application.OpenObservationIngress(initial.Id, initial.ViewerId);
                QueueObservation(initial);
            }
        } catch {
            ingress?.Dispose(); application.Dispose(); throw;
        }
    }

    internal TableApplication.Probe View => application.TestPort;
    internal PresentationFrame Frame => frame ?? throw new InvalidOperationException("The presentation has not received its first update.");
    internal TimeSpan Elapsed => TimeSpan.FromTicks(clock.Ticks);
    internal long ElapsedTicks => clock.Ticks;
    internal Point Viewport { get; private set; }
    internal ObservationDeliveryMode Delivery { get; }
    internal string? AcceptedHit => application.AcceptedHit;
    internal bool AcceptedDrag => application.AcceptedDrag;
    internal event Action<GameSnapshot, TimeSpan, ProjectionPresentationSettings> Accepted
    {
        add => application.ProjectionAccepted += value;
        remove => application.ProjectionAccepted -= value;
    }

    internal bool TryQueueObservation(GameSnapshot state) => !disposed && ingress?.TryPost(state) == true;
    internal void QueueObservation(GameSnapshot state)
    {
        if (!TryQueueObservation(state))
            throw new InvalidOperationException("The observation exceeded this presentation's live ingress scope or bound.");
    }

    internal PresentationFrame UpdateAt(TimeSpan total, MouseState mouse, KeyboardState keys = default,
        Point? viewport = null, bool active = true)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (total.Ticks < clock.Ticks) throw new ArgumentOutOfRangeException(nameof(total), "Presentation time cannot move backwards.");
        var elapsed = total - Elapsed;
        clock.Ticks = total.Ticks;
        Viewport = viewport ?? Viewport;
        application.Update(new(total, elapsed), mouse, keys, Viewport, inputActive: active);
        return ReadFrame();
    }

    // Exactly one update, including zero elapsed time; no hidden resampling.
    internal PresentationFrame Tick(TimeSpan elapsed, MouseState mouse, KeyboardState keys = default,
        Point? viewport = null, bool active = true)
        => UpdateAt(Elapsed + elapsed, mouse, keys, viewport, active);

    internal PresentationFrame ReadFrame()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        // Failure journeys retain and assert their expected error state themselves.
        if (!allowErrors && View.Error.Length > 0) throw new InvalidOperationException(View.Error);
        return frame = application.ReadPresentation();
    }

    internal void Deactivate() => application.DeactivateInput();
    internal void EnableLiveTableStudy() => application.EnableLiveTableStudy();
    internal void Capture(ProjectionCapture? capture) => application.CaptureProjection(capture);
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        ingress?.Dispose(); application.Dispose();
    }

    private sealed class ControlledClock : TimeProvider
    {
        internal long Ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(Ticks);
        public override long GetTimestamp() => Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }
}
