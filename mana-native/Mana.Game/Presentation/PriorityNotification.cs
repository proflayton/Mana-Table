using Mana.Contracts;

namespace Mana.Table.Presentation;

// Attention timing only. Cohorts deduplicate reminders; they never authorize a
// pass, manufacture a response window or merge the rules engine's stack entries.
internal sealed class PriorityNotification
{
    internal const double DwellSeconds = 2, CooldownSeconds = 10, MuteSeconds = 300;
    private readonly record struct Cohort(int Turn, string Phase, long? Interruption);
    private readonly HashSet<Cohort> consumed = [];
    private readonly Queue<Cohort> retained = new();
    private string? game;
    private int viewer;
    private Cohort? pending;
    private double waitingSince, lastSound = double.NegativeInfinity;
    internal double MutedUntil { get; private set; }
    internal bool IsMuted(double now) => now < MutedUntil;
    internal void ToggleMute(double now) => MutedUntil = IsMuted(now) ? 0 : now + MuteSeconds;

    internal bool Update(GameSnapshot? state, bool awaitingUser, bool soundsEnabled, double now)
    {
        if (state == null || game != state.Id || viewer != state.ViewerId) {
            game = state?.Id; viewer = state?.ViewerId ?? 0;
            pending = null; consumed.Clear(); retained.Clear();
            MutedUntil = 0; lastSound = double.NegativeInfinity;
        }
        if (state == null || !awaitingUser || !double.IsFinite(now)) { pending = null; return false; }
        var cohort = new Cohort(state.Turn, state.PhaseKey, state.StackInterruptionVersion);
        if (consumed.Contains(cohort)) { pending = null; return false; }
        // Suppression consumes the current reminder rather than queuing a chime
        // for the end of a mute/cooldown or a later audio preference change.
        if (!soundsEnabled || IsMuted(now) || now - lastSound < CooldownSeconds) {
            Consume(cohort); return false;
        }
        if (pending != cohort) { pending = cohort; waitingSince = now; return false; }
        if (now - waitingSince < DwellSeconds) return false;
        Consume(cohort); lastSound = now;
        return true;
    }

    private void Consume(Cohort cohort)
    {
        consumed.Add(cohort); retained.Enqueue(cohort); pending = null;
        while (retained.Count > 64) consumed.Remove(retained.Dequeue());
    }
}
