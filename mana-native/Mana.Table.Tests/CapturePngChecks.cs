using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Color = Microsoft.Xna.Framework.Color;
using Microsoft.Xna.Framework.Graphics;

namespace Mana.Table;

internal static class CapturePngChecks
{
    internal static void Run(Action<bool, string> check)
    {
        bool Reject(Action action) {
            try { action(); return false; }
            catch (Exception error) when (error is ArgumentException or OverflowException or IOException or InvalidDataException) { return true; }
        }
        foreach (var (width, height) in new[] { (0, 1), (1, 0), (-1, 1), (int.MaxValue, 2), (int.MaxValue, 1) }) {
            using var output = new MemoryStream();
            check(Reject(() => CapturePng.Write(output, width, height, [])) && output.Length == 0,
                "PNG capture rejects invalid or overflowing dimensions before writing any bytes: " + width + "x" + height);
        }
        foreach (int length in new[] { 3, 5 }) {
            using var output = new MemoryStream();
            check(Reject(() => CapturePng.Write(output, 2, 2, new Color[length])) && output.Length == 0,
                "PNG capture requires exactly the complete owned frame before output: " + length);
        }
        check(Reject(() => CapturePng.Write(null!, 1, 1, [Color.Red])), "A missing capture stream cannot report completion");
        using (var readOnly = new MemoryStream([], writable: false))
            check(Reject(() => CapturePng.Write(readOnly, 1, 1, [Color.Red])) && readOnly.Length == 0,
                "A read-only capture stream is rejected without output");
        check(ReferenceCrc("123456789"u8) == 0xcbf43926U, "The independent PNG CRC oracle matches the standard CRC32 check value");
        foreach (var (width, height) in new[] { (1, 1), (7, 5), (65, 17) }) {
            var pixels = Pattern(width, height);
            using var output = new MemoryStream(); CapturePng.Write(output, width, height, pixels);
            check(VerifyPng(output.ToArray(), width, height, pixels) >= 1,
                "Independent chunk/CRC/zlib/Sub reconstruction preserves every RGBA byte and row: " + width + "x" + height);
        }
        var entropy = new Color[40000]; uint value = 0x12345678U;
        for (int index = 0; index < entropy.Length; index++) {
            value ^= value << 13; value ^= value >> 17; value ^= value << 5;
            entropy[index] = new((byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24));
        }
        using (var output = new MemoryStream()) {
            CapturePng.Write(output, 200, 200, entropy);
            check(VerifyPng(output.ToArray(), 200, 200, entropy) > 1,
                "A fixed high-entropy frame crosses bounded64KiB IDAT chunks while retaining one complete zlib stream");
        }
        var expected = Pattern(7, 5);
        using var valid = new MemoryStream(); CapturePng.Write(valid, 7, 5, expected);
        byte[] corrupted = valid.ToArray(); corrupted[29] ^= 1;
        check(Reject(() => VerifyPng(corrupted, 7, 5, expected)), "The independent PNG oracle rejects a corrupted chunk CRC");
        check(Reject(() => VerifyPng(valid.ToArray()[..^1], 7, 5, expected)), "A truncated final chunk cannot be mistaken for a complete capture");
        foreach (int after in new[] { 0, 8, 40, checked((int)valid.Length - 2) }) {
            using var output = new ThrowingOutput(after); bool completed = false;
            check(Reject(() => { CapturePng.Write(output, 7, 5, expected); completed = true; }) && !completed,
                "Capture output failures propagate before success, including finalization: " + after);
        }
    }

