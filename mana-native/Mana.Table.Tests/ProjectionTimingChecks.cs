using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mana.Contracts;
using Mana.Scenarios;
using Microsoft.Xna.Framework;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

internal static class ProjectionTimingChecks
{
    internal static void Run(string profile, GameSnapshot source, string revision, Action<bool, string> check)
    {
        var settings = new ProjectionPresentationSettings(false, 1, false, 1000, 740);
        var zero = new ProjectionRecording(1, "fixture", revision, [source], new([0], 0, settings));
        var json = JsonSerializer.Serialize(zero);
        check(JsonSerializer.Deserialize<ProjectionRecording>(json) is { Timeline.EndTicks: 0 } restored
                && restored.Timeline.Settings == settings,
            "Explicit zero timing and false presentation settings survive the immutable recording JSON constructor");
        foreach (string field in new[] { "AcceptedAtTicks", "EndTicks", "Settings" }) {
            var root = JsonNode.Parse(json)!.AsObject(); root["Timeline"]!.AsObject().Remove(field);
            check(Rejected(() => JsonSerializer.Deserialize<ProjectionRecording>(root.ToJsonString())),
                "A present timeline requires its explicit " + field + " rather than inferring a default");
        }
        foreach (string field in new[] { "ReducedMotion", "TextScale", "TableStudy", "Width", "Height" }) {
            var root = JsonNode.Parse(json)!.AsObject(); root["Timeline"]!["Settings"]!.AsObject().Remove(field);
            check(Rejected(() => JsonSerializer.Deserialize<ProjectionRecording>(root.ToJsonString())),
                "A captured settings object requires its explicit " + field + " even when the valid value is false");
        }
        foreach (string field in new[] { "Timeline", "SourceRevision" }) {
            var root = JsonNode.Parse(JsonSerializer.Serialize(new ProjectionRecording(1, "fixture", revision, [source])))!.AsObject();
            root.Remove(field);
            var legacy = JsonSerializer.Deserialize<ProjectionRecording>(root.ToJsonString());
            check(legacy != null && legacy.Timeline == null && (field != "SourceRevision" || legacy.SourceRevision == null),
                "Legacy format one keeps omitted " + field + " explicitly unknown without making older snapshots strict");
        }
        long maximum = ProjectionTimeline.MaximumDurationTicks;
        foreach (var item in new (ImmutableArray<long> Times, long End)[] {
            ([], 0), ([1], 1), ([-1], 0), ([0, 3, 2], 3), ([0, 2], 1), ([0], -1),
            ([0], maximum + 1), ([0, long.MaxValue], long.MaxValue),
            (Enumerable.Repeat(0L, ProjectionRecording.MaximumObservations + 1).ToImmutableArray(), 0)
        }) check(Rejected(() => _ = new ProjectionTimeline(item.Times, item.End, settings)),
            "A malformed or excessive timeline cannot invent order, duration or an unbounded replay clock");
        check(Rejected(() => _ = new ProjectionRecording(1, "fixture", revision, [source], new([0, 0], 0, settings)))
                && Rejected(() => _ = new ProjectionPresentationSettings(false, float.NaN, false, 1000, 740))
                && Rejected(() => _ = new ProjectionPresentationSettings(false, 1.31f, false, 1000, 740))
                && Rejected(() => _ = new ProjectionPresentationSettings(false, 1, false, 639, 740))
                && Rejected(() => _ = new ProjectionPresentationSettings(false, 1, false, 1000, 449))
                && Rejected(() => _ = new ProjectionPresentationSettings(false, 1, false, 8193, 740)),
            "Timeline ordinals match observations exactly and viewport/scale bounds reject unsupported rendering inputs");
        CaptureBounds(source, revision, settings, check);
        foreach (bool reduced in new[] { false, true })
            foreach (var size in new[] { new Point(1000, 740), new Point(1600, 900) })
                Replay(profile, revision, reduced, size, check);
    }

    private static bool Rejected(Action action)
    {
        try { action(); return false; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or JsonException) { return true; }
    }

