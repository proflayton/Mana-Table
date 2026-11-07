using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Mana.Contracts;
using Mana.Scenarios;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

// Bounded files are storage chunks of one continuous accepted stream. They do
// not create new match/presentation baselines or another rules observation.
internal static class FanoutProjectionArchive
{
    internal const string Provider = "forge-ordinary-polled-fanout";
    internal static readonly long DisplayDrainTicks = TimeSpan.FromSeconds(12).Ticks;
    private const long PayloadBudget = ProjectionRecording.MaximumBytes - 65536;
    internal sealed record Part(string File, int FirstOrdinal, int EndOrdinal, long StartTicks, long EndTicks, long Bytes, string Sha256);
    internal sealed record Manifest(int Version, string Audience, string? SourceRevision, int TotalCheckpoints,
        long AcceptedCallbacks, long IdenticalCallbacks, ImmutableArray<long> GlobalAcceptedAtTicks, long CapturedEndTicks,
        long DisplayDrainTicks, ImmutableArray<Part> Parts);

    internal static Manifest Write(string directory, string? sourceRevision, FanoutAcceptedProjectionWindow window,
        long payloadBudget = PayloadBudget)
    {
        if (window.Observations.IsDefaultOrEmpty || window.Observations.Length > ProjectionRecording.MaximumObservations
            || window.AcceptedAtTicks.Length != window.Observations.Length || payloadBudget is <= 0 or > PayloadBudget
            || window.AcceptedCallbacks < window.Observations.Length
            || window.AcceptedCallbacks - window.Observations.Length != window.IdenticalCallbacks || window.IdenticalCallbacks < 0)
            throw new InvalidDataException("A projection archive retains one bounded complete accepted checkpoint stream.");
        Directory.CreateDirectory(directory);
        var sizes = window.Observations.Select(SerializedBytes).ToArray();
        var ranges = new List<(int Start, int End)>();
        int start = 0; long bytes = 0;
        for (int ordinal = 0; ordinal < sizes.Length; ordinal++) {
            if (sizes[ordinal] > payloadBudget) throw new InvalidDataException("One checkpoint exceeds the bounded recording payload budget.");
            if (bytes + sizes[ordinal] > payloadBudget) { ranges.Add((start, ordinal)); start = ordinal; bytes = 0; }
            bytes += sizes[ordinal];
        }
        ranges.Add((start, sizes.Length));
        var parts = ImmutableArray.CreateBuilder<Part>();
        var settings = new ProjectionPresentationSettings(false, 1, true, 1000, 740);
        foreach (var range in ranges) {
            long origin = window.AcceptedAtTicks[range.Start];
            long end = range.End < sizes.Length ? window.AcceptedAtTicks[range.End] : checked(window.EndTicks + DisplayDrainTicks);
            var recording = new ProjectionRecording(1, Provider, sourceRevision,
                window.Observations[range.Start..range.End],
                new(window.AcceptedAtTicks[range.Start..range.End].Select(ticks => ticks - origin).ToImmutableArray(), end - origin, settings));
            string name = "part-" + parts.Count.ToString("D3") + ".json";
            string file = Path.Combine(directory, name);
            // WriteNew streams through the unchanged64MiB file boundary.
            recording.WriteNew(file);
            using var stream = File.OpenRead(file);
            parts.Add(new(name, range.Start, range.End, origin, end, stream.Length, Convert.ToHexString(SHA256.HashData(stream))));
        }
        var manifest = new Manifest(1, "viewer-private", sourceRevision, sizes.Length, window.AcceptedCallbacks,
            window.IdenticalCallbacks, window.AcceptedAtTicks, window.EndTicks, DisplayDrainTicks, parts.ToImmutable());
        using var output = new FileStream(Path.Combine(directory, "manifest.json"), FileMode.CreateNew);
        JsonSerializer.Serialize(output, manifest);
        return manifest;
    }

