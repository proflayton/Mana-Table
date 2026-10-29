using Mana.Contracts;

namespace Mana.AI;

// Host-private, authorized knowledge at one decision boundary. This format is
// diagnostic input for an offline policy, never a provider command or saved game.
public sealed record AiDecisionReplayCase(int FormatVersion, string Id, string PolicyId,
    GameSnapshot State, DecisionActionDomain Domain, DecisionSemantics Semantics,
    AiSeatHistory History, long Seed)
{
    public TimeSpan RemainingTime { get; init; } = TimeSpan.FromSeconds(3);
    public int RemainingWork { get; init; } = 4096;
    public AiTurnPlan? Plan { get; init; }
    public long PlanVersion { get; init; }
    public static AiDecisionReplayCase Capture(string id, string policyId, AiDecisionRequest request)
        => new(1, id, policyId, request.Frame.State, request.Frame.Domain, request.Frame.Semantics, request.History, request.Seed) {
            RemainingTime = request.RemainingTime, RemainingWork = request.Work.Remaining,
            Plan = request.Plan, PlanVersion = request.PlanVersion };
}

public sealed record AiDecisionReplayResult(string CaseId, string PolicyId, AiSeatStatus Status,
    DecisionCommand? Proposal, AiDecisionRecord? Evidence);

/// <summary>
/// Re-evaluates captured seat knowledge through the production one-shot session
/// and command-scope checks. A ready result is only a proposal, not an engine
/// receipt. No connection, rules RNG, other seat or live execution is available.
/// </summary>
public static class AiDecisionReplay
{
    public static async Task<AiDecisionReplayResult> EvaluateAsync(AiDecisionReplayCase recorded, IAiPolicy policy,
        AiSeatLimits? limits = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(recorded); ArgumentNullException.ThrowIfNull(policy);
        if (recorded.FormatVersion != 1 || string.IsNullOrWhiteSpace(recorded.Id)
            || string.IsNullOrWhiteSpace(recorded.PolicyId) || recorded.History is not { } history
            || history.Decisions.IsDefault || history.Events.IsDefault || history.AttemptedRoutes == null
            || history.Decisions.Length > 4096 || history.Events.Length > 4096 || history.AttemptedRoutes.Count > 4096
            || recorded.RemainingTime <= TimeSpan.Zero || recorded.RemainingWork <= 0
            || recorded.PlanVersion < 0 || recorded.PlanVersion == long.MaxValue)
            throw new ArgumentException("A replay requires versioned private decision evidence.", nameof(recorded));
        var frame = new AiDecisionFrame(recorded.State, domain: recorded.Domain, semantics: recorded.Semantics);
        if (history.Decisions.Any(entry => entry == null || entry.Stamp.GameId != frame.State.Id || entry.Stamp.SeatId != frame.State.ViewerId
                || entry.Stamp.Revision < 0 || entry.Stamp.Revision > frame.State.Revision
                || entry.Stamp.BoardRevision < 0 || entry.Stamp.BoardRevision > frame.State.BoardRevision || entry.Stamp.OfferVersion < 0
                || entry.PlanVersion < 0 || entry.RetainedPlan is { } retained && (retained.GameId != frame.State.Id
                    || retained.SeatId != frame.State.ViewerId || retained.Version != entry.PlanVersion)
                || entry.ProposedPlanTransition?.Next is { } proposed && (proposed.GameId != frame.State.Id || proposed.SeatId != frame.State.ViewerId))
            || history.Events.Any(entry => entry == null || entry.Turn > frame.State.Turn)
            || history.AttemptedRoutes.Any(route => route == null || route.Turn != frame.State.Turn))
            throw new ArgumentException("A replay cannot mix another seat, future event or turn's private history.", nameof(recorded));
        if (history.ImpactEvents is { } impacts) AiImpactHistory.ValidateRecorded(impacts, frame.State);
        if (history.RouteLimitReached)
            return new(recorded.Id, policy.Descriptor.Id, AiSeatStatus.BudgetExceeded, null, null);
        // Reverse the documented first-episode derivation so the existing seat
        // runner supplies exactly the recorded evaluation seed, not a new draw.
        long baseSeed = AiPolicySeeds.FirstEpisodeBase(recorded.Seed, frame.State.ViewerId);
        using var session = new AiSeatSession(frame.State.Id, frame.State.ViewerId,
            new ReplayPolicy(policy, history), baseSeed, limits ?? new(recorded.RemainingTime, recorded.RemainingWork));
        session.Observe(frame);
        session.RestorePlanForReplay(recorded.Plan, recorded.PlanVersion);
        await session.EvaluateAsync(cancellation).ConfigureAwait(false);
        var proposed = session.TakeCommand();
        return new(recorded.Id, policy.Descriptor.Id, proposed != null ? AiSeatStatus.Ready : session.View.Status, proposed?.Command,
            session.History.Decisions.LastOrDefault(entry => entry.Outcome != AiDecisionOutcome.Submitted));
    }

    private sealed class ReplayPolicy(IAiPolicy policy, AiSeatHistory history) : IAiPolicy
    {
        public AiPolicyDescriptor Descriptor => policy.Descriptor;
        public Task<AiProposal> EvaluateAsync(AiDecisionRequest request, CancellationToken cancellation)
            => policy.EvaluateAsync(request with { History = history }, cancellation);
    }
}
