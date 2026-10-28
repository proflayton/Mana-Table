using Mana.Contracts;

namespace Mana.AI;

public enum AiSeatStatus { Idle, Thinking, Ready, Submitted, WaitingForObservation, Unavailable, Invalid, Cancelled, BudgetExceeded, NeedsRecovery, Closed }
public enum AiExecutionReceipt { Accepted, Rejected, Unknown }
public sealed record AiSeatView(AiDecisionStamp? Stamp, AiSeatStatus Status, string Detail = "");

/// <summary>Only the session can create this one-shot handoff. Acceptance is not effect resolution.</summary>
public sealed class AiSubmission
{
    public AiDecisionStamp Stamp { get; }
    public DecisionCommand Command { get; }
    public string ReasonCode { get; }
    public AiTask Task { get; }
    internal long Seed { get; }
    internal int RemainingWork { get; }
    internal TimeSpan Elapsed { get; }
    internal AiTurnRoute? Route { get; }
    internal string EpisodeId { get; }
    internal AiPlanTransition? PlanTransition { get; }
    internal AiSubmission(AiDecisionStamp stamp, AiTask task, DecisionCommand command, string reason,
        long seed, int remainingWork, TimeSpan elapsed, AiTurnRoute? route, string episodeId, AiPlanTransition? planTransition)
        => (Stamp, Task, Command, ReasonCode, Seed, RemainingWork, Elapsed, Route, EpisodeId, PlanTransition)
            = (stamp, task, command, reason, seed, remainingWork, elapsed, route, episodeId, planTransition);
}

/// <summary>
/// One game's one seat. Owns authority, budgets and bounded history, not transport
/// or strategy. The host submits a taken command through its normal connection
/// and reports the actual receipt. The policy never receives that connection.
/// </summary>
public sealed class AiSeatSession : IDisposable
{
    private readonly object gate = new();
    private readonly string gameId;
    private readonly int seatId;
    private readonly IAiPolicy policy;
    private readonly string policyId;
    private readonly long baseSeed;
    private readonly AiSeatLimits limits;
    private readonly TimeProvider time;
    private readonly AiHistoryBuffer history;
    private AiDecisionFrame? frame;
    private AiDecisionStamp? watermark;
    private AiSeatView view = new(null, AiSeatStatus.Idle);
    private CancellationTokenSource? evaluationCancellation;
    private Task<AiSeatView>? evaluation;
    private AiDecisionStamp? evaluatingStamp, completedStamp;
    private AiProposal.Answer? ready;
    private AiSubmission? submitted;
    private AiTurnPlan? plan;
    private long planVersion;
    private bool submittedPlanRevoked;
    private string episode = "";
    private long episodeSequence, episodeSeed;
    private AiWorkBudget work = null!;
    private readonly Dictionary<string, Allowance> executionAllowances = new(StringComparer.Ordinal);
    private Allowance? allowance;
    private Allowance? evaluatingAllowance;
    private long evaluationStarted;
    private sealed class Allowance(long seed, int units)
    {
        internal long Seed { get; } = seed;
        internal AiWorkBudget Work { get; } = new(units);
        internal TimeSpan Elapsed { get; set; }
    }
    private bool closed;

    public AiSeatSession(string gameId, int seatId, IAiPolicy policy, long seed, AiSeatLimits limits, TimeProvider? time = null)
    {
        if (string.IsNullOrWhiteSpace(gameId) || seatId < 0) throw new ArgumentException("Bind AI sessions to one game and seat.");
        ArgumentNullException.ThrowIfNull(policy); ArgumentNullException.ThrowIfNull(limits);
        if (policy.Descriptor == null || !policy.Descriptor.Tasks.SetEquals(AiTasks.All))
            throw new ArgumentException("A whole-seat policy must declare every decision family.", nameof(policy));
        this.gameId = gameId; this.seatId = seatId; this.policy = policy; policyId = policy.Descriptor.Id;
        baseSeed = seed; this.limits = limits; this.time = time ?? TimeProvider.System;
        history = new(limits.HistoryCapacity);
    }

    public AiSeatView View { get { lock (gate) return view; } }
    public AiSeatHistory History { get { lock (gate) return history.Snapshot; } }
    public AiTurnPlan? Plan { get { lock (gate) return plan; } }
    public long PlanVersion { get { lock (gate) return planVersion; } }

