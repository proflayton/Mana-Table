using Mana.Contracts;
using System.Security.Cryptography;
using System.Text;

namespace Mana.Magic;

/// <summary>Magic's remote art and disk cache. The renderer only sees image bytes.</summary>
public sealed class MagicArtSource : ICardArtSource
{
    private readonly string directory;
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private readonly SemaphoreSlim serial = new(1);
    public MagicArtSource(string profile)
    {
        directory = Path.Combine(profile, "art"); Directory.CreateDirectory(directory);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ManaTableNative/0.1"); http.DefaultRequestHeaders.Accept.ParseAdd("image/*");
    }
    public async Task<byte[]?> LoadAsync(ArtRequest art, CancellationToken cancellationToken)
    {
        var file = Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(art.Key))) + ".jpg");
        if (File.Exists(file)) return await File.ReadAllBytesAsync(file, cancellationToken);
        await serial.WaitAsync(cancellationToken);
        try {
            var bytes = await http.GetByteArrayAsync("https://api.scryfall.com/cards/named?exact=" + Uri.EscapeDataString(art.Name) + "&format=image&version=normal&face=" + art.Face, cancellationToken);
            await File.WriteAllBytesAsync(file, bytes, cancellationToken);
            await Task.Delay(150, cancellationToken); return bytes;
        } finally { serial.Release(); }
    }
    public void Dispose() => http.Dispose();
}
