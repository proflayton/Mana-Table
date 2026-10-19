using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Mana.Renderer;
using Gdi = System.Drawing;
using Color = Microsoft.Xna.Framework.Color;

namespace Mana.Table;

internal static class BitmapPixelSmoke
{
    internal static void Run(Action<bool, string> check)
    {
        // Independent expected colors: the fractional pixel has alpha 85/255,
        // so its straight RGB (51,102,153) becomes exactly (17,34,51).
        Color[] expected = [new(201, 77, 13, 255), new(17, 34, 51, 85), Color.Transparent, new(23, 89, 211, 255)];
        byte[] top = [13, 77, 201, 255, 153, 102, 51, 85];
        byte[] bottom = [43, 87, 121, 0, 211, 89, 23, 255];
        const int width = 2, height = 2, stride = 12;
        var storage = Enumerable.Repeat((byte)0xe7, stride * height).ToArray();
        Buffer.BlockCopy(top, 0, storage, 0, top.Length);
        Buffer.BlockCopy(bottom, 0, storage, stride, bottom.Length);
        IntPtr memory = Marshal.AllocHGlobal(storage.Length);
        try {
            Marshal.Copy(storage, 0, memory, storage.Length);
            var pixels = BitmapPixels.ReadArgb32(width, height, memory, stride);
            check(pixels.Length == 4, "Bitmap transfer returns one pixel per logical position, excluding row padding");
            check(pixels[0] == expected[0] && pixels[3] == expected[3], "Straight BGRA storage becomes the correct opaque RGBA corner colors");
            check(pixels[1] == expected[1], "Fractional alpha premultiplies every color channel exactly once and preserves alpha");
            check(pixels[2] == Color.Transparent, "A transparent pixel with nonzero source RGB cannot produce a colored fringe");
            var unchanged = new byte[storage.Length];
            Marshal.Copy(memory, unchanged, 0, unchanged.Length);
            check(unchanged.SequenceEqual(storage), "Reading pixels preserves the caller's native storage and padding bytes");

            // The logical top row starts at the higher address in this buffer.
            // Distinct row colors and nonpixel padding expose either a flip or
            // treating the padded stride as tightly packed pixel data.
            Array.Fill(storage, (byte)0xc4);
            Buffer.BlockCopy(top, 0, storage, stride, top.Length);
            Buffer.BlockCopy(bottom, 0, storage, 0, bottom.Length);
            Marshal.Copy(storage, 0, memory, storage.Length);
            var reversed = BitmapPixels.ReadArgb32(width, height, IntPtr.Add(memory, stride), -stride);
            check(reversed.SequenceEqual(expected), "Negative padded stride retains the same logical top-to-bottom pixel order");
        } finally { Marshal.FreeHGlobal(memory); }

        using var bitmap = new Gdi.Bitmap(width, height, PixelFormat.Format32bppArgb);
        bitmap.SetPixel(0, 0, Gdi.Color.FromArgb(255, 201, 77, 13));
        bitmap.SetPixel(1, 0, Gdi.Color.FromArgb(85, 51, 102, 153));
        bitmap.SetPixel(0, 1, Gdi.Color.FromArgb(0, 121, 87, 43));
        bitmap.SetPixel(1, 1, Gdi.Color.FromArgb(255, 23, 89, 211));
        var integrated = BitmapPixels.ReadArgb32(bitmap);
        check(integrated.SequenceEqual(expected), "A real ARGB bitmap follows the same channel, row and premultiplied-alpha contract");
        check(bitmap.GetPixel(1, 0).ToArgb() == Gdi.Color.FromArgb(85, 51, 102, 153).ToArgb(), "Bitmap conversion leaves the original straight-alpha pixel unchanged");

        var locked = bitmap.LockBits(new Gdi.Rectangle(0, 0, width, height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try {
            check(locked.Scan0 != IntPtr.Zero && Math.Abs((long)locked.Stride) >= width * 4, "Bitmap conversion releases its lock so the caller can lock the same bitmap again");
            Marshal.Copy(new byte[] { 255, 0, 0, 255 }, 0, locked.Scan0, 4);
        } finally { bitmap.UnlockBits(locked); }
        var edited = BitmapPixels.ReadArgb32(bitmap);
        check(edited[0] == new Color(0, 0, 255, 255) && edited.Skip(1).SequenceEqual(expected.Skip(1)),
            "A subsequent bitmap conversion reads the caller's new pixels without stale or shared output");
    }
}
