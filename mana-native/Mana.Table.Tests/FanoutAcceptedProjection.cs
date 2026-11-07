using System.Collections.Immutable;
using System.Diagnostics;
using Mana.Contracts;
using Mana.Scenarios;

namespace Mana.Table;

// Actual ordinary owner callbacks, not polling attempts, own these timestamps.
// The declared fixture is bounded; overflow fails evidence rather than silently
// trimming the initial cast or a meaningful trigger publication from replay.
internal sealed class FanoutAcceptedProjection
{
    private readonly List<GameSnapshot> observations = [];
    private readonly List<long> offsets = [];
    private long? origin;
    internal int Count => observations.Count;
    internal long AcceptedCallbacks { get; private set; }
    internal long IdenticalCallbacks { get; private set; }

    internal void Observe(GameSnapshot state)
    {
        long now = Stopwatch.GetTimestamp();
        origin ??= now;
        AcceptedCallbacks++;
        // A repeated poll can decode a fresh object for the exact same complete
        // publication. Preserve its first acceptance and actual later time gap;
        // only byte-equivalent adjacent payloads can share that checkpoint.
        if (observations.LastOrDefault() is { } prior && ConnectedPlayerDriver.SameOrdinaryPublication(prior, state)) {
            IdenticalCallbacks++; return;
        }
        if (observations.Count >= ProjectionRecording.MaximumObservations)
            throw new InvalidDataException("The declared fanout exceeded its distinct accepted-projection window; no initial or different intermediate frame may be discarded.");
        observations.Add(state); offsets.Add(Stopwatch.GetElapsedTime(origin.Value, now).Ticks);
    }

    internal FanoutAcceptedProjectionWindow Complete()
    {
        if (origin == null || observations.Count == 0)
            throw new InvalidDataException("The fanout has no actual accepted playing observations to replay.");
        return new(observations.ToImmutableArray(), offsets.ToImmutableArray(),
            Stopwatch.GetElapsedTime(origin.Value).Ticks, AcceptedCallbacks, IdenticalCallbacks);
    }
}

internal sealed record FanoutAcceptedProjectionWindow(ImmutableArray<GameSnapshot> Observations,
    ImmutableArray<long> AcceptedAtTicks, long EndTicks, long AcceptedCallbacks, long IdenticalCallbacks);
