using Mana.Contracts;

namespace Mana.AI;

public enum AiSeatStatus { Idle, Thinking, Ready, Submitted, WaitingForObservation, Unavailable, Invalid, Cancelled, BudgetExceeded, NeedsRecovery, Closed }
public enum AiExecutionReceipt { Accepted, Rejected, Unknown }
public sealed record AiSeatView(AiDecisionStamp? Stamp, AiSeatStatus Status, string Detail = "")
{
    public AiFailure? Failure { get; init; }
}

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
    internal AiFailure FailureEvidence(AiFailureKind kind, string code)
        => new(kind, code, Stamp, Task, EpisodeId, RemainingWork, Elapsed);
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
    private GameSnapshot? lastObservation;
    private bool preserveAllowance;
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
        PriorityAutomation = policy.Descriptor.PriorityAutomation;
        baseSeed = seed; this.limits = limits; this.time = time ?? TimeProvider.System;
        history = new(limits.HistoryCapacity);
    }

    public AiSeatView View { get { lock (gate) return view; } }
    public AiPriorityAutomation PriorityAutomation { get; }
    public AiSeatHistory History { get { lock (gate) return history.Snapshot; } }
    public AiTurnPlan? Plan { get { lock (gate) return plan; } }
    public long PlanVersion { get { lock (gate) return planVersion; } }

    // Continuation observations contribute actual filtered event memory without
    // fabricating policy episodes, commands or accepted-input records.
    public bool ObserveAutomaticProgress(GameSnapshot state)
    {
        // Reject malformed facts before advancing any observation/restoration
        // watermark, including when the seat has no current input.
        ExecutionFrames.Validate(state); PublicActivityFacts.Validate(state);
        if (!ObserveRestoration(state)) return false;
        lock (gate) {
            if (closed) return false;
            history.ObserveTurn(state.Turn); history.Observe(state.Activity); history.ObserveProgress(state);
            return true;
        }
    }

    public bool Observe(AiDecisionFrame? next)
    {
        if (next != null && !ObserveRestoration(next.State)) return false;
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
                history.ObserveProgress(next.State);
                if (episode != next.EpisodeId) {
                    episode = next.EpisodeId;
                    if (preserveAllowance && allowance != null) {
                        // A restored input is fresh authority, not fresh thinking time.
                    } else if (next.ExecutionBudgetRootId.Length > 0) {
                        if (!executionAllowances.TryGetValue(next.ExecutionBudgetRootId, out allowance)) {
                            allowance = NewAllowance(); executionAllowances.Add(next.ExecutionBudgetRootId, allowance);
                        }
                    } else allowance = NewAllowance();
                    episodeSeed = allowance.Seed; work = allowance.Work;
                }
                preserveAllowance = false;
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

    // Called even when this seat has no decision or is waiting for presentation.
    // A restored board must revoke ready/in-flight policy work immediately.
    public bool ObserveRestoration(GameSnapshot state)
    {
        CancellationTokenSource? cancel = null;
        lock (gate) {
            if (closed) return false;
            if (state.Id != gameId || state.ViewerId != seatId) throw new ArgumentException("A restoration observation belongs to this exact game and seat.");
            if (lastObservation is { } previous && (state.Revision < previous.Revision || state.BoardRevision < previous.BoardRevision)) return false;
            UndoFacts.ValidateProgress(lastObservation, state);
            history.ValidateTurn(state.Turn);
            bool restored = UndoFacts.RestoredSince(lastObservation, state);
            bool sameTurn = lastObservation?.Turn == state.Turn;
            if (restored) ExecutionFrames.Validate(state);
            lastObservation = state;
            if (!restored) return true;
            FinishActiveTime();
            if (frame != null && view.Status is AiSeatStatus.Thinking or AiSeatStatus.Ready)
                Record(frame, AiDecisionOutcome.Cancelled, ready?.Command, "action-restored");
            cancel = evaluationCancellation; evaluationCancellation = null;
            ready = null; frame = null; completedStamp = null;
            submittedPlanRevoked = true;
            bool hadPlan = plan != null || planVersion > 0 || submitted != null;
            plan = null; if (hadPlan && planVersion < long.MaxValue) planVersion++;
            preserveAllowance = sameTurn;
            history.Observe(state.Activity); history.Restore(state);
            if (view.Status != AiSeatStatus.NeedsRecovery && submitted == null) view = new(null, AiSeatStatus.Idle);
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
                var failure = CaptureFailure(current, remaining <= TimeSpan.Zero ? AiFailureKind.TimeLimit
                    : work.Remaining == 0 ? AiFailureKind.WorkLimit : AiFailureKind.RouteLimit,
                    remaining <= TimeSpan.Zero ? "time-budget" : work.Remaining == 0 ? "work-budget" : "route-limit");
                Record(current, AiDecisionOutcome.BudgetExceeded, null, "episode-budget", failure: failure);
                completedStamp = current.Stamp;
                return Task.FromResult(view = new(current.Stamp, AiSeatStatus.BudgetExceeded, "Decision budget exhausted.") { Failure = failure });
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
        AiFailureKind failureKind = AiFailureKind.InvalidPolicyOutput;
        string failureCode = "missing-proposal";
        bool policyCancellation = false;
        string detail = "The policy failed.", reason = "policy-failed";
        try {
            // The policy may ignore cancellation; its late result still has no
            // authority. The request carries immutable history, never live memory.
            var token = cancellation.Token;
            token.ThrowIfCancellationRequested();
            var remaining = request.RemainingTime - time.GetElapsedTime(scheduledAt);
            if (remaining <= TimeSpan.Zero) throw new TimeoutException();
            var computation = Task.Run(() => EvaluatePolicyAsync(request, token));
            _ = computation.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            var evaluated = await computation.WaitAsync(remaining, time, token).ConfigureAwait(false);
            proposal = evaluated.Proposal;
            if (evaluated.Failure is { } status) {
                failure = status; failureKind = AiFailureKind.PolicyException; failureCode = evaluated.Code;
                policyCancellation = status == AiSeatStatus.Cancelled && evaluated.PolicyFault;
                (detail, reason) = status switch {
                    AiSeatStatus.BudgetExceeded => ("Decision time exhausted.", "time-budget"),
                    AiSeatStatus.Cancelled => ("Evaluation cancelled.", "cancelled"),
                    _ => ("The policy failed.", "policy-failed")
                };
            }
        } catch (TimeoutException) {
            failure = AiSeatStatus.BudgetExceeded; detail = "Decision time exhausted."; reason = "time-budget";
            failureKind = AiFailureKind.TimeLimit;
            failureCode = "time-budget";
        } catch (OperationCanceledException) {
            failure = AiSeatStatus.Cancelled; detail = "Evaluation cancelled."; reason = "cancelled";
        } catch (Exception) {
            // Retain the category only. A policy exception may name private cards.
            failureKind = AiFailureKind.PolicyException;
            failureCode = "policy-failed";
        }
        finally { Cancel(cancellation); cancellation.Dispose(); }
        lock (gate) {
            if (closed || frame?.Stamp != request.Frame.Stamp || !ReferenceEquals(evaluationCancellation, cancellation))
                return new(request.Frame.Stamp, AiSeatStatus.Cancelled, "Superseded observation.");
            evaluationCancellation = null;
            FinishActiveTime();
            completedStamp = request.Frame.Stamp;
            if (Elapsed >= limits.DecisionTime) {
                proposal = null; failure = AiSeatStatus.BudgetExceeded; detail = "Decision time exhausted."; reason = "time-budget";
                failureKind = AiFailureKind.TimeLimit;
                failureCode = "time-budget";
            }
            if (proposal is AiProposal.Unavailable unavailable) {
                if (!AiFailure.ValidCode(unavailable.Capability)) {
                    var cause = CaptureFailure(frame, AiFailureKind.InvalidPolicyOutput, "invalid-capability");
                    Record(frame, AiDecisionOutcome.Invalid, null, "invalid-capability", failure: cause);
                    return view = new(frame.Stamp, AiSeatStatus.Invalid, "Missing-capability results require a stable identifier.") { Failure = cause };
                }
                bool limited = unavailable.LimitedWork is { WasExceeded: true } budget && budget.BelongsTo(work);
                var unavailableCause = CaptureFailure(frame, limited ? AiFailureKind.WorkLimit : AiFailureKind.CapabilityUnavailable,
                    limited && !ReferenceEquals(unavailable.LimitedWork, work) ? "local-work-budget" : unavailable.Capability);
                Record(frame, AiDecisionOutcome.Unavailable, null, "missing-capability", failure: unavailableCause);
                return view = new(frame.Stamp, AiSeatStatus.Unavailable, unavailable.Capability) { Failure = unavailableCause };
            }
            if (proposal is AiProposal.Answer answer) {
                if (!AiFailure.ValidCode(answer.ReasonCode) || answer.Command == null || !frame.Allows(answer.Command)) {
                    var cause = CaptureFailure(frame, AiFailureKind.InvalidPolicyOutput, "unoffered-answer");
                    Record(frame, AiDecisionOutcome.Invalid, null, "unoffered-answer", failure: cause);
                    return view = new(frame.Stamp, AiSeatStatus.Invalid, "The answer is outside the current offers or constraints.") { Failure = cause };
                }
                if (!AiPlanValidation.IsTransitionValid(answer.PlanTransition, planVersion, frame, plan)) {
                    var cause = CaptureFailure(frame, AiFailureKind.InvalidPolicyOutput, "invalid-plan-transition");
                    Record(frame, AiDecisionOutcome.Invalid, null, "invalid-plan-transition", failure: cause);
                    return view = new(frame.Stamp, AiSeatStatus.Invalid, "The proposed plan is stale or references unavailable resources.") { Failure = cause };
                }
                ready = answer;
                Record(frame, AiDecisionOutcome.Offered, answer.Command, answer.ReasonCode, answer.PlanTransition);
                return view = new(frame.Stamp, AiSeatStatus.Ready);
            }
            var failureCause = failure == AiSeatStatus.Cancelled && !policyCancellation ? null : CaptureFailure(frame, failureKind, failureCode);
            Record(frame, failure switch { AiSeatStatus.BudgetExceeded => AiDecisionOutcome.BudgetExceeded,
                AiSeatStatus.Cancelled => AiDecisionOutcome.Cancelled, _ => AiDecisionOutcome.Invalid }, null, reason, failure: failureCause);
            return view = new(frame.Stamp, failure, detail) { Failure = failureCause };
        }
    }

    // Distinguish an exception produced by the policy from our own WaitAsync
    // deadline. Keep the legacy coarse status/reason and discard private text.
    private readonly record struct PolicyEvaluation(AiProposal? Proposal, AiSeatStatus? Failure = null,
        string Code = "", bool PolicyFault = false);
    private async Task<PolicyEvaluation> EvaluatePolicyAsync(AiDecisionRequest request, CancellationToken token)
    {
        try { return new(await policy.EvaluateAsync(request, token).ConfigureAwait(false)); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new(null, AiSeatStatus.Cancelled, "cancelled"); }
        catch (OperationCanceledException) { return new(null, AiSeatStatus.Cancelled, "policy-cancelled", true); }
        catch (TimeoutException) { return new(null, AiSeatStatus.BudgetExceeded, "policy-timeout", true); }
        catch (Exception) { return new(null, AiSeatStatus.Invalid, "policy-failed", true); }
    }

    public AiSubmission? TakeCommand()
    {
        lock (gate) {
            if (closed || view.Status != AiSeatStatus.Ready || frame == null || ready == null || submitted != null) return null;
            if (Elapsed >= limits.DecisionTime || !frame.Allows(ready.Command)) {
                bool timedOut = Elapsed >= limits.DecisionTime;
                var failure = CaptureFailure(frame, timedOut ? AiFailureKind.TimeLimit : AiFailureKind.InvalidPolicyOutput,
                    timedOut ? "handoff-time-budget" : "handoff-authority");
                Record(frame, AiDecisionOutcome.BudgetExceeded, ready.Command, "handoff-budget", failure: failure);
                ready = null; view = new(frame.Stamp, AiSeatStatus.BudgetExceeded) { Failure = failure }; return null;
            }
            AiTurnRoute? route = null;
            if (ready.Command is UseOfferedAction offered) {
                var source = frame.SourceActions.First(set => set.SourceCardKey == offered.Card);
                var semantic = frame.Domain.Actions.FirstOrDefault(action => action.ActionId == offered.ActionId);
                if (semantic is { StableId.Length: > 0 }) {
                    route = new(frame.State.Turn, source.SourceCardId, semantic.StableId);
                    history.BeginRoute(route, frame, semantic);
                }
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
            var failure = receipt == AiExecutionReceipt.Unknown
                ? submission.FailureEvidence(AiFailureKind.ExecutionUncertain, "execution-unknown") : null;
            history.Receipt(submission.Route, receipt);
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
                    PlanTransitionApplied = applied, Failure = failure });
            submitted = null;
            submittedPlanRevoked = false;
            view = receipt == AiExecutionReceipt.Unknown ? new(frame?.Stamp, AiSeatStatus.NeedsRecovery, "Execution acknowledgement is uncertain.") { Failure = failure }
                : new(frame?.Stamp, frame?.Stamp == submission.Stamp ? AiSeatStatus.WaitingForObservation : AiSeatStatus.Idle);
            return true;
        }
    }

    private AiFailure CaptureFailure(AiDecisionFrame current, AiFailureKind kind, string code)
        => new(kind, code, current.Stamp, current.Task, current.EpisodeId, work.Remaining, Elapsed);

    private void Record(AiDecisionFrame current, AiDecisionOutcome outcome, DecisionCommand? command, string reason,
        AiPlanTransition? transition = null, AiFailure? failure = null) => history.Record(
        new(current.Stamp, current.Task, policyId, outcome, command, reason, episodeSeed, work.Remaining, Elapsed) {
            EpisodeId = current.EpisodeId, RetainedPlan = plan, PlanVersion = planVersion, ProposedPlanTransition = transition, Failure = failure });

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
                        Route = handoff.Route, EpisodeId = handoff.EpisodeId, RetainedPlan = plan, PlanVersion = planVersion, ProposedPlanTransition = handoff.PlanTransition,
                        Failure = handoff.FailureEvidence(AiFailureKind.ExecutionUncertain, "seat-closed-with-submission") });
            if (submitted != null) history.Receipt(submitted.Route, AiExecutionReceipt.Unknown);
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
}
