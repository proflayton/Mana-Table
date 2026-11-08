using System.Collections.Immutable;
using Mana.AI;
using Mana.Client;
using Mana.Contracts;

namespace Mana.Table;

// Controlled ports exercise the actual connected-player owner. They do not
// simulate rules or turn a rich read into an ordinary accepted publication.
internal static class PolledFullGameContractChecks
{
    internal static async Task RunAsync(string profile, Action<bool, string> check)
    {
        await PolledInputReceiptChecks.RunAsync(check);
        MixedGamePublicCohortChecks.Run(check);
        FanoutProjectionCaptureChecks.Run(check);
        FanoutProjectionArchiveChecks.Run(profile, check);
        await OrdinaryOwner(profile, check);
        await RetiredRichReads(profile, check);
        await HeldRichRead(profile, check);
        await RejectedCommandWaitsForOrdinary(profile, check);
        await SourceActionUsesOrdinaryOwner(profile, check);
        await ServerOwnedRoutinePriority(profile, check);
        await FullControlRoutinePriority(profile, check);
        await ServerHeldActionablePriority(profile, check);
        await RetiredQueuedServerAction(profile, check);
        await ServerAutoQuiet(profile, check);
        await ServerAutoResponses(profile, check);
        await ServerAutoStops(profile, check);
        await ServerAutoBatchedAttention(profile, check);
        await PolledEvidenceInboxChecks.RunAsync(profile, check);
        await CampaignOwnerWaitChecks.RunAsync(profile, check);
        await PollScheduling(profile, check);
        await DuePollCannotStarveAuto(profile, check);
        await SlowPollCannotStarveDecision(profile, check);
    }

    private static async Task PollScheduling(string profile, Action<bool, string> check)
    {
        var state = Priority();
        using var run = new DriverCase(profile, "poll-scheduling", state, state);
        check(run.Driver.TimeUntilNextPoll == TimeSpan.Zero && run.Port.Reads == 0,
            "Reading the next poll deadline does not create a poll or change its schedule");
        await run.Prime();
        check(run.Driver.TimeUntilNextPoll == TimeSpan.FromMilliseconds(50),
            "A no-state bootstrap poll retains its normal 50 ms poll interval");
        run.Ticks = 49;
        await run.Driver.ObserveAsync();
        check(run.Driver.TimeUntilNextPoll == TimeSpan.FromMilliseconds(1) && run.Port.Reads == 1,
            "The scheduler exposes the remaining poll delay without admitting an early ordinary read");
        run.Ticks = 50;
        run.Port.HoldNextRead = true;
        await run.Driver.ObserveAsync();
        await run.Port.NextReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        check(run.Driver.TimeUntilNextPoll == null && run.Driver.Client.Polling,
            "A pending ordinary poll has no timer due until its actual queued completion arrives");
        run.Port.ReleaseNextRead.TrySetResult(state);
        await Drain(run.Driver.Client);
        check(run.Driver.TimeUntilNextPoll == TimeSpan.FromMilliseconds(120),
            "After its ordinary completion a decision-bearing client preserves the existing 120 ms cadence");
        run.Driver.Dispose();
        check(run.Driver.TimeUntilNextPoll == null, "A closed player exposes no future poll deadline");
    }

    private static async Task SlowPollCannotStarveDecision(string profile, Action<bool, string> check)
    {
        var first = Opening();
        using var run = new DriverCase(profile, "slow-poll-decision-progress", first, first,
            passing: ConnectedPlayerPassingMode.FullControl);
        await run.Prime();
        await run.Driver.ObserveAsync();
        run.Ticks = 50;
        run.Port.HoldNextRead = true;
        await run.Driver.ObserveAsync();
        await run.Port.NextReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var current = first with { Revision = 11, BoardRevision = 11,
            Decision = first.Decision! with { Id = "decision-after-slow-poll" } };
        run.Port.State = run.Port.RichState = current;
        // The poll took longer than its existing 120 ms cadence. A ready
        // accepted decision gets the same precedence as native local input.
        run.Ticks = 250;
        run.Port.ReleaseNextRead.TrySetResult(current);
        await Drain(run.Driver.Client);
        run.Port.NextReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Port.ReleaseNextRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Port.HoldNextRead = true;
        run.Port.AfterExecute = current with { Revision = 12, BoardRevision = 12, Decision = null };
        try {
            await run.Driver.ObserveAsync();
            check(run.Port.Reads == 2 && run.Port.OwnedReads == 2 && !run.Driver.Client.Polling,
                "A poll completing after its cadence prepares the accepted current decision before starting another ordinary poll");
            run.Port.HoldNextRead = false;
            await run.Driver.StepAsync();
            check(run.Port.Reads == 3 && run.Port.OwnedReads == 2 && run.Policy.Evaluations == 1
                    && run.Port.Commands is [KeepOpeningHand { DecisionId: "decision-after-slow-poll" }]
                    && run.Driver.ClientAdmissions == 1 && run.Driver.ProviderAcceptances == 1
                    && !run.Driver.Client.Polling,
                "A poll completing after its cadence cannot start another poll ahead of the accepted current decision and starve ordinary policy progress");
        } finally {
            run.Port.ReleaseNextRead.TrySetResult(run.Port.State);
            await Drain(run.Driver.Client);
        }
    }

