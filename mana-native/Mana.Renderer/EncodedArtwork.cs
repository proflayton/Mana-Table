using System.Buffers.Binary;
using Mana.Contracts;

namespace Mana.Renderer;

/// <summary>Bounded, unambiguous dimensions before an artwork decoder can allocate GPU storage.</summary>
public readonly record struct ArtworkImageInfo(int Width, int Height, int Levels, long Bytes, long SourceBytes);

public static class EncodedArtwork
{
    public const int MaximumBytes = ArtImageLimits.MaximumEncodedBytes, MaximumDimension = 4096, MaximumPixels = 4 * 1024 * 1024;

    public static bool TryDescribe(ReadOnlySpan<byte> data, out ArtworkImageInfo image)
    {
        image = default;
        if (data.Length is < 4 or > MaximumBytes) return false;
        int width = 0, height = 0;
        bool valid = data.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            ? Png(data, out width, out height) : data[0] == 255 && data[1] == 216 && Jpeg(data, out width, out height);
        if (!valid || width is <= 0 or > MaximumDimension || height is <= 0 or > MaximumDimension
            || (long)width * height > MaximumPixels) return false;
        int levels = 1;
        for (int side = Math.Max(width, height); side > 1; side /= 2) levels++;
        image = new(width, height, levels, ColorBytes(width, height, levels), checked((long)width * height * 4));
        return true;
    }

    public static long ColorBytes(int width, int height, int levels)
    {
        if (width <= 0 || height <= 0 || levels <= 0 || levels > 32) throw new ArgumentOutOfRangeException(nameof(width));
        long bytes = 0;
        for (int level = 0; level < levels; level++) {
            bytes = checked(bytes + checked((long)width * height * 4));
            if (width == 1 && height == 1 && level + 1 < levels) throw new ArgumentOutOfRangeException(nameof(levels));
            width = Math.Max(1, width / 2); height = Math.Max(1, height / 2);
        }
        return bytes;
    }

    private static bool Png(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = height = 0;
        int offset = 8; bool header = false, pixels = false;
        while (offset <= data.Length - 12) {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
            if (length > (long)data.Length - offset - 12) return false;
            var kind = data.Slice(offset + 4, 4); int count = (int)length;
            var body = data.Slice(offset + 8, count);
            if (kind.SequenceEqual("IHDR"u8)) {
                if (header || offset != 8 || count != 13) return false;
                uint w = BinaryPrimitives.ReadUInt32BigEndian(body), h = BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
                if (w > int.MaxValue || h > int.MaxValue) return false;
                width = (int)w; height = (int)h;
                int depth = body[8], color = body[9];
                if (!(color switch { 0 => depth is 1 or 2 or 4 or 8 or 16, 2 or 4 or 6 => depth is 8 or 16,
                    3 => depth is 1 or 2 or 4 or 8, _ => false }) || body[10] != 0 || body[11] != 0 || body[12] > 1) return false;
                header = true;
            } else if (!header || kind.SequenceEqual("acTL"u8)) return false;
            else if (kind.SequenceEqual("IDAT"u8)) pixels = true;
            else if (kind.SequenceEqual("IEND"u8)) return count == 0 && pixels && offset + 12 == data.Length;
            offset += count + 12;
        }
        return false;
    }

    private static bool Jpeg(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = height = 0;
        int offset = 2; bool frame = false, scan = false, entropy = false;
        while (offset < data.Length) {
            if (entropy) {
                while (offset < data.Length && data[offset] != 255) offset++;
            }
            if (offset >= data.Length || data[offset++] != 255) return false;
            while (offset < data.Length && data[offset] == 255) offset++;
            if (offset >= data.Length) return false;
            byte marker = data[offset++];
            if (entropy && (marker == 0 || marker is >= 208 and <= 215)) continue;
            entropy = false;
            if (marker == 217) return frame && scan && offset == data.Length;
            if (marker == 216 || marker == 0 || marker is >= 208 and <= 215 || marker == 220 || marker == 200 || marker == 204) return false;
            if (marker == 1) continue;
            if (offset > data.Length - 2) return false;
            int length = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
            if (length < 2 || length > data.Length - offset) return false;
            if (marker is >= 192 and <= 207 && marker != 196) {
                if (marker is not (192 or 194) || frame || length < 8 || data[offset + 2] != 8) return false;
                int components = data[offset + 7];
                if (components is not (1 or 3 or 4) || length != 8 + 3 * components) return false;
                height = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 3)..]);
                width = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 5)..]); frame = true;
            }
            if (marker == 218) {
                if (!frame || length < 6 || length != 6 + 2 * data[offset + 2]) return false;
                scan = true; entropy = true;
            }
            offset += length;
        }
        return false;
    }
}
