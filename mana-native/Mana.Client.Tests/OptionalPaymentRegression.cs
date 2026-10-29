using System.Collections.Immutable;
using Mana.Client;
using Mana.Contracts;

internal static class OptionalPaymentRegression
{
    internal static readonly Type[] CommandTypes = [typeof(AutoPayOptionalPayment)];
    internal static async Task RunAsync(Action<bool, string> check)
    {
        await DefaultsAndSavedAnswers(check);
        await ContinuationsAndControl(check);
        ScopeAndLifecycle(check);
        await CapturedUnpayableQueue(check);
    }

    private static GameSnapshot Payment(OptionalManaPaymentAvailability availability = OptionalManaPaymentAvailability.Payable,
        long revision = 1, string decision = "payment-0", string policy = "fixed-one", string offer = "offer-0", int amount = 1)
    {
        var state = FiniteResolutionRegression.State(2, paying: true, revision: revision);
        var payload = (PaymentPayload)state.Decision!.Payload;
        return state with { Decision = state.Decision with { Id = decision, Payload = payload with {
            UnpaidManaParts = [new("{" + amount + "}", 1)],
            OptionalDecline = payload.OptionalDecline! with { OfferId = offer },
            OptionalPaymentSupport = new(policy, [new("{" + amount + "}", 1)], availability,
                availability == OptionalManaPaymentAvailability.Payable)
        } } };
    }

    private static async Task DefaultsAndSavedAnswers(Action<bool, string> check)
    {
        foreach (var availability in Enum.GetValues<OptionalManaPaymentAvailability>()) {
            var initial = Payment(availability);
            var port = new Port(initial, FiniteResolutionRegression.State(2, completed: 1, revision: 2));
            var session = new ClientSession(port); session.Accept(initial);
            bool started = session.AdvanceOptionalPayments((_, _) => false); await Pump(session);
            check(started == (availability == OptionalManaPaymentAvailability.Unpayable)
                && port.Commands.Count == (started ? 1 : 0) && port.Commands.All(command => command is CancelPayment)
                && session.OptionalPaymentPolicies.IsEmpty,
                "Only an engine-proved unpayable optional bill is quietly declined by default; payable and unknown bills remain questions");
            session.Close();
        }
        var offered = Payment();
        check(GameCommandPermissions.Allows(offered, new AutoPayOptionalPayment(offered.Id, offered.Decision!.Id, "offer-0"))
                && !GameCommandPermissions.Allows(offered, new AutoPayOptionalPayment(offered.Id, offered.Decision.Id, "other-offer"))
                && !GameCommandPermissions.Allows(offered, new AutoPayOptionalPayment(offered.Id, "old-decision", "offer-0"))
                && !GameCommandPermissions.Allows(offered with { DecisionOwnerId = 1 }, new AutoPayOptionalPayment(offered.Id, offered.Decision.Id, "offer-0")),
            "Automatic optional funding requires the exact current certified offer, decision and owning seat");
        foreach (var mode in new[] { OptionalPaymentMode.AutoPay, OptionalPaymentMode.AutoDecline }) {
            var initial = Payment();
            var after = FiniteResolutionRegression.State(2, completed: 1, revision: 2);
            var port = new Port(initial, after);
            var session = new ClientSession(port); session.Accept(initial);
            check(session.TrySetOptionalPaymentPolicy(mode) && port.Commands.Count == 0,
                "Saving an optional answer is source-specific local consent and never itself yields or spends mana");
            session.AdvanceOptionalPayments((_, _) => false); await Pump(session);
            check(port.Commands.Count == 1 && (mode == OptionalPaymentMode.AutoPay
                    ? port.Commands[0] is AutoPayOptionalPayment { OfferId: "offer-0" }
                    : port.Commands[0] is CancelPayment)
                    && session.OptionalPaymentPolicies is [{ Mode: var saved }] && saved == mode,
                "A source preference answers only its exact current optional offer, then remains dormant at priority");
            check(!session.AdvanceOptionalPayments((_, _) => false) && port.Commands.Count == 1,
                "An optional answer never becomes a pass-priority or end-turn command");
            session.Close();
        }
    }