    private static async Task DuePollCannotStarveAuto(string profile, Action<bool, string> check)
    {
        var initial = AutoPriority();
        using var run = new DriverCase(profile, "due-poll-auto-progress", initial, initial,
            passing: ConnectedPlayerPassingMode.ServerAuto);
        NativeAutoPort(run);
        await run.Prime();
        // The accepted frame arrived after the previous observation's cadence.
        // Hold whichever read starts next to distinguish an ordinary poll from
        // the exact Quiet grant's normal command readback without timing races.
        run.Ticks = 250;
        run.Port.HoldNextRead = true;
        string original = ((PriorityPayload)initial.Decision!.Payload).QuietYieldToken;
        try {
            await run.Driver.ObserveAsync();
            await run.Port.NextReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check(run.Port.Commands is [GrantPriorityContinuation { Mode: PriorityContinuationMode.Quiet } grant]
                    && grant.QuietYieldToken == original && run.Driver.Client.Busy && !run.Driver.Client.Polling
                    && run.Port.OwnedReads == 0 && run.Policy.Evaluations == 0,
                "An accepted automatic Quiet window dispatches its original-token consent before another due ordinary poll, without a per-prompt policy fallback");
        } finally {
            run.Port.ReleaseNextRead.TrySetResult(run.Port.State);
            await Drain(run.Driver.Client);
        }
        check(run.Driver.AutoEvidence is { AcceptedGrants: 1, QuietAcceptedGrants: 1, QuietConsentWindows: 1,
                AcceptedPolicyPriorityPasses: 0 } && run.Driver.ClientAdmissions == 0 && run.Driver.ProviderAcceptances == 0
                && run.Driver.ConsentEvidence.Receipts.Single().Command.QuietYieldToken == original,
            "The delayed-poll Auto gesture retains one accepted original-token grant and no invented player-input receipt");
    }

    private static async Task OrdinaryOwner(string profile, Action<bool, string> check)
    {
        var state = Priority();
        var extra = ExtraSource(state, "rich-only");
        var rich = state with { ActionSources = [extra] };
        using var run = new DriverCase(profile, "ordinary-owner", state, rich);
        await run.Prime();
        check(run.Driver.Client.ObservationDelivery == ObservationDeliveryMode.Polled
                && ReferenceEquals(run.Driver.Client.State, state) && run.Port.Reads == 1
                && run.Port.OwnedReads == 0 && run.Driver.OrdinaryPublications == 1,
            "The full-game connected player admits its initial state only through default Polled lobby/match ingress");
        await run.Driver.ObserveAsync();
        check(ReferenceEquals(run.Driver.Last?.State, state) && ReferenceEquals(run.Driver.Client.State, state)
                && run.Driver.Last is { State.ActionSources.IsEmpty: true } && run.Port.OwnedReads == 1
                && run.Port.Reads == 1 && run.Port.Commands.Count == 0,
            "A matching rich read may add readonly source knowledge without replacing the ordinary state, reading the rules port again or dispatching");
        run.Port.AfterExecute = state with { Revision = 11, BoardRevision = 11, Decision = null };
        await run.Driver.StepAsync();
        check(run.Policy.Evaluations == 1 && ReferenceEquals(run.Policy.PhysicalState, state)
                && run.Policy.PhysicalState is { ActionSources.IsEmpty: true }
                && run.Port.Commands is [PassPriority passed] && passed.GameId == state.Id && passed.DecisionId == state.Decision!.Id
                && run.Driver.ClientAdmissions == 1 && run.Driver.ProviderAcceptances == 1
                && run.Driver.OrdinaryPublications == 2 && run.Port.Reads == 2 && run.Port.OwnedReads == 1
                && run.Port.SourceReads == 0 && run.Port.HistoryReads == 0,
            "The scripted policy sees the ordinary physical payload and submits once through the normal client/receipt/readback lane with no second reader");
    }

    private static async Task RetiredRichReads(string profile, Action<bool, string> check)
    {
        var basic = Opening();
        var priority = Priority();
        var withSource = priority with { ActionSources = [ExtraSource(priority, "ordinary-source")] };
        foreach (string change in new[] { "newer", "older", "game", "audience", "private-face", "private-key", "source-key", "terminal" }) {
            var ordinary = change == "source-key" ? withSource : basic;
            var rich = change switch {
                "newer" => ordinary with { Revision = ordinary.Revision + 1 },
                "older" => ordinary with { Revision = ordinary.Revision - 1 },
                "game" => ordinary with { Id = "foreign-game" },
                "audience" => ordinary with { ViewerId = 1 },
                "private-face" => ChangeOwnHand(ordinary, card => card with { Name = "Unaccepted private face" }),
                "private-key" => ChangeOwnHand(ordinary, card => card with { Key = "unaccepted-private-key" }),
                "source-key" => ordinary with { ActionSources = [ordinary.ActionSources[0] with {
                    Card = ordinary.ActionSources[0].Card with { Key = "unaccepted-source-key" } }] },
                _ => ordinary with { Status = "finished", Decision = null,
                    Outcome = new(ordinary.Id, MatchOutcomeKind.Draw, [], [], "AllPlayersDrew") }
            };
            using var run = new DriverCase(profile, "retired-" + change, ordinary, rich);
            await run.Prime();
            await run.Driver.ObserveAsync();
            await run.Driver.StepAsync();
            check(ReferenceEquals(run.Driver.Client.State, ordinary) && ReferenceEquals(run.Driver.Last?.State, ordinary)
                    && run.Driver.Client.State is { Status: "playing" } && run.Driver.OrdinaryPublications == 1
                    && run.Driver.RetiredProposals == 1 && run.Policy.Evaluations == 0
                    && run.Port.Reads == 1 && run.Port.OwnedReads == 1 && run.Port.Commands.Count == 0,
                "A rich " + change + " mismatch grants no command, accepted state, private face or artificial terminal outcome");
            await run.Driver.ObserveAsync();
            check(run.Port.OwnedReads == 1 && run.Port.Reads == 1 && run.Port.Commands.Count == 0,
                "Retiring the rich " + change + " proposal cannot silently retry its unchanged ordinary context");
        }
    }

