using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.Table;

// Evidence ownership follows accepted immutable audience frames. Repeated script
// ticks are not new observations; a fresh object still has to prove its facts,
// even when its board/revision numbers match a previously accepted publication.
internal sealed class MixedGamePublicCohorts
{
    private sealed record Owner(GameSnapshot State, string Facts,
        ForgePublicProjectionFacts.ExecutionCorrelation Correlations);
    private sealed class Cohort(string facts, ForgePublicProjectionFacts.ExecutionCorrelation correlations, int viewer)
    {
        internal string Facts { get; } = facts;
        internal ForgePublicProjectionFacts.ExecutionCorrelation Correlations = correlations;
        internal HashSet<int> Viewers { get; } = [viewer];
        internal bool Complete;
    }
    private readonly ImmutableHashSet<int> participants;
    private readonly int capacity;
    private readonly Dictionary<int, Owner> owners = [];
    private readonly Dictionary<(string Game, long Board), Cohort> cohorts = [];
    private string? game;
    private long comparedFrames, immutableRepeatSkips;
    private int maximumRetained;
    internal int Comparisons { get; private set; }
    internal int FourAudienceCohorts { get; private set; }
    internal MixedPublicComparisonEvidence Evidence => new(comparedFrames, immutableRepeatSkips,
        maximumRetained, cohorts.Count, owners.Count);

    internal MixedGamePublicCohorts(IEnumerable<int> participants, int capacity = 64)
    {
        this.participants = participants.ToImmutableHashSet();
        if (this.participants.Count != 4 || this.participants.Any(id => id < 0) || capacity < 1)
            throw new ArgumentException("A mixed-game comparison owns four distinct participants and a positive cohort capacity.");
        this.capacity = capacity;
    }

    internal MixedPublicObservation Observe(int viewer, GameSnapshot frame)
    {
        if (!participants.Contains(viewer) || frame.ViewerId != viewer)
            throw new InvalidDataException("The mixed-game public comparison received a foreign audience.");
        owners.TryGetValue(viewer, out var priorOwner);
        if (ReferenceEquals(priorOwner?.State, frame)) { immutableRepeatSkips++; return new(false, false, null); }
        if (frame.Status == "error" || frame.Error != null) throw new InvalidDataException(frame.Error ?? "Rules error.");
        if (frame.Players.Length != 4 || !frame.Players.Select(player => player.Id).ToHashSet().SetEquals(participants))
            throw new InvalidDataException("The mixed table changed its player mapping.");
        if (game != null && frame.Id != game) throw new InvalidDataException("An audience changed games during the match.");
        if (priorOwner != null && (frame.Revision < priorOwner.State.Revision || frame.BoardRevision < priorOwner.State.BoardRevision))
            throw new InvalidDataException("A mixed-game audience rolled back an accepted publication.");

        string facts = ForgePublicProjectionFacts.Capture(frame);
        var correlations = ForgePublicProjectionFacts.CaptureExecutionCorrelations(frame);
        if (!correlations.Valid) throw new InvalidDataException("An audience published invalid execution references.");
        var ownerCorrelations = correlations;
        if (priorOwner != null && priorOwner.State.BoardRevision == frame.BoardRevision) {
            if (priorOwner.Facts != facts)
                throw new InvalidDataException("An audience contradicted its same public board after a fresh observation.");
            ownerCorrelations = ForgePublicProjectionFacts.MergeExecutionCorrelations(priorOwner.Correlations, correlations)
                ?? throw new InvalidDataException("An audience contradicted its admitted same-board execution references.");
        }
        var key = (frame.Id, frame.BoardRevision);
        bool first = !cohorts.TryGetValue(key, out var cohort);
        if (cohort == null) cohorts.Add(key, cohort = new(facts, ownerCorrelations, viewer));
        else {
            if (cohort.Facts != facts) throw new InvalidDataException("The same published board disagreed across observations; see private-final-observations.json.");
            cohort.Correlations = ForgePublicProjectionFacts.MergeExecutionCorrelations(cohort.Correlations, ownerCorrelations)
                ?? throw new InvalidDataException("Audiences disclosed contradictory references for the same stack occurrence.");
            if (cohort.Viewers.Add(viewer)) Comparisons++;
        }
        if (cohort.Viewers.Count == 4 && !cohort.Complete) { cohort.Complete = true; FourAudienceCohorts++; }
        owners[viewer] = new(frame, facts, ownerCorrelations); game = frame.Id; comparedFrames++;
        if (cohorts.Count > capacity)
            foreach (var stale in cohorts.Keys.OrderByDescending(item => item.Board).Skip(capacity).ToArray()) cohorts.Remove(stale);
        maximumRetained = Math.Max(maximumRetained, cohorts.Count);
        return new(true, first, facts);
    }
}

internal readonly record struct MixedPublicObservation(bool Compared, bool FirstCohort, string? Facts);
internal sealed record MixedPublicComparisonEvidence(long ComparedFrames, long ImmutableRepeatSkips,
    int MaximumRetainedCohorts, int RetainedCohorts, int AudienceCount);
