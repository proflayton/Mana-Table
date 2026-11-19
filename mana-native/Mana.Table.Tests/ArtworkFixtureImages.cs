using System.Drawing;
using System.Drawing.Imaging;
using Mana.Contracts;

namespace Mana.Table;

// Original geometric fixtures generated with the Windows encoder; the progressive
// fixture reuses the unchanged Forge skin image under the repository's existing notices.
internal static class ArtworkFixtureImages
{
    internal static byte[] Png(int width = 8, int height = 8) => Encode(width, height, ImageFormat.Png);
    internal static byte[] Jpeg() => Encode(13, 9, ImageFormat.Jpeg);
    internal static byte[] Progressive() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Artwork", "forge-progressive.jpg"));
    private static byte[] Encode(int width, int height, ImageFormat format)
    {
        using var image = new Bitmap(width, height);
        using (var graphics = Graphics.FromImage(image)) {
            graphics.Clear(Color.Gold);
            using var brush = new SolidBrush(Color.RoyalBlue);
            graphics.FillRectangle(brush, 0, 0, Math.Max(1, width / 2), height);
        }
        using var stream = new MemoryStream(); image.Save(stream, format); return stream.ToArray();
    }
    internal sealed class Source(byte[]? bytes = null, Func<ArtRequest, byte[]?>? choose = null) : ICardArtSource
    {
        private readonly byte[] image = bytes ?? Png();
        private readonly Dictionary<string, int> reads = new(StringComparer.Ordinal);
        internal int Reads(string key) { lock (reads) return reads.GetValueOrDefault(key); }
        internal int Total { get { lock (reads) return reads.Values.Sum(); } }
        public Task<byte[]?> LoadAsync(ArtRequest request, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            lock (reads) reads[request.Key] = reads.GetValueOrDefault(request.Key) + 1;
            return Task.FromResult(choose == null ? image : choose(request));
        }
        public void Dispose() { }
    }
}