    private static async Task HeldRichRead(string profile, Action<bool, string> check)
    {
        var first = Opening();
        using var run = new DriverCase(profile, "held-rich", first, first);
        await run.Prime();
        run.Port.HoldOwned = true;
        var pending = run.Driver.ObserveAsync();
        await run.Port.OwnedStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var newer = first with { Revision = 11, BoardRevision = 11,
            Decision = first.Decision! with { Id = "new-ordinary-decision" } };
        run.Port.State = newer;
        run.Driver.Client.Poll(true, _ => { });
        await Drain(run.Driver.Client);
        check(ReferenceEquals(run.Driver.Client.State, newer) && run.Port.Reads == 2 && run.Port.Commands.Count == 0,
            "A held rich read does not block the same player's actual ordinary poll from admitting a newer current decision");
        run.Port.ReleaseOwned.TrySetResult(new(first, DecisionActionDomain.None, new()));
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        await run.Driver.StepAsync();
        check(ReferenceEquals(run.Driver.Client.State, newer) && ReferenceEquals(run.Driver.Last?.State, newer)
                && run.Driver.RetiredProposals == 1 && run.Policy.Evaluations == 0 && run.Port.Commands.Count == 0,
            "A late old rich completion cannot revive its decision or authorize work after a newer ordinary publication");
    }

    private static async Task RejectedCommandWaitsForOrdinary(string profile, Action<bool, string> check)
    {
        var first = Opening();
        using var run = new DriverCase(profile, "rejected-command", first, first);
        await run.Prime();
        await run.Driver.ObserveAsync();
        run.Port.ExecuteError = new EngineCommandRejectedException("Controlled definite dispatch rejection");
        await run.Driver.StepAsync();
        check(run.Port.Commands.Count == 1 && run.Driver.ProviderAcceptances == 0 && !run.Driver.Client.NeedsRecovery,
            "A definite rejected full-game command records no provider acceptance and retires its current proposal");
        run.Port.HoldNextRead = true;
        run.Driver.Client.Poll(true, _ => { });
        await run.Port.NextReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await run.Driver.ObserveAsync();
        await run.Driver.StepAsync();
        check(run.Port.Commands.Count == 1 && run.Port.OwnedReads == 1 && run.Policy.Evaluations == 1,
            "A held ordinary poll after rejection cannot replay the old proposal or mistake its old stamp for a fresh failure");
        var fresh = first with { Revision = 11, BoardRevision = 11,
            Decision = first.Decision! with { Id = "fresh-after-rejection" } };
        run.Port.State = run.Port.RichState = fresh;
        run.Port.ReleaseNextRead.TrySetResult(fresh);
        await Drain(run.Driver.Client);
        run.Port.ExecuteError = null;
        run.Port.AfterExecute = fresh with { Revision = 12, BoardRevision = 12, Decision = null };
        await run.Driver.ObserveAsync();
        await run.Driver.StepAsync();
        check(run.Port.Commands.Count == 2 && run.Port.Commands[0] is KeepOpeningHand { DecisionId: "opening-owner" }
                && run.Port.Commands[1] is KeepOpeningHand { DecisionId: "fresh-after-rejection" }
                && run.Driver.ProviderAcceptances == 1 && run.Policy.Evaluations == 2,
            "Only a freshly polled ordinary decision and matching rich read permit one new command after definite rejection");
    }

    private static async Task SourceActionUsesOrdinaryOwner(string profile, Action<bool, string> check)
    {
        var state = Priority();
        using (var run = new DriverCase(profile, "source-current", state, state, useSource: true)) {
            await run.Prime(); await run.Driver.ObserveAsync();
            run.Port.AfterExecute = state with { Revision = 11, BoardRevision = 11, Decision = null };
            await run.Driver.StepAsync();
            var card = state.Viewer!.Zone("Hand").Cards[0];
            check(run.Port.SourceReads == 1 && run.Port.Commands.Single() == new UseOfferedAction(
                    state.Id, state.Decision!.Id, card.Key, "controlled-play")
                    && run.Driver.ClientAdmissions == 1 && run.Driver.ProviderAcceptances == 1 && run.Port.Reads == 2,
                "A rich offered route still requires the ordinary current SourceActionSession query and normal Submit/ACK/readback before it counts as accepted");
        }
        using (var run = new DriverCase(profile, "source-retired", state, state, useSource: true)) {
            await run.Prime(); await run.Driver.ObserveAsync();
            run.Port.HoldSource = true;
            var pending = run.Driver.StepAsync();
            await run.Port.SourceStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var newer = state with { Revision = 11, BoardRevision = 11,
                Decision = state.Decision! with { Id = "new-source-owner" } };
            var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            run.Driver.Client.Changed += (_, current) => { if (ReferenceEquals(current, newer)) accepted.TrySetResult(); };
            run.Port.State = newer;
            // The driver's existing pump remains the sole model owner while it
            // awaits the source. Schedule the poll on that same owner queue.
            run.Driver.Client.Post(() => run.Driver.Client.Poll(true, _ => { }));
            await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            run.Port.ReleaseSource.TrySetResult(Offers(state));
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            check(ReferenceEquals(run.Driver.Client.State, newer) && run.Driver.RetiredProposals == 1
                    && run.Port.SourceReads is >= 1 and <= 2 && run.Port.Commands.Count == 0
                    && run.Driver.ClientAdmissions == 0 && run.Driver.ProviderAcceptances == 0,
                "A held source-query completion cannot dispatch its old rich proposal after a new ordinary owner is admitted through the same client pump");
        }
    }

    private static SourceActionSet Offers(GameSnapshot state)
    {
        var card = state.Viewer!.Zone("Hand").Cards[0];
        return new(state.Id, state.Decision!.Id, card.Key, CardViews.Identity(card),
            [new("controlled-play", "play", "Play this land")]) { Revision = state.Revision };
    }

