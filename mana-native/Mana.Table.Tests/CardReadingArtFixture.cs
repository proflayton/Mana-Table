using System.Drawing;
using System.Drawing.Imaging;
using Mana.Contracts;

namespace Mana.Table;

// Encoded pixels still pass through CardArt, image decoding and GPU mipmap
// generation. The deliberately missing image exercises the rules-text fallback.
internal sealed class CardReadingArtFixture : ICardArtSource
{
    private static readonly byte[] Pattern = CreatePattern();

    public Task<byte[]?> LoadAsync(ArtRequest art, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<byte[]?>(art.Name == "No-art readability fixture" ? null : Pattern);
    }

    private static byte[] CreatePattern()
    {
        using var bitmap = new Bitmap(488, 680, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap)) {
            for (int y = 0; y < bitmap.Height; y += 16)
                for (int x = 0; x < bitmap.Width; x += 16) {
                    bool light = (x / 16 + y / 16) % 2 == 0;
                    using var brush = new SolidBrush(light ? Color.FromArgb(255, 220, 165, 94) : Color.FromArgb(255, 43, 82, 112));
                    graphics.FillRectangle(brush, x, y, 16, 16);
                }
            using var pen = new Pen(Color.FromArgb(255, 240, 232, 203), 3);
            graphics.DrawLine(pen, 0, 0, bitmap.Width - 1, bitmap.Height - 1);
            graphics.DrawLine(pen, bitmap.Width - 1, 0, 0, bitmap.Height - 1);
        }
        using var output = new MemoryStream();
        bitmap.Save(output, ImageFormat.Png);
        return output.ToArray();
    }

    public void Dispose() { }
}