    public bool Observe(AiDecisionFrame? next)
    {
        CancellationTokenSource? cancel;
        lock (gate) {
            if (closed) return false;
            if (next != null) {
                if (next.Stamp.GameId != gameId || next.Stamp.SeatId != seatId)
                    throw new ArgumentException("A seat cannot observe another seat's knowledge or another game.", nameof(next));
                if (watermark is { } prior) {
                    if (next.Stamp.Revision < prior.Revision || next.Stamp.BoardRevision < prior.BoardRevision
                        || next.Stamp.Revision == prior.Revision && next.Stamp.OfferVersion < prior.OfferVersion) return false;
                    if (next.Stamp.Revision == prior.Revision && next.Stamp.DecisionId != prior.DecisionId)
                        throw new ArgumentException("A changed decision needs a new observation revision.", nameof(next));
                    if (next.Stamp == prior) return false;
                }
                // Validate chronology before revoking work, changing memory or
                // advancing the observation watermark. Invalid input is atomic.
                history.ValidateTurn(next.State.Turn);
            }
            if (frame != null && view.Status is AiSeatStatus.Thinking or AiSeatStatus.Ready)
                Record(frame, AiDecisionOutcome.Cancelled, ready?.Command, "observation-revoked");
            FinishActiveTime();
            if (next != null) {
                // A memory proposal cannot survive losing the resources it was
                // based on while its command acknowledgement is in flight.
                if (submitted?.PlanTransition?.Next is { } pendingPlan
                    && !AiPlanValidation.CanRetain(pendingPlan, pendingPlan.Version, next)) submittedPlanRevoked = true;
                if (plan != null && !AiPlanValidation.CanRetain(plan, planVersion, next)) {
                    plan = null;
                    if (planVersion < long.MaxValue) planVersion++;
                }
                watermark = next.Stamp;
                history.ObserveTurn(next.State.Turn);
                history.Observe(next.State.Activity);
                if (episode != next.EpisodeId) {
                    episode = next.EpisodeId;
                    if (next.ExecutionBudgetRootId.Length > 0) {
                        if (!executionAllowances.TryGetValue(next.ExecutionBudgetRootId, out allowance)) {
                            allowance = NewAllowance(); executionAllowances.Add(next.ExecutionBudgetRootId, allowance);
                        }
                    } else allowance = NewAllowance();
                    episodeSeed = allowance.Seed; work = allowance.Work;
                }
                // A producing occurrence can finish before a copied ability.
                // The authoritative frame retains every active occurrence and
                // its root, even after its completed history leaves the window.
                if (next.State.Executions != null) {
                    var liveRoots = ExecutionFrames.LiveBudgetRoots(next.State);
                    foreach (var root in executionAllowances.Keys.Where(root => !liveRoots.Contains(root)).ToArray()) executionAllowances.Remove(root);
                }
            }
            cancel = evaluationCancellation; evaluationCancellation = null;
            frame = next; ready = null;
            if (view.Status != AiSeatStatus.NeedsRecovery && submitted == null)
                view = new(next?.Stamp, AiSeatStatus.Idle);
        }
        Cancel(cancel);
        return true;
    }

    public Task<AiSeatView> EvaluateAsync(CancellationToken cancellation = default)
    {
        lock (gate) {
            if (closed || frame == null || submitted != null || view.Status == AiSeatStatus.NeedsRecovery
                || completedStamp == frame.Stamp) return Task.FromResult(view);
            if (evaluation is { IsCompleted: false } && evaluatingStamp == frame.Stamp) return evaluation;
            var current = frame;
            TimeSpan remaining = limits.DecisionTime - Elapsed;
            if (remaining <= TimeSpan.Zero || work.Remaining == 0 || history.RouteLimitReached) {
                Record(current, AiDecisionOutcome.BudgetExceeded, null, "episode-budget");
                completedStamp = current.Stamp;
                return Task.FromResult(view = new(current.Stamp, AiSeatStatus.BudgetExceeded, "Decision budget exhausted."));
            }
            var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            evaluationCancellation = cancel; evaluatingStamp = current.Stamp;
            var request = new AiDecisionRequest(current, history.Snapshot, episodeSeed, remaining, work) {
                Plan = plan, PlanVersion = planVersion };
            view = new(current.Stamp, AiSeatStatus.Thinking);
            long scheduledAt = time.GetTimestamp();
            evaluatingAllowance = allowance; evaluationStarted = scheduledAt;
            evaluation = Task.Run(() => EvaluateCoreAsync(request, cancel, scheduledAt));
            return evaluation;
        }
    }