    private static async Task ServerOwnedRoutinePriority(string profile, Action<bool, string> check)
    {
        var initial = ServerPriority(PriorityResponseAvailability.Absent);
        using var run = new DriverCase(profile, "server-routine-owner", initial, initial);
        run.Port.PriorityContinuationAvailable = true;
        run.Port.AfterCommand = command => command is GrantPriorityContinuation grant
            ? initial with { Revision = 11, PriorityContinuation = Granted(grant, PriorityContinuationStatus.Active) } : null;
        await run.Prime(); await run.Driver.ObserveAsync(); await Drain(run.Driver.Client);
        await run.Driver.ObserveAsync(); await run.Driver.StepAsync();
        var advanced = run.Port.State with { Revision = 12,
            PriorityContinuation = run.Port.State.PriorityContinuation! with { AutomaticPasses = 17 } };
        run.Port.State = advanced;
        run.Driver.Client.Poll(true, _ => { }); await Drain(run.Driver.Client);
        await run.Driver.ObserveAsync(); await run.Driver.StepAsync();
        check(run.Driver.Client.SupportsPriorityContinuation && ReferenceEquals(run.Driver.Client.State, advanced)
                && run.Port.Commands is [GrantPriorityContinuation { Mode: PriorityContinuationMode.NoResponse, PhaseStops.IsEmpty: true }]
                && run.Port.OwnedReads == 0 && run.Policy.Evaluations == 0
                && run.Driver.ClientAdmissions == 0 && run.Driver.ProviderAcceptances == 0
                && run.Driver.AutoEvidence is { AcceptedGrants: 1, CertifiedAutomaticPasses: 17, AcceptedPolicyPriorityPasses: 0 },
            "An ordinary polled owner grants server Auto once, observes its certified progress and never queries/evaluates/dispatches a per-prompt fallback for routine priority");
    }

    private static async Task FullControlRoutinePriority(string profile, Action<bool, string> check)
    {
        var initial = ServerPriority(PriorityResponseAvailability.Absent);
        using var run = new DriverCase(profile, "full-control-routine", initial, initial,
            passing: ConnectedPlayerPassingMode.FullControl);
        run.Port.PriorityContinuationAvailable = true;
        run.Port.AfterExecute = initial with { Revision = 11, BoardRevision = 11, Decision = null };
        await run.Prime(); await run.Driver.ObserveAsync(); await run.Driver.StepAsync();
        check(run.Port.Commands is [PassPriority] && run.Driver.ProviderAcceptances == 1
                && run.Driver.AutoEvidence is { AcceptedGrants: 0, CertifiedAutomaticPasses: 0, AcceptedPolicyPriorityPasses: 1 },
            "An explicitly FullControl ordinary test player passes its own current prompt without installing standing Auto or claiming its receipts");
    }

    private static async Task ServerHeldActionablePriority(string profile, Action<bool, string> check)
    {
        foreach (var availability in new[] { PriorityResponseAvailability.Available, PriorityResponseAvailability.Unknown }) {
            var initial = ServerPriority(availability);
            using var run = new DriverCase(profile, "server-held-" + availability, initial, initial);
            run.Port.PriorityContinuationAvailable = true;
            run.Port.AfterCommand = command => command switch {
                GrantPriorityContinuation grant => initial with { Revision = 11, PriorityContinuation = Granted(grant, PriorityContinuationStatus.Held) },
                RevokePriorityContinuation => run.Port.State with { Revision = 12,
                    PriorityContinuation = run.Port.State.PriorityContinuation! with { Status = PriorityContinuationStatus.Revoked, ReasonCode = "withdrawn" } },
                PassPriority => run.Port.State with { Revision = 13, BoardRevision = 11, Decision = null },
                _ => null
            };
            await run.Prime(); await run.Driver.ObserveAsync(); await Drain(run.Driver.Client);
            run.Port.RichState = run.Port.State;
            await run.Driver.ObserveAsync(); await run.Driver.StepAsync();
            check(run.Port.Commands is [GrantPriorityContinuation, PassPriority]
                    && run.Policy.Evaluations == 1 && run.Port.OwnedReads == 1
                    && run.Driver.ClientAdmissions == 1 && run.Driver.ProviderAcceptances == 1
                    && run.Driver.AutoEvidence is { AcceptedGrants: 1, AcceptedWithdrawals: 0, AcceptedPolicyPriorityPasses: 1 }
                    && run.Driver.Client.PriorityContinuationReserved
                    && !run.Driver.Client.NeedsRecovery && !run.Driver.Client.UncertainCommand,
                "A server-held " + availability + " window preserves standing Auto while counting only the ordinary policy action's actual command receipt");
        }
    }

    private static async Task RetiredQueuedServerAction(string profile, Action<bool, string> check)
    {
        var initial = ServerPriority(PriorityResponseAvailability.Available);
        using var run = new DriverCase(profile, "server-retired-queued-action", initial, initial);
        run.Port.PriorityContinuationAvailable = true;
        run.Port.AfterCommand = command => command switch {
            GrantPriorityContinuation grant => initial with { Revision = 11, PriorityContinuation = Granted(grant, PriorityContinuationStatus.Held) },
            RevokePriorityContinuation => run.Port.State with { Revision = 13,
                Decision = run.Port.State.Decision! with { Id = "new-prompt-after-negative-consent" },
                PriorityContinuation = run.Port.State.PriorityContinuation! with { Status = PriorityContinuationStatus.Revoked, ReasonCode = "withdrawn" } },
            _ => throw new InvalidDataException("A retired queued action must never reach the controlled provider.")
        };
        await run.Prime(); await run.Driver.ObserveAsync(); await Drain(run.Driver.Client);
        var payment = run.Port.State with { Revision = 12, BoardRevision = 11,
            Decision = new("manual-mana-prompt", new DecisionContext(Message: "Pay mana."), new PaymentPayload(new("Pay", true), new("Cancel", true))),
            Players = run.Port.State.Players.Select(player => player.Id == initial.ViewerId
                ? player with { Mana = ImmutableDictionary<string, int>.Empty.Add("U", 1) } : player).ToImmutableArray() };
        run.Port.State = payment; run.Port.RichState = payment;
        run.Driver.Client.Poll(true, _ => { }); await Drain(run.Driver.Client);
        await run.Driver.ObserveAsync(); await run.Driver.StepAsync();
        check(run.Port.Commands is [GrantPriorityContinuation, RevokePriorityContinuation]
                && run.Driver.ClientAdmissions == 1 && run.Driver.ProviderAcceptances == 0
                && run.Driver.RetiredProposals == 1 && run.Driver.AutoEvidence.AcceptedWithdrawals == 1
                && !run.Driver.Client.NeedsRecovery && !run.Driver.Client.UncertainCommand,
            "A negative consent receipt cannot be credited as manual mana when its queued payment prompt retires before dispatch");
    }

