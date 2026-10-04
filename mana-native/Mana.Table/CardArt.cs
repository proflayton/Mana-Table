using System.Collections.Concurrent;
using Mana.Renderer;
using Microsoft.Xna.Framework.Graphics;
using Mana.Contracts;

namespace Mana.Table;

internal sealed class CardArt : IDisposable
{
    private readonly ICardArtSource source;
    private readonly ConcurrentQueue<(string Name, byte[] Bytes)> loaded = new();
    private readonly Dictionary<string, Texture2D?> textures = [];
    private readonly CancellationTokenSource stopped = new();
    public CardArt(ICardArtSource source) { this.source = source; }
    public void Update(GraphicsDevice graphics)
    {
        for (int uploaded = 0; uploaded < 2 && loaded.TryDequeue(out var item); uploaded++) {
            try { using var stream = new MemoryStream(item.Bytes); textures[item.Name] = TextureFiltering.Load(graphics, stream); }
            catch { /* Printed text remains available if an image is corrupt. */ }
        }
    }
    public Texture2D? Get(Card card)
    {
        if (card.FaceDown || string.IsNullOrEmpty(card.Name)) return null;
        string name = string.IsNullOrWhiteSpace(card.ArtName) ? card.Name : card.ArtName;
        string face = card.ArtFace == "back" ? "back" : "front";
        string cacheName = face == "back" ? name + "\0back" : name;
        if (textures.TryGetValue(cacheName, out var texture)) return texture;
        textures[cacheName] = null;
        _ = Task.Run(async () => {
            try {
                var bytes = await source.LoadAsync(new(name, face), stopped.Token);
                if (bytes != null) loaded.Enqueue((cacheName, bytes));
            } catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException) { }
        });
        return null;
    }
    public void Dispose() { stopped.Cancel(); source.Dispose(); foreach (var texture in textures.Values) texture?.Dispose(); }
}
