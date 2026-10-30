using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Mana.Client;
using Mana.Contracts;

internal static class ClientDiagnosticRegression
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        await DirectJourney(check);
        await FailureBoundaries(check);
        await RetiredAndForeignReceipts(check);
        await AutomaticJourney(check);
        await SafeProjection(check);
        await PublicationCoalescing(check);
    }

    private static async Task DirectJourney(Action<bool, string> check)
    {
        var number = State(new NumberPayload("2", new(0, 10)));
        var target = number with { Revision = 2, Decision = new("targets", Source(),
            new TargetSelectionPayload(new("Finish", true), new("Cancel", true), new(1, 2), [1]) { Progress = new(1, [1]) }) };
        var port = new Port(target); var client = new ClientSession(port); var events = Track(client);
        client.Accept(number); events.Clear();
        int completed = 0; client.Completed += () => completed++;
        check(!client.Submit(new ChooseNumber(number.Id, "old-decision", 2)) && port.Commands == 0
                && events.Single() is { Stage: ClientDiagnosticStage.LocalDeclined, Failure: ClientDiagnosticFailure.NotOffered },
            "Diagnostics distinguish a locally declined stale choice from a provider dispatch");
        events.Clear();
        check(client.Submit(new ChooseNumber(number.Id, number.Decision!.Id, 2)), "A current numeric cast choice enters the ordinary session lane");
        await Drain(client);
        var attempt = events.Where(item => item.AttemptId != null).ToArray();
        check(attempt.Select(item => item.Stage).SequenceEqual(new[] { ClientDiagnosticStage.Admitted,
                ClientDiagnosticStage.DispatchStarted, ClientDiagnosticStage.ProviderReturned, ClientDiagnosticStage.ProviderObservationReturned })
                && attempt.Select(item => item.AttemptId).Distinct().Count() == 1 && attempt.All(item => item.SubmittedNumber == 2),
            "One numeric attempt records local admission, dispatch, provider return and observation separately");
        check(attempt[0].Facts is { Decision: ClientDiagnosticDecision.Number, DecisionSourceName: "Crackle with Power" }
                && attempt[^1].Facts is { Decision: ClientDiagnosticDecision.Targets, SelectedTargetCount: 1, TargetCompletionOffered: true },
            "A cast journey records the explicit disclosed source portrait, chosen number and authoritative staged-target progress");
        check(events.Last() is { AttemptId: null, Stage: ClientDiagnosticStage.ObservationAccepted }
                && ReferenceEquals(client.State, target) && completed == 1 && port.Commands == 1,
            "Owner-thread publication is an uncorrelated receipt, while normal completion and game state remain intact");
        var journey = events.ToList();

        port.Next = target with { Revision = 3, Decision = new("payment", Source(), Payment()) };
        events.Clear();
        check(client.Submit(new CompleteSelection(target.Id, target.Decision!.Id)), "Explicit target completion remains an ordinary current command");
        await Drain(client);
        journey.AddRange(events);
        var bill = events.Single(item => item.Stage == ClientDiagnosticStage.ProviderObservationReturned).Facts;
        check(bill is { Decision: ClientDiagnosticDecision.Payment, ManaPaymentOffered: true, ManaBillUnknownOrTruncated: false }
                && bill.RemainingMana.SequenceEqual(new[] { new ClientDiagnosticManaPart("{6}", 1), new ClientDiagnosticManaPart("{R}", 2) }),
            "The next payment receipt retains a counted generic and colored bill without expanding it");
        string serializedJourney = JsonSerializer.Serialize(journey);
        check(journey.Any(item => item is { Command: ClientDiagnosticCommand.ChooseNumber, SubmittedNumber: 2 })
                && journey.Any(item => item.Command == ClientDiagnosticCommand.CompleteSelection)
                && serializedJourney.Contains("Crackle with Power", StringComparison.Ordinal)
                && serializedJourney.Contains("SelectedTargetCount", StringComparison.Ordinal)
                && serializedJourney.Contains("RemainingMana", StringComparison.Ordinal)
                && !serializedJourney.Contains("source-id", StringComparison.Ordinal) && !serializedJourney.Contains("targets", StringComparison.Ordinal),
            "A serialized scripted cast reconstructs its source, X choice, target completion and bill without action handles or a snapshot dump");

        events.Clear();
        check(client.Submit(new PayMana(target.Id, "payment")), "An offered Auto payment still dispatches normally");
        await Drain(client);
        check(events.Count(item => item.Stage == ClientDiagnosticStage.ProviderReturned) == 1
                && events.Single(item => item.Stage == ClientDiagnosticStage.ProviderObservationReturned).Facts.RemainingMana.SequenceEqual(bill.RemainingMana)
                && !events.Any(item => item.Stage == ClientDiagnosticStage.ObservationAccepted) && completed == 3 && port.Commands == 3,
            "A returned payment that retains its bill is recorded honestly, without an invented applied result or repeated unchanged publication");
    }

    private static async Task FailureBoundaries(Action<bool, string> check)
    {
        foreach (bool rejected in new[] { true, false }) {
            var state = State(new PriorityPayload(new("Pass", true)));
            var port = new Port(state) { ExecuteError = rejected ? new EngineCommandRejectedException("PRIVATE-REJECTION") : new IOException("PRIVATE-TRANSPORT") };
            var client = new ClientSession(port); var events = Track(client); client.Accept(state); events.Clear();
            int completed = 0; client.Completed += () => completed++;
            check(client.Submit(new PassPriority(state.Id, state.Decision!.Id)), "A failure probe begins with one current scoped command");
            await Drain(client);
            check(events.Any(item => item.Stage == (rejected ? ClientDiagnosticStage.ProviderRejected : ClientDiagnosticStage.OutcomeUnknown))
                    && !events.Any(item => item.Stage is ClientDiagnosticStage.ProviderReturned or ClientDiagnosticStage.ObservationAccepted)
                    && port.Commands == 1 && port.Observations == 0 && completed == 1,
                "Provider rejection and uncertain dispatch failure remain distinct even though both invoke Completed exactly once");
            client.Pump();
            check(port.Commands == 1 && !JsonSerializer.Serialize(events.ToArray()).Contains("PRIVATE-", StringComparison.Ordinal),
                "Failure diagnostics neither replay the command nor include raw exception text");
        }
        var initial = State(new PriorityPayload(new("Pass", true)));
        var observedFailure = new Port(initial) { ObserveError = new EngineCommandRejectedException("PRIVATE-OBSERVATION") };
        var session = new ClientSession(observedFailure); var observedEvents = Track(session); session.Accept(initial); observedEvents.Clear();
        session.Submit(new PassPriority(initial.Id, initial.Decision!.Id)); await Drain(session);
        check(observedEvents.Any(item => item.Stage == ClientDiagnosticStage.ProviderReturned)
                && observedEvents.Any(item => item is { Stage: ClientDiagnosticStage.OutcomeUnknown, Failure: ClientDiagnosticFailure.ObservationFailure })
                && !observedEvents.Any(item => item.Stage == ClientDiagnosticStage.ProviderRejected) && observedFailure.Commands == 1,
            "A rejected-exception type thrown by observation cannot retroactively label the returned command as rejected");
    }

    private static async Task RetiredAndForeignReceipts(Action<bool, string> check)
    {
        foreach (bool close in new[] { false, true }) {
            var state = State(new PriorityPayload(new("Pass", true)));
            var port = new Port(state with { Revision = 2, Decision = new("late", Source("LATE-SECRET"), Payment()) }) { Hold = true };
            var client = new ClientSession(port); var events = Track(client); client.Accept(state); events.Clear();
            client.Submit(new PassPriority(state.Id, state.Decision!.Id)); await port.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (close) client.Close(); else client.Reset();
            events.Clear(); port.Release.TrySetResult(); await port.Observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Until(() => events.Any(item => item.Stage == ClientDiagnosticStage.ProviderObservationReturned));
            if (!close) await Drain(client); else client.Pump();
            var receipt = events.Single(item => item.Stage == ClientDiagnosticStage.ProviderObservationReturned);
            check(receipt is { Failure: ClientDiagnosticFailure.Superseded, Facts.Decision: ClientDiagnosticDecision.Undisclosed }
                    && receipt.Facts.DecisionSourceName == null && receipt.Facts.RemainingMana.IsEmpty
                    && !events.Any(item => item.Stage == ClientDiagnosticStage.ObservationAccepted),
                "Reset and close retain provider-return evidence while redacting retired decision facts and never certifying acceptance");
            if (!close) check(events.Any(item => item.Stage == ClientDiagnosticStage.ObservationDiscarded),
                "A late command receipt rejected by the owner boundary is recorded as a discarded publication");
        }
        var original = State(new PriorityPayload(new("Pass", true)));
        var foreign = new Port(original with { ViewerId = 1, Revision = 2, DecisionOwnerId = 1,
            Decision = new("foreign", Source("FOREIGN-SECRET"), Payment()) });
        var session = new ClientSession(foreign); var captured = Track(session); session.Accept(original); captured.Clear();
        session.Submit(new PassPriority(original.Id, original.Decision!.Id)); await Drain(session);
        check(captured.Where(item => item.Stage is ClientDiagnosticStage.ProviderObservationReturned or ClientDiagnosticStage.ObservationAccepted)
                .All(item => item.Facts is { Decision: ClientDiagnosticDecision.Undisclosed, DecisionSourceName: null }
                    && item.Facts.RemainingMana.IsEmpty),
            "A foreign audience's returned or published snapshot contributes no source portrait, target progress or payment details");
    }

    private static async Task AutomaticJourney(Action<bool, string> check)
    {
        var initial = State(new PriorityPayload(new("Pass", true), CanAutoPass: true, QuietYieldToken: "PRIVATE-TOKEN"));
        var next = initial with { Revision = 2, Decision = new("next", new(), new PriorityPayload(new("Pass", true), CanAutoPass: false)) };
        var port = new Port(next); var client = new ClientSession(port); var events = Track(client); client.Accept(initial); events.Clear();
        check(client.Advance(state => state.Decision?.Payload is PriorityPayload { CanAutoPass: true }, (_, _) => false),
            "Automatic priority uses its existing eligibility and continuation lane");
        await Drain(client);
        check(events.Count(item => item is { Origin: ClientDiagnosticOrigin.Automatic, Stage: ClientDiagnosticStage.DispatchStarted, Command: ClientDiagnosticCommand.AutoPassPriority }) == 1
                && events.Count(item => item.Stage == ClientDiagnosticStage.ProviderObservationReturned) == 1
                && events.Single(item => item.Stage == ClientDiagnosticStage.ObservationAccepted).AttemptId == null && port.Commands == 1,
            "Automatic commands record actual dispatch and returned observation without inventing a causal publication acknowledgement");

        var quietInitial = initial with { ActivePlayerId = 1 };
        var quietPort = new Port(quietInitial with { Revision = 2, Decision = next.Decision });
        var quiet = new ClientSession(quietPort); var quietEvents = Track(quiet); quiet.Accept(quietInitial); quietEvents.Clear();
        check(quiet.AdvanceQuietly("PRIVATE-TOKEN", _ => true, (_, _) => false), "An explicit quiet-window continuation uses its existing token permission");
        await Drain(quiet);
        check(quietEvents.Any(item => item is { Origin: ClientDiagnosticOrigin.Quiet, Stage: ClientDiagnosticStage.ProviderReturned,
                Command: ClientDiagnosticCommand.PassForNow }) && quietPort.Commands == 1
                && !JsonSerializer.Serialize(quietEvents.ToArray()).Contains("PRIVATE-TOKEN", StringComparison.Ordinal),
            "Quiet passing records its real semantic command while omitting the authorization token");

        var payment = FiniteResolutionRegression.State(2, paying: true);
        var optional = (PaymentPayload)payment.Decision!.Payload;
        payment = payment with { Decision = payment.Decision with { Payload = optional with {
            UnpaidManaParts = [new("{1}", 1)],
            OptionalPaymentSupport = new("fixed-one", [new("{1}", 1)], OptionalManaPaymentAvailability.Payable, true)
        } } };
        var paymentPort = new Port(FiniteResolutionRegression.State(2, completed: 1, revision: 2));
        var paymentClient = new ClientSession(paymentPort); var paymentEvents = Track(paymentClient);
        paymentClient.Accept(payment); paymentEvents.Clear();
        check(paymentClient.TrySetOptionalPaymentPolicy(OptionalPaymentMode.AutoPay),
            "An explicitly authorized finite optional-payment policy can be prepared for the diagnostics journey");
        check(paymentClient.AdvanceOptionalPayments((_, _) => false), "The finite optional-payment lane starts normally");
        await Drain(paymentClient);
        check(paymentEvents.Any(item => item is { Origin: ClientDiagnosticOrigin.Finite, Stage: ClientDiagnosticStage.DispatchStarted,
                Command: ClientDiagnosticCommand.AutoPayOptionalPayment }) && paymentPort.Commands == 1,
            "Finite automatic payment commands use the same diagnostics rather than escaping direct-Submit coverage");
    }

    private static async Task SafeProjection(Action<bool, string> check)
    {
        var privateCard = new Card { Name = "PRIVATE-HAND", Key = "PRIVATE-KEY", VisualId = "PRIVATE-ID", Selectable = true };
        var state = State(new TextPayload("PRIVATE-INITIAL")) with {
            Players = [new(0, "PRIVATE-PLAYER", 40, true, false, ImmutableDictionary<string, int>.Empty,
                [new("Hand", 1, [privateCard], null)])],
            Decision = new("PRIVATE-DECISION", new("PRIVATE-MESSAGE", "PRIVATE-TITLE", new("PRIVATE-SOURCE-ID", privateCard with { FaceDown = true })),
                new TextPayload("PRIVATE-INITIAL")),
            Notices = ["PRIVATE-NOTICE"], Activity = [new(1, 1, "MAIN1", "event", 0, "PRIVATE-CHAT", null, null)] };
        var port = new Port(state); var client = new ClientSession(port); var events = Track(client); client.Accept(state); events.Clear();
        client.DiagnosticRecorded += _ => throw new IOException("A diagnostic subscriber failed");
        int laterSubscriber = 0; client.DiagnosticRecorded += _ => laterSubscriber++;
        check(client.Submit(new EnterText(state.Id, state.Decision!.Id, "PRIVATE-TEXT")), "A text decision still executes when a diagnostic subscriber throws");
        await Drain(client);
        string json = JsonSerializer.Serialize(events.ToArray());
        check(port.Commands == 1 && ReferenceEquals(client.State, state) && laterSubscriber == 4
                && !json.Contains("PRIVATE-", StringComparison.Ordinal) && events.All(item => item.Facts.DecisionSourceName == null),
            "Diagnostics isolate every subscriber and exclude hidden portraits, zones, handles, tokens, text, chat and raw contexts");

        var parts = Enumerable.Range(0, 40).Select(_ => new ManaBillPart("{R}", int.MaxValue)).ToImmutableArray();
        var billState = State(Payment() with { UnpaidManaParts = parts.SetItem(0, new("PRIVATE-SYMBOL", 1)) });
        var billPort = new Port(billState); var billClient = new ClientSession(billPort); var bills = Track(billClient); billClient.Accept(billState); bills.Clear();
        billClient.Submit(new PayMana(billState.Id, billState.Decision!.Id)); await Drain(billClient);
        check(bills.All(item => item.Facts.RemainingMana.Length <= 32 && item.Facts.ManaBillUnknownOrTruncated)
                && !JsonSerializer.Serialize(bills.ToArray()).Contains("PRIVATE-SYMBOL", StringComparison.Ordinal)
                && bills.First().Facts.RemainingMana.All(part => part.Count == int.MaxValue),
            "Huge or malformed bills remain counted and bounded, omit unknown symbols, and explicitly retain unknown/truncated status");
        var source = Source(new string('A', 161));
        var oversized = State(new NumberPayload("2", new(0, 3))) with { Decision = new("number", source, new NumberPayload("2", new(0, 3))) };
        var oversizedClient = new ClientSession(new Port(oversized)); var oversizedEvents = Track(oversizedClient); oversizedClient.Accept(oversized);
        check(oversizedEvents.Single().Facts.DecisionSourceName == null, "An oversized explicit source name is omitted rather than copying unbounded text");
    }

    private static async Task PublicationCoalescing(Action<bool, string> check)
    {
        var state = State(new PriorityPayload(new("Pass", true)));
        var port = new Port(state); var client = new ClientSession(port); var events = Track(client); client.Accept(state); events.Clear();
        for (int i = 0; i < 10; i++) client.Accept(state with { });
        client.Poll(false, _ => { }); await Drain(client);
        check(events.IsEmpty, "Identical accepted snapshots and ordinary polling do not consume the bounded diagnostic history");
        client.Accept(state with { Decision = state.Decision! with { Id = "changed" } });
        check(events.Single() is { AttemptId: null, Stage: ClientDiagnosticStage.ObservationAccepted },
            "A genuinely changed decision produces one uncorrelated accepted-publication event");

        var reentrant = new ClientSession(new Port(state)); var publications = Track(reentrant);
        int changed = 0;
        reentrant.Changed += (_, current) => {
            changed++;
            if (current?.Revision == 1) reentrant.Accept(state with { Revision = 2 });
        };
        reentrant.Accept(state);
        check(publications.Select(item => item.Facts.Revision).SequenceEqual(new long?[] { 1, 2 })
                && publications.All(item => item is { AttemptId: null, Stage: ClientDiagnosticStage.ObservationAccepted })
                && changed == 2 && reentrant.State?.Revision == 2,
            "Reentrant Changed callbacks preserve actual publication order in diagnostics without changing callback or replica behavior");
    }

    private static ConcurrentQueue<ClientDiagnostic> Track(ClientSession session)
    {
        var events = new ConcurrentQueue<ClientDiagnostic>(); session.DiagnosticRecorded += events.Enqueue; return events;
    }
    private static GameSnapshot State(DecisionPayload payload) => new("diagnostic-game", "playing", null, null, 0, 1, "Main", "MAIN1", 0,
        [new(0, "You", 40, true, false, ImmutableDictionary<string, int>.Empty, []),
         new(1, "Other", 40, false, false, ImmutableDictionary<string, int>.Empty, [])], [], null,
        new("current", Source(), payload)) { Revision = 1, DecisionOwnerId = 0, PriorityPlayerId = 0 };
    private static DecisionContext Source(string name = "Crackle with Power") => new(Source: new("source-id",
        new Card { Name = name, VisualId = "source-id" }, 0, "Stack"));
    private static PaymentPayload Payment() => new(new("Auto", true), new("Cancel", true)) {
        PayerId = 0, UnpaidManaParts = [new("{6}", 1), new("{R}", 2)]
    };
    private static async Task Drain(ClientSession client)
    {
        await Until(() => { client.Pump(); return !client.Busy && !client.Polling; }); client.Pump();
    }
    private static async Task Until(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!ready()) { if (DateTime.UtcNow >= deadline) throw new TimeoutException("Diagnostic session did not settle"); await Task.Delay(5); }
    }
    private sealed class Port(GameSnapshot next) : IMatchConnection
    {
        internal GameSnapshot Next = next;
        internal Exception? ExecuteError, ObserveError;
        internal bool Hold;
        internal int Commands, Observations;
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Observed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task ExecuteAsync(GameCommand command)
        {
            Interlocked.Increment(ref Commands); Started.TrySetResult();
            if (Hold) await Release.Task;
            if (ExecuteError != null) throw ExecuteError;
        }
        public Task<GameSnapshot?> ObserveAsync()
        {
            Interlocked.Increment(ref Observations); Observed.TrySetResult();
            return ObserveError == null ? Task.FromResult<GameSnapshot?>(Next) : Task.FromException<GameSnapshot?>(ObserveError);
        }
        public Task ConcedeAsync(string gameId) => throw new InvalidOperationException("Diagnostic tests never concede");
    }
}