    private static GameSnapshot ServerPriority(PriorityResponseAvailability availability) => Priority() with {
        PriorityContinuation = new(0, "", null, PriorityContinuationStatus.Idle, "idle", 0, []),
        Decision = Priority().Decision! with { Payload = ((PriorityPayload)Priority().Decision!.Payload) with {
            CanAutoPass = availability == PriorityResponseAvailability.Absent, ResponseAvailability = availability
        } }
    };

    private static GameSnapshot AutoPriority() => PriorityAcknowledgmentChecks.Quiet() with {
        PriorityContinuation = new(0, "", null, PriorityContinuationStatus.Idle, "idle", 0, []),
        Decision = PriorityAcknowledgmentChecks.Quiet().Decision! with { Payload =
            ((PriorityPayload)PriorityAcknowledgmentChecks.Quiet().Decision!.Payload) with {
                CanAutoPass = false, ResponseAvailability = PriorityResponseAvailability.Available } }
    };

    private static void NativeAutoPort(DriverCase run, Func<GameSnapshot, GameSnapshot>? passed = null)
    {
        run.Port.PriorityContinuationAvailable = true;
        run.Port.AfterCommand = command => {
            var state = run.Port.State;
            var next = command switch {
                GrantPriorityContinuation grant => state with { Revision = state.Revision + 1,
                    PriorityContinuation = Granted(grant, !(state.ActivePlayerId == state.ViewerId && grant.PhaseStops.Contains(state.PhaseKey))
                        && (grant.Mode == PriorityContinuationMode.Quiet
                        || ((PriorityPayload)state.Decision!.Payload).ResponseAvailability == PriorityResponseAvailability.Absent)
                        ? PriorityContinuationStatus.Active : PriorityContinuationStatus.Held) },
                RevokePriorityContinuation revoke => state with { Revision = state.Revision + 1,
                    PriorityContinuation = state.PriorityContinuation! with { Sequence = revoke.ThroughSequence,
                        Status = PriorityContinuationStatus.Revoked, ReasonCode = "withdrawn" } },
                PassPriority => passed?.Invoke(state) ?? state with { Revision = state.Revision + 1, Decision = null },
                _ => throw new InvalidDataException("An Auto control fixture cannot invent another command.")
            };
            run.Port.RichState = next;
            return next;
        };
    }

    private static async Task PublishAuto(DriverCase run, GameSnapshot state)
    {
        run.Port.State = state; run.Port.RichState = state;
        run.Driver.Client.Poll(true, _ => { }); await Drain(run.Driver.Client);
        await run.Driver.ObserveAsync(); await Drain(run.Driver.Client);
    }

    private static async Task ServerAutoQuiet(string profile, Action<bool, string> check)
    {
        var initial = AutoPriority();
        using var run = new DriverCase(profile, "server-auto-quiet", initial, initial,
            passing: ConnectedPlayerPassingMode.ServerAuto);
        NativeAutoPort(run);
        await run.Prime(); await run.Driver.ObserveAsync(); await Drain(run.Driver.Client);
        await run.Driver.ObserveAsync(); await run.Driver.StepAsync();
        var original = ((PriorityPayload)initial.Decision!.Payload).QuietYieldToken;
        check(run.Port.Commands is [GrantPriorityContinuation { Mode: PriorityContinuationMode.Quiet } grant]
                && grant.QuietYieldToken == original && run.Port.OwnedReads == 0 && run.Policy.Evaluations == 0
                && run.Driver.ClientAdmissions == 0 && run.Driver.ProviderAcceptances == 0,
            "Shipping ServerAuto uses the shared attention predicate to consent once to an unchanged opponent window with a playable instant, without policy pass spam or input credit");
        var active = run.Port.State;
        await PublishAuto(run, active with { Revision = active.Revision + 1, Status = "resolving", Decision = null,
            DecisionOwnerId = 1, PriorityPlayerId = 1, ActionClass = "priority" });
        await run.Driver.StepAsync();
        check(run.Port.Commands.Count == 1 && run.Policy.Evaluations == 0 && run.Driver.Client.PriorityContinuationReserved,
            "A foreign priority publication retains the original Quiet grant without answering another seat or creating a second runner");
        await PublishAuto(run, active with { Revision = run.Port.State.Revision + 1,
            PriorityContinuation = active.PriorityContinuation! with { AutomaticPasses = 23 } });
        await run.Driver.StepAsync();
        check(run.Driver.AutoEvidence is { AcceptedGrants: 1, NoResponseAcceptedGrants: 0, QuietAcceptedGrants: 1,
                CertifiedAutomaticPasses: 23, NoResponseCertifiedAutomaticPasses: 0, QuietCertifiedAutomaticPasses: 23, QuietConsentWindows: 1 }
                && run.Driver.ConsentEvidence is { TotalGrants: 1, RetiredPrefix: 0, Receipts.Length: 1 }
                && run.Driver.ConsentEvidence.Receipts[0].Command.QuietYieldToken == original
                && run.Driver.ConsentEvidence.Receipts[0].CertifiedAutomaticPasses == 23,
            "Per-mode Auto evidence credits only an accepted grant's ordinary certified pass high-water and retains its exact original-token private receipt");
        var changed = run.Port.State with { Revision = run.Port.State.Revision + 1,
            Decision = run.Port.State.Decision! with { Payload = ((PriorityPayload)run.Port.State.Decision.Payload) with {
                QuietYieldToken = "unrelated-replacement-token" } } };
        await PublishAuto(run, changed);
        check(run.Driver.AutoEvidence.QuietAcceptedGrants == 1 && run.Port.Commands.OfType<GrantPriorityContinuation>().All(command =>
                command.Mode != PriorityContinuationMode.Quiet || command.QuietYieldToken == original)
                && run.Port.State.PriorityContinuation?.Mode == PriorityContinuationMode.NoResponse,
            "A changed permission token ends the original Quiet window and requires attention instead of silently granting a replacement token");
    }