    internal static ProjectionRecording Read(string directory, Point authoredViewport)
    {
        using var manifestStream = File.OpenRead(Path.Combine(directory, "manifest.json"));
        if (manifestStream.Length is <= 0 or > 262144) throw new InvalidDataException("Projection part manifest is empty or oversized.");
        var manifest = JsonSerializer.Deserialize<Manifest>(manifestStream) ?? throw new InvalidDataException("Missing projection part manifest.");
        if (manifest.Version != 1 || manifest.Audience != "viewer-private" || manifest.TotalCheckpoints is <= 0 or > ProjectionRecording.MaximumObservations
            || manifest.GlobalAcceptedAtTicks.IsDefault || manifest.GlobalAcceptedAtTicks.Length != manifest.TotalCheckpoints
            || manifest.GlobalAcceptedAtTicks[0] != 0 || manifest.Parts.IsDefaultOrEmpty || manifest.Parts.Length > manifest.TotalCheckpoints
            || manifest.DisplayDrainTicks != DisplayDrainTicks || manifest.IdenticalCallbacks < 0
            || manifest.AcceptedCallbacks < manifest.TotalCheckpoints
            || manifest.AcceptedCallbacks - manifest.TotalCheckpoints != manifest.IdenticalCallbacks)
            throw new InvalidDataException("Projection parts changed their bounded owner, counts or global timing.");
        var frames = ImmutableArray.CreateBuilder<GameSnapshot>();
        for (int index = 0; index < manifest.Parts.Length; index++) {
            var part = manifest.Parts[index];
            long end = index + 1 < manifest.Parts.Length ? manifest.Parts[index + 1].StartTicks : checked(manifest.CapturedEndTicks + DisplayDrainTicks);
            if (part.File != "part-" + index.ToString("D3") + ".json" || part.FirstOrdinal != frames.Count
                || part.EndOrdinal <= part.FirstOrdinal || part.EndOrdinal > manifest.TotalCheckpoints
                || part.StartTicks != manifest.GlobalAcceptedAtTicks[part.FirstOrdinal] || part.EndTicks != end
                || part.Bytes is <= 0 or > ProjectionRecording.MaximumBytes)
                throw new InvalidDataException("Projection chunks lost their exact ordinal or timing seam.");
            string file = Path.Combine(directory, part.File);
            using (var stream = File.OpenRead(file))
                if (stream.Length != part.Bytes || !Convert.ToHexString(SHA256.HashData(stream)).Equals(part.Sha256, StringComparison.Ordinal))
                    throw new InvalidDataException("A stored projection part differs from its captured bytes.");
            var recording = ProjectionRecording.Read(file, Provider);
            if (recording.SourceRevision != manifest.SourceRevision || recording.Observations.Length != part.EndOrdinal - part.FirstOrdinal
                || recording.Timeline is not { } timeline || timeline.EndTicks != part.EndTicks - part.StartTicks
                || !timeline.AcceptedAtTicks.Select(ticks => ticks + part.StartTicks)
                    .SequenceEqual(manifest.GlobalAcceptedAtTicks[part.FirstOrdinal..part.EndOrdinal]))
                throw new InvalidDataException("Projection part contents disagree with their declared continuous acceptance window.");
            frames.AddRange(recording.Observations);
        }
        if (frames.Count != manifest.TotalCheckpoints) throw new InvalidDataException("Projection archive omitted a final checkpoint.");
        // Review viewport/settings are authored: no original graphical surface
        // was captured by these ordinary graphics-free client owners.
        var settings = new ProjectionPresentationSettings(false, 1, true, authoredViewport.X, authoredViewport.Y);
        return new(1, Provider, manifest.SourceRevision, frames.ToImmutable(),
            new(manifest.GlobalAcceptedAtTicks, checked(manifest.CapturedEndTicks + DisplayDrainTicks), settings));
    }

    internal static long SerializedBytes(GameSnapshot state)
    {
        using var count = new CountingStream(); JsonSerializer.Serialize(count, state); return count.Length;
    }
    private sealed class CountingStream : Stream
    {
        private long count;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => count;
        public override long Position { get => count; set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int size) => count = checked(count + size);
        public override void Write(ReadOnlySpan<byte> buffer) => count = checked(count + buffer.Length);
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int size) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
