using System.Collections.Immutable;
using System.Text.Json;
using Mana.Contracts;
using Mana.Scenarios;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

internal static class FanoutProjectionArchiveChecks
{
    internal static void Run(string profile, Action<bool, string> check)
    {
        string directory = Path.Combine(profile, "fanout-archive-controls");
        var baseline = PresentationSmoke.Example() with { Revision = 10, BoardRevision = 10 };
        var frames = Enumerable.Range(0, 4).Select(index => baseline with { Revision = 10 + index, BoardRevision = 10 + index }).ToImmutableArray();
        var window = new FanoutAcceptedProjectionWindow(frames, [0, 1, 1, 100], 200, 7, 3);
        long payload = frames.Max(FanoutProjectionArchive.SerializedBytes);
        var manifest = FanoutProjectionArchive.Write(directory, null, window, payload);
        var restored = FanoutProjectionArchive.Read(directory, new(1540, 980));
        check(manifest.Parts.Length == 4 && manifest.TotalCheckpoints == 4
                && manifest.Parts.All(part => part.Bytes > 0 && part.Bytes <= ProjectionRecording.MaximumBytes)
                && restored.Observations.Zip(frames).All(pair => JsonSerializer.Serialize(pair.First) == JsonSerializer.Serialize(pair.Second))
                && restored.Timeline!.AcceptedAtTicks.SequenceEqual(window.AcceptedAtTicks)
                && restored.Timeline.EndTicks == window.EndTicks + FanoutProjectionArchive.DisplayDrainTicks
                && restored.Timeline.Settings is { Width: 1540, Height: 980 },
            "Streamed bounded projection parts round-trip every complete payload, exact global/equal-time offsets and final dwell with separately authored review settings");
        using (var replay = new ProjectionReplaySession(restored, Path.Combine(directory, "continuous-playback"), new(1540, 980))) {
            var initial = replay.Frame.Cards.Select(card => (card.Id, card.Zone, card.Request.Owner)).ToArray();
            while (replay.Advance())
                if (!replay.Frame.Cards.Select(card => (card.Id, card.Zone, card.Request.Owner)).SequenceEqual(initial))
                    throw new InvalidDataException("A projection storage seam reset the production card occurrences.");
            check(replay.Accepted.Count == 4 && replay.Accepted.Select(entry => entry.At.Ticks).SequenceEqual(window.AcceptedAtTicks)
                    && replay.Playback.RejectedCommands == 0 && replay.Playback.ObservationRequests == 0,
                "All four storage parts feed one continuous production replay/session with stable identities and no provider authority");
        }
        string partFile = Path.Combine(directory, manifest.Parts[0].File);
        byte[] original = File.ReadAllBytes(partFile);
        File.AppendAllText(partFile, " ");
        check(Rejected(() => FanoutProjectionArchive.Read(directory, new(1000, 740))),
            "A changed stored projection part fails its actual byte/hash witness before it can enter replay");
        File.WriteAllBytes(partFile, original);
        string manifestFile = Path.Combine(directory, "manifest.json");
        foreach (var invalid in new[] {
            manifest with { TotalCheckpoints = ProjectionRecording.MaximumObservations + 1 },
            manifest with { Parts = manifest.Parts.SetItem(1, manifest.Parts[1] with { FirstOrdinal = 0 }) },
            manifest with { Parts = manifest.Parts.SetItem(1, manifest.Parts[1] with { StartTicks = 2 }) },
            manifest with { Parts = manifest.Parts.SetItem(0, manifest.Parts[0] with { File = "../foreign.json" }) }
        }) {
            File.WriteAllText(manifestFile, JsonSerializer.Serialize(invalid));
            check(Rejected(() => FanoutProjectionArchive.Read(directory, new(1000, 740))),
                "Projection chunks cannot relax the512 total checkpoint cap, duplicate/reorder ordinals, move timing seams or read foreign part paths");
        }
    }

    private static bool Rejected(Action action)
    {
        try { action(); return false; } catch (InvalidDataException) { return true; }
    }
}