    private static async Task ServerAutoResponses(string profile, Action<bool, string> check)
    {
        foreach (bool newAction in new[] { false, true }) {
            var initial = AutoPriority();
            using var run = new DriverCase(profile, "server-auto-response-" + newAction, initial, initial,
                passing: ConnectedPlayerPassingMode.ServerAuto);
            NativeAutoPort(run, state => newAction
                ? PriorityAcknowledgmentChecks.Stack(PriorityAcknowledgmentChecks.Resolve(state), "unacknowledged-next-action")
                : PriorityAcknowledgmentChecks.Resolve(state));
            await run.Prime(); await run.Driver.ObserveAsync(); await Drain(run.Driver.Client);
            var stacked = PriorityAcknowledgmentChecks.Stack(run.Port.State);
            await PublishAuto(run, stacked);
            await run.Driver.ObserveAsync();
            check(run.Port.Commands.OfType<GrantPriorityContinuation>().Count(command => command.Mode == PriorityContinuationMode.Quiet) == 1
                    && run.Port.State.Stack.Length == 1 && run.Port.State.PriorityContinuation?.Mode == PriorityContinuationMode.NoResponse
                    && run.Port.OwnedReads == 1 && run.Policy.Evaluations == 0,
                "An announced stack action interrupts Quiet and remains an actionable ordinary response until the actual manual policy answers it");
            await run.Driver.StepAsync();
            await run.Driver.ObserveAsync(); await Drain(run.Driver.Client);
            await run.Driver.ObserveAsync();
            check(run.Driver.ClientAdmissions == 1 && run.Driver.ProviderAcceptances == 1
                    && run.Driver.AutoEvidence.AcceptedPolicyPriorityPasses == 1 && run.Policy.Evaluations == 1,
                "The exact admitted manual response is counted separately from every NoResponse/Quiet grant and negative consent receipt");
            check(newAction ? run.Driver.AutoEvidence.QuietAcceptedGrants == 1 && run.Port.State.Stack.Length == 1
                    : run.Driver.AutoEvidence.QuietAcceptedGrants == 2 && run.Port.State.Stack.IsEmpty
                        && run.Port.Commands.OfType<GrantPriorityContinuation>().Last().QuietYieldToken == "after-accepted-resolution",
                newAction ? "A new public action arriving during the accepted pass keeps its own response unacknowledged and blocks automatic Quiet rearming"
                    : "Only the shared exact-response lineage allows Quiet to resume after correlated resolution, using the current provider token");
        }
    }

    private static async Task ServerAutoStops(string profile, Action<bool, string> check)
    {
        var initial = AutoPriority();
        using (var run = new DriverCase(profile, "server-auto-held", initial, initial,
            passing: ConnectedPlayerPassingMode.ServerAuto)) {
            NativeAutoPort(run);
            await run.Prime(); await run.Driver.ObserveAsync(); await Drain(run.Driver.Client);
            run.Driver.SetAutoHeld(true);
            await run.Driver.ObserveAsync(); await Drain(run.Driver.Client); await run.Driver.ObserveAsync();
            check(run.Driver.AutoEvidence.QuietAcceptedGrants == 1 && !run.Driver.Client.PriorityContinuationReserved
                    && run.Port.State.Decision != null && run.Policy.Evaluations == 0,
                "Taking control holds native Auto and acknowledges withdrawal without answering or renewing its unchanged window");
            run.Driver.SetAutoHeld(false);
            await run.Driver.ObserveAsync(); await Drain(run.Driver.Client);
            check(run.Driver.AutoEvidence is { QuietAcceptedGrants: 2, QuietConsentWindows: 1 },
                "Explicitly resuming Auto may consent again to the same unchanged original window without inventing a distinct token");
            var required = run.Port.State with { Revision = run.Port.State.Revision + 1, ActionClass = "selection", PriorityPlayerId = null,
                Decision = new("required-number", new(), new NumberPayload("1")) };
            await PublishAuto(run, required); await run.Driver.ObserveAsync();
            check(run.Port.State.Decision?.Payload is NumberPayload && run.Driver.AutoEvidence.QuietAcceptedGrants == 2
                    && run.Driver.ClientAdmissions == 0 && run.Driver.ProviderAcceptances == 0,
                "A required nonpriority choice ends Quiet and remains unanswered by the Auto coordinator");
        }
        var own = initial with { ActivePlayerId = initial.ViewerId, PhaseKey = "MAIN1",
            Decision = initial.Decision! with { Payload = ((PriorityPayload)initial.Decision.Payload) with {
                CanAutoPass = true, ResponseAvailability = PriorityResponseAvailability.Absent } } };
        using (var run = new DriverCase(profile, "server-auto-own-stop", own, own,
            passing: ConnectedPlayerPassingMode.ServerAuto, phaseStops: ["MAIN1"])) {
            NativeAutoPort(run);
            await run.Prime(); await run.Driver.ObserveAsync(); await Drain(run.Driver.Client);
            check(run.Port.Commands is [GrantPriorityContinuation { Mode: PriorityContinuationMode.NoResponse, PhaseStops: ["MAIN1"] }]
                    && run.Driver.AutoEvidence.QuietAcceptedGrants == 0 && run.Driver.ClientAdmissions == 0,
                "An own-turn configured phase stop reaches the server intact and never becomes opponent-style Quiet consent");
            await run.Driver.ObserveAsync(); await run.Driver.StepAsync();
            check(run.Driver.AutoEvidence is { NoResponseAcceptedGrants: 1, AcceptedPolicyPriorityPasses: 1, CertifiedAutomaticPasses: 0 }
                    && run.Driver.ProviderAcceptances == 1 && run.Policy.Evaluations == 1,
                "Even certified absence of a response cannot bypass an own-turn phase stop: crossing it requires a separate admitted manual policy input");
        }
    }