    private static async Task ContinuationsAndControl(Action<bool, string> check)
    {
        var initial = Payment(amount: 2, policy: "fixed-two");
        var partial = Payment(revision: 2, decision: "payment-step-2", amount: 2, policy: "fixed-two");
        partial = partial with { Decision = partial.Decision! with { Payload = ((PaymentPayload)partial.Decision.Payload) with {
            UnpaidManaParts = [new("{1}", 1)], OptionalDecline = ((PaymentPayload)partial.Decision.Payload).OptionalDecline! with { PaymentKey = "remaining-one" }
        } } };
        var port = new Port(initial, partial, FiniteResolutionRegression.State(2, completed: 1, revision: 3));
        var session = new ClientSession(port); session.Accept(initial); session.TrySetOptionalPaymentPolicy(OptionalPaymentMode.AutoPay);
        session.PauseOptionalPayments();
        check(!session.AdvanceOptionalPayments((_, _) => false) && session.OptionalPaymentPolicies[0].Paused,
            "Reading pauses optional automation without erasing the source preference");
        session.ResumeOptionalPayments(); session.AdvanceOptionalPayments((_, _) => false); await Pump(session);
        check(port.Commands is [AutoPayOptionalPayment { DecisionId: "payment-0" }, AutoPayOptionalPayment { DecisionId: "payment-step-2" }]
                && session.OptionalPaymentPolicies.Length == 1,
            "A multi-step bill uses fresh rechecked funding gestures while retaining the actual original fixed bill identity");
        session.Close();

        port = new Port(initial, initial);
        session = new ClientSession(port); session.Accept(initial); session.TrySetOptionalPaymentPolicy(OptionalPaymentMode.AutoPay);
        session.AdvanceOptionalPayments((_, _) => false); await Pump(session);
        session.ClearAllOptionalPaymentPolicies(); session.TrySetOptionalPaymentPolicy(OptionalPaymentMode.AutoPay);
        session.AdvanceOptionalPayments((_, _) => false); await Pump(session);
        check(port.Commands.Count == 1,
            "An acknowledged unchanged decision cannot spend again across worker chunks or clearing and rearming the preference");
        session.Close();

        initial = Payment();
        port = new Port(initial, Payment(revision: 2, decision: "renewed-decision-same-offer"));
        session = new ClientSession(port); session.Accept(initial); session.TrySetOptionalPaymentPolicy(OptionalPaymentMode.AutoDecline);
        session.AdvanceOptionalPayments((_, _) => false); await Pump(session);
        check(port.Commands is [CancelPayment],
            "A negative answer consumes the actual offer even if its prompt handle refreshes, while affirmative funding uses per-step decisions");
        session.Close();

        var interrupted = Payment(revision: 2, decision: "after-unseen-choice") with { ResolutionChoiceVersion = 2 };
        port = new Port(initial, interrupted);
        session = new ClientSession(port); session.Accept(initial); session.TrySetOptionalPaymentPolicy(OptionalPaymentMode.AutoPay);
        session.AdvanceOptionalPayments((_, _) => false); await Pump(session);
        check(port.Commands.Count == 1 && session.OptionalPaymentPolicies is [{ Paused: true }],
            "A required choice between observations pauses the payment chain even if the same optional offer is current again");
        session.Close();

        var held = new Held(FiniteResolutionRegression.State(2, completed: 1, revision: 2));
        session = new ClientSession(held); session.Accept(Payment()); session.TrySetOptionalPaymentPolicy(OptionalPaymentMode.AutoPay);
        session.AdvanceOptionalPayments((_, _) => false); await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.ClearAllOptionalPaymentPolicies(); held.Release.TrySetResult(); await Pump(session);
        check(held.Commands.Count == 1 && held.Observations == 1 && session.State?.Revision == 2 && session.OptionalPaymentPolicies.IsEmpty,
            "Take control withdraws future payment consent while still reconciling the result of one command already sent");
        session.Close();

        var unknown = Payment(OptionalManaPaymentAvailability.Unknown, revision: 2, decision: "unknown-step");
        port = new Port(Payment(), unknown);
        session = new ClientSession(port); session.Accept(Payment()); session.TrySetOptionalPaymentPolicy(OptionalPaymentMode.AutoPay);
        session.AdvanceOptionalPayments((_, _) => false); await Pump(session);
        check(session.OptionalPaymentPolicies is [{ Paused: true }] && !session.AdvanceOptionalPayments((_, _) => false)
                && port.Commands.Count == 1,
            "An unsupported funding continuation requires explicit rearming rather than answering another choice or silently resuming");
        session.Close();
    }

