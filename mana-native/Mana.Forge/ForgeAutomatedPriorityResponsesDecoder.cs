using System.Text.Json;
using Mana.Contracts;

namespace Mana.Forge;

internal static class ForgeAutomatedPriorityResponsesDecoder
{
    private static readonly HashSet<string> Fields = ["schemaVersion", "status", "gameId", "seatId", "decisionId",
        "boardRevision", "publicActivitySequence", "responseAvailability"];

    internal static AutomatedPriorityResponsesReceipt Decode(JsonElement raw, long epoch)
    {
        try {
            if (raw.ValueKind != JsonValueKind.Object) throw Invalid();
            var fields = raw.EnumerateObject().Select(item => item.Name).ToArray();
            if (fields.Length != Fields.Count || !Fields.SetEquals(fields)
                || raw.GetProperty("schemaVersion").GetInt32() != 1 || raw.GetProperty("status").GetString() != "prepared") throw Invalid();
            var receipt = new AutomatedPriorityResponsesReceipt(epoch, raw.GetProperty("gameId").GetString()!,
                raw.GetProperty("seatId").GetInt32(), raw.GetProperty("decisionId").GetString()!,
                raw.GetProperty("boardRevision").GetInt64(), raw.GetProperty("publicActivitySequence").GetInt64(),
                raw.GetProperty("responseAvailability").GetString() switch {
                    "Absent" => PriorityResponseAvailability.Absent,
                    "Available" => PriorityResponseAvailability.Available,
                    "Unknown" => PriorityResponseAvailability.Unknown,
                    _ => throw Invalid()
                });
            receipt.Validate();
            return receipt;
        } catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException
            or FormatException or OverflowException or KeyNotFoundException) {
            throw new IOException("The rules provider published invalid priority response preparation metadata.", error);
        }
    }

    private static ArgumentException Invalid() => new("Priority response preparation must use its negotiated closed schema.");
}
