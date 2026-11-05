using System.Collections.Immutable;
using System.Text.Json;
using Mana.Contracts;

namespace Mana.Client;

public sealed record HistoryGap(long FirstSequence, long LastSequence, string Reason);
public sealed record MatchHistoryAction(string Id, long FirstSequence, long LastSequence, int Turn, string PhaseKey,
    int? ActorPlayerId, string? SourceCardId, string SourceName, string? ParentId, ExecutionKind? Kind,
    ExecutionStep? Step, ExecutionOutcome? Outcome, ImmutableArray<HistoryRecord> Records,
    ImmutableArray<string> Children, bool HistoryIncomplete)
{
    // Null/absent historical facts never become a known empty target list.
    public ImmutableArray<HistoryRecord> Announcements => Records.Where(record => record.Announcement != null).ToImmutableArray();
}

/// <summary>An immutable reading model of admitted historical facts. It does not
/// retain cards, current decision handles, or commands and needs no live engine.</summary>
public sealed record MatchHistoryReadModel(string GameId, int ViewerId, long Cursor,
    ImmutableArray<HistoryRecord> Records, ImmutableArray<MatchHistoryAction> Actions, ImmutableArray<HistoryGap> Gaps)
{
    public const int MaximumRetainedRecords = 32768;
    public const int MaximumRetainedBytes = 32 * 1024 * 1024;
    public long RetainedBytes { get; private init; }
    public bool CoverageComplete => Gaps.IsEmpty;
    public static MatchHistoryReadModel Empty(string gameId = "", int viewerId = -1) => new(gameId, viewerId, 0, [], [], []);

    public static MatchHistoryReadModel Build(string gameId, int viewerId, ImmutableArray<HistoryRecord> records,
        ImmutableArray<HistoryGap> gaps = default, long? cursor = null)
    {
        if (records.IsDefault) throw new ArgumentException("History records must be an immutable collection.", nameof(records));
        gaps = gaps.IsDefault ? [] : gaps;
        HistoryFacts.Validate(new(HistoryFacts.SchemaVersion, gameId, viewerId, 1, 0, 0, false, []));
        long previous = 0;
        for (int offset = 0; offset < records.Length; offset += HistoryFacts.MaximumBatchRecords) {
            var chunk = records.Skip(offset).Take(HistoryFacts.MaximumBatchRecords).ToImmutableArray();
            if (chunk[0].Sequence <= previous) throw new ArgumentException("History records must remain ordered.", nameof(records));
            HistoryFacts.Validate(new(HistoryFacts.SchemaVersion, gameId, viewerId, chunk[0].Sequence,
                chunk[^1].Sequence, chunk[^1].Sequence, false, chunk));
            previous = chunk[^1].Sequence;
        }
        long last = cursor ?? previous;
        if (last < previous || gaps.Any(gap => gap == null || gap.FirstSequence < 1 || gap.LastSequence < gap.FirstSequence || gap.LastSequence > last
            || string.IsNullOrWhiteSpace(gap.Reason) || gap.Reason.Length > 128 || gap.Reason.Any(char.IsControl)))
            throw new ArgumentException("History coverage must retain ordered source bounds.", nameof(gaps));
        long bytes = records.Sum(record => (long)HistoryFacts.RecordBytes(record));
        int remove = 0;
        while (records.Length - remove > MaximumRetainedRecords || bytes > MaximumRetainedBytes)
            bytes -= HistoryFacts.RecordBytes(records[remove++]);
        if (remove > 0) {
            gaps = AddGap(gaps, new(records[0].Sequence, records[remove - 1].Sequence, "reader-retention"));
            records = records.RemoveRange(0, remove);
        }
        return new(gameId, viewerId, last, records, Reduce(records, gaps), gaps) { RetainedBytes = bytes };
    }

    public MatchHistoryReadModel Append(HistoryBatch batch, out HistoryBatch? accepted)
    {
        HistoryFacts.Validate(batch);
        accepted = null;
        if (batch.GameId != GameId || batch.ViewerId != ViewerId)
            throw new ArgumentException("A history batch cannot change its reading audience.", nameof(batch));
        if (batch.NextSequence < Cursor) return this;
        var retained = Records.ToDictionary(item => item.Sequence);
        foreach (var item in batch.Records.Where(item => item.Sequence <= Cursor))
            if (retained.TryGetValue(item.Sequence, out var existing) && !Same(existing, item))
                throw new ArgumentException("A historical occurrence changed after admission.", nameof(batch));
        var added = batch.Records.Where(item => item.Sequence > Cursor).ToImmutableArray();
        if (batch.NextSequence == Cursor && added.IsEmpty) return this;
        var gaps = Gaps;
        if (batch.FirstSequence - 1 > Cursor)
            gaps = AddGap(gaps, new(Cursor + 1, batch.FirstSequence - 1, "provider-retention"));
        var records = Records.AddRange(added);
        long bytes = RetainedBytes + added.Sum(record => (long)HistoryFacts.RecordBytes(record));
        int remove = 0;
        while (records.Length - remove > MaximumRetainedRecords || bytes > MaximumRetainedBytes)
            bytes -= HistoryFacts.RecordBytes(records[remove++]);
        if (remove > 0) {
            gaps = AddGap(gaps, new(records[0].Sequence, records[remove - 1].Sequence, "reader-retention"));
            records = records.RemoveRange(0, remove);
        }
        accepted = batch with { Records = added };
        return new(GameId, ViewerId, batch.NextSequence, records, Reduce(records, gaps), gaps) { RetainedBytes = bytes };
    }

    public static MatchHistoryReadModel Read(IEnumerable<HistoryBatch> batches)
    {
        ArgumentNullException.ThrowIfNull(batches);
        MatchHistoryReadModel? model = null;
        foreach (var batch in batches) {
            model ??= Empty(batch.GameId, batch.ViewerId);
            model = model.Append(batch, out _);
        }
        return model ?? Empty();
    }

    private static bool Same(HistoryRecord first, HistoryRecord second) => ReferenceEquals(first, second)
        || JsonElement.DeepEquals(JsonSerializer.SerializeToElement(first), JsonSerializer.SerializeToElement(second));

    private static ImmutableArray<HistoryGap> AddGap(ImmutableArray<HistoryGap> gaps, HistoryGap next)
    {
        if (gaps.Any(gap => gap == next)) return gaps;
        if (!gaps.IsEmpty && gaps[^1].Reason == next.Reason && gaps[^1].LastSequence < long.MaxValue
            && gaps[^1].LastSequence + 1 == next.FirstSequence)
            return gaps.SetItem(gaps.Length - 1, gaps[^1] with { LastSequence = next.LastSequence });
        if (gaps.Length == 64)
            gaps = [new(gaps[0].FirstSequence, gaps[1].LastSequence, "earlier-coverage-incomplete"), .. gaps.Skip(2)];
        return gaps.Add(next);
    }

    private static ImmutableArray<MatchHistoryAction> Reduce(ImmutableArray<HistoryRecord> records, ImmutableArray<HistoryGap> gaps)
    {
        string Id(HistoryRecord record) => record.Execution?.Occurrence.Id ?? record.ExecutionId
            ?? (record.Activity?.ExecutionId is { Length: > 0 } execution ? execution : "event:" + record.Sequence);
        var groups = records.GroupBy(Id, StringComparer.Ordinal).ToArray();
        var parents = groups.ToDictionary(group => group.Key, group => group.Select(item => item.Execution?.Occurrence.ParentId ?? item.ParentId)
            .LastOrDefault(parent => parent != null), StringComparer.Ordinal);
        var childIds = parents.Where(item => item.Value != null)
            .ToLookup(item => item.Value!, item => item.Key, StringComparer.Ordinal);
        var finished = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in parents.Keys) {
            var path = new HashSet<string>(StringComparer.Ordinal);
            string? current = id;
            while (current != null && parents.ContainsKey(current) && !finished.Contains(current)) {
                if (!path.Add(current)) throw new ArgumentException("Historical causal ancestry cannot contain a cycle.");
                current = parents[current];
            }
            finished.UnionWith(path);
        }
        return groups.Select(group => {
            var items = group.ToImmutableArray();
            var first = items[0];
            var occurrence = items.Select(item => item.Execution?.Occurrence).LastOrDefault(item => item != null);
            string? source = occurrence?.SourceCardId ?? items.Select(item => item.Activity == null ? item.SourceCardId : item.Activity.Cause?.CardId)
                .LastOrDefault(id => id != null);
            string name = occurrence?.SourceName ?? items.Select(item => item.Activity == null ? item.SourceName : item.Activity.Cause?.Name)
                .LastOrDefault(name => name != null)
                ?? "Unattributed action";
            int? actor = occurrence?.ActorPlayerId ?? items.Select(item => item.Activity?.Cause?.ControllerId).LastOrDefault(id => id != null);
            var children = childIds[group.Key].ToImmutableArray();
            bool missingStart = occurrence != null && !items.Any(item => item.Execution is { } execution
                && (execution.Transition?.Step ?? execution.Occurrence.Step) == ExecutionStep.Preparation);
            bool missingParent = parents[group.Key] is { } parent && !parents.ContainsKey(parent);
            bool gap = gaps.Any(gap => gap.FirstSequence <= items[^1].Sequence && gap.LastSequence >= first.Sequence);
            return new MatchHistoryAction(group.Key, first.Sequence, items[^1].Sequence, first.Turn, first.PhaseKey,
                actor, source, name, parents[group.Key], occurrence?.Kind, occurrence?.Step, occurrence?.Outcome,
                items, children, missingStart || missingParent || gap);
        }).OrderBy(action => action.FirstSequence).ToImmutableArray();
    }
}