    private static async Task ServerAutoBatchedAttention(string profile, Action<bool, string> check)
    {
        var initial = AutoPriority() with { ActivePlayerId = AutoPriority().ViewerId };
        using var run = new DriverCase(profile, "server-auto-batched-attention", initial, initial,
            passing: ConnectedPlayerPassingMode.ServerAuto);
        NativeAutoPort(run);
        await run.Prime(); await run.Driver.ObserveAsync(); await Drain(run.Driver.Client);
        var before = run.Port.State;
        var required = before with { Revision = before.Revision + 1, ActionClass = "selection", PriorityPlayerId = null,
            Decision = new("intermediate-required-choice", new(), new NumberPayload("1")) };
        var current = before with { Revision = required.Revision + 1,
            Decision = before.Decision! with { Id = "current-after-batched-choice" } };
        run.Driver.Client.Post(() => run.Driver.Client.Accept(required));
        run.Driver.Client.Post(() => run.Driver.Client.Accept(current));
        run.Driver.Client.Pump();
        check(run.Driver.AutoNeedsResponse && ReferenceEquals(run.Driver.Client.State, current)
                && run.Policy.Evaluations == 0 && run.Driver.ProviderAcceptances == 0,
            "Each accepted callback feeds shared attention even when one owner pump admits a required-choice frame and the later current priority frame");
        run.Port.State = current; run.Port.RichState = current;
        await run.Driver.ObserveAsync(); await run.Driver.StepAsync();
        check(!run.Driver.AutoNeedsResponse && run.Driver.ClientAdmissions == 1 && run.Driver.ProviderAcceptances == 1
                && run.Port.Commands.Last() is PassPriority { DecisionId: "current-after-batched-choice" },
            "An actual manual policy answer acknowledges only the exact current accepted snapshot after a batched pump, with no stale attention revision or metadata input credit");
    }

    private static PriorityContinuationState Granted(GrantPriorityContinuation command, PriorityContinuationStatus status)
        => new(command.Sequence, command.GrantId, command.Mode, status,
            status == PriorityContinuationStatus.Active ? "no-response" : "response-available", 0, command.PhaseStops);

    private static DecisionActionDomain Domain(GameSnapshot state) => new(state.Revision, ActionDomainCoverage.Complete,
        [Offers(state)], [new("controlled-play", ActionEffectKind.Land, [ActionEffectKind.Land], [], ActionAffordability.Payable)]);

    private static GameSnapshot Opening() => PresentationSmoke.Example() with {
        Revision = 10, BoardRevision = 10,
        Decision = new("opening-owner", new DecisionContext(Message: "Keep this hand?"),
            new OpeningHandPayload(new("Keep", true), new("Mulligan", true)))
    };

    private static GameSnapshot Priority() => PresentationSmoke.Example() with { Revision = 10, BoardRevision = 10 };

    private static ActionSourceOccurrence ExtraSource(GameSnapshot state, string id) => new(state.Decision!.Id,
        "Sideboard", state.ViewerId, new Card {
            Key = "source:" + id, VisualId = id, Name = "Known sideboard card", Type = "Creature", Selectable = false
        });

    private static GameSnapshot ChangeOwnHand(GameSnapshot state, Func<Card, Card> change) => state with {
        Players = state.Players.Select(player => player.Id != state.ViewerId ? player : player with {
            Zones = player.Zones.Select(zone => zone.Name != "Hand" ? zone : zone with {
                Cards = zone.Cards.Select((card, index) => index == 0 ? change(card) : card).ToImmutableArray()
            }).ToImmutableArray()
        }).ToImmutableArray()
    };