    private async Task<AiSeatView> EvaluateCoreAsync(AiDecisionRequest request, CancellationTokenSource cancellation, long scheduledAt)
    {
        AiProposal? proposal = null;
        AiSeatStatus failure = AiSeatStatus.Invalid;
        string detail = "The policy failed.", reason = "policy-failed";
        try {
            // The policy may ignore cancellation; its late result still has no
            // authority. The request carries immutable history, never live memory.
            var token = cancellation.Token;
            token.ThrowIfCancellationRequested();
            var remaining = request.RemainingTime - time.GetElapsedTime(scheduledAt);
            if (remaining <= TimeSpan.Zero) throw new TimeoutException();
            var computation = Task.Run(() => policy.EvaluateAsync(request, token));
            _ = computation.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            proposal = await computation.WaitAsync(remaining, time, token).ConfigureAwait(false);
        } catch (TimeoutException) {
            failure = AiSeatStatus.BudgetExceeded; detail = "Decision time exhausted."; reason = "time-budget";
        } catch (OperationCanceledException) {
            failure = AiSeatStatus.Cancelled; detail = "Evaluation cancelled."; reason = "cancelled";
        } catch (Exception) { /* Provider/model diagnostics must not expose private cards through exception messages. */ }
        finally { Cancel(cancellation); cancellation.Dispose(); }
        lock (gate) {
            if (closed || frame?.Stamp != request.Frame.Stamp || !ReferenceEquals(evaluationCancellation, cancellation))
                return new(request.Frame.Stamp, AiSeatStatus.Cancelled, "Superseded observation.");
            evaluationCancellation = null;
            FinishActiveTime();
            completedStamp = request.Frame.Stamp;
            if (Elapsed >= limits.DecisionTime) {
                proposal = null; failure = AiSeatStatus.BudgetExceeded; detail = "Decision time exhausted."; reason = "time-budget";
            }
            if (proposal is AiProposal.Unavailable unavailable) {
                if (!ValidCode(unavailable.Capability)) {
                    Record(frame, AiDecisionOutcome.Invalid, null, "invalid-capability");
                    return view = new(frame.Stamp, AiSeatStatus.Invalid, "Missing-capability results require a stable identifier.");
                }
                Record(frame, AiDecisionOutcome.Unavailable, null, "missing-capability");
                return view = new(frame.Stamp, AiSeatStatus.Unavailable, unavailable.Capability);
            }
            if (proposal is AiProposal.Answer answer) {
                if (!ValidCode(answer.ReasonCode) || answer.Command == null || !frame.Allows(answer.Command)) {
                    Record(frame, AiDecisionOutcome.Invalid, null, "unoffered-answer");
                    return view = new(frame.Stamp, AiSeatStatus.Invalid, "The answer is outside the current offers or constraints.");
                }
                if (!AiPlanValidation.IsTransitionValid(answer.PlanTransition, planVersion, frame, plan)) {
                    Record(frame, AiDecisionOutcome.Invalid, null, "invalid-plan-transition");
                    return view = new(frame.Stamp, AiSeatStatus.Invalid, "The proposed plan is stale or references unavailable resources.");
                }
                ready = answer;
                Record(frame, AiDecisionOutcome.Offered, answer.Command, answer.ReasonCode, answer.PlanTransition);
                return view = new(frame.Stamp, AiSeatStatus.Ready);
            }
            Record(frame, failure switch { AiSeatStatus.BudgetExceeded => AiDecisionOutcome.BudgetExceeded,
                AiSeatStatus.Cancelled => AiDecisionOutcome.Cancelled, _ => AiDecisionOutcome.Invalid }, null, reason);
            return view = new(frame.Stamp, failure, detail);
        }
    }

    public AiSubmission? TakeCommand()
    {
        lock (gate) {
            if (closed || view.Status != AiSeatStatus.Ready || frame == null || ready == null || submitted != null) return null;
            if (Elapsed >= limits.DecisionTime || !frame.Allows(ready.Command)) {
                Record(frame, AiDecisionOutcome.BudgetExceeded, ready.Command, "handoff-budget");
                ready = null; view = new(frame.Stamp, AiSeatStatus.BudgetExceeded); return null;
            }
            AiTurnRoute? route = null;
            if (ready.Command is UseOfferedAction offered) {
                var source = frame.SourceActions.First(set => set.SourceCardKey == offered.Card);
                var semantic = frame.Domain.Actions.FirstOrDefault(action => action.ActionId == offered.ActionId);
                if (semantic is { StableId.Length: > 0 }) route = new(frame.State.Turn, source.SourceCardId, semantic.StableId);
            }
            submitted = new(frame.Stamp, frame.Task, ready.Command, ready.ReasonCode,
                episodeSeed, work.Remaining, Elapsed, route, frame.EpisodeId, ready.PlanTransition); ready = null;
            submittedPlanRevoked = false;
            Record(frame, AiDecisionOutcome.Submitted, submitted.Command, submitted.ReasonCode, submitted.PlanTransition);
            view = new(frame.Stamp, AiSeatStatus.Submitted);
            return submitted;
        }
    }