    private static void ScopeAndLifecycle(Action<bool, string> check)
    {
        foreach (var kind in new[] { "bill", "source-hidden", "control", "zone", "game", "viewer", "reset" }) {
            var initial = Payment(); var session = new ClientSession(new Port(initial, initial)); session.Accept(initial);
            session.TrySetOptionalPaymentPolicy(OptionalPaymentMode.AutoPay);
            // A concealed/departed source no longer advertises a live optional
            // offer. Model that accepted publication, not a contradictory face.
            var next = FiniteResolutionRegression.State(2, completed: 2, revision: 2);
            if (kind == "bill") next = Payment(revision: 2, policy: "new-fixed-bill", amount: 2);
            else if (kind == "game") next = next with { Id = "new-game" };
            else if (kind == "viewer") next = next with { ViewerId = 1 };
            else if (kind is "source-hidden" or "control" or "zone") {
                var source = initial.Viewer!.Zone("Battlefield").Cards[0];
                var zones = kind == "source-hidden" ? ImmutableArray.Create(new Zone("Battlefield", 1, [source with { FaceDown = true }], null))
                    : kind == "zone" ? ImmutableArray.Create(new Zone("Graveyard", 1, [source], null)) : [];
                next = next with { Players = next.Players.SetItem(0, next.Viewer! with { Zones = zones }) };
            }
            if (kind == "reset") session.Reset(); else session.Accept(next);
            check(session.OptionalPaymentPolicies.IsEmpty,
                "A saved source payment permission retires at its accepted " + kind + " boundary");
            session.Close();
        }
        var state = Payment(OptionalManaPaymentAvailability.Unknown);
        var unknownSession = new ClientSession(new Port(state, state)); unknownSession.Accept(state);
        check(!unknownSession.TrySetOptionalPaymentPolicy(OptionalPaymentMode.AutoPay)
                && unknownSession.TrySetOptionalPaymentPolicy(OptionalPaymentMode.AutoDecline),
            "Unknown funding cannot enable affirmative automation but does not prevent a deliberate optional decline preference");
        unknownSession.Close();
    }

    private static async Task CapturedUnpayableQueue(Action<bool, string> check)
    {
        var initial = FiniteResolutionRegression.State(2);
        var first = Payment(OptionalManaPaymentAvailability.Unpayable, revision: 2);
        var nextPriority = FiniteResolutionRegression.State(2, completed: 1, revision: 3);
        var second = FiniteResolutionRegression.State(2, completed: 1, paying: true, revision: 4);
        second = second with { Decision = second.Decision! with { Payload = ((PaymentPayload)second.Decision.Payload) with {
            UnpaidManaParts = [new("{1}", 1)],
            OptionalPaymentSupport = new("fixed-one", [new("{1}", 1)], OptionalManaPaymentAvailability.Unpayable, false)
        } } };
        var port = new Port(initial, first, nextPriority, second, FiniteResolutionRegression.State(2, completed: 2, revision: 5));
        var session = new ClientSession(port); session.Accept(initial);
        var finite = FiniteResolutionRegression.Arm(initial)!;
        session.AdvanceFinite(finite, (_, _) => false); await Pump(session);
        check(port.Commands is [PassPriority, CancelPayment, PassPriority, CancelPayment]
                && finite.Progress.CompletedCount == 2 && session.OptionalPaymentPolicies.IsEmpty,
            "Captured priority yielding continues through positively unpayable costs without a Resolve/Cancel gesture for each trigger");
        session.Close();
        foreach (var availability in new[] { OptionalManaPaymentAvailability.Unknown, OptionalManaPaymentAvailability.Payable }) {
            finite = FiniteResolutionRegression.Arm(initial)!;
            check(!finite.CanContinue(Payment(availability, revision: 2)),
                "Captured priority-only consent cannot decline a payable or unsupported optional cost");
        }
    }

    private static async Task Pump(ClientSession session)
    {
        var end = DateTime.UtcNow.AddSeconds(5);
        while (session.Busy && DateTime.UtcNow < end) { session.Pump(); await Task.Delay(1); }
        session.Pump(); if (session.Busy) throw new TimeoutException("Optional payment did not release the serial command lane.");
    }

    private sealed class Port(GameSnapshot initial, params GameSnapshot[] observations) : IMatchConnection
    {
        private GameSnapshot current = initial;
        private int index;
        internal List<GameCommand> Commands { get; } = [];
        public Task ExecuteAsync(GameCommand command)
        {
            if (!GameCommandPermissions.Allows(current, command)) throw new InvalidOperationException("Stale optional command");
            Commands.Add(command); return Task.CompletedTask;
        }
        public Task<GameSnapshot?> ObserveAsync()
        {
            current = observations[Math.Min(index++, observations.Length - 1)];
            return Task.FromResult<GameSnapshot?>(current);
        }
        public Task ConcedeAsync(string gameId) => Task.CompletedTask;
    }

    private sealed class Held(GameSnapshot after) : IMatchConnection
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal List<GameCommand> Commands { get; } = [];
        internal int Observations;
        public async Task ExecuteAsync(GameCommand command) { Commands.Add(command); Started.TrySetResult(); await Release.Task; }
        public Task<GameSnapshot?> ObserveAsync() { Observations++; return Task.FromResult<GameSnapshot?>(after); }
        public Task ConcedeAsync(string gameId) => Task.CompletedTask;
    }
}
