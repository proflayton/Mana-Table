using Mana.Contracts;
using System.Security.Cryptography;
using System.Text;

namespace Mana.Magic;

/// <summary>Magic's remote art and disk cache. The renderer only sees image bytes.</summary>
public sealed class MagicArtSource : ICardArtSource
{
    private readonly string directory;
    private readonly HttpClient http;
    private readonly SemaphoreSlim serial = new(1);
    private readonly TimeProvider clock;
    public MagicArtSource(string profile, HttpMessageHandler? handler = null) : this(profile, handler, TimeProvider.System) { }
    public MagicArtSource(string profile, HttpMessageHandler? handler, TimeProvider clock)
    {
        http = handler == null ? new() : new(handler);
        // ResponseHeadersRead does not apply HttpClient.Timeout to the body.
        // One explicit deadline below owns headers and the complete streamed body.
        http.Timeout = Timeout.InfiniteTimeSpan;
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        directory = Path.Combine(profile, "art"); Directory.CreateDirectory(directory);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ManaTableNative/0.1"); http.DefaultRequestHeaders.Accept.ParseAdd("image/*");
    }
    public async Task<byte[]?> LoadAsync(ArtRequest art, CancellationToken cancellationToken)
    {
        var file = Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(art.Key))) + ".jpg");
        if (File.Exists(file)) return await ReadCache(file, cancellationToken);
        await serial.WaitAsync(cancellationToken);
        try {
            if (File.Exists(file)) return await ReadCache(file, cancellationToken);
            string card = art.ExactPrinting
                ? Uri.EscapeDataString(art.Set.ToLowerInvariant()) + "/" + Uri.EscapeDataString(art.CollectorNumber) + "?"
                : "named?exact=" + Uri.EscapeDataString(art.Name) + "&";
            // An unavailable exact printing keeps the readable card fallback;
            // never cache unrelated artwork under its identity.
            byte[] bytes;
            try {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12), clock);
                using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
                var token = stopping.Token;
                using var response = await http.GetAsync("https://api.scryfall.com/cards/" + card + "format=image&version=normal&face="
                    + (art.Face == "back" ? "back" : "front"), HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();
                long? declared = response.Content.Headers.ContentLength;
                if (declared > ArtImageLimits.MaximumEncodedBytes) throw new IOException("Artwork exceeds its encoded-byte limit.");
                await using var stream = await response.Content.ReadAsStreamAsync(token);
                bytes = await ReadBounded(stream, token);
                if (declared.HasValue && declared.Value != bytes.LongLength) throw new IOException("Artwork ended before its declared content length.");
            }
            finally { await Task.Delay(150, cancellationToken); }
            // Publish only a complete image. A cancelled or interrupted write
            // must not become the next launch's trusted cache entry.
            string temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, file, overwrite: true);
                return bytes;
            } finally {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        } finally { serial.Release(); }
    }
    private static async Task<byte[]> ReadCache(string file, CancellationToken cancellation)
    {
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > ArtImageLimits.MaximumEncodedBytes) throw new IOException("Cached artwork exceeds its encoded-byte limit.");
        return await ReadBounded(stream, cancellation);
    }
    private static async Task<byte[]> ReadBounded(Stream stream, CancellationToken cancellation)
    {
        using var result = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        while (true) {
            int count = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length,
                ArtImageLimits.MaximumEncodedBytes - (int)result.Length + 1)), cancellation);
            if (count == 0) return result.ToArray();
            if (result.Length + count > ArtImageLimits.MaximumEncodedBytes) throw new IOException("Artwork exceeds its encoded-byte limit.");
            result.Write(buffer, 0, count);
        }
    }
    public void Dispose() => http.Dispose();
}
