using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Mana.Contracts;

namespace Mana.Scenarios;

// Baseline presentation settings only. Camera gestures, pointer input and asset
// loading are deliberately absent; this is not a complete interaction recording.
public sealed record ProjectionPresentationSettings
{
    [JsonInclude, JsonRequired] public bool ReducedMotion { get; private init; }
    [JsonInclude, JsonRequired] public float TextScale { get; private init; }
    [JsonInclude, JsonRequired] public bool TableStudy { get; private init; }
    [JsonInclude, JsonRequired] public int Width { get; private init; }
    [JsonInclude, JsonRequired] public int Height { get; private init; }

    [JsonConstructor]
    public ProjectionPresentationSettings(bool reducedMotion, float textScale, bool tableStudy, int width, int height)
    {
        if (!float.IsFinite(textScale) || textScale is < 1 or > 1.3f
            || width is < 640 or > 8192 || height is < 450 or > 8192)
            throw new ArgumentException("Recording presentation settings require a supported scale and bounded viewport.");
        ReducedMotion = reducedMotion; TextScale = textScale; TableStudy = tableStudy; Width = width; Height = height;
    }
}

public sealed record ProjectionTimeline
{
    public static readonly long MaximumDurationTicks = TimeSpan.FromMinutes(10).Ticks;
    // The array index is the observation ordinal. Equal offsets retain that order.
    [JsonInclude, JsonRequired] public ImmutableArray<long> AcceptedAtTicks { get; private init; }
    [JsonInclude, JsonRequired] public long EndTicks { get; private init; }
    [JsonInclude, JsonRequired] public ProjectionPresentationSettings Settings { get; private init; }

    [JsonConstructor]
    public ProjectionTimeline(ImmutableArray<long> acceptedAtTicks, long endTicks, ProjectionPresentationSettings settings)
    {
        if (acceptedAtTicks.IsDefaultOrEmpty || acceptedAtTicks.Length > ProjectionRecording.MaximumObservations
            || acceptedAtTicks[0] != 0 || endTicks < 0 || endTicks > MaximumDurationTicks || settings == null)
            throw new ArgumentException("A recording timeline requires a bounded ordered window and explicit presentation settings.");
        long previous = 0;
        foreach (long ticks in acceptedAtTicks) {
            if (ticks < previous || ticks > endTicks) throw new ArgumentException("Acceptance offsets must be ordered and end within the captured dwell.");
            previous = ticks;
        }
        AcceptedAtTicks = acceptedAtTicks; EndTicks = endTicks; Settings = settings;
    }
}

public enum ProjectionCaptureStopReason
{
    None, Completed, InvalidObservation, SettingsChanged, ScopeChanged,
    PublicationReversed, TimeReversed, DurationLimit, ObservationLimit
}

// The owner supplies times from the same update clock as production presentation.
// No files, provider access, polling or commands are owned by this bounded capture.
public sealed class ProjectionCapture(string providerId, string? sourceRevision, ProjectionPresentationSettings settings)
{
    private readonly List<GameSnapshot> observations = [];
    private readonly List<long> acceptedAtTicks = [];
    private long origin, lastObserved;
    public ProjectionCaptureStopReason StopReason { get; private set; }
    public bool IsStopped => StopReason != ProjectionCaptureStopReason.None;

    public bool TryAppend(GameSnapshot state, TimeSpan acceptedAt, ProjectionPresentationSettings currentSettings)
    {
        if (!ObserveEnvironment(acceptedAt, currentSettings, state)) return false;
        if (observations.Count >= ProjectionRecording.MaximumObservations) return Stop(ProjectionCaptureStopReason.ObservationLimit);
        // This observer must not turn invalid or malformed input into a gameplay
        // exception. Completion/persistence remains an explicit operation.
        try {
            if (state == null || string.IsNullOrWhiteSpace(state.Id) || state.Players.IsDefault || state.Stack.IsDefault
                || state.Activity.IsDefault || state.Viewer == null || state.Revision < 0 || state.BoardRevision < 0)
                return Stop(ProjectionCaptureStopReason.InvalidObservation);
            ExecutionFrames.Validate(state);
            PublicActivityFacts.Validate(state);
        } catch (Exception error) when (error is ArgumentException or InvalidOperationException or NullReferenceException or KeyNotFoundException) {
            return Stop(ProjectionCaptureStopReason.InvalidObservation);
        }
        var previous = observations.LastOrDefault();
        if (previous != null && (state.Revision < previous.Revision || state.BoardRevision < previous.BoardRevision
            || state.Executions != null && previous.Executions != null && state.Executions.LastSequence < previous.Executions.LastSequence))
            return Stop(ProjectionCaptureStopReason.PublicationReversed);
        if (observations.Count == 0) origin = acceptedAt.Ticks;
        if (acceptedAt.Ticks - origin > ProjectionTimeline.MaximumDurationTicks) return Stop(ProjectionCaptureStopReason.DurationLimit);
        observations.Add(state); acceptedAtTicks.Add(acceptedAt.Ticks - origin); return true;
    }

    // Sample every presentation update, including quiet dwell with no publication.
    // Returning to the original settings cannot erase an observed interruption.
    public bool ObserveEnvironment(TimeSpan at, ProjectionPresentationSettings? currentSettings, GameSnapshot? currentState)
    {
        if (IsStopped) return false;
        if (settings == null || currentSettings != settings) return Stop(ProjectionCaptureStopReason.SettingsChanged);
        if (observations.FirstOrDefault() is { } baseline
            && (currentState == null || baseline.Id != currentState.Id || baseline.ViewerId != currentState.ViewerId))
            return Stop(ProjectionCaptureStopReason.ScopeChanged);
        if (at.Ticks < 0 || at.Ticks < lastObserved) return Stop(ProjectionCaptureStopReason.TimeReversed);
        if (observations.Count > 0 && at.Ticks - origin > ProjectionTimeline.MaximumDurationTicks)
            return Stop(ProjectionCaptureStopReason.DurationLimit);
        lastObserved = at.Ticks; return true;
    }

    public ProjectionRecording Complete(TimeSpan endedAt, ProjectionPresentationSettings? currentSettings, GameSnapshot? currentState)
    {
        ObserveEnvironment(endedAt, currentSettings, currentState);
        if (IsStopped || observations.Count == 0)
            throw new InvalidOperationException("The capture is empty or stopped: " + StopReason + ".");
        var timeline = new ProjectionTimeline(acceptedAtTicks.ToImmutableArray(), endedAt.Ticks - origin, settings);
        var recording = new ProjectionRecording(ProjectionRecording.CurrentFormat, providerId, sourceRevision, observations.ToImmutableArray(), timeline);
        StopReason = ProjectionCaptureStopReason.Completed; return recording;
    }
    private bool Stop(ProjectionCaptureStopReason reason) { StopReason = reason; return false; }
}
