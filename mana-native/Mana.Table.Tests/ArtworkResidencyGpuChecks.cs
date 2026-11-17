using System.Diagnostics;
using System.Text.Json;
using Mana.Contracts;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal static class ArtworkResidencyGpuChecks
{
    private static Card Face(string key) => new() { Name = key, VisualId = key, Type = "Creature" };
    internal static void Run(GraphicsDevice device, Action<bool, string> check, string profile)
    {
        foreach (byte[] bytes in new[] { ArtworkFixtureImages.Png(), ArtworkFixtureImages.Jpeg(), ArtworkFixtureImages.Progressive() }) {
            check(EncodedArtwork.TryDescribe(bytes, out var description), "The real decoder fixture has admitted bounded dimensions");
            using var texture = TextureFiltering.Load(device, bytes, description);
            check(texture.Width == description.Width && texture.Height == description.Height && texture.LevelCount == description.Levels,
                "Actual PNG/baseline/progressive JPEG decoding agrees with its admitted dimensions and mip payload");
        }
        CheckPinnedFrames(device, check);
        CheckDelayedCapture(device, check);
        CheckOwnerClose(device, check);
        CheckDecoderFailure(device, check);
        CheckSoak(device, check, profile);
    }
    private static Texture2D Await(CardArt art, GraphicsDevice device, Card card)
    {
        var watch = Stopwatch.StartNew();
        Texture2D? result;
        while ((result = art.Get(card)) == null) {
            if (watch.Elapsed > TimeSpan.FromSeconds(3)) throw new TimeoutException("Artwork fixture did not admit " + card.Name);
            art.Update(device); Thread.Yield();
        }
        return result;
    }
    private static DrawingFrame Frame(params Texture2D[] textures)
    {
        using var drawing = new DrawList(); drawing.BeginComposition();
        foreach (var texture in textures) drawing.Image(texture, new Rectangle(0, 0, 8, 8));
        return drawing.Complete();
    }
    private static void CheckPinnedFrames(GraphicsDevice device, Action<bool, string> check)
    {
        using var source = new ArtworkFixtureImages.Source();
        using var art = new CardArt(source, new(680, 340, 1024, 16));
        var a = Face("pinned-a"); var b = Face("pinned-b"); var c = Face("denied-c");
        var first = Await(art, device, a); var second = Await(art, device, b);
        using var drawing = new DrawList(); drawing.BeginComposition();
        drawing.Image(first, new(0, 0, 8, 8)); drawing.Sprite(second, new(12, 12), new(8));
        drawing.Surface(first, new(new(20), new(8)), Color.White);
        drawing.Portrait(second, new(28), 4, Color.White);
        drawing.ProjectedEllipse(first, new(new(36), new(8)), Color.White);
        var both = drawing.Complete();
        check(both.TextureResources.Count == 2, "Every texture-bearing draw variant contributes to a deduplicated completed-frame inventory");
        foreach (var command in both.Commands)
            check(new DrawingFrame([command], 1, 1).TextureResources.Count == 1,
                "The individual " + command.GetType().Name + " command independently retains its texture in the frame inventory");
        IDisposable current = art.PinFrame(both);
        art.Get(c);
        var wait = Stopwatch.StartNew();
        while (art.Metrics.PressureDenials == 0) {
            if (wait.Elapsed > TimeSpan.FromSeconds(3)) throw new TimeoutException("Pinned artwork pressure was not observed.");
            art.Update(device); Thread.Yield();
        }
        int reads = source.Reads(c.Name); long generation = art.Metrics.CapacityGeneration;
        for (int index = 0; index < 100; index++) {
            var next = art.PinFrame(both); current.Dispose(); current = next;
            art.Get(c); art.Update(device);
        }
        check(source.Reads(c.Name) == reads && art.Metrics.CapacityGeneration == generation
            && !first.IsDisposed && !second.IsDisposed && art.Readiness([c]).Unavailable == 1,
            "One hundred equal-frame swaps keep denied artwork on a stable fallback without disk reloads or disposing pinned textures");
        var captures = new List<IDisposable>();
        for (int index = 0; index < 4; index++) {
            check(art.TryPinCapture(Frame(first), out var capture), "An available optional capture pins its actual artwork"); captures.Add(capture!);
        }
        check(!art.TryPinCapture(Frame(first), out var refused) && refused == null,
            "Optional capture exhaustion returns refusal without consuming the mandatory publication slot");
        using (art.PinFrame(both)) { }
        check(!first.IsDisposed && art.Metrics.Captures == 4, "Mandatory live publication continues while optional captures are full");
        foreach (var capture in captures) { capture.Dispose(); capture.Dispose(); }
        var onlyA = art.PinFrame(Frame(first)); current.Dispose(); current = onlyA;
        art.Preload([a, c]);
        var third = Await(art, device, c);
        check(second.IsDisposed && !first.IsDisposed && !third.IsDisposed,
            "The actual owner disposes the released victim while delayed retained-frame artwork remains live");
        using (var target = new RenderTarget2D(device, 8, 8)) {
            var previous = device.GetRenderTargets(); device.SetRenderTarget(target);
            try { using var canvas = new Canvas(device); canvas.Draw(Frame(first)); }
            finally { device.SetRenderTargets(previous); }
            var pixels = new Color[64]; target.GetData(pixels);
            check(pixels.Any(pixel => pixel.R > 0 && pixel.A > 0), "A retained old frame still paints real pixels after eviction pressure");
        }
        art.Preload([a, b]);
        var reloaded = Await(art, device, b);
        var mipPixels = new Color[4]; reloaded.GetData(2, null, mipPixels, 0, mipPixels.Length);
        check(source.Reads(b.Name) == 2 && !ReferenceEquals(second, reloaded) && !reloaded.IsDisposed && mipPixels.Any(pixel => pixel.A > 0),
            "Revisiting an actually evicted face reloads a different live GPU texture with populated mip levels");
        current.Dispose(); art.Dispose(); art.Dispose();
        check(first.IsDisposed && reloaded.IsDisposed && art.Metrics is { Residents: 0, ResidentBytes: 0, QueuedBytes: 0, Captures: 0 },
            "Owner shutdown and duplicate lease disposal release all resident payload exactly once without stale upload membership");
    }
    private static void CheckSoak(GraphicsDevice device, Action<bool, string> check, string profile)
    {
        using var source = new ArtworkFixtureImages.Source();
        using var art = new CardArt(source);
        Texture2D? first = null;
        var watch = Stopwatch.StartNew();
        var samples = new List<object>();
        for (int index = 0; index < 2000; index++) {
            var texture = Await(art, device, Face("Residency face " + index)); first ??= texture;
            if ((index + 1) % 250 == 0) samples.Add(new { faces = index + 1, logicalPayload = art.Metrics,
                wallSeconds = watch.Elapsed.TotalSeconds, processWorkingSet = Environment.WorkingSet });
        }
        check(first!.IsDisposed && art.Metrics is { Residents: <= 1024, Metadata: <= 4096, ResidentBytes: <= 234881024 },
            "Browsing two thousand real GPU faces disposes old unpinned artwork while resident count, payload and metadata plateau");
        var revisit = Await(art, device, Face("Residency face 0"));
        check(source.Reads("Residency face 0") == 2 && source.Total == 2001 && !revisit.IsDisposed,
            "The bounded long-browse owner reloads an early evicted page without churning through retired foreground demand");
        File.WriteAllText(Path.Combine(profile, "artwork-residency-soak.json"), JsonSerializer.Serialize(new {
            accounting = "Logical RGBA mip payload, queued/held encoded stages and separate device-upload bytes; excludes source read/copy and decoder buffers. These numbers are neither total CPU memory nor measured physical VRAM.",
            samples, elapsedSeconds = watch.Elapsed.TotalSeconds, final = art.Metrics
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static void CheckDecoderFailure(GraphicsDevice device, Action<bool, string> check)
    {
        byte[] valid = ArtworkFixtureImages.Png(), corrupt = valid.ToArray();
        for (int index = 8; index < corrupt.Length - 12;) {
            int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(corrupt.AsSpan(index));
            if (corrupt.AsSpan(index + 4, 4).SequenceEqual("IDAT"u8)) { Array.Fill(corrupt, (byte)0, index + 8, length); break; }
            index += length + 12;
        }
        check(EncodedArtwork.TryDescribe(corrupt, out _), "Corrupt pixels retain unambiguous metadata so the actual decoder failure path is exercised");
        using var source = new ArtworkFixtureImages.Source(choose: request => request.Name == "Corrupt pixels" ? corrupt : valid);
        using var art = new CardArt(source);
        var broken = Face("Corrupt pixels"); art.Get(broken);
        art.PendingLoad.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult(); art.Update(device);
        check(art.Metrics is { Residents: 0, ResidentBytes: 0, UploadFailures: 1 } && art.Readiness([broken]).Unavailable == 1,
            "An actual decoder failure releases its GPU reservation and reports bounded failure evidence while preserving fallback readiness");
        check(!Await(art, device, Face("Valid after decode failure")).IsDisposed,
            "A valid face still uploads after a failed decode without stale resident or queued membership");
    }
    private static void CheckDelayedCapture(GraphicsDevice device, Action<bool, string> check)
    {
        using var source = new ArtworkFixtureImages.Source();
        using var art = new CardArt(source, new(680, 340, 1024, 16));
        var a = Face("capture-old-a"); var b = Face("capture-next-b"); var c = Face("capture-pressure-c");
        var old = Await(art, device, a); var next = Await(art, device, b);
        var frameA = Frame(old); var frameB = Frame(next);
        var currentA = art.PinFrame(frameA);
        check(art.TryPinCapture(frameA, out var heldCapture), "A delayed capture acquires its artwork lease before the old publication retires");
        using var currentB = art.PinFrame(frameB); currentA.Dispose();
        art.Preload([b, c]); art.Get(c);
        art.PendingLoad.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult(); art.Update(device);
        check(!old.IsDisposed && !next.IsDisposed && art.Readiness([c]).Unavailable == 1,
            "An old capture alone keeps A alive across publication of B while bounded pressure uses fallback");
        using (var target = new RenderTarget2D(device, 8, 8)) {
            var previous = device.GetRenderTargets(); device.SetRenderTarget(target);
            try { using var canvas = new Canvas(device); canvas.Draw(frameA); }
            finally { device.SetRenderTargets(previous); }
            var pixels = new Color[64]; target.GetData(pixels);
            check(pixels.Any(pixel => pixel.A > 0), "The delayed A capture paints valid GPU pixels after the live frame has become B");
        }
        heldCapture!.Dispose();
        check(!Await(art, device, c).IsDisposed && old.IsDisposed && !next.IsDisposed,
            "Retiring the delayed capture makes only its old artwork reclaimable while B stays pinned");
    }
    private static void CheckOwnerClose(GraphicsDevice device, Action<bool, string> check)
    {
        using var source = new ArtworkFixtureImages.Source();
        var art = new CardArt(source);
        var texture = Await(art, device, Face("Held owner-close face"));
        var observedLeases = new List<CardArt.FrameLease>();
        int disposals = 0; bool revokedBeforeDisposal = false;
        texture.Disposing += (_, _) => {
            disposals++;
            revokedBeforeDisposal = observedLeases.All(lease => !lease.Active && lease.Drawing == null && lease.RetainedKeys == 0)
                && art.Metrics is { LiveLeases: 0, MandatoryFrames: 0, Captures: 0 };
        };
        var frame = Frame(texture);
        var mandatory = Enumerable.Range(0, 4).Select(_ => art.PinFrame(frame)).ToArray();
        observedLeases.AddRange(mandatory);
        bool extraMandatoryRejected = false;
        try { art.PinFrame(frame); } catch (InvalidOperationException) { extraMandatoryRejected = true; }
        var captures = new List<CardArt.FrameLease>();
        for (int index = 0; index < 4; index++) {
            if (!art.TryPinCapture(frame, out var capture)) throw new InvalidOperationException("The owner-close capture fixture exhausted its separate quota early.");
            captures.Add(capture!);
            observedLeases.Add(capture!);
        }
        check(extraMandatoryRejected && art.Metrics is { LiveLeases: 8, MandatoryFrames: 4, Captures: 4 },
            "Fixed mandatory roles and the independent capture quota bound every live artwork lease before owner shutdown");
        art.Dispose();
        check(mandatory.Concat(captures).All(lease => !lease.Active && lease.Drawing == null && lease.RetainedKeys == 0)
            && art.Metrics is { LiveLeases: 0, MandatoryFrames: 0, Captures: 0, Residents: 0, ResidentBytes: 0, DeviceUploadBytes: 0 }
            && texture.IsDisposed && disposals == 1 && revokedBeforeDisposal,
            "Closing the actual owner revokes every held mandatory/capture drawing and clears its references before the texture is disposed once");
        foreach (var lease in mandatory.Concat(captures)) { lease.Dispose(); lease.Dispose(); }
        Task.Run(() => { foreach (var lease in mandatory.Concat(captures)) lease.Dispose(); })
            .WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        art.Dispose();
        bool closedMandatoryRejected = false;
        try { art.PinFrame(frame); } catch (ObjectDisposedException) { closedMandatoryRejected = true; }
        check(closedMandatoryRejected && !art.TryPinCapture(frame, out _) && disposals == 1
            && art.Metrics is { LiveLeases: 0, MandatoryFrames: 0, Captures: 0 },
            "Delayed lease disposal after owner close is inert and neither capture nor mandatory paint authority can be reopened");
    }
}
