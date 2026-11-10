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
        AutomatedAudienceLease audience;
        lock (worldScopeGate) {
            generation = worldGeneration;
            audience = RequireAudience();
        }
        int requestedVersion = owner.HistoryAnnouncementsAvailable ? HistoryFacts.SchemaVersion : HistoryFacts.LegacySchemaVersion;
        var raw = await Request("automatedMatchHistory", owner.HistoryAnnouncementsAvailable
            ? new { gameId, seatId, afterSequence, limit, historySchemaVersion = requestedVersion }
            : (object)new { gameId, seatId, afterSequence, limit }, expectedOwner: owner,
            beforeDispatch: () => { lock (worldScopeGate) RequireCapturedAudience(); });
        var batch = DecodeAutomatedHistory(raw, gameId, seatId, afterSequence, limit, requestedVersion);
        lock (worldScopeGate) RequireCapturedAudience();
        return batch;

        void RequireCapturedAudience()
        {
            if (!ReferenceEquals(RequireAudience(), audience))
                throw new EngineCommandRejectedException("The automated history audience retired before its reply completed.");
        }

        AutomatedAudienceLease RequireAudience()
        {
            EnsureSimulationOwner(owner, generation);
            if (!automatedAudiences.TryGetValue((gameId, seatId), out var lease)
                || !ReferenceEquals(lease.Owner, owner) || lease.Generation != generation)
                throw new EngineCommandRejectedException("Observe this host-owned automated audience before reading its history.");
            return lease;
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
