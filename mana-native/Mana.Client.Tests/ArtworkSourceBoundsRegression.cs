using System.Net;
using System.Security.Cryptography;
using System.Text;
using Mana.Client;
using Mana.Contracts;
using Mana.Magic;

internal static class ArtworkSourceBoundsRegression
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        const string prefix = "ManaTable-art-bounds-";
        string profile = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        try {
            var requested = new ArtRequest("Bounded exact face", "front", "TST", "7");
            var oversizedHeader = new ForwardStream(ArtImageLimits.MaximumEncodedBytes + 1L);
            using (var source = new MagicArtSource(profile, new Handler(() => Response(oversizedHeader, ArtImageLimits.MaximumEncodedBytes + 1L)))) {
                await RejectIo(() => source.LoadAsync(requested, default));
                check(oversizedHeader.BytesRead == 0 && Directory.GetFiles(Path.Combine(profile, "art")).Length == 0,
                    "An oversized Content-Length rejects artwork before reading its body or publishing a cache file");
            }
            var chunked = new ForwardStream(ArtImageLimits.MaximumEncodedBytes + 1024L);
            using (var source = new MagicArtSource(profile, new Handler(() => Response(chunked)))) {
                await RejectIo(() => source.LoadAsync(requested, default));
                check(chunked.BytesRead == ArtImageLimits.MaximumEncodedBytes + 1L
                    && Directory.GetFiles(Path.Combine(profile, "art")).Length == 0,
                    "An unknown-length artwork body stops after one byte beyond the cap and publishes neither final nor temporary cache data");
            }
            using (var source = new MagicArtSource(profile, new Handler(() => Response(new ForwardStream(7), 20)))) {
                await RejectIo(() => source.LoadAsync(requested, default));
                check(Directory.GetFiles(Path.Combine(profile, "art")).Length == 0,
                    "A body shorter than its declared length cannot become a trusted cached image");
            }
            var partial = new ForwardStream(20, failAfter: 7);
            using (var source = new MagicArtSource(profile, new Handler(() => Response(partial)))) {
                await RejectIo(() => source.LoadAsync(requested, default));
                check(Directory.GetFiles(Path.Combine(profile, "art")).Length == 0,
                    "A stream failing after partial artwork leaves no reusable image or temporary file");
            }
            var blocked = new BlockedStream(); var responses = new Handler(() => Response(blocked));
            using (var source = new MagicArtSource(profile, responses)) {
                using var cancel = new CancellationTokenSource();
                var pending = source.LoadAsync(requested, cancel.Token);
                await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(3)); cancel.Cancel();
                await RejectCancellation(pending);
                check(blocked.Disposed && Directory.GetFiles(Path.Combine(profile, "art")).Length == 0,
                    "Cancelling a streamed image disposes its body and cannot publish incomplete cache contents");
                responses.Create = () => Response(new ForwardStream(19), 19);
                byte[]? bytes = await source.LoadAsync(requested, default);
                int before = responses.Requests;
                check(bytes is { Length: 19 } && (await source.LoadAsync(requested, default))!.SequenceEqual(bytes)
                    && responses.Requests == before,
                    "A complete bounded retry preserves exact-printing cache identity and is reused without another request");
            }
            string cache = Path.Combine(profile, "art", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requested.Key))) + ".jpg");
            using (var file = new FileStream(cache, FileMode.Create, FileAccess.Write)) file.SetLength(ArtImageLimits.MaximumEncodedBytes + 1L);
            var unusedNetwork = new Handler(() => Response(new ForwardStream(1)));
            using (var source = new MagicArtSource(profile, unusedNetwork)) {
                await RejectIo(() => source.LoadAsync(requested, default));
                check(unusedNetwork.Requests == 0, "An oversized cached artwork file is rejected by its open stream length before allocating its contents");
            }
            string local = Path.Combine(profile, "local.png");
            using (var file = new FileStream(local, FileMode.Create, FileAccess.Write)) file.SetLength(ArtImageLimits.MaximumEncodedBytes + 1L);
            using (var source = new LocalArtSource(profile, new Dictionary<string, string> { [requested.Key] = "local.png" })) {
                await RejectIo(() => source.LoadAsync(requested, default));
                await File.WriteAllBytesAsync(local, [1, 2, 3]);
                check((await source.LoadAsync(requested, default))!.SequenceEqual(new byte[] { 1, 2, 3 }),
                    "Local manifest artwork enforces the same byte cap and still reads an ordinary bounded replacement");
            }
            await CheckTotalDeadline(profile, check);
        } finally {
            string resolved = Path.GetFullPath(profile), temporary = Path.GetFullPath(Path.GetTempPath());
            if (!resolved.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith(prefix, StringComparison.Ordinal))
                throw new InvalidOperationException("Artwork fixture cleanup left its allocated temporary directory.");
            Directory.Delete(resolved, recursive: true);
        }
    }
    private static async Task CheckTotalDeadline(string profile, Action<bool, string> check)
    {
        var clock = new DeadlineClock(); var body = new BlockedStream(); var headers = new HeldHeaders(body);
        using var source = new MagicArtSource(profile, headers, clock);
        var pending = source.LoadAsync(new("Total deadline", "front"), default);
        await headers.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        clock.Advance(TimeSpan.FromSeconds(11)); headers.Release.TrySetResult();
        await body.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        clock.Advance(TimeSpan.FromSeconds(1)); await RejectCancellation(pending);
        check(clock.Created == 1 && body.Disposed,
            "Artwork headers and streamed body share one twelve-second deadline; body admission cannot renew the header allowance");
    }
    private static HttpResponseMessage Response(Stream stream, long? length = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        if (length.HasValue) response.Content.Headers.ContentLength = length;
        return response;
    }
    private static async Task RejectIo(Func<Task<byte[]?>> work)
    {
        try { await work(); throw new InvalidOperationException("Oversized or incomplete artwork was accepted."); }
        catch (IOException) { }
    }
    private static async Task RejectCancellation(Task<byte[]?> work)
    {
        try { await work.WaitAsync(TimeSpan.FromSeconds(3)); throw new InvalidOperationException("Cancelled artwork was accepted."); }
        catch (OperationCanceledException) { }
    }
    private sealed class Handler(Func<HttpResponseMessage> create) : HttpMessageHandler
    {
        internal Func<HttpResponseMessage> Create = create;
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        { Requests++; return Task.FromResult(Create()); }
    }
    private class ForwardStream(long size, long failAfter = long.MaxValue) : Stream
    {
        internal long BytesRead;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellation = default)
        {
            cancellation.ThrowIfCancellationRequested();
            if (BytesRead >= failAfter) throw new IOException("Controlled interrupted body.");
            int count = (int)Math.Min(buffer.Length, Math.Min(size - BytesRead, failAfter - BytesRead));
            buffer.Span[..count].Fill(17); BytesRead += count; return ValueTask.FromResult(count);
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).GetAwaiter().GetResult();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class BlockedStream : ForwardStream
    {
        internal BlockedStream() : base(0) { }
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Disposed;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellation = default)
        { Started.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellation); return 0; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class HeldHeaders(BlockedStream body) : HttpMessageHandler
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        { Started.TrySetResult(); await Release.Task.WaitAsync(cancellation); return Response(body); }
    }
    private sealed class DeadlineClock : TimeProvider
    {
        private readonly List<Timer> timers = [];
        private long ticks;
        internal int Created;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { lock (timers) { Created++; var timer = new Timer(this, callback, state); timers.Add(timer); timer.Change(dueTime, period); return timer; } }
        internal void Advance(TimeSpan elapsed)
        {
            Timer[] ready;
            lock (timers) { ticks += elapsed.Ticks; ready = timers.Where(timer => timer.Due <= ticks).ToArray(); foreach (var timer in ready) timer.Due = long.MaxValue; }
            foreach (var timer in ready) timer.Callback(timer.State);
        }
        private sealed class Timer(DeadlineClock owner, TimerCallback callback, object? state) : ITimer
        {
            internal readonly TimerCallback Callback = callback;
            internal readonly object? State = state;
            internal long Due;
            public bool Change(TimeSpan due, TimeSpan period) { lock (owner.timers) { Due = due == Timeout.InfiniteTimeSpan ? long.MaxValue : owner.ticks + due.Ticks; return true; } }
            public void Dispose() { lock (owner.timers) owner.timers.Remove(this); }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
