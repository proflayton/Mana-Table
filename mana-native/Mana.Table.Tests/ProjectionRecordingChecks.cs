using System.Collections.Immutable;
using System.Text.Json;
using Mana.Contracts;
using Mana.Scenarios;

namespace Mana.Table;

internal static class ProjectionRecordingChecks
{
    internal static void Run(string profile, Action<bool, string> check)
    {
        var source = PresentationSmoke.Example() with { Revision = 10, BoardRevision = 10,
            Activity = [], Executions = new(1, 0, [], []) };
        var next = source with { Revision = 11, BoardRevision = 11 };
        const string revision = "e6ca011f27b7ab17a7a77947b6b2c7ae3a356c60";
        var recording = new ProjectionRecording(1, "fixture", revision, [source, next]);
        string directory = Path.Combine(profile, "recorded-projection"); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "viewer-private-projection.json");
        recording.WriteNew(path);
        var restored = ProjectionRecording.Read(path, "fixture", revision);
        check(restored.Observations.Select(state => state.Revision).SequenceEqual([10L, 11L])
                && restored.Observations[0].Viewer!.Zone("Hand").Cards.Select(card => card.VisualId)
                    .SequenceEqual(source.Viewer!.Zone("Hand").Cards.Select(card => card.VisualId)),
            "A persisted projection window retains the authorized viewer's hand and exact ordered observations with explicit source provenance");
        bool Rejected(Action action) {
            try { action(); return false; }
            catch (Exception error) when (error is ArgumentException or IOException or JsonException or EngineCommandRejectedException) { return true; }
        }
        check(Rejected(() => recording.WriteNew(path))
                && Rejected(() => ProjectionRecording.Read(path, "other-provider"))
                && Rejected(() => ProjectionRecording.Read(path, expectedSource: new string('a', 40))),
            "Recording persistence preserves existing evidence and rejects a requested provider or source mismatch");
        foreach (var frames in new ImmutableArray<GameSnapshot>[] {
            [], [source, next with { Id = "other-game" }], [source, next with { ViewerId = 1 }],
            [source, next with { Revision = 9 }], [source, next with { BoardRevision = 9 }],
            [source, next with { Executions = new(1, 1, [], [new(1, "invented", ExecutionStep.Cost)]) }],
            Enumerable.Repeat(source, ProjectionRecording.MaximumObservations + 1).ToImmutableArray()
        }) check(Rejected(() => _ = new ProjectionRecording(1, "fixture", revision, frames)),
            "A recording rejects mixed disclosure audiences, changed games, reversed publications, invented execution references and excessive windows");
        check(Rejected(() => _ = new ProjectionRecording(2, "fixture", revision, [source]))
                && Rejected(() => _ = new ProjectionRecording(1, "fixture", "not-a-source-revision", [source])),
            "An unsupported recording schema or malformed declared source cannot silently become a playable trace");
        string truncated = Path.Combine(directory, "truncated.json"); File.WriteAllText(truncated, "{\"FormatVersion\":1");
        check(Rejected(() => ProjectionRecording.Read(truncated)), "A truncated capture remains invalid evidence instead of a partial accepted recording");
        string oversized = Path.Combine(directory, "oversized.json");
        using (var file = new FileStream(oversized, FileMode.CreateNew)) file.SetLength(ProjectionRecording.MaximumBytes + 1);
        check(Rejected(() => ProjectionRecording.Read(oversized)),
            "An oversized capture fails its byte bound before JSON parsing or retaining observations");

        var playback = new ProjectionPlayback(restored);
        var command = new PassPriority(source.Id, source.Decision!.Id);
        check(GameCommandPermissions.Allows(source, command)
                && Rejected(() => playback.ExecuteAsync(command).GetAwaiter().GetResult())
                && Rejected(() => playback.ConcedeAsync(source.Id).GetAwaiter().GetResult())
                && playback.RejectedCommands == 2 && playback.Position == 0,
            "Even a formerly valid current decision and concede cannot execute through the isolated recording connection");
        check(playback.Advance() && playback.Current.Revision == 11 && !playback.Advance()
                && playback.Current.Revision == 11,
            "Sequential playback owns one bounded cursor and reaching its end cannot restart effects or manufacture a new publication");

        foreach (var size in new[] { new Microsoft.Xna.Framework.Point(1000, 740), new Microsoft.Xna.Framework.Point(1600, 900) }) {
            using var run = new ProjectionReplaySession(restored, Path.Combine(directory, size.X.ToString()), size);
            var initial = run.Frame;
            check(initial.Stamp.GameId == source.Id && initial.Stamp.Revision == source.Revision
                    && run.Advance() && run.Frame.Stamp.Revision == next.Revision
                    && run.Frame.Cards.Select(card => (card.Id, card.Zone)).SequenceEqual(initial.Cards.Select(card => (card.Id, card.Zone)))
                    && run.Playback.RejectedCommands == 0 && run.Playback.ObservationRequests == 0,
                "A recorded observation uses production presentation at both review sizes without rebuilding identities or issuing rules commands");
        }
        ProjectionTimingChecks.Run(directory, source, revision, check);
    }
}
