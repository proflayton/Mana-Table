using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Color = Microsoft.Xna.Framework.Color;
using Gdi = System.Drawing;

namespace Mana.Renderer;

internal static class BitmapPixels
{
    internal static Color[] ReadArgb32(Gdi.Bitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        var bounds = new Gdi.Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var locked = bitmap.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try { return ReadArgb32(bitmap.Width, bitmap.Height, locked.Scan0, locked.Stride); }
        finally { bitmap.UnlockBits(locked); }
    }

    // Scan0 addresses the logical first row; signed stride also supports bottom-up buffers.
    internal static Color[] ReadArgb32(int width, int height, IntPtr scan0, int stride)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (scan0 == IntPtr.Zero) throw new ArgumentNullException(nameof(scan0));
        int rowBytes = checked(width * 4);
        if (Math.Abs((long)stride) < rowBytes) throw new ArgumentOutOfRangeException(nameof(stride));
        var data = new Color[checked(width * height)];
        var row = new byte[rowBytes];
        for (int y = 0; y < height; y++) {
            Marshal.Copy(IntPtr.Add(scan0, checked(y * stride)), row, 0, rowBytes);
            for (int x = 0; x < width; x++) {
                int offset = x * 4;
                data[y * width + x] = Color.FromNonPremultiplied(row[offset + 2], row[offset + 1], row[offset], row[offset + 3]);
            }
        }
        return data;
    }
}
