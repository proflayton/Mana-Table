using System.Buffers.Binary;
using System.IO.Compression;
using Color = Microsoft.Xna.Framework.Color;

namespace Mana.Table;

// Lossless diagnostic captures: fixed RGBA8/Sub, no color or geometry transforms.
internal static class CapturePng
{
    private static readonly uint[] CrcTable = MakeCrcTable();
    internal static void Write(Stream output, int width, int height, ReadOnlySpan<Color> pixels)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite) throw new ArgumentException("The PNG output must be writable", nameof(output));
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        int count = checked(width * height), rowLength = checked(width * 4 + 1);
        if (pixels.Length != count) throw new ArgumentException("Exactly one complete RGBA frame is required", nameof(pixels));
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        Span<byte> header = stackalloc byte[13];
        header.Clear(); BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)height); header[8] = 8; header[9] = 6;
        Chunk(output, "IHDR"u8, header);
        using (var idat = new Idat(output)) {
            using (var zlib = new ZLibStream(idat, CompressionLevel.Fastest, leaveOpen: true)) {
                byte[] row = new byte[rowLength];
                for (int y = 0; y < height; y++) {
                    row[0] = 1;
                    for (int x = 0; x < width; x++) {
                        Color pixel = pixels[y * width + x]; int offset = x * 4 + 1;
                        row[offset] = pixel.R; row[offset + 1] = pixel.G; row[offset + 2] = pixel.B; row[offset + 3] = pixel.A;
                    }
                    // Sub predicts each byte from the original byte four places left.
                    for (int index = row.Length - 1; index >= 5; index--) row[index] = unchecked((byte)(row[index] - row[index - 4]));
                    zlib.Write(row);
                }
            }
            idat.Finish();
        }
        Chunk(output, "IEND"u8, ReadOnlySpan<byte>.Empty);
    }
    private static void Chunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(number, (uint)data.Length); output.Write(number); output.Write(type); output.Write(data);
        uint crc = uint.MaxValue;
        foreach (byte value in type) crc = CrcTable[(crc ^ value) & 255] ^ (crc >> 8);
        foreach (byte value in data) crc = CrcTable[(crc ^ value) & 255] ^ (crc >> 8);
        BinaryPrimitives.WriteUInt32BigEndian(number, ~crc); output.Write(number);
    }
    private static uint[] MakeCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++) {
            uint crc = i;
            for (int bit = 0; bit < 8; bit++) crc = (crc & 1) == 0 ? crc >> 1 : 0xedb88320U ^ (crc >> 1);
            table[i] = crc;
        }
        return table;
    }
    private sealed class Idat(Stream output) : Stream
    {
        private readonly byte[] buffer = new byte[65536];
        private int count;
        internal void Finish() => FlushChunk();
        private void FlushChunk() { if (count == 0) return; Chunk(output, "IDAT"u8, buffer.AsSpan(0, count)); count = 0; }
        public override void Write(ReadOnlySpan<byte> value)
        {
            while (!value.IsEmpty) {
                int length = Math.Min(value.Length, buffer.Length - count);
                value[..length].CopyTo(buffer.AsSpan(count)); count += length; value = value[length..];
                if (count == buffer.Length) FlushChunk();
            }
        }
        public override void Write(byte[] value, int offset, int length) => Write(value.AsSpan(offset, length));
        public override void Flush() => FlushChunk();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] value, int offset, int length) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
