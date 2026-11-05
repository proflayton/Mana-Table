using Mana.Contracts;
using System.Text.Json;

namespace Mana.Forge;

public sealed partial class ForgeEngine : IAutomatedMatchHistory
{
    public bool SupportsAutomatedHistory => current is {
        AutomationAvailable: true, AutomatedHistoryAvailable: true, Failure: null };

    public async Task<HistoryBatch> AutomatedHistoryAsync(string gameId, int seatId, long afterSequence, int limit = 128)
    {
        if (string.IsNullOrWhiteSpace(gameId) || seatId < 0 || afterSequence < 0
            || limit is < 1 or > HistoryFacts.MaximumBatchRecords)
            throw new ArgumentException("Automated history requires an owned game, seat and bounded source cursor.");
        var owner = current;
        if (owner is not { AutomationAvailable: true, AutomatedHistoryAvailable: true, Failure: null })
            throw new NotSupportedException("This provider does not expose automated-seat history.");
        long generation;
        lock (worldScopeGate) {
            generation = worldGeneration;
            RequireAudience();
        }
        int requestedVersion = owner.HistoryAnnouncementsAvailable ? HistoryFacts.SchemaVersion : HistoryFacts.LegacySchemaVersion;
        var raw = await Request("automatedMatchHistory", owner.HistoryAnnouncementsAvailable
            ? new { gameId, seatId, afterSequence, limit, historySchemaVersion = requestedVersion }
            : (object)new { gameId, seatId, afterSequence, limit });
        var batch = DecodeAutomatedHistory(raw, gameId, seatId, afterSequence, limit, requestedVersion);
        lock (worldScopeGate) RequireAudience();
        return batch;

        void RequireAudience()
        {
            EnsureSimulationOwner(owner, generation);
            if (!automatedScopes.ContainsKey((gameId, seatId)))
                throw new EngineCommandRejectedException("Observe this host-owned automated audience before reading its history.");
        }
    }

    internal static HistoryBatch DecodeAutomatedHistory(JsonElement raw, string gameId, int seatId, long afterSequence, int limit,
        int maximumVersion = HistoryFacts.SchemaVersion)
    {
        var batch = ForgeHistoryDecoder.Decode(raw, maximumVersion);
        if (batch.GameId != gameId || batch.ViewerId != seatId || batch.NextSequence < afterSequence
            || batch.Records.Length > limit || batch.Records.Any(record => record.Sequence <= afterSequence))
            throw new IOException("The provider returned a different automated history audience or cursor.");
        return batch;
    }
}
