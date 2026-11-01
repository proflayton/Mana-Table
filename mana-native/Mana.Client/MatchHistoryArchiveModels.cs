using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.Client;

public enum HistoryExportScope { Public, IncludeOwnPrivate }
public enum HistoryArchiveState { Recording, Closed, Interrupted }
public sealed record HistoryArchiveHealth(bool Available, long DroppedBatches, string? Issue);
public sealed record HistoryArchiveSummary(string ArchiveId, string GameId, int ViewerId,
    string? Version, string? SourceRevision, DateTimeOffset StartedAt, DateTimeOffset UpdatedAt,
    HistoryArchiveState State, bool Finished, long RecordCount, long LastSequence,
    long ObservedThrough, bool HasGaps, long JournalBytes, string LastHash);
public sealed record HistoryArchiveIssue(string Kind, long Count = 0);
public sealed record HistoryArchiveDocument(HistoryArchiveSummary Summary, ImmutableArray<HistoryBatch> Batches,
    ImmutableArray<HistoryArchiveIssue> Issues)
{
    public ImmutableArray<HistoryRecord> Records => Batches.SelectMany(batch => batch.Records).ToImmutableArray();
}

/// <summary>Read-only, audience-scoped receipts. Recording never performs filesystem work on its caller.
/// Historical identities are evidence only; this port cannot submit a game command.</summary>
public interface IMatchHistoryArchive
{
    HistoryArchiveHealth Health { get; }
    void Record(HistoryBatch acceptedBatch);
    void MarkClosed(string gameId, int viewerId, bool finished, bool incomplete = false);
    Task<ImmutableArray<HistoryArchiveSummary>> ListAsync(CancellationToken cancellation = default);
    Task<HistoryArchiveDocument?> ReadAsync(string archiveId, CancellationToken cancellation = default);
    Task ExportAsync(string archiveId, Stream destination, HistoryExportScope scope = HistoryExportScope.Public,
        CancellationToken cancellation = default, long? focusSequence = null);
}

internal sealed record HistoryArchiveLimits(int MaximumMatches = 10, long MaximumTotalBytes = 256L * 1024 * 1024,
    long MaximumMatchBytes = 32L * 1024 * 1024, int MaximumQueuedBatches = 64,
    int MaximumBatchBytes = HistoryFacts.MaximumBatchBytes, int MaximumJournalEntries = 65536)
{
    internal TimeSpan MaximumAge { get; init; } = TimeSpan.FromDays(30);
}
