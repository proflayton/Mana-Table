using System.Buffers.Binary;
using System.Diagnostics;
using Mana.Contracts;
using Mana.Renderer;

namespace Mana.Table;

internal static class ArtworkResidencyChecks
{
    internal static void Run(Action<bool, string> check)
    {
        byte[] png = ArtworkFixtureImages.Png(3, 5), jpeg = ArtworkFixtureImages.Jpeg(), progressive = ArtworkFixtureImages.Progressive();
        check(EncodedArtwork.TryDescribe(png, out var odd) && odd is { Width: 3, Height: 5, Levels: 3, Bytes: 72, SourceBytes: 60 },
            "Artwork accounts each actual odd-sized RGBA mip instead of using compressed length or a rounded four-thirds estimate");
        check(EncodedArtwork.TryDescribe(jpeg, out _) && EncodedArtwork.TryDescribe(progressive, out _),
            "Real Windows baseline JPEG and unchanged Forge progressive JPEG fixtures pass bounded preflight");
        var duplicate = png.Take(33).Concat(png.Skip(8).Take(25)).Concat(png.Skip(33)).ToArray();
        var oversized = png.ToArray(); BinaryPrimitives.WriteUInt32BigEndian(oversized.AsSpan(16), 65535);
        var appended = png.Concat(new byte[] { 0 }).ToArray();
        var animated = png.Take(33).Concat(new byte[] { 0, 0, 0, 0, 97, 99, 84, 76, 0, 0, 0, 0 }).Concat(png.Skip(33)).ToArray();
        int frameOffset = FindFrame(jpeg);
        int frameLength = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(frameOffset + 2));
        var conflictJpeg = jpeg.Take(frameOffset).Concat(jpeg.Skip(frameOffset).Take(frameLength + 2)).Concat(jpeg.Skip(frameOffset)).ToArray();
        var deferred = jpeg.Take(frameOffset).Concat(new byte[] { 255, 220, 0, 4, 0, 9 }).Concat(jpeg.Skip(frameOffset)).ToArray();
        foreach (var malformed in new[] { duplicate, oversized, appended, animated, conflictJpeg, deferred,
            png.Take(png.Length - 1).ToArray(), jpeg.Take(jpeg.Length - 1).ToArray(), new byte[] { 71, 73, 70, 56 } }) {
            bool rejectedBeforeDevice = false;
            try { TextureFiltering.Load(null!, new MemoryStream(malformed)); }
            catch (InvalidDataException) { rejectedBeforeDevice = true; }
            check(!EncodedArtwork.TryDescribe(malformed, out _) && rejectedBeforeDevice,
                "Ambiguous, unsupported, oversized or truncated artwork is rejected before any graphics-device access (" + malformed.Length + " bytes)");
        }
        EncodedArtwork.TryDescribe(ArtworkFixtureImages.Png(), out var image);
        var policy = new ArtworkResidency(new(1360, 1020, 1616, 4));
        foreach (string key in new[] { "a", "b", "c", "d" }) { using var admission = policy.TryReserve(key, image)!; admission.Commit(image.Bytes); }
        policy.Pin(["a", "b", "c", "d"]);
        long generation = policy.CapacityGeneration;
        for (int index = 0; index < 100; index++) { policy.Pin(["a", "b", "c", "d"]); policy.Unpin(["a", "b", "c", "d"]); }
        check(policy.CapacityGeneration == generation && policy.TryReserve("e", image) == null && policy.Bytes == 1360,
            "Equal-frame pin swaps preserve capacity generation and all-pinned pressure never exceeds the resident bound");
        policy.Unpin(["b"]);
        using (var admission = policy.TryReserve("e", image)!) {
            check(admission.Evicted.SequenceEqual(["b"]) && policy.Contains("a") && policy.CapacityGeneration > generation,
                "Releasing the last pin changes actual admission opportunity and evicts only its unpinned texture");
            admission.Commit(image.Bytes);
        }
        check(policy.Bytes == 1360 && policy.PeakBytes <= 1616 && policy.Count == 4,
            "Committed artwork retains independent resident, temporary upload and object-count bounds");
        policy.Unpin(["a", "c", "d"]);
        using (policy.TryReserve("aborted", image)) { }
        check(!policy.Contains("aborted") && policy.TryReserve("after-abort", image) is { } retry && Abort(retry),
            "An aborted upload releases its reservation and cannot strand the device admission lane");
        CheckMetadata(check);
        CheckQueueAndShutdown(check);
    }
    private static bool Abort(IDisposable reservation) { reservation.Dispose(); reservation.Dispose(); return true; }
    private static int FindFrame(byte[] bytes)
    {
        for (int offset = 2; offset < bytes.Length - 1; offset++)
            if (bytes[offset] == 255 && bytes[offset + 1] is 192 or 194) return offset;
        throw new InvalidOperationException("The authored JPEG has no expected frame header.");
    }
    private static void CheckMetadata(Action<bool, string> check)
    {
        using var source = new ArtworkFixtureImages.Source();
        using var art = new CardArt(source);
        var watch = Stopwatch.StartNew();
        for (int index = 0; index < 4300; index++) {
            var card = new Card { Name = "Warm history " + index, Type = "Creature" };
            art.Preload([], [card]);
            while (source.Total <= index) {
                if (watch.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Artwork metadata fixture did not drain.");
                Thread.Yield();
            }
        }
        check(art.Metrics is { Metadata: <= 4096, Residents: 0, Queued: 0 },
            "Warming more than four thousand real source identities bounds historical metadata without building a GPU deck copy");
    }
    private static void CheckQueueAndShutdown(Action<bool, string> check)
    {
        byte[] png = ArtworkFixtureImages.Png();
        using var encoded = new MemoryStream();
        encoded.Write(png.AsSpan(0, png.Length - 12));
        byte[] padding = new byte[3 * 1024 * 1024];
        Span<byte> length = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(length, padding.Length);
        encoded.Write(length); encoded.Write("npAD"u8); encoded.Write(padding);
        uint crc = uint.MaxValue;
        foreach (byte value in "npAD"u8) crc = Crc(crc, value);
        foreach (byte value in padding) crc = Crc(crc, value);
        BinaryPrimitives.WriteUInt32BigEndian(length, ~crc); encoded.Write(length);
        encoded.Write(png.AsSpan(png.Length - 12));
        using (var source = new ArtworkFixtureImages.Source(encoded.ToArray())) {
            var art = new CardArt(source);
            art.Preload(Enumerable.Range(0, 5).Select(index => new Card { Name = "Queue bytes " + index, Type = "Creature" }));
            var watch = Stopwatch.StartNew();
            while (art.Metrics.HeldBytes == 0) {
                if (watch.Elapsed > TimeSpan.FromSeconds(3)) throw new TimeoutException("Artwork byte backpressure did not settle.");
                Thread.Yield();
            }
            check(art.Metrics is { Queued: 2, QueuedBytes: <= 8388608, HeldBytes: <= 4194304 } && source.Total == 3,
                "Actual encoded-byte backpressure holds one bounded completion without overfilling the queue or starting a fourth load");
            art.Dispose(); art.PendingLoad.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult(); art.Dispose();
            check(art.Metrics is { Queued: 0, QueuedBytes: 0, HeldBytes: 0, Residents: 0 },
                "Disposal cancels a worker held by byte pressure and releases its queued payload without waiting for a graphics update");
        }
        var held = new HeldSource(); var retiring = new CardArt(held);
        retiring.Get(new Card { Name = "Late retiring source", Type = "Creature" });
        held.Started.Task.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        retiring.Dispose(); held.Result.TrySetResult(png);
        retiring.PendingLoad.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        check(retiring.Metrics is { Residents: 0, Queued: 0, Metadata: 0 },
            "A source ignoring cancellation cannot install a late result into a disposed artwork owner");
    }
    private static uint Crc(uint crc, byte value)
    {
        crc ^= value;
        for (int bit = 0; bit < 8; bit++) crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xedb88320;
        return crc;
    }
    private sealed class HeldSource : ICardArtSource
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<byte[]?> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<byte[]?> LoadAsync(ArtRequest request, CancellationToken cancellation) { Started.TrySetResult(); return Result.Task; }
        public void Dispose() { }
    }
}
