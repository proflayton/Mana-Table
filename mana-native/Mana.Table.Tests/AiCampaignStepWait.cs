using System.Diagnostics;
using Mana.Contracts;

namespace Mana.Table;

// Test-driver scheduling only. Every next host step still independently reads
// current authority; public progress supplies no command or policy allowance.
internal sealed class AiCampaignStepWait(string gameId, TimeProvider? clock = null)
{
    private HashSet<int>? players;
    private long revision, board, restoration;
    private bool baselineRequired = true;
    private long yields, timers, completed, failed, yieldTicks, timerTicks, maximumYieldTicks, maximumTimerTicks;

    internal AiCampaignStepWaitEvidence Evidence => new(yields, timers, completed, failed, Stopwatch.Frequency,
        yieldTicks, timerTicks, maximumYieldTicks, maximumTimerTicks);

    internal async Task WaitAsync(GameSnapshot? validatedPublic)
    {
        bool active = Advanced(validatedPublic);
        if (active) yields++; else timers++;
        long started = Stopwatch.GetTimestamp();
        try {
            if (active) await Task.Yield();
            else await Task.Delay(TimeSpan.FromMilliseconds(1), clock ?? TimeProvider.System);
            completed++;
        } catch { failed++; throw; }
        finally {
            long elapsed = Stopwatch.GetTimestamp() - started;
            if (active) { yieldTicks += elapsed; maximumYieldTicks = Math.Max(maximumYieldTicks, elapsed); }
            else { timerTicks += elapsed; maximumTimerTicks = Math.Max(maximumTimerTicks, elapsed); }
        }
    }

    private bool Advanced(GameSnapshot? state)
    {
        // Callers run their existing complete spectator/privacy validators
        // first. These bounded scalar guards also fail closed in direct checks.
        if (state == null || string.IsNullOrWhiteSpace(state.Id) || state.Id != gameId
            || state.ViewerId != -1 || state.Decision != null || state.Error != null
            || state.Status is not ("playing" or "resolving") || state.Revision < 0 || state.BoardRevision < 0
            || state.Players.IsDefault || state.Players.Length != 4
            || state.Players.Any(player => player == null || player.Id < 0)
            || state.Players.Count(player => !player.Eliminated) < 2) {
            baselineRequired = true; return false;
        }
        var currentPlayers = state.Players.Select(player => player.Id).ToHashSet();
        if (currentPlayers.Count != 4 || players != null && !players.SetEquals(currentPlayers)) {
            baselineRequired = true; return false;
        }
        // Only scalar restoration consistency belongs here. The caller retains
        // complete Undo/offer/identity validation; this creates no Undo authority.
        if (state.Undo is { } undo && (undo.BarrierVersion < 0 || undo.RestorationVersion < 0
            || undo.RestorationVersion == 0 && undo.LastRestoration != null
            || undo.RestorationVersion > 0 && (undo.LastRestoration == null
                || undo.LastRestoration.Version != undo.RestorationVersion))) {
            baselineRequired = true; return false;
        }
        long currentRestoration = state.Undo?.RestorationVersion ?? 0;
        if (state.Revision < revision || state.BoardRevision < board || currentRestoration < restoration) {
            baselineRequired = true; return false;
        }
        players ??= currentPlayers;
        bool baseline = baselineRequired || currentRestoration > restoration;
        bool advanced = state.BoardRevision > board;
        revision = state.Revision; board = state.BoardRevision; restoration = currentRestoration;
        baselineRequired = false;
        return !baseline && advanced;
    }
}

internal sealed record AiCampaignStepWaitEvidence(long ActiveYields, long IdleTimers, long Completed, long Failed,
    long TimestampFrequency, long ActiveElapsedTicks, long IdleElapsedTicks, long MaximumActiveElapsedTicks, long MaximumIdleElapsedTicks)
{
    public string Scope => "Elapsed around awaited driver scheduling includes continuation dispatch; not pure timer service, blocked time or CPU. Diagnostic only, never a qualification threshold.";
}
