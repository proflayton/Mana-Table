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
        var restoredFrames = restored.Open().SelectMany(page => page.Observations).ToArray();
        check(manifest.Parts.Length == 4 && manifest.TotalCheckpoints == 4
                && manifest.Parts.All(part => part.Bytes > 0 && part.Bytes <= ProjectionRecording.MaximumBytes)
                && restoredFrames.Zip(frames).All(pair => JsonSerializer.Serialize(pair.First) == JsonSerializer.Serialize(pair.Second))
                && restored.AcceptedAtTicks.SequenceEqual(window.AcceptedAtTicks)
                && restored.EndTicks == window.EndTicks + FanoutProjectionArchive.DisplayDrainTicks
                && restored.Settings is { Width: 1540, Height: 980 },
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
        var longFrames = Enumerable.Range(0, 513).Select(index => baseline with { Revision = 10 + index, BoardRevision = 10 + index }).ToImmutableArray();
        longFrames = longFrames.SetItem(512, longFrames[511] with { Activity = [new(1, baseline.Turn, baseline.PhaseKey,
            "draw", baseline.ViewerId, "A changed same-identity seam activity.", null, null)] });
        var longWindow = new FanoutAcceptedProjectionWindow(longFrames, Enumerable.Range(0, 513).Select(index => (long)index).ToImmutableArray(), 600, 513, 0);
        string longDirectory = Path.Combine(profile, "fanout-513-checkpoints");
        var longManifest = FanoutProjectionArchive.Write(longDirectory, null, longWindow);
        using (var replay = new ProjectionReplaySession(FanoutProjectionArchive.Read(longDirectory, new(1000, 740)),
                Path.Combine(longDirectory, "one-owner-playback"), new(1000, 740))) {
            var initialOwners = replay.Frame.Cards.Select(card => (card.Id, card.Request.Owner)).ToArray();
            while (replay.Advance())
                if (!replay.Frame.Cards.Select(card => (card.Id, card.Request.Owner)).SequenceEqual(initialOwners))
                    throw new InvalidDataException("The 512th checkpoint seam replaced presentation ownership.");
            check(longManifest.Parts.Length == 2 && longManifest.Parts.All(part => part.EndOrdinal - part.FirstOrdinal <= ProjectionRecording.MaximumObservations
                    && part.Bytes <= ProjectionRecording.MaximumBytes) && replay.Accepted.Count == 513
                    && replay.Accepted.Select(item => item.State.Revision).SequenceEqual(longFrames.Select(frame => frame.Revision))
                    && replay.Accepted.Select(item => item.At.Ticks).SequenceEqual(longWindow.AcceptedAtTicks)
                    && JsonSerializer.Serialize(replay.Accepted[^1].State) == JsonSerializer.Serialize(longFrames[^1])
                    && replay.Playback.RejectedCommands == 0 && replay.Playback.ObservationRequests == 0,
                "The real overflow boundary round-trips 513 distinct checkpoints through bounded pages and one unchanged presentation owner, clock and observation-only connection");
        }
        // Re-sign a valid one-frame page containing a backwards publication.
        // Hash checks alone must not make a new storage baseline authoritative.
        string seamFile = Path.Combine(directory, manifest.Parts[1].File);
        byte[] seamOriginal = File.ReadAllBytes(seamFile);
        var seamRecording = ProjectionRecording.Read(seamFile);
        var backwards = new ProjectionRecording(1, seamRecording.ProviderId, seamRecording.SourceRevision,
            [seamRecording.Observations[0] with { Revision = 9, BoardRevision = 9 }], seamRecording.Timeline);
        File.WriteAllText(seamFile, JsonSerializer.Serialize(backwards));
        byte[] seamBytes = File.ReadAllBytes(seamFile);
        var forged = manifest with { Parts = manifest.Parts.SetItem(1, manifest.Parts[1] with {
            Bytes = seamBytes.Length, Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(seamBytes)) }) };
        File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(forged));
        check(Rejected(() => {
            using var stream = new PagedProjectionPlayback(FanoutProjectionArchive.Read(directory, new(1000, 740)));
            while (stream.Advance()) { }
        }), "Even a hash-consistent storage page cannot reverse the continuous publication at a seam");
        File.WriteAllBytes(seamFile, seamOriginal);
        File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(manifest));
        string partFile = Path.Combine(directory, manifest.Parts[0].File);
        byte[] original = File.ReadAllBytes(partFile);
        File.AppendAllText(partFile, " ");
        check(Rejected(() => FanoutProjectionArchive.Read(directory, new(1000, 740))),
            "A changed stored projection part fails its actual byte/hash witness before it can enter replay");
        File.WriteAllBytes(partFile, original);
        string manifestFile = Path.Combine(directory, "manifest.json");
        foreach (var invalid in new[] {
            manifest with { TotalCheckpoints = FanoutAcceptedProjection.MaximumCheckpoints + 1 },
            manifest with { Parts = Enumerable.Repeat(manifest.Parts[0], FanoutProjectionArchive.MaximumParts + 1).ToImmutableArray() },
            manifest with { Parts = manifest.Parts.SetItem(1, manifest.Parts[1] with { FirstOrdinal = 0 }) },
            manifest with { Parts = manifest.Parts.SetItem(1, manifest.Parts[1] with { StartTicks = 2 }) },
            manifest with { Parts = manifest.Parts.SetItem(0, manifest.Parts[0] with { File = "../foreign.json" }) }
        }) {
            File.WriteAllText(manifestFile, JsonSerializer.Serialize(invalid));
            check(Rejected(() => FanoutProjectionArchive.Read(directory, new(1000, 740))),
                "Projection chunks cannot exceed the explicit whole-stream quota, duplicate/reorder ordinals, move timing seams or read foreign part paths");
        }
    }

    private static bool Rejected(Action action)
    {
        try { action(); return false; } catch (InvalidDataException) { return true; }
    }
}