    private static void CaptureBounds(GameSnapshot source, string revision, ProjectionPresentationSettings settings,
        Action<bool, string> check)
    {
        var sameSettings = new ProjectionPresentationSettings(false, 1, false, 1000, 740);
        var changed = new ProjectionPresentationSettings(true, 1, false, 1000, 740);
        var capture = new ProjectionCapture("fixture", revision, settings);
        long origin = TimeSpan.TicksPerSecond * 17;
        bool first = capture.TryAppend(source, TimeSpan.FromTicks(origin), sameSettings);
        bool second = capture.TryAppend(source with { Revision = source.Revision + 1 }, TimeSpan.FromTicks(origin), sameSettings);
        var complete = capture.Complete(TimeSpan.FromTicks(origin + 19), sameSettings, source);
        check(first && second && complete.Timeline is { EndTicks: 19 } timeline
                && timeline.AcceptedAtTicks.SequenceEqual([0L, 0L]) && timeline.Settings == settings
                && capture.StopReason == ProjectionCaptureStopReason.Completed
                && !capture.TryAppend(source, TimeSpan.FromTicks(origin + 20), settings)
                && Rejected(() => capture.Complete(TimeSpan.FromTicks(origin + 20), settings, source)),
            "Accepted equal-time publications retain ordinal order, normalize only their clock origin and complete exactly once with final dwell");
        foreach (var item in new (GameSnapshot State, TimeSpan At, ProjectionPresentationSettings Settings, ProjectionCaptureStopReason Reason)[] {
            (source, TimeSpan.Zero, changed, ProjectionCaptureStopReason.SettingsChanged),
            (source with { Id = "another-game" }, TimeSpan.Zero, settings, ProjectionCaptureStopReason.ScopeChanged),
            (source with { ViewerId = source.Players.First(player => player.Id != source.ViewerId).Id }, TimeSpan.Zero, settings, ProjectionCaptureStopReason.ScopeChanged),
            (source with { Revision = source.Revision - 1 }, TimeSpan.Zero, settings, ProjectionCaptureStopReason.PublicationReversed),
            (source with { BoardRevision = source.BoardRevision - 1 }, TimeSpan.Zero, settings, ProjectionCaptureStopReason.PublicationReversed),
            (source, TimeSpan.FromTicks(-1), settings, ProjectionCaptureStopReason.TimeReversed),
            (source, TimeSpan.FromTicks(ProjectionTimeline.MaximumDurationTicks + 1), settings, ProjectionCaptureStopReason.DurationLimit),
            (source with { Players = default }, TimeSpan.Zero, settings, ProjectionCaptureStopReason.InvalidObservation)
        }) {
            var bounded = new ProjectionCapture("fixture", revision, settings);
            bounded.TryAppend(source, TimeSpan.Zero, settings);
            bool accepted = bounded.TryAppend(item.State, item.At, item.Settings);
            check(!accepted && bounded.IsStopped && bounded.StopReason == item.Reason
                    && !bounded.TryAppend(source, TimeSpan.Zero, settings)
                    && Rejected(() => bounded.Complete(TimeSpan.Zero, settings, source)),
                "Capture stops safely and cannot certify a rejected sample: " + item.Reason);
        }
        var count = new ProjectionCapture("fixture", revision, settings);
        bool allAccepted = true;
        for (int index = 0; index < ProjectionRecording.MaximumObservations; index++)
            allAccepted &= count.TryAppend(source, TimeSpan.Zero, settings);
        check(allAccepted && !count.TryAppend(source, TimeSpan.Zero, settings)
                && count.StopReason == ProjectionCaptureStopReason.ObservationLimit
                && Rejected(() => count.Complete(TimeSpan.Zero, settings, source)),
            "The exact observation capacity is accepted but one extra publication stops without throwing or certifying a shortened recording");
        var duration = new ProjectionCapture("fixture", revision, settings);
        check(duration.TryAppend(source, TimeSpan.Zero, settings)
                && duration.TryAppend(source, TimeSpan.FromTicks(ProjectionTimeline.MaximumDurationTicks), settings)
                && duration.Complete(TimeSpan.FromTicks(ProjectionTimeline.MaximumDurationTicks), settings, source).Timeline!.EndTicks
                    == ProjectionTimeline.MaximumDurationTicks,
            "The duration limit itself remains valid without an off-by-one rejection");
        foreach (var end in new[] { TimeSpan.FromTicks(-1), TimeSpan.FromTicks(ProjectionTimeline.MaximumDurationTicks + 1) }) {
            var bounded = new ProjectionCapture("fixture", revision, settings); bounded.TryAppend(source, TimeSpan.Zero, settings);
            check(Rejected(() => bounded.Complete(end, settings, source)) && bounded.IsStopped,
                "Final dwell cannot run backwards or extend beyond the capture clock bound");
        }
        var finalSettings = new ProjectionCapture("fixture", revision, settings); finalSettings.TryAppend(source, TimeSpan.Zero, settings);
        check(Rejected(() => finalSettings.Complete(TimeSpan.Zero, changed, source))
                && finalSettings.StopReason == ProjectionCaptureStopReason.SettingsChanged,
            "A setting changed during the last dwell is rejected even when no new observation arrived");
        var empty = new ProjectionCapture("fixture", revision, settings);
        check(Rejected(() => empty.Complete(TimeSpan.Zero, settings, source)), "An empty capture cannot invent an initial accepted baseline");
    }

