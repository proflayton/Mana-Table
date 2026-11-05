using Mana.Contracts;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mana.Forge;

public sealed partial class ForgeEngine : IMatchHistory
{
    public bool SupportsHistory => current is { HistoryAvailable: true, Failure: null };

    public async Task<HistoryBatch> HistoryAsync(string gameId, long afterSequence, int limit = 128)
    {
        if (string.IsNullOrWhiteSpace(gameId) || afterSequence < 0 || limit is < 1 or > HistoryFacts.MaximumBatchRecords)
            throw new ArgumentException("History requires a game and bounded source cursor.");
        var owner = current;
        if (owner is not { HistoryAvailable: true, Failure: null })
            throw new NotSupportedException("This provider does not expose match history.");
        int requestedVersion = owner.HistoryAnnouncementsAvailable ? HistoryFacts.SchemaVersion : HistoryFacts.LegacySchemaVersion;
        var raw = await Request("matchHistory", owner.HistoryAnnouncementsAvailable
            ? new { gameId, afterSequence, limit, historySchemaVersion = requestedVersion } : (object)new { gameId, afterSequence, limit });
        if (current != owner || owner.Failure != null) throw new IOException("The history provider changed.");
        var batch = ForgeHistoryDecoder.Decode(raw, requestedVersion);
        if (batch.GameId != gameId || batch.NextSequence < afterSequence)
            throw new IOException("The provider returned a different history scope or an older cursor.");
        return batch;
    }
}

// The archive is typed data, not a raw provider response or a live snapshot.
internal static class ForgeHistoryDecoder
{
    private static readonly JsonSerializerOptions Json = new() {
        PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    internal static HistoryBatch Decode(JsonElement raw, int maximumVersion = HistoryFacts.SchemaVersion)
    {
        try {
            var batch = raw.Deserialize<HistoryBatch>(Json) ?? throw new JsonException("Missing history batch.");
            if (batch.SchemaVersion > maximumVersion) throw new JsonException("The provider used an unrequested history schema.");
            HistoryFacts.Validate(batch);
            return batch;
        } catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or OverflowException) {
            throw new IOException("The rules provider published an invalid match history batch.", error);
        }
    }
}
