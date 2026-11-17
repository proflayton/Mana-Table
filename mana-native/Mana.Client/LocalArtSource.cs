using Mana.Contracts;

namespace Mana.Client;

/// <summary>An artist-owned asset manifest. Unknown cards simply use the renderer's text fallback.</summary>
public sealed class LocalArtSource(string directory, IReadOnlyDictionary<string, string> manifest) : ICardArtSource
{
    public Task<byte[]?> LoadAsync(ArtRequest art, CancellationToken cancellationToken)
    {
        if (!manifest.TryGetValue(art.Key, out var asset)) return Task.FromResult<byte[]?>(null);
        var root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        var file = Path.GetFullPath(Path.Combine(root, asset));
        if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("Art manifest path leaves its asset directory");
        return Read(file, cancellationToken);
    }
    private static async Task<byte[]?> Read(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > ArtImageLimits.MaximumEncodedBytes) throw new IOException("Local artwork exceeds its encoded-byte limit.");
        using var result = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        while (true) {
            int count = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length,
                ArtImageLimits.MaximumEncodedBytes - (int)result.Length + 1)), token);
            if (count == 0) return result.ToArray();
            if (result.Length + count > ArtImageLimits.MaximumEncodedBytes) throw new IOException("Local artwork exceeds its encoded-byte limit.");
            result.Write(buffer, 0, count);
        }
    }
    public void Dispose() { }
}
