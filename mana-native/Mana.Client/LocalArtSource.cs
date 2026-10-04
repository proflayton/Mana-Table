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
    private static async Task<byte[]?> Read(string path, CancellationToken token) => await File.ReadAllBytesAsync(path, token);
    public void Dispose() { }
}
