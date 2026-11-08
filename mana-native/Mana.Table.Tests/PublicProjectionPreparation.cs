using Mana.Contracts;

namespace Mana.Table;

// One campaign's serialized evidence/drain/cohort owner. Background producers
// only enqueue observations; they never access these lazy slots. Threads may
// change across awaits, but concurrent preparation is not part of this API.
internal sealed class PublicProjectionPreparation(IEnumerable<int> knownAudienceIds)
{
    internal sealed record Counts(int KnownAudienceSlots, int RetainedFrames, long FactsPrepared, long FactsReused,
        long CorrelationsPrepared, long CorrelationsReused);
    private sealed class Slot
    {
        internal GameSnapshot? Frame;
        internal string? Facts;
        internal ForgePublicProjectionFacts.ExecutionCorrelation? Correlations;
    }
    private readonly Dictionary<int, Slot> slots = knownAudienceIds.Distinct().ToDictionary(id => id, _ => new Slot());
    internal long FactsPrepared { get; private set; }
    internal long FactsReused { get; private set; }
    internal long CorrelationsPrepared { get; private set; }
    internal long CorrelationsReused { get; private set; }
    internal int KnownAudienceSlots => slots.Count;
    internal int RetainedFrames => slots.Values.Count(slot => slot.Frame != null);
    internal Counts Evidence => new(KnownAudienceSlots, RetainedFrames, FactsPrepared, FactsReused, CorrelationsPrepared, CorrelationsReused);

    private Slot? Current(GameSnapshot frame)
    {
        // Unknown audiences still take the original uncached derivation path.
        // Their admission/rejection remains the existing consumer's decision.
        if (!slots.TryGetValue(frame.ViewerId, out var slot)) return null;
        if (!ReferenceEquals(slot.Frame, frame)) {
            slot.Frame = frame; slot.Facts = null; slot.Correlations = null;
        }
        return slot;
    }
    internal string Facts(GameSnapshot frame)
    {
        var slot = Current(frame);
        if (slot?.Facts is { } existing) { FactsReused++; return existing; }
        string facts = ForgePublicProjectionFacts.Capture(frame);
        FactsPrepared++;
        if (slot != null) slot.Facts = facts;
        return facts;
    }
    internal ForgePublicProjectionFacts.ExecutionCorrelation ExecutionCorrelations(GameSnapshot frame)
    {
        var slot = Current(frame);
        if (slot?.Correlations is { } existing) { CorrelationsReused++; return existing; }
        var correlations = ForgePublicProjectionFacts.CaptureExecutionCorrelations(frame);
        CorrelationsPrepared++;
        if (slot != null && correlations.Valid) slot.Correlations = correlations;
        return correlations;
    }
    internal void Clear()
    {
        foreach (var slot in slots.Values) { slot.Frame = null; slot.Facts = null; slot.Correlations = null; }
    }
}
