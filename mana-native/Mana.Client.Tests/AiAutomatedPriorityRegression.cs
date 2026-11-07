using System.Collections.Immutable;
using Mana.AI;
using Mana.Contracts;

// Authored provider certificates exercise ownership and scheduling. They are not
// evidence that a real rules input passed or a natural game completed.
internal static class AiAutomatedPriorityRegression
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        DescriptorAndClosedControls(check);
        await FullControlAndLegacy(check);
        await CertificatesAndChoices(check);
        await PacingAndHumanCredit(check);
        await PublishedActionBarrier(check);
        await LegacyRegistration(check);
        await Overflow(check);
        await RetiredAndLateControls(check);
        await UnknownGrant(check);
        await CancelledGrantFence(check);
        await UnknownWithdrawalCleanup(check);
    }

    private static void DescriptorAndClosedControls(Action<bool, string> check)
    {
        check(new AiPolicyDescriptor("custom.v1", AiTasks.All).PriorityAutomation == AiPriorityAutomation.FullControl
            && new CasualCommanderPolicy().Descriptor.PriorityAutomation == AiPriorityAutomation.NoResponse,
            "Custom AI policies default to full control; the casual policy explicitly opts into verified NoResponse");
        bool invalid = false;
        try { _ = new AiPolicyDescriptor("invalid.v1", AiTasks.All, (AiPriorityAutomation)99); }
        catch (ArgumentOutOfRangeException) { invalid = true; }
        check(invalid && typeof(AutomatedPriorityControl).Assembly.GetTypes().Where(type => type.IsSubclassOf(typeof(AutomatedPriorityControl)))
            .Select(type => type.Name).Order().SequenceEqual(new[] { "Acknowledge", "NoResponse", "Withdraw" }),
            "Automated priority metadata has a closed ACK/NoResponse/withdraw family with no Quiet, finite or gameplay variant");
        foreach (var control in new AutomatedPriorityControl[] {
            new AutomatedPriorityControl.Acknowledge("", 0, 0), new AutomatedPriorityControl.Acknowledge("g", -1, 0),
            new AutomatedPriorityControl.NoResponse("g", "", 1, "grant", []),
            new AutomatedPriorityControl.NoResponse("g", "d", 0, "grant", []),
            new AutomatedPriorityControl.NoResponse("g", "d", 1, "grant", ["BAD"]),
            new AutomatedPriorityControl.Withdraw("g", 0) }) {
            invalid = false; try { control.Validate(); } catch (ArgumentException) { invalid = true; }
            check(invalid, "Malformed automated metadata cannot acquire an owned control scope: " + control.GetType().Name);
        }
    }

    private static async Task FullControlAndLegacy(Action<bool, string> check)
    {
        foreach (bool supported in new[] { false, true }) {
            var port = new Port { Supported = supported }; var policy = new Policy(AiPriorityAutomation.FullControl);
            await using var host = new AiTableHost(port, _ => policy, 1);
            await host.StepAsync();
            check(policy.Calls == 1 && port.Commands is [PassPriority] && port.Controls.Count == 0,
                "Full control preserves the ordinary policy path regardless of optional host capability: " + supported);
        }
        var legacy = new Port { Supported = false }; var opted = new Policy();
        await using var fallback = new AiTableHost(legacy, _ => opted, 1);
        await fallback.StepAsync();
        check(opted.Calls == 1 && legacy.Commands is [PassPriority] && legacy.Controls.Count == 0,
            "A policy opt-in alone cannot activate absent provider capability or manufacture server consent");
    }

    private static async Task CertificatesAndChoices(Action<bool, string> check)
    {
        var port = new Port(); var policy = new Policy(); var host = new AiTableHost(port, _ => policy, 1);
        try {
            await Arm(host);
            check(port.Controls is [AutomatedPriorityControl.Acknowledge, AutomatedPriorityControl.NoResponse]
                && policy.Calls == 0 && port.Commands.Count == 0 && Accepted(host) == 0,
                "Only actual ACK and NoResponse control receipts reserve routine priority; metadata earns no policy/input acceptance");
            port.State = port.State with { Revision = port.State.Revision + 1,
                PriorityContinuation = port.State.PriorityContinuation! with { AutomaticPasses = 7 } };
            await host.StepAsync(); await host.StepAsync();
            var proof = host.PriorityContinuations.Single();
            check(proof.AcceptedAcknowledgements == 1 && proof.AcceptedGrants == 1 && proof.CertifiedAutomaticPasses == 7
                && Accepted(host) == 0 && policy.Calls == 0,
                "Repeated observed grant high-water counts once and remains separate from fabricated per-prompt decision records");
            foreach (var availability in new[] { PriorityResponseAvailability.Available, PriorityResponseAvailability.Unknown }) {
                port.Priority(availability);
                await Arm(host);
                check(policy.Calls == (availability == PriorityResponseAvailability.Available ? 1 : 2)
                    && port.Commands.Last() is PassPriority && host.LastFault == null,
                    "An available or unknown response remains a real current policy decision, never inferred absent: " + availability);
            }
            port.RequiredChoice(); await Arm(host);
            check(policy.Calls == 3 && port.Commands.Last() is DeclineConfirmation && Accepted(host) == 3,
                "An accepted standing grant cannot answer or hide a required choice");
            port.State = port.State with { Status = "finished", Decision = null, Revision = port.State.Revision + 1,
                Players = [port.State.Players[0] with { Eliminated = true }],
                PriorityContinuation = port.State.PriorityContinuation! with { Status = PriorityContinuationStatus.Revoked } };
            await host.StepAsync();
        } finally { await host.DisposeAsync(); }
        check(host.PriorityContinuations.Single() is { Retired: true, AcceptedWithdrawals: >= 2, CertifiedAutomaticPasses: 7 }
            && port.Controls.Last() is AutomatedPriorityControl.Withdraw && port.Commands.Count == 3,
            "Closing or terminalizing an automated owner preserves stable certificate evidence and negatively fences its grant");
    }

    private static async Task PacingAndHumanCredit(Action<bool, string> check)
    {
        var port = new Port(); var policy = new Policy(); var pace = new Pace();
        var host = new AiTableHost(port, _ => policy, 1, pacing: pace);
        try {
            await host.StepAsync();
            check(port.Controls.Count == 0 && policy.Calls == 0,
                "An announcement pause withholds both bot observation ACK and new continuation consent");
            pace.Open = true; await Arm(host);
            port.Hold("awaiting-observation");
            await host.StepAsync();
            check(port.Commands.Count == 0 && port.Controls.Count == 2 && policy.Calls == 0,
                "A registered human reading-credit hold cannot become a manual AI priority bypass");
            pace.Open = false; await host.StepAsync(); await host.StepAsync();
            check(port.Controls is [AutomatedPriorityControl.Acknowledge, AutomatedPriorityControl.NoResponse,
                    AutomatedPriorityControl.Withdraw] && policy.Calls == 0,
                "A new presentation pause withdraws active bot consent without acknowledging or acting through the pause");
        } finally { await host.DisposeAsync(); }
    }

    private static async Task LegacyRegistration(Action<bool, string> check)
    {
        var port = new Port(); var policy = new Policy();
        await using var host = new AiTableHost(port, _ => policy, 1);
        await Arm(host); port.Hold("awaiting-observation-registration");
        await host.StepAsync(); await host.StepAsync();
        check(port.Controls.Count(control => control is AutomatedPriorityControl.NoResponse) == 1
            && port.Controls.Count(control => control is AutomatedPriorityControl.Withdraw) == 1
            && port.Commands is [PassPriority] && policy.Calls == 1,
            "An explicitly unsupported legacy observer registration withdraws optional Auto and keeps the ordinary paced policy usable");
    }

    private static async Task PublishedActionBarrier(Action<bool, string> check)
    {
        var clock = new Clock(); var port = new Port(); var policy = new Policy();
        var pace = new TimedAiHostPacing(TimeSpan.FromSeconds(2.4), clock);
        await using var host = new AiTableHost(port, _ => policy, 1, pacing: pace);
        await Arm(host);
        check(port.Controls is [AutomatedPriorityControl.Acknowledge, AutomatedPriorityControl.NoResponse],
            "The real announcement pacer permits initial NoResponse only after its accepted-frame ACK");
        port.State = port.State with { Revision = port.State.Revision + 1, BoardRevision = 2,
            StackInterruptionVersion = 1,
            Stack = [new("Announced card", "", false, null) { Id = "new-public-cast" }],
            Activity = [new(1, 1, "MAIN1", "cast", 1, "Announced a card", null, null)],
            PriorityObservation = port.State.PriorityObservation! with { BoardRevision = 2, PublicActivitySequence = 1 },
            PriorityContinuation = port.State.PriorityContinuation! with { Status = PriorityContinuationStatus.Held,
                ReasonCode = "awaiting-observation" } };
        // The real server test supplies this pre-poll hold. This authored port
        // verifies that observing it cannot acknowledge through the reading beat.
        await host.StepAsync(); await host.StepAsync();
        clock.Advance(TimeSpan.FromMilliseconds(2399)); await host.StepAsync();
        check(port.Controls is [AutomatedPriorityControl.Acknowledge, AutomatedPriorityControl.NoResponse,
                AutomatedPriorityControl.Withdraw] && policy.Calls == 0 && port.Commands.Count == 0,
            "A changed public-action signature with unused old credit receives no ACK, grant or policy input during the actual 2.4 s announcement pause");
        clock.Advance(TimeSpan.FromMilliseconds(1)); await host.StepAsync();
        check(port.Controls.Last() is AutomatedPriorityControl.Acknowledge { BoardRevision: 2, PublicActivitySequence: 1 }
            && port.Controls.Count(control => control is AutomatedPriorityControl.NoResponse) == 1,
            "After reading, even one new activity forces ACK of the changed action signature before another grant");
        await host.StepAsync(); await host.StepAsync();
        int acknowledged = port.Controls.Count(control => control is AutomatedPriorityControl.Acknowledge);
        for (int index = 0; index < 3; index++) {
            port.State = port.State with { Revision = port.State.Revision + 1 }; await host.StepAsync();
        }
        check(acknowledged == 2 && port.Controls.Count(control => control is AutomatedPriorityControl.Acknowledge) == acknowledged
            && port.Controls.Count(control => control is AutomatedPriorityControl.NoResponse) == 2
            && policy.Calls == 0 && host.LastFault == null,
            "Pure priority revisions retain their acknowledged signature without extra metadata RPCs or a new pass timer");
    }

    private static async Task RetiredAndLateControls(Action<bool, string> check)
    {
        var port = new Port(); var policy = new Policy();
        var entered = NewSignal(); var release = NewSignal();
        port.BeforeControl = async control => { if (control is AutomatedPriorityControl.Acknowledge) { entered.TrySetResult(); await release.Task; } };
        await using var host = new AiTableHost(port, _ => policy, 1);
        var first = host.StepAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        port.Epoch++; release.TrySetResult(); await first;
        check(host.PriorityContinuations.Single().AcceptedAcknowledgements == 0 && policy.Calls == 0
            && host.LastFault == null && port.Commands.Count == 0,
            "A delayed old-epoch metadata completion cannot establish new consent, certificates or policy authority");
        port.BeforeControl = null; port.RetireControls = true;
        await host.StepAsync();
        check(host.PriorityContinuations.Any(proof => proof.Retired) && policy.Calls == 0 && host.LastFault == null,
            "Known same-game metadata retirement requests fresh authority without a fake rules fault or same-frame policy answer");
    }

    private static async Task Overflow(Action<bool, string> check)
    {
        var port = new Port(); var policy = new Policy();
        await using var host = new AiTableHost(port, _ => policy, 1);
        await Arm(host);
        port.State = port.State with { Revision = port.State.Revision + 1,
            PriorityObservation = port.State.PriorityObservation! with { Overflowed = true } };
        await host.StepAsync(); await host.StepAsync();
        check(host.LastFault?.ReasonCode == "priority-observation-overflow" && policy.Calls == 0
            && port.Commands.Count == 0 && port.Controls.Last() is AutomatedPriorityControl.Withdraw,
            "Lost registered bot delivery evidence is negatively fenced and fails closed instead of falling through to manual priority");
    }

    private static async Task UnknownGrant(Action<bool, string> check)
    {
        var port = new Port { LoseGrantReceipt = true }; var policy = new Policy();
        await using var host = new AiTableHost(port, _ => policy, 1);
        await host.StepAsync(); await host.StepAsync(); await host.StepAsync();
        check(host.LastFault?.ReasonCode == "priority-control-unknown" && port.Reports.Count == 1
            && port.Controls.Count(control => control is AutomatedPriorityControl.NoResponse) == 1
            && port.Controls.Last() is AutomatedPriorityControl.Withdraw
            && port.State.PriorityContinuation?.Status == PriorityContinuationStatus.Revoked
            && policy.Calls == 0 && Accepted(host) == 0,
            "A lost affirmative metadata receipt is negatively fenced and faults closed without regranting or falling through to a policy input");
    }

    private static async Task CancelledGrantFence(Action<bool, string> check)
    {
        var port = new Port(); var policy = new Policy(); var entered = NewSignal(); var release = NewSignal();
        var host = new AiTableHost(port, _ => policy, 1);
        await host.StepAsync();
        port.BeforeControl = async control => { if (control is AutomatedPriorityControl.NoResponse) { entered.TrySetResult(); await release.Task; } };
        using var cancellation = new CancellationTokenSource();
        var pending = host.StepAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
        try { await pending; throw new InvalidOperationException("Expected cancellation."); } catch (OperationCanceledException) { }
        await host.DisposeAsync(); release.TrySetResult(); await port.PositiveFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        check(port.Controls.Last() is AutomatedPriorityControl.Withdraw && port.PositiveAccepted == 0
            && port.State.PriorityContinuation?.Status == PriorityContinuationStatus.Revoked
            && host.PriorityContinuations.Single() is { Retired: true, AcceptedGrants: 0, CertifiedAutomaticPasses: 0 }
            && policy.Calls == 0 && port.Commands.Count == 0,
            "An uncooperative delayed grant cannot cross disposal's negative sequence fence or revive an AI owner");
    }

    private static async Task UnknownWithdrawalCleanup(Action<bool, string> check)
    {
        var port = new Port(); var policy = new Policy(); var host = new AiTableHost(port, _ => policy, 1);
        await Arm(host); port.LoseWithdrawReceipt = true;
        Exception? failure = null;
        try { await host.DisposeAsync(); } catch (Exception error) { failure = error; }
        bool closed = false;
        try { await host.StepAsync(); } catch (ObjectDisposedException) { closed = true; }
        check(failure is IOException && closed && host.Histories.IsEmpty
            && host.PriorityContinuations.Single() is { Retired: false, AcceptedWithdrawals: 0 }
            && port.Controls.Count(control => control is AutomatedPriorityControl.Withdraw) == 1
            && port.Commands.Count == 0 && policy.Calls == 0,
            "An uncertain disposal withdrawal still closes every local policy owner while preserving unknown server consent instead of falsely certifying retirement");
    }

    private static async Task Arm(AiTableHost host) { await host.StepAsync(); await host.StepAsync(); await host.StepAsync(); }
    private static int Accepted(AiTableHost host) => host.Histories.Sum(history => history.Decisions.Count(record => record.Outcome == AiDecisionOutcome.Accepted));
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Pace : IAiHostPacing
    {
        internal bool Open;
        public void Observe(ImmutableArray<AiTableProgress> progress) { }
        public bool Ready(string gameId, int seatId) => Open;
    }
    private sealed class Clock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        internal void Advance(TimeSpan elapsed) => ticks += elapsed.Ticks;
    }
    private sealed class Policy(AiPriorityAutomation mode = AiPriorityAutomation.NoResponse) : IAiPolicy
    {
        internal int Calls;
        public AiPolicyDescriptor Descriptor { get; } = new("automated-priority-test.v1", AiTasks.All, mode);
        public Task<AiProposal> EvaluateAsync(AiDecisionRequest request, CancellationToken cancellation)
        {
            Calls++;
            var state = request.Frame.State;
            DecisionCommand command = state.Decision!.Payload is ConfirmationPayload
                ? new DeclineConfirmation(state.Id, state.Decision.Id) : new PassPriority(state.Id, state.Decision.Id);
            return Task.FromResult<AiProposal>(new AiProposal.Answer(command, "ordinary-current-input"));
        }
    }
    private sealed class Port : IAutomatedSeatProvider, IAutomatedPriorityContinuation
    {
        internal GameSnapshot State = new("automated-priority-test", "playing", null, null, 0, 1, "Main", "MAIN1", 0,
            [new Player(0, "Bot", 40, false, false, ImmutableDictionary<string, int>.Empty, [])], [], null,
            new("priority-1", new(), new PriorityPayload(new("Pass", true), CanAutoPass: true) {
                ResponseAvailability = PriorityResponseAvailability.Absent })) {
                Revision = 1, BoardRevision = 1, DecisionOwnerId = 0, PriorityPlayerId = 0, StackInterruptionVersion = 0,
                PriorityContinuation = new(0, "", null, PriorityContinuationStatus.Idle, "", 0, []),
                PriorityObservation = new(1, 0, 0, 0, false, false)
            };
        internal bool Supported = true, RetireControls, LoseGrantReceipt, LoseWithdrawReceipt;
        internal long Epoch = 1, Fence;
        internal int PositiveAccepted;
        internal List<GameCommand> Commands = [];
        internal List<AutomatedPriorityControl> Controls = [];
        internal List<string> Reports = [];
        internal Func<AutomatedPriorityControl, Task>? BeforeControl;
        internal TaskCompletionSource PositiveFinished = NewSignal();
        public bool SupportsAutomatedPriorityContinuation => Supported;
        public long AutomatedPriorityEpoch => Epoch;
        public Task EnableAutomatedSeatsAsync(CancellationToken cancellation = default) => Task.CompletedTask;
        public Task<ImmutableArray<AutomatedSeatObservation>> ObserveAutomatedSeatsAsync(CancellationToken cancellation = default)
            => Task.FromResult(ImmutableArray.Create(new AutomatedSeatObservation(0, State,
                State.Decision?.Payload is PriorityPayload ? new(1, ActionDomainCoverage.Complete, [], []) : DecisionActionDomain.None, new())));
        public Task ExecuteAutomatedAsync(int seatId, GameCommand command, CancellationToken cancellation = default)
        {
            if (seatId != 0 || !GameCommandPermissions.Allows(State, command)) throw new InvalidDataException("Unowned test command.");
            Commands.Add(command);
            State = State with { Revision = State.Revision + 1, Decision = null, Status = "resolving" };
            return Task.CompletedTask;
        }
        public async Task<AutomatedPriorityControlResult> ExecuteAutomatedPriorityControlAsync(long epoch, int seatId,
            AutomatedPriorityControl control, CancellationToken cancellation = default)
        {
            control.Validate(); Controls.Add(control);
            if (BeforeControl != null) await BeforeControl(control);
            if (epoch != Epoch || seatId != 0 || control.GameId != State.Id || RetireControls)
                return AutomatedPriorityControlResult.Retired;
            switch (control) {
                case AutomatedPriorityControl.Acknowledge ack:
                    State = State with { Revision = State.Revision + 1, PriorityObservation = State.PriorityObservation! with {
                        Registered = true, AcknowledgedBoardRevision = ack.BoardRevision,
                        AcknowledgedPublicActivitySequence = ack.PublicActivitySequence } }; break;
                case AutomatedPriorityControl.NoResponse grant:
                    try {
                        if (grant.Sequence <= Fence) return AutomatedPriorityControlResult.Retired;
                        PositiveAccepted++;
                        var availability = (State.Decision?.Payload as PriorityPayload)?.ResponseAvailability;
                        State = State with { Revision = State.Revision + 1, PriorityContinuation = new(grant.Sequence,
                            grant.GrantId, PriorityContinuationMode.NoResponse,
                            availability == PriorityResponseAvailability.Absent ? PriorityContinuationStatus.Active : PriorityContinuationStatus.Held,
                            availability == PriorityResponseAvailability.Absent ? "" : availability == PriorityResponseAvailability.Available
                                ? "response-available" : "response-unknown", 0, grant.PhaseStops) };
                        if (LoseGrantReceipt) throw new IOException("Controlled lost affirmative receipt.");
                    } finally { PositiveFinished.TrySetResult(); }
                    break;
                case AutomatedPriorityControl.Withdraw withdraw:
                    Fence = Math.Max(Fence, withdraw.ThroughSequence);
                    State = State with { Revision = State.Revision + 1, PriorityContinuation = State.PriorityContinuation! with {
                        Sequence = Math.Max(State.PriorityContinuation.Sequence, Fence), Status = PriorityContinuationStatus.Revoked } };
                    if (LoseWithdrawReceipt) throw new IOException("Controlled lost negative receipt.");
                    break;
            }
            return AutomatedPriorityControlResult.Accepted;
        }
        public Task ReportAutomationFaultAsync(string gameId, int seatId, string reasonCode, CancellationToken cancellation = default)
        { Reports.Add(reasonCode); return Task.CompletedTask; }
        internal void Priority(PriorityResponseAvailability availability)
        {
            State = State with { Revision = State.Revision + 1, Status = "playing",
                Decision = new("priority-" + (State.Revision + 1), new(), new PriorityPayload(new("Pass", true),
                    CanAutoPass: availability == PriorityResponseAvailability.Absent) { ResponseAvailability = availability }),
                PriorityContinuation = State.PriorityContinuation! with { Status = State.PriorityContinuation!.Status == PriorityContinuationStatus.Revoked
                    ? PriorityContinuationStatus.Revoked : PriorityContinuationStatus.Held,
                    ReasonCode = availability == PriorityResponseAvailability.Available ? "response-available" : "response-unknown" } };
        }
        internal void RequiredChoice() => State = State with { Revision = State.Revision + 1, Status = "playing",
            Decision = new("choice-" + State.Revision, new(), new ConfirmationPayload(new("Yes", true), new("No", true))),
            PriorityContinuation = State.PriorityContinuation! with { Status = PriorityContinuationStatus.Held, ReasonCode = "required-choice" } };
        internal void Hold(string reason) => State = State with { Revision = State.Revision + 1,
            PriorityContinuation = State.PriorityContinuation! with { Status = PriorityContinuationStatus.Held, ReasonCode = reason } };
    }
}