    private static void Replay(string profile, string revision, bool reduced, Point size, Action<bool, string> check)
    {
        var settings = new ProjectionPresentationSettings(reduced, 1.3f, true, size.X, size.Y);
        var baseline = CounterDamageScenario.Snapshot(1) with {
            Revision = 10, BoardRevision = 10, Activity = [], Stack = [], Decision = null, Executions = new(1, 0, [], []) };
        var damaged = CounterDamageScenario.Snapshot(2) with { Revision = 11, BoardRevision = 11, Executions = new(1, 0, [], []) };
        var refreshed = damaged with { Revision = 12, BoardRevision = 12 };
        const long boundary = 1234567;
        long end = TimeSpan.FromSeconds(5).Ticks;
        var timed = new ProjectionRecording(1, "fixture", revision, [baseline, damaged, refreshed], new([0, boundary, boundary], end, settings));
        string directory = Path.Combine(profile, "timed-" + size.X + "-" + reduced); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "timed-private-projection.json"); timed.WriteNew(path);
        var loaded = ProjectionRecording.Read(path, "fixture", revision);
        check(loaded.Timeline?.Settings == settings && loaded.Timeline.AcceptedAtTicks.SequenceEqual([0L, boundary, boundary])
                && loaded.Timeline.EndTicks == end,
            "A persisted timed window retains exact observation ordinals, non-frame offsets, final dwell and rendering settings");
        check(Rejected(() => { using var invalid = new ProjectionReplaySession(loaded, Path.Combine(directory, "wrong-size"), new(size.X + 1, size.Y)); }),
            "Timed playback cannot silently reframe a captured viewport as a different rendering condition");
        using var run = new ProjectionReplaySession(loaded, Path.Combine(directory, "playback"), size);
        check(run.Accepted.Count == 1 && run.Accepted[0].At == TimeSpan.Zero && run.ElapsedTicks == 0
                && run.Frame.Stamp.Revision == 10 && run.Frame.Stamp.Time == 0,
            "Timed playback establishes one exact zero-time accepted baseline through the production update pump");
        run.TickTo(boundary - 1);
        check(run.Accepted.Count == 1 && run.Frame.Stamp.Revision == 10 && run.View.Impacts.Count == 0,
            "A future accepted observation cannot appear one tick before its recorded boundary");
        run.TickTo(boundary);
        check(run.Accepted.Select(entry => entry.State.Revision).SequenceEqual([10L, 11L, 12L])
                && run.Accepted.Select(entry => entry.At.Ticks).SequenceEqual([0L, boundary, boundary])
                && run.Accepted.All(entry => entry.Settings == settings) && run.ElapsedTicks == boundary
                && run.Frame.Stamp.Revision == 12 && run.Frame.Stamp.Time == TimeSpan.FromTicks(boundary).TotalSeconds,
            "Equal-time observations retain their original order in one due group and stamp presentation at the exact sub-frame acceptance time");
        run.TickTo(boundary + TimeSpan.FromSeconds(.2).Ticks);
        check(run.View.Impacts.Any(impact => impact is { Kind: "damage", Amount: 3, CardId: CounterDamageScenario.TargetId })
                && run.View.Match!.Players[1].Zone("Battlefield").Cards.Single(card => card.VisualId == CounterDamageScenario.TargetId)
                    is { Power: 6, Toughness: 6, Damage: 3 }
                && run.View.TextScale == 1.3f,
            "Timed " + (reduced ? "reduced-motion" : "normal-motion") + " playback at " + size.X
                + " keeps actual damage feedback and settled creature values under the recorded text scale");
        run.Finish();
        check(run.ElapsedTicks == end && run.Frame.Stamp.Time == TimeSpan.FromTicks(end).TotalSeconds
                && run.Frame.Stamp.Revision == 12 && run.View.Impacts.Count == 0 && run.Accepted.Count == 3
                && run.Playback.ObservationRequests == 0 && run.Playback.RejectedCommands == 0
                && run.View.Match!.Players[1].Zone("Battlefield").Cards.Single(card => card.VisualId == CounterDamageScenario.TargetId).Damage == 3,
            "Final recorded dwell expires transient impact without another observation, background RPC or command while authoritative damage remains");
        var recapture = new ProjectionCapture("fixture", revision, settings);
        bool acceptedAll = run.Accepted.All(entry => recapture.TryAppend(entry.State, entry.At, entry.Settings));
        var captured = recapture.Complete(TimeSpan.FromTicks(run.ElapsedTicks), settings, refreshed);
        check(acceptedAll && captured.Timeline!.AcceptedAtTicks.SequenceEqual(loaded.Timeline!.AcceptedAtTicks)
                && captured.Timeline.EndTicks == loaded.Timeline.EndTicks,
            "The production acceptance hook can recapture the same exact offsets and final dwell without copying raw provider reads");
        check(Rejected(() => run.TickTo(end - 1)) && Rejected(() => run.TickTo(end + 1))
                && run.ElapsedTicks == end && run.Playback.ObservationRequests == 0,
            "Replay cannot reverse its clock or run beyond captured dwell to manufacture another observation");
        run.View.Accept(refreshed);
        check(run.Accepted.Count == 3,
            "An authored acceptance outside the production update pump cannot acquire a stale presentation timestamp in a timed capture");
        var liveCapture = new ProjectionCapture("fixture", revision, settings);
        run.Capture(liveCapture);
        run.View.Post(() => run.View.Accept(refreshed)); run.UpdateViewport(size);
        run.UpdateViewport(new(size.X + 1, size.Y)); run.UpdateViewport(size);
        check(liveCapture.StopReason == ProjectionCaptureStopReason.SettingsChanged
                && Rejected(() => liveCapture.Complete(TimeSpan.FromTicks(end), settings, refreshed)),
            "Changing and restoring the viewport during quiet dwell stops the update-owned capture even without another accepted publication");
        run.UpdateViewport(new(500, 400)); run.UpdateViewport(size);
        var unsupported = new ProjectionCapture("fixture", revision, settings);
        run.Capture(unsupported); run.UpdateViewport(size);
        run.UpdateViewport(new(500, 400)); run.UpdateViewport(size);
        check(unsupported.StopReason == ProjectionCaptureStopReason.SettingsChanged
                && Rejected(() => unsupported.Complete(TimeSpan.FromTicks(end), settings, refreshed)),
            "An unsupported viewport safely stops active or already stopped diagnostics without throwing from the application's normal update");
        var ended = new ProjectionCapture("fixture", revision, settings);
        run.Capture(ended); run.View.Post(() => run.View.Accept(refreshed)); run.UpdateViewport(size);
        run.View.Post(() => run.View.Accept(null!)); run.UpdateViewport(size);
        check(ended.StopReason == ProjectionCaptureStopReason.ScopeChanged
                && Rejected(() => ended.Complete(TimeSpan.FromTicks(end), settings, null)),
            "Returning to a null match revokes capture scope without certifying the cleared presentation as final dwell for the old game");
        run.Capture(null);
    }
}
