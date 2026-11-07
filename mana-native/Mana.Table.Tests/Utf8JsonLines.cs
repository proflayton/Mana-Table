using System.Text;
using System.Text.Json;

namespace Mana.Table;

// The evidence owner serializes complete records before committing them. Reuse
// UTF-8 storage instead of allocating/encoding a large UTF-16 string per frame.
internal sealed class Utf8JsonLines(Stream destination) : IDisposable
{
    private readonly MemoryStream record = new();
    private static readonly byte[] Newline = Encoding.UTF8.GetBytes(Environment.NewLine);
    private bool disposed;

    internal void Write<T>(T value)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        record.Position = 0; record.SetLength(0);
        JsonSerializer.Serialize(record, value);
        // A serializer failure leaves the previous complete destination record
        // intact. I/O failures still propagate to the ordinary evidence owner.
        destination.Write(record.GetBuffer(), 0, checked((int)record.Length));
        destination.Write(Newline);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { destination.Dispose(); }
        finally { record.Dispose(); }
    }
}
