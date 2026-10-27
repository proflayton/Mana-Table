using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.Renderer;

/// <summary>One accepted observation and its newly reported, viewer-filtered events.</summary>
public sealed class PublicActivityBatch
{
    internal PublicActivityBatch(GameSnapshot? before, GameSnapshot after, ImmutableArray<ActivityEntry> entries, bool historyGap,
        ImmutableArray<ExecutionTransition> executions = default, bool executionBaseline = false, bool executionGap = false)
    { Before = before; After = after; Entries = entries; HasHistoryGap = historyGap;
        Executions = executions.IsDefault ? [] : executions; ExecutionBaseline = executionBaseline; ExecutionGap = executionGap; }
    public GameSnapshot? Before { get; }
    public GameSnapshot After { get; }
    public ImmutableArray<ActivityEntry> Entries { get; }
    public bool IsBaseline => Before == null || Before.Id != After.Id || Before.ViewerId != After.ViewerId;
    public bool HasHistoryGap { get; }
    public ImmutableArray<ExecutionTransition> Executions { get; }
    public bool ExecutionBaseline { get; }
    public bool ExecutionGap { get; }
}

/// <summary>Consumes accepted activity once for every feedback consumer. No card faces are retained.</summary>
public sealed class PublicActivityCursor
{
    private (string Game, int Viewer)? scope;
    private long lastActivity;
    public void Clear() { scope = null; lastActivity = 0; }
    public PublicActivityBatch Observe(GameSnapshot? before, GameSnapshot after,
        ImmutableArray<ExecutionTransition> executions = default, bool executionBaseline = false, bool executionGap = false)
    {
        bool baseline = executionBaseline || before == null || before.Id != after.Id || before.ViewerId != after.ViewerId;
        long latest = after.Activity.Select(entry => entry.Id).DefaultIfEmpty().Max();
        if (baseline || scope != (after.Id, after.ViewerId)) {
            scope = (after.Id, after.ViewerId);
            lastActivity = baseline ? latest : before!.Activity.Select(entry => entry.Id).DefaultIfEmpty().Max();
        }
        var entries = baseline ? ImmutableArray<ActivityEntry>.Empty
            : after.Activity.Where(entry => entry.Id > lastActivity).ToImmutableArray();
        // Filtered/bounded history need not have contiguous IDs. Only a feed
        // with no retained overlap and a skipped next ID makes old anchors uncertain.
        bool gap = entries.Length > 0 && lastActivity < long.MaxValue
            && entries.Min(entry => entry.Id) > lastActivity + 1 && !after.Activity.Any(entry => entry.Id <= lastActivity);
        lastActivity = Math.Max(lastActivity, latest);
        return new(baseline ? null : before, after, entries, gap, executions, executionBaseline, executionGap);
    }
}
