using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.AI;

/// <summary>Public progress only: no card faces, hand contents, action offers or decision answers.</summary>
public sealed record AiTableProgress(string GameId, int SeatId, int Turn, int? ActivePlayerId,
    ImmutableArray<string> StackIds, long PublicActionId)
{
    public AiResolutionProgress? Resolution { get; init; }
    public long RestorationVersion { get; init; }
    internal static AiTableProgress From(GameSnapshot state) => new(state.Id, state.ViewerId, state.Turn, state.ActivePlayerId,
        state.Stack.Select(item => item.Id).ToImmutableArray(),
        state.Activity.Where(entry => entry.Kind is "cast" or "ability" or "resolved" or "land" or "combat")
            .Select(entry => entry.Id).DefaultIfEmpty().Max()) { Resolution = AiResolutionProgress.From(state),
                RestorationVersion = state.Undo?.RestorationVersion ?? 0 };
}

/// <summary>Host cadence, independent of strategy, legality, and headless evaluation speed.</summary>
public interface IAiHostPacing
{
    void Observe(ImmutableArray<AiTableProgress> progress);
    bool Ready(string gameId, int seatId);
}

/// <summary>Allows one public action to be announced before the next bot command.
/// Waiting never caches a command: the host must obtain a fresh seat observation on its next step.</summary>
public sealed class TimedAiHostPacing(TimeSpan duration, TimeProvider? clock = null) : IAiHostPacing
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private sealed record Beat(AiTableProgress Progress, long Started, bool Waiting, AiResolutionCadence? Cohort);
    private readonly Dictionary<(string Game, int Seat), Beat> beats = [];

    public void Observe(ImmutableArray<AiTableProgress> progress)
    {
        var active = progress.Select(item => (item.GameId, item.SeatId)).ToHashSet();
        foreach (var retired in beats.Keys.Where(key => !active.Contains(key)).ToArray()) beats.Remove(retired);
        foreach (var item in progress) {
            bool baseline = !beats.TryGetValue((item.GameId, item.SeatId), out var before);
            // Activity IDs belong to one viewer's filtered feed, not the table.
            // Observe every seat together so a shared action's beat starts once
            // for each audience, even while only one of them has a prompt.
            bool changed = baseline ? !item.StackIds.IsEmpty : before!.Progress.Turn != item.Turn
                || before.Progress.ActivePlayerId != item.ActivePlayerId || before.Progress.PublicActionId != item.PublicActionId
                || !before.Progress.StackIds.SequenceEqual(item.StackIds)
                || before.Progress.RestorationVersion != item.RestorationVersion
                || AiResolutionCadence.Changed(before.Progress.Resolution, item.Resolution);
            bool capturedProgress = changed && !baseline && before!.Progress.RestorationVersion == item.RestorationVersion && before.Progress.Turn == item.Turn
                && before.Progress.ActivePlayerId == item.ActivePlayerId && before.Cohort?.Continue(item.Resolution) == true;
            var cohort = capturedProgress || !changed ? before?.Cohort : AiResolutionCadence.Capture(item.Resolution);
            beats[(item.GameId, item.SeatId)] = new(item, changed && !capturedProgress ? time.GetTimestamp() : before?.Started ?? 0,
                changed || before?.Waiting == true, cohort);
        }
    }

    public bool Ready(string gameId, int seatId) => beats.TryGetValue((gameId, seatId), out var beat)
        && (!beat.Waiting || time.GetElapsedTime(beat.Started) >= duration);
}
