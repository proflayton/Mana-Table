using System.Collections.Immutable;
using System.Text.Json;
using Mana.Contracts;

namespace Mana.Forge;

// This negotiated reply is consent/delivery metadata only. It cannot become a
// snapshot, an action domain or a policy frame. The provider supplies its epoch.
internal static class ForgeAutomatedPriorityMetadataDecoder
{
    private static readonly string[] ReceiptFields = ["schemaVersion", "gameId", "seatId", "revision", "boardRevision",
        "stackInterruptionVersion", "turn", "status", "playerActive", "decisionOwnerId", "priorityPlayerId",
        "decisionId", "priorityDecisionId", "continuation", "observation"];
    private static readonly string[] ContinuationFields = ["sequence", "grantId", "mode", "status", "reasonCode", "automaticPasses", "phaseStops"];
    private static readonly string[] ObservationFields = ["boardRevision", "publicActivitySequence", "acknowledgedBoardRevision",
        "acknowledgedPublicActivitySequence", "registered", "overflowed"];

    internal static AutomatedPriorityReceipt Decode(JsonElement raw, long epoch)
    {
        try {
            Fields(raw, ReceiptFields);
            if (Int(raw, "schemaVersion") != 1) throw Invalid();
            int turn = Int(raw, "turn");
            var status = String(raw, "status") switch {
                "starting" => AutomatedPriorityMatchStatus.Starting, "playing" => AutomatedPriorityMatchStatus.Playing,
                "resolving" => AutomatedPriorityMatchStatus.Resolving, "finished" => AutomatedPriorityMatchStatus.Finished,
                "error" => AutomatedPriorityMatchStatus.Error, _ => throw Invalid()
            };
            var continuation = raw.GetProperty("continuation"); Fields(continuation, ContinuationFields);
            var observation = raw.GetProperty("observation"); Fields(observation, ObservationFields);
            var stops = continuation.GetProperty("phaseStops");
            if (stops.ValueKind != JsonValueKind.Array || stops.GetArrayLength() > 6) throw Invalid();
            var phases = stops.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String
                ? value.GetString()! : throw Invalid()).ToImmutableArray();
            var receipt = new AutomatedPriorityReceipt(epoch, String(raw, "gameId"), Int(raw, "seatId"), Long(raw, "revision"),
                Long(raw, "boardRevision"), Long(raw, "stackInterruptionVersion"), status, Boolean(raw, "playerActive"),
                NullableInt(raw, "decisionOwnerId"), NullableInt(raw, "priorityPlayerId"), NullableString(raw, "decisionId"),
                NullableString(raw, "priorityDecisionId"),
                new(Long(continuation, "sequence"), String(continuation, "grantId"), NullableString(continuation, "mode") switch {
                    null => null, "NoResponse" => PriorityContinuationMode.NoResponse, "Quiet" => PriorityContinuationMode.Quiet,
                    _ => throw Invalid()
                }, String(continuation, "status") switch {
                    "Idle" => PriorityContinuationStatus.Idle, "Active" => PriorityContinuationStatus.Active,
                    "Held" => PriorityContinuationStatus.Held, "Revoked" => PriorityContinuationStatus.Revoked, _ => throw Invalid()
                }, String(continuation, "reasonCode"), Int(continuation, "automaticPasses"), phases),
                new(Long(observation, "boardRevision"), Long(observation, "publicActivitySequence"),
                    Long(observation, "acknowledgedBoardRevision"), Long(observation, "acknowledgedPublicActivitySequence"),
                    Boolean(observation, "registered"), Boolean(observation, "overflowed")));
            receipt.Validate();
            if (turn < 0 || receipt.PriorityDecisionId != null && (turn == 0 || !receipt.PlayerActive
                || receipt.Status != AutomatedPriorityMatchStatus.Playing || receipt.DecisionOwnerId != receipt.SeatId
                || receipt.PriorityPlayerId != receipt.SeatId)) throw Invalid();
            return receipt;
        } catch (Exception error) when (error is ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException) {
            throw new IOException("The rules provider published invalid automated priority metadata.", error);
        }
    }

    private static void Fields(JsonElement value, string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid();
        int seen = 0;
        foreach (var property in value.EnumerateObject()) {
            int index = Array.IndexOf(expected, property.Name);
            if (index < 0 || (seen & 1 << index) != 0) throw Invalid();
            seen |= 1 << index;
        }
        if (seen != (1 << expected.Length) - 1) throw Invalid();
    }
    private static long Long(JsonElement value, string name)
        => value.GetProperty(name) is { ValueKind: JsonValueKind.Number } item && item.TryGetInt64(out long number) ? number : throw Invalid();
    private static int Int(JsonElement value, string name)
        => value.GetProperty(name) is { ValueKind: JsonValueKind.Number } item && item.TryGetInt32(out int number) ? number : throw Invalid();
    private static int? NullableInt(JsonElement value, string name)
        => value.GetProperty(name).ValueKind == JsonValueKind.Null ? null : Int(value, name);
    private static bool Boolean(JsonElement value, string name) => value.GetProperty(name).ValueKind switch {
        JsonValueKind.True => true, JsonValueKind.False => false, _ => throw Invalid()
    };
    private static string String(JsonElement value, string name)
        => value.GetProperty(name) is { ValueKind: JsonValueKind.String } item ? item.GetString()! : throw Invalid();
    private static string? NullableString(JsonElement value, string name)
        => value.GetProperty(name).ValueKind == JsonValueKind.Null ? null : String(value, name);
    private static ArgumentException Invalid() => new("Automated priority metadata must use its negotiated closed schema.");
}