    // Called once from a real rendered capture while its graphics context is
    // owned by Draw. Public MonoGame decoding is independent of the encoder.
    internal static void RunGpu(GraphicsDevice device, string profile, Action<bool, string> check)
    {
        var evidence = new List<object>();
        foreach (var (width, height) in new[] { (1, 1), (7, 5), (65, 17) }) {
            var pixels = Pattern(width, height);
            using var original = new Texture2D(device, width, height); original.SetData(pixels);
            foreach (bool optimized in new[] { false, true }) {
                string path = Path.Combine(profile, $"capture-png-parity-{width}x{height}-{(optimized ? "new" : "old")}.png");
                using (var output = File.Create(path)) {
                    if (optimized) CapturePng.Write(output, width, height, pixels);
                    else original.SaveAsPng(output, width, height);
                }
                using var input = File.OpenRead(path);
                using var decoded = Texture2D.FromStream(device, input, colorProcessor: null);
                var actual = new Color[pixels.Length]; decoded.GetData(actual);
                check(decoded.Width == width && decoded.Height == height && pixels.SequenceEqual(actual),
                    "The public GPU PNG decoder preserves source dimensions, RGBA, zero-alpha RGB and orientation: "
                    + width + "x" + height + (optimized ? " new" : " old"));
                if (optimized) VerifyPng(File.ReadAllBytes(path), width, height, pixels);
                byte[] rgba = pixels.SelectMany(pixel => new[] { pixel.R, pixel.G, pixel.B, pixel.A }).ToArray();
                evidence.Add(new { width, height, optimized, file = Path.GetFileName(path),
                    rgbaSha256 = Convert.ToHexString(SHA256.HashData(rgba)),
                    pngSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) });
            }
        }
        File.WriteAllText(Path.Combine(profile, "capture-png-pixel-parity.json"), JsonSerializer.Serialize(new {
            format = "RGBA8-Sub", decodedPixelParity = true,
            graphicsAdapter = device.Adapter.Description, graphicsProfile = device.GraphicsProfile.ToString(),
            graphicsFrameworkVersion = typeof(GraphicsDevice).Assembly.GetName().Version?.ToString(), captures = evidence
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static Color[] Pattern(int width, int height)
    {
        var pixels = new Color[checked(width * height)];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            pixels[y * width + x] = new((byte)(x * 17 + y * 3), (byte)(x * 5 + y * 29), (byte)(x * 31 + y * 11),
                (byte)(((x + y) % 4) switch { 0 => 0, 1 => 64, 2 => 128, _ => 255 }));
        return pixels;
    }
    private static uint ReferenceCrc(ReadOnlySpan<byte> values)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in values) {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xedb88320U;
        }
        return ~crc;
    }
    private static int VerifyPng(byte[] png, int width, int height, Color[] expected)
    {
        void Require(bool valid, string reason) { if (!valid) throw new InvalidDataException(reason); }
        Require(png.AsSpan(0, Math.Min(8, png.Length)).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10}), "PNG signature");
        using var compressed = new MemoryStream(); int offset = 8, ordinal = 0, idat = 0; bool ended = false;
        while (offset < png.Length) {
            Require(png.Length - offset >= 12, "Truncated PNG chunk");
            int length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4)));
            Require(length <= png.Length - offset - 12, "PNG chunk exceeds its complete input");
            var type = png.AsSpan(offset + 4, 4); var data = png.AsSpan(offset + 8, length);
            Require(ReferenceCrc(png.AsSpan(offset + 4, length + 4)) == BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8 + length, 4)), "PNG chunk CRC");
            string name = Encoding.ASCII.GetString(type);
            if (ordinal++ == 0) {
                Require(name == "IHDR" && length == 13 && BinaryPrimitives.ReadUInt32BigEndian(data) == width
                    && BinaryPrimitives.ReadUInt32BigEndian(data[4..]) == height && data[8..].SequenceEqual(new byte[] {8,6,0,0,0}), "Fixed RGBA8 PNG header");
            } else if (name == "IDAT") {
                Require(!ended && length <= 65536, "PNG IDAT bound and ordering"); compressed.Write(data); idat++;
            } else if (name == "IEND") {
                Require(length == 0 && offset + 12 == png.Length, "Final empty PNG IEND"); ended = true;
            } else throw new InvalidDataException("Unexpected PNG chunk");
            offset += length + 12;
        }
        Require(ended && idat > 0, "Complete PNG chunks");
        compressed.Position = 0; using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        byte[] row = new byte[checked(width * 4 + 1)];
        for (int y = 0; y < height; y++) {
            zlib.ReadExactly(row); Require(row[0] == 1, "Fixed PNG Sub filter");
            for (int index = 5; index < row.Length; index++) row[index] = unchecked((byte)(row[index] + row[index - 4]));
            for (int x = 0; x < width; x++) {
                Color pixel = expected[y * width + x]; int at = x * 4 + 1;
                Require(row[at] == pixel.R && row[at + 1] == pixel.G && row[at + 2] == pixel.B && row[at + 3] == pixel.A, "Exact PNG RGBA row");
            }
        }
        Require(zlib.ReadByte() == -1, "No extra PNG rows");
        return idat;
    }
    private sealed class ThrowingOutput(int after) : Stream
    {
        private readonly MemoryStream inner = new();
        public override void Write(ReadOnlySpan<byte> buffer) { if (Position + buffer.Length > after) throw new IOException("Authored capture write failure"); inner.Write(buffer); }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
