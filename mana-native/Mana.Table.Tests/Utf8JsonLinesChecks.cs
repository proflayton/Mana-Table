using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Mana.Table;

internal static class Utf8JsonLinesChecks
{
    internal static void Run(Action<bool, string> check)
    {
        using var output = new MemoryStream();
        var records = new[] {
            new { kind = "proposal", id = 1, text = "A full private record: \"é\" \n", amount = long.MaxValue },
            new { kind = "receipt", id = 2, text = new string('x', 640_000), amount = 3L },
            new { kind = "evidence-handoff", id = 3, text = "After the large record", amount = 0L }
        };
        using (var writer = new Utf8JsonLines(new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))) {
            foreach (var item in records) writer.Write(item);
        }
        output.Position = 0;
        using var decoded = new StreamReader(new GZipStream(output, CompressionMode.Decompress));
        string expected = string.Concat(records.Select(item => JsonSerializer.Serialize(item) + Environment.NewLine));
        check(decoded.ReadToEnd() == expected,
            "Buffered UTF8 evidence preserves every full record, Unicode, exact numeric values, ordering and the existing decoded JSON-lines bytes");

        using var atomic = new MemoryStream();
        using (var writer = new Utf8JsonLines(atomic)) {
            writer.Write(new { id = 1 });
            bool rejected = false;
            try { writer.Write(new FailingRecord()); }
            catch (InvalidOperationException) { rejected = true; }
            writer.Write(new { id = 2 });
            var rows = Encoding.UTF8.GetString(atomic.ToArray()).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            check(rejected && rows.Length == 2 && rows.Select(row => JsonDocument.Parse(row).RootElement.GetProperty("id").GetInt32())
                    .SequenceEqual([1, 2]),
                "A failed evidence serialization commits no partial row and cannot corrupt preceding or subsequent complete records");
        }
    }

    private sealed class FailingRecord
    {
        public int Started => 1;
        public int Failure => throw new InvalidOperationException("Controlled evidence serialization failure.");
    }
}
