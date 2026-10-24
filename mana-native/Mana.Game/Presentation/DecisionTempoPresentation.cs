using Mana.Contracts;

namespace Mana.Table.Presentation;

// Interpolates a server clock for paint only. Zero never submits a game command.
internal sealed class DecisionTempoPresentation
{
    private DecisionTempoFrame? frame;
    private double receivedAt;
    internal void Observe(GameSnapshot? state, double now)
    {
        if (ReferenceEquals(frame, state?.Tempo)) return;
        frame = state?.Tempo;
        receivedAt = now;
    }
    internal ClockPaint? For(int playerId, double now)
    {
        var clock = frame?.Clocks.FirstOrDefault(item => item.PlayerId == playerId);
        if (clock == null) return null;
        long elapsed = clock.Phase is TempoPhase.Thinking or TempoPhase.Grace ? (long)(Math.Max(0, now - receivedAt) * 1000) : 0;
        long remaining = Math.Max(0, clock.RemainingMs - elapsed);
        return new(clock.Phase, remaining, clock.LimitMs <= 0 ? 0 : Math.Clamp((float)remaining / clock.LimitMs, 0, 1));
    }
}
internal sealed record ClockPaint(TempoPhase Phase, long RemainingMs, float Fraction);
