using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.Client;

public sealed record ExecutionBatch(ImmutableArray<ExecutionTransition> Transitions, bool Baseline = false, bool Gap = false)
{
    public static ExecutionBatch Empty { get; } = new([]);
}

// Admit only an already accepted observation. Global retention fences, rather
// than adjacent visible sequence numbers, detect lost history after filtering.
public sealed class ExecutionCursor
{
    private string gameId = "";
    private int viewerId = -1;
    private long sequence;

    public ExecutionBatch Observe(GameSnapshot? state, bool baseline = false)
    {
        if (state?.Executions is not { } frame) { Reset(); return ExecutionBatch.Empty; }
        Validate(state);
        bool first = gameId != state.Id || viewerId != state.ViewerId;
        bool gap = !first && sequence < frame.FirstSequence - 1;
        var transitions = first || baseline || gap ? [] : frame.Transitions.Where(item => item.Sequence > sequence).ToImmutableArray();
        gameId = state.Id; viewerId = state.ViewerId; sequence = frame.LastSequence;
        return new(transitions, first || baseline || gap, gap);
    }

    internal void Validate(GameSnapshot? state)
    {
        if (state == null) return;
        ExecutionFrames.Validate(state);
        if (state.Executions is { } frame && gameId == state.Id && viewerId == state.ViewerId && frame.LastSequence < sequence)
            throw new ArgumentException("Execution history cannot move backward within one game and viewer.", nameof(state));
    }

    public void Reset() { gameId = ""; viewerId = -1; sequence = 0; }
}