    public bool Report(AiSubmission submission, AiExecutionReceipt receipt)
    {
        if (!Enum.IsDefined(receipt)) throw new ArgumentOutOfRangeException(nameof(receipt));
        if (submission == null) return false;
        lock (gate) {
            if (closed || !ReferenceEquals(submitted, submission)) return false;
            var outcome = receipt switch { AiExecutionReceipt.Accepted => AiDecisionOutcome.Accepted,
                AiExecutionReceipt.Rejected => AiDecisionOutcome.Rejected, _ => AiDecisionOutcome.ExecutionUnknown };
            bool applied = false;
            if (receipt == AiExecutionReceipt.Accepted && !submittedPlanRevoked && frame != null
                && planVersion < long.MaxValue && submission.PlanTransition is { } change && change.ExpectedVersion == planVersion
                && (change.Next == null || AiPlanValidation.CanRetain(change.Next, planVersion + 1, frame))) {
                plan = change.Next; planVersion++; applied = true;
            }
            // Use the submitted stamp even if the engine has already published
            // the next choice. A receipt is not that next choice's outcome.
            history.Record(new(submission.Stamp, submission.Task, policyId, outcome,
                submission.Command, submission.ReasonCode, submission.Seed, submission.RemainingWork, submission.Elapsed) {
                    Route = submission.Route, EpisodeId = submission.EpisodeId,
                    RetainedPlan = plan, PlanVersion = planVersion, ProposedPlanTransition = submission.PlanTransition,
                    PlanTransitionApplied = applied });
            submitted = null;
            submittedPlanRevoked = false;
            view = receipt == AiExecutionReceipt.Unknown ? new(frame?.Stamp, AiSeatStatus.NeedsRecovery, "Execution acknowledgement is uncertain.")
                : new(frame?.Stamp, frame?.Stamp == submission.Stamp ? AiSeatStatus.WaitingForObservation : AiSeatStatus.Idle);
            return true;
        }
    }

    private void Record(AiDecisionFrame current, AiDecisionOutcome outcome, DecisionCommand? command, string reason,
        AiPlanTransition? transition = null) => history.Record(
        new(current.Stamp, current.Task, policyId, outcome, command, reason, episodeSeed, work.Remaining, Elapsed) {
            EpisodeId = current.EpisodeId, RetainedPlan = plan, PlanVersion = planVersion, ProposedPlanTransition = transition });

    // Offline replay restores already-retained memory before the first policy
    // evaluation; it receives the same validation as live observed resources.
    internal void RestorePlanForReplay(AiTurnPlan? retained, long version)
    {
        lock (gate) {
            if (closed || frame == null || evaluation != null || submitted != null || planVersion != 0
                || version < 0 || version == long.MaxValue || retained != null && !AiPlanValidation.CanRetain(retained, version, frame))
                throw new ArgumentException("A replay requires valid retained memory for its current seat observation.");
            plan = retained; planVersion = version;
        }
    }

    private Allowance NewAllowance() => new(AiPolicySeeds.Episode(baseSeed, seatId, ++episodeSequence), limits.WorkUnits);
    private TimeSpan Elapsed => (allowance?.Elapsed ?? TimeSpan.Zero)
        + (ReferenceEquals(allowance, evaluatingAllowance) && evaluatingAllowance != null ? time.GetElapsedTime(evaluationStarted) : TimeSpan.Zero);
    private void FinishActiveTime()
    {
        if (evaluatingAllowance == null) return;
        evaluatingAllowance.Elapsed += time.GetElapsedTime(evaluationStarted);
        evaluatingAllowance = null;
    }

    public void Dispose()
    {
        CancellationTokenSource? cancel;
        lock (gate) {
            if (closed) return;
            closed = true; cancel = evaluationCancellation; evaluationCancellation = null;
            FinishActiveTime();
            if (frame != null && view.Status is AiSeatStatus.Thinking or AiSeatStatus.Ready)
                Record(frame, AiDecisionOutcome.Cancelled, ready?.Command, "seat-closed");
            if (submitted is { } handoff)
                history.Record(new(handoff.Stamp, handoff.Task, policyId, AiDecisionOutcome.ExecutionUnknown,
                    handoff.Command, handoff.ReasonCode, handoff.Seed, handoff.RemainingWork, handoff.Elapsed) {
                        EpisodeId = handoff.EpisodeId, RetainedPlan = plan, PlanVersion = planVersion, ProposedPlanTransition = handoff.PlanTransition });
            ready = null; frame = null; submitted = null;
            plan = null;
            executionAllowances.Clear();
            view = new(null, AiSeatStatus.Closed);
        }
        Cancel(cancel);
    }
    private static void Cancel(CancellationTokenSource? cancellation)
    {
        try { cancellation?.Cancel(); } catch (ObjectDisposedException) { } catch (AggregateException) { }
    }
    private static bool ValidCode(string? code) => !string.IsNullOrWhiteSpace(code) && code.Length <= 96
        && code.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
}
