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
    public MagicArtSource(string profile, HttpMessageHandler? handler = null)
    {
        http = handler == null ? new() : new(handler);
        http.Timeout = TimeSpan.FromSeconds(12);
        directory = Path.Combine(profile, "art"); Directory.CreateDirectory(directory);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ManaTableNative/0.1"); http.DefaultRequestHeaders.Accept.ParseAdd("image/*");
    }
    public async Task<byte[]?> LoadAsync(ArtRequest art, CancellationToken cancellationToken)
    {
        var file = Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(art.Key))) + ".jpg");
        if (File.Exists(file)) return await File.ReadAllBytesAsync(file, cancellationToken);
        await serial.WaitAsync(cancellationToken);
        try {
            if (File.Exists(file)) return await File.ReadAllBytesAsync(file, cancellationToken);
            string card = art.ExactPrinting
                ? Uri.EscapeDataString(art.Set.ToLowerInvariant()) + "/" + Uri.EscapeDataString(art.CollectorNumber) + "?"
                : "named?exact=" + Uri.EscapeDataString(art.Name) + "&";
            // An unavailable exact printing keeps the readable card fallback;
            // never cache unrelated artwork under its identity.
            byte[] bytes;
            try { bytes = await http.GetByteArrayAsync("https://api.scryfall.com/cards/" + card + "format=image&version=normal&face=" + (art.Face == "back" ? "back" : "front"), cancellationToken); }
            finally { await Task.Delay(150, cancellationToken); }
            await File.WriteAllBytesAsync(file, bytes, cancellationToken);
            return bytes;
        } finally { serial.Release(); }
    }
    public void Dispose() => http.Dispose();
}
