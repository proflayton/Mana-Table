using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Renderer;

/// <summary>Build a filtered texture once, retaining the original resolution for inspection.</summary>
public static class TextureFiltering
{
    public static Texture2D Load(GraphicsDevice device, Stream stream)
    {
        using var encoded = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        int count;
        while ((count = stream.Read(buffer, 0, buffer.Length)) != 0) {
            if (encoded.Length + count > EncodedArtwork.MaximumBytes) throw new InvalidDataException("Artwork exceeds its encoded-byte limit.");
            encoded.Write(buffer, 0, count);
        }
        byte[] bytes = encoded.ToArray();
        if (!EncodedArtwork.TryDescribe(bytes, out var image)) throw new InvalidDataException("Unsupported or ambiguous artwork dimensions.");
        return Load(device, bytes, image);
    }
    public static Texture2D Load(GraphicsDevice device, byte[] bytes, ArtworkImageInfo image)
    {
        if (!EncodedArtwork.TryDescribe(bytes, out var actual) || actual != image)
            throw new InvalidDataException("Artwork changed after dimension admission.");
        using var stream = new MemoryStream(bytes, writable: false);
        using var original = Texture2D.FromStream(device, stream);
        if (original.Width != image.Width || original.Height != image.Height || original.Format != SurfaceFormat.Color)
            throw new InvalidDataException("The artwork decoder contradicted its admitted dimensions or format.");
        var filtered = new RenderTarget2D(device, original.Width, original.Height, true,
            SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
        var previous = device.GetRenderTargets();
        try {
            using var batch = new SpriteBatch(device);
            device.SetRenderTarget(filtered);
            batch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp);
            batch.Draw(original, new Rectangle(0, 0, original.Width, original.Height), Color.White);
            batch.End();
            // Unbinding generates the mip chain on the GPU. Small and oblique
            // cards sample a suitable level instead of aliasing the full image.
            device.SetRenderTargets(previous);
            return filtered;
        } catch {
            try { device.SetRenderTargets(previous); }
            finally { filtered.Dispose(); }
            throw;
        }
    }
}