    private static async Task Drain(ClientSession client)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (client.Busy || client.Polling) {
            client.Pump();
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("The controlled ordinary client did not settle.");
            await Task.Yield();
        }
        client.Pump();
    }

    private sealed class DriverCase : IDisposable
    {
        private readonly AiCampaignDiagnostics evidence;
        internal Port Port { get; }
        internal ScriptedPolicy Policy { get; }
        internal ConnectedPlayerDriver Driver { get; }
        internal long Ticks;
        internal DriverCase(string profile, string name, GameSnapshot ordinary, GameSnapshot rich, bool useSource = false,
            ConnectedPlayerPassingMode passing = ConnectedPlayerPassingMode.ServerNoResponse, ImmutableArray<string> phaseStops = default)
        {
            string directory = Path.Combine(profile, "polled-full-game-contracts", name);
            Directory.CreateDirectory(directory);
            evidence = new(directory);
            Port = new(ordinary, rich) { UseSource = useSource };
            Policy = new(useSource);
            Driver = new(Port, Port, Port, Port, Port, evidence, 261104,
                MixedGameDriveMode.PolledBackground, Policy, () => Ticks, passing, phaseStops);
            Driver.BindLobby(Port.Lobby);
        }
        internal async Task Prime()
        {
            await Driver.ObserveAsync();
            await Port.OrdinaryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Port.ReleaseOrdinary.TrySetResult(Port.State);
            await Drain(Driver.Client);
        }
        public void Dispose() { Driver.Dispose(); evidence.Dispose(); }
    }

    private sealed class ScriptedPolicy(bool useSource) : IAiPolicy
    {
        internal int Evaluations;
        internal GameSnapshot? PhysicalState;
        public AiPolicyDescriptor Descriptor { get; } = new("polled-full-game-contract.v1", AiTasks.All);
        public Task<AiProposal> EvaluateAsync(AiDecisionRequest request, CancellationToken cancellation)
        {
            Evaluations++; PhysicalState = request.Frame.State;
            DecisionCommand answer = useSource ? new UseOfferedAction(PhysicalState.Id, PhysicalState.Decision!.Id,
                request.Frame.SourceActions[0].SourceCardKey, request.Frame.SourceActions[0].Actions[0].Id)
                : PhysicalState.Decision!.Payload is PaymentPayload && PhysicalState.Viewer!.Mana.GetValueOrDefault("U") > 0
                ? new SpendPoolMana(PhysicalState.Id, PhysicalState.Decision.Id, "U")
                : PhysicalState.Decision!.Payload is OpeningHandPayload
                ? new KeepOpeningHand(PhysicalState.Id, PhysicalState.Decision.Id)
                : new PassPriority(PhysicalState.Id, PhysicalState.Decision.Id);
            return Task.FromResult<AiProposal>(new AiProposal.Answer(answer, "controlled-owned-input"));
        }
    }

    private sealed class Port(GameSnapshot initial, GameSnapshot rich) : IMatchConnection, ITableSetup,
        ISourceActions, IOwnedDecisionObservations, IMatchHistory
    {
        private GameSnapshot state = initial;
        internal GameSnapshot State { get => Volatile.Read(ref state); set => Volatile.Write(ref state, value); }
        internal GameSnapshot RichState { get; set; } = rich;
        internal GameSnapshot? AfterExecute;
        internal Func<GameCommand, GameSnapshot?>? AfterCommand;
        internal Exception? ExecuteError;
        internal int Reads, OwnedReads, SourceReads, HistoryReads;
        internal bool HoldOwned, HoldNextRead, HoldSource, UseSource;
        internal bool PriorityContinuationAvailable;
        public bool SupportsPriorityContinuation => PriorityContinuationAvailable;
        internal List<GameCommand> Commands { get; } = [];
        internal TaskCompletionSource OrdinaryStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<GameSnapshot?> ReleaseOrdinary { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource OwnedStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<OwnedDecisionObservation?> ReleaseOwned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource NextReadStarted { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<GameSnapshot?> ReleaseNextRead { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource SourceStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<SourceActionSet> ReleaseSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Lobby Lobby { get; } = new("host", "playing", null, false, null, true, "", null, [], [], "controlled-table");
        public async Task<GameSnapshot?> ObserveAsync()
        {
            if (Interlocked.Increment(ref Reads) == 1) { OrdinaryStarted.TrySetResult(); return await ReleaseOrdinary.Task; }
            if (HoldNextRead) { HoldNextRead = false; NextReadStarted.TrySetResult(); return await ReleaseNextRead.Task; }
            return State;
        }
        public Task ExecuteAsync(GameCommand command)
        {
            Commands.Add(command);
            if (ExecuteError is { } error) return Task.FromException(error);
            if (AfterCommand?.Invoke(command) is { } response) State = response;
            else if (AfterExecute is { } next) State = next;
            return Task.CompletedTask;
        }
        public Task ConcedeAsync(string gameId) => throw new InvalidOperationException("No synthetic concession is permitted.");
        public Task<OwnedDecisionObservation?> ObserveDecisionAsync(CancellationToken cancellation = default)
        {
            Interlocked.Increment(ref OwnedReads); OwnedStarted.TrySetResult();
            return HoldOwned ? ReleaseOwned.Task : Task.FromResult<OwnedDecisionObservation?>(new(RichState,
                UseSource ? Domain(RichState) : RichState.Decision?.Payload is PriorityPayload or PaymentPayload
                    ? new(RichState.Revision, ActionDomainCoverage.Complete, [], []) {
                        PoolManaColors = RichState.Decision.Payload is PaymentPayload
                            ? RichState.Viewer!.Mana.Where(pair => pair.Value > 0).Select(pair => pair.Key).ToImmutableArray() : []
                    } : DecisionActionDomain.None, new()));
        }
        public Task<SourceActionSet> SourceActionsAsync(string gameId, string decisionId, string sourceCardKey)
        {
            SourceReads++;
            if (!UseSource) throw new InvalidOperationException("The controlled decision has no source-action query.");
            if (HoldSource) { HoldSource = false; SourceStarted.TrySetResult(); return ReleaseSource.Task; }
            return Task.FromResult(Offers(State));
        }
        public bool SupportsHistory => false;
        public Task<HistoryBatch> HistoryAsync(string gameId, long afterSequence, int limit = 128)
        {
            HistoryReads++; throw new InvalidOperationException("No second history/state reader is enabled by this fixture.");
        }
        public Task<Lobby> LobbyAsync() => Task.FromResult(Lobby);
        public Task<SoloSetup> PrepareSoloAsync(string deckId) => throw new NotSupportedException();
        public Task StartSoloAsync(string deckId, ImmutableArray<string> opponentIds) => throw new NotSupportedException();
        public Task<Lobby> HostAsync(int players, bool automaticPortForwarding) => throw new NotSupportedException();
        public Task<Lobby> JoinAsync(string address) => throw new NotSupportedException();
        public Task<Lobby> SelectDeckAsync(LobbyDeckSelection selection) => throw new NotSupportedException();
        public Task SetReadyAsync(bool ready) => throw new NotSupportedException();
        public Task StartAsync() => throw new NotSupportedException();
        public Task ReturnToLobbyAsync() => throw new NotSupportedException();
        public Task LeaveAsync() => throw new NotSupportedException();
    }
}
