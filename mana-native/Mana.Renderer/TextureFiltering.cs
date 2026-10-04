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
        using var original = Texture2D.FromStream(device, stream);
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
            device.SetRenderTargets(previous);
            filtered.Dispose();
            throw;
        }
    }
}
