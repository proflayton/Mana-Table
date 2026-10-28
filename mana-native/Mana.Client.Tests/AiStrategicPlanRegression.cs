using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Mana.AI;
using Mana.Contracts;

internal static class AiStrategicPlanRegression
{
    private static readonly AiSeatLimits Limits = new(TimeSpan.FromSeconds(10), 4096);

    internal static async Task RunAsync(Action<bool, string> check)
    {
        await ReceiptAndObservation(check);
        await CarryAcrossCombat(check);
        await RejectedAndInvalid(check);
        await CancelledAndLate(check);
        await ResourceLifetime(check);
        await ReplayMemory(check);
    }

    private static async Task CarryAcrossCombat(Action<bool, string> check)
    {
        foreach (var (replace, allowed) in new (Func<AiResourceReservation, AiResourceReservation>, bool)[] {
            (prior => prior with { ManaSymbols = prior.ManaSymbols.ToArray().ToImmutableArray(),
                ManaSourceIds = prior.ManaSourceIds.ToArray().ToImmutableArray() }, true),
            (prior => prior with { StableRouteId = "invented-during-combat" }, false),
            (prior => prior with { ManaSourceIds = [] }, false),
            (prior => prior with { GenericMana = 1 }, false)
        }) {
            using var session = Seat(new Immediate(request => request.Frame.Task != AiTask.Attack ? Hold(request)
                : Answer(request) with { PlanTransition = new(request.PlanVersion, Plan(request) with {
                    Purpose = AiPlanPurpose.Defend, Reservations = [replace(request.Plan!.Reservations.Single())] }) }));
            session.Observe(Frame()); await session.EvaluateAsync(); session.Report(session.TakeCommand()!, AiExecutionReceipt.Accepted);
            session.Observe(Frame(2, payload: new AttackPayload(new("Attack", true)), omitOffers: true));
            await session.EvaluateAsync();
            var command = session.TakeCommand();
            if (allowed) {
                bool accepted = command is { Command: CompleteAttackers } ticket && session.Report(ticket, AiExecutionReceipt.Accepted);
                check(accepted && session.Plan is { Version: 2, Purpose: AiPlanPurpose.Defend, Reservations.Length: 1 },
                    "Combat planning can carry an equal held response while adding a defensive intention without needing a current response offer");
            } else check(command == null && session.View.Status == AiSeatStatus.Invalid && session.Plan is { Version: 1 },
                "A combat-only observation cannot invent a new response route, bill or funding witness by modifying carried strategic memory");
        }
    }

    private static async Task ReceiptAndObservation(Action<bool, string> check)
    {
        AiDecisionRequest? latest = null;
        using var session = Seat(new Immediate(request => { latest = request; return Hold(request); }));
        var first = Frame(); session.Observe(first);
        await session.EvaluateAsync();
        check(session.Plan == null && session.PlanVersion == 0 && session.View.Status == AiSeatStatus.Ready,
            "Evaluating a strategic plan creates a proposal without mutating the seat's retained intention");
        var submission = session.TakeCommand()!;
        check(session.Plan == null && session.History.Decisions.All(record => !record.PlanTransitionApplied),
            "Handing an AI command to its host cannot commit strategic memory before a real execution receipt");
        session.Report(submission, AiExecutionReceipt.Accepted);
        check(session.Plan is { Version: 1, Purpose: AiPlanPurpose.HoldInteraction } && session.PlanVersion == 1
                && session.History.Decisions.Last() is { PlanTransitionApplied: true, PlanVersion: 1, RetainedPlan.Version: 1 }
                && session.History.Events.IsEmpty,
            "An accepted command retains the validated intention once without inventing an observed or resolved effect");
        string memory = JsonSerializer.Serialize(session.Plan);
        check(!memory.Contains("response-offer", StringComparison.Ordinal) && !memory.Contains("response-key", StringComparison.Ordinal)
                && memory.Contains("response-occurrence", StringComparison.Ordinal) && memory.Contains("counter-route", StringComparison.Ordinal),
            "A held response keeps occurrence identity, semantic route and required resources without retaining executable action handles");

        var refreshed = Frame(2); session.Observe(refreshed); await session.EvaluateAsync();
        check(latest?.Plan == session.Plan && latest?.PlanVersion == 1 && latest?.Frame.Stamp == refreshed.Stamp,
            "A later decision receives retained immutable strategy alongside fresh current action authority");
        var second = session.TakeCommand()!;
        session.Report(second, AiExecutionReceipt.Rejected);
        session.Observe(Frame(3, payload: new PaymentPayload(new("Pay", true), new("Cancel", true)), omitOffers: true));
        await session.EvaluateAsync();
        check(latest?.Frame.Task == AiTask.Payment && latest.Plan is { Version: 1 }
                && session.TakeCommand()?.Command is PayMana,
            "A required payment can carry the same held intention without a current response offer or an old priority command");
    }

    private static async Task RejectedAndInvalid(Action<bool, string> check)
    {
        foreach (var receipt in new[] { AiExecutionReceipt.Rejected, AiExecutionReceipt.Unknown }) {
            using var session = Seat(new Immediate(Hold));
            session.Observe(Frame()); await session.EvaluateAsync(); var submission = session.TakeCommand()!;
            session.Report(submission, receipt);
            check(session.Plan == null && session.PlanVersion == 0
                    && !session.History.Decisions.Last().PlanTransitionApplied
                    && (receipt != AiExecutionReceipt.Unknown || session.View.Status == AiSeatStatus.NeedsRecovery),
                $"A {receipt} execution receipt cannot advance strategic memory or pretend its resources were spent");
        }
        foreach (var malformed in new Func<AiDecisionRequest, AiProposal.Answer>[] {
            request => Hold(request) with { PlanTransition = new(9, Plan(request)) },
            request => Hold(request) with { PlanTransition = new(0, Plan(request) with { SeatId = 7 }) },
            request => Hold(request) with { PlanTransition = new(0, Plan(request) with { CreatedFrom = request.Frame.Stamp with { DecisionId = "old-input" } }) },
            request => Hold(request) with { PlanTransition = new(0, Plan(request) with { Reservations = [Reservation() with { SourceCardId = "foreign-card" }] }) },
            request => Hold(request) with { PlanTransition = new(0, Plan(request) with { Reservations = [Reservation() with { StableRouteId = "invented-route" }] }) },
            request => Hold(request) with { PlanTransition = new(0, Plan(request) with { Reservations = [Reservation() with { ManaSourceIds = ["opponent-mana"] }] }) },
            request => Hold(request) with { PlanTransition = new(0, Plan(request) with { Reservations = [Reservation() with { ManaSourceIds = ["own-blocker"] }] }) },
            request => Hold(request) with { PlanTransition = new(0, Plan(request) with { Reservations = [Reservation() with { GenericMana = 9 }] }) },
            request => Hold(request) with { Command = new PassPriority(request.Frame.State.Id, "revoked-input") }
        }) {
            using var session = Seat(new Immediate(request => malformed(request)));
            session.Observe(Frame()); await session.EvaluateAsync();
            check(session.View.Status == AiSeatStatus.Invalid && session.TakeCommand() == null && session.Plan == null,
                "A stale, foreign, fabricated or unoffered plan-bearing answer cannot cross the session handoff boundary");
        }
    }

    private static async Task CancelledAndLate(Action<bool, string> check)
    {
        var delayed = new Controlled();
        using var cancelled = Seat(delayed);
        cancelled.Observe(Frame()); var evaluation = cancelled.EvaluateAsync(); var request = await delayed.Request(1);
        cancelled.Observe(Frame(2));
        request.Result.SetResult(Hold(request.Context)); await evaluation;
        check(cancelled.Plan == null && cancelled.PlanVersion == 0 && cancelled.TakeCommand() == null,
            "A cancelled computation cannot publish strategic memory even if its late answer ignores cancellation");

        using var late = Seat(new Immediate(Hold));
        late.Observe(Frame()); await late.EvaluateAsync(); var handed = late.TakeCommand()!;
        late.Observe(Frame(2, tapMana: true));
        late.Observe(Frame(3));
        late.Report(handed, AiExecutionReceipt.Accepted);
        check(late.Plan == null && late.PlanVersion == 0 && !late.History.Decisions.Last().PlanTransitionApplied,
            "A late accepted receipt cannot resurrect a proposed resource reservation after its funding was invalidated and later returned");

        using var validLate = Seat(new Immediate(Hold));
        validLate.Observe(Frame()); await validLate.EvaluateAsync(); var valid = validLate.TakeCommand()!;
        validLate.Observe(Frame(2)); validLate.Report(valid, AiExecutionReceipt.Accepted);
        check(validLate.Plan is { Version: 1 } && validLate.History.Decisions.Last().Stamp == valid.Stamp,
            "An early observation with unchanged resources permits its eventual accepted intention without reassigning the receipt's original decision");

        using var closed = Seat(new Immediate(Hold));
        closed.Observe(Frame()); await closed.EvaluateAsync(); var abandoned = closed.TakeCommand()!;
        closed.Dispose();
        check(!closed.Report(abandoned, AiExecutionReceipt.Accepted) && closed.Plan == null && closed.View.Status == AiSeatStatus.Closed,
            "Closing a seat revokes every outstanding plan-bearing command and releases retained strategy");
    }

    private static async Task ResourceLifetime(Action<bool, string> check)
    {
        foreach (var next in new[] {
            Frame(2, tapMana: true), Frame(2, responseZone: "Exile"), Frame(2, removeMana: true),
            Frame(2, transferMana: true), Frame(2, turn: 8), Frame(2, tapBlocker: true)
        }) {
            using var session = Seat(new Immediate(Hold));
            session.Observe(Frame()); await session.EvaluateAsync(); session.Report(session.TakeCommand()!, AiExecutionReceipt.Accepted);
            var previous = session.Plan!;
            session.Observe(next);
            check(session.Plan == null && session.PlanVersion == 2 && previous is { Version: 1, Reservations.Length: 1 },
                "Authoritative resource or turn changes invalidate retained reservations with a new memory version while old snapshots stay immutable");
        }
        using var pool = Seat(new Immediate(request => Hold(request) with { PlanTransition = new(request.PlanVersion,
            Plan(request) with { Reservations = [Reservation() with { ManaSourceIds = [], PoolMana = Mana(("U", 1)) }] }) }));
        pool.Observe(Frame(poolBlue: 1)); await pool.EvaluateAsync(); pool.Report(pool.TakeCommand()!, AiExecutionReceipt.Accepted);
        pool.Observe(Frame(2));
        check(pool.Plan == null && pool.PlanVersion == 2,
            "Consuming reserved floating mana invalidates its held response even when every permanent remains untapped");

        using var rejected = Seat(new Immediate(Hold));
        rejected.Observe(Frame()); await rejected.EvaluateAsync(); rejected.Report(rejected.TakeCommand()!, AiExecutionReceipt.Accepted);
        rejected.Observe(Frame(2)); await rejected.EvaluateAsync(); var pending = rejected.TakeCommand()!;
        rejected.Observe(Frame(3, tapMana: true)); rejected.Observe(Frame(4));
        rejected.Report(pending, AiExecutionReceipt.Accepted);
        check(rejected.Plan == null && rejected.PlanVersion == 2 && !rejected.History.Decisions.Last().PlanTransitionApplied,
            "An outstanding older transition cannot overwrite the version created by authoritative invalidation of retained memory");

        using var clear = Seat(new Immediate(request => request.Plan == null ? Hold(request)
            : Answer(request) with { PlanTransition = new(request.PlanVersion, null) }));
        clear.Observe(Frame()); await clear.EvaluateAsync(); clear.Report(clear.TakeCommand()!, AiExecutionReceipt.Accepted);
        clear.Observe(Frame(2)); await clear.EvaluateAsync(); clear.Report(clear.TakeCommand()!, AiExecutionReceipt.Accepted);
        check(clear.Plan == null && clear.PlanVersion == 2 && clear.History.Decisions.Last().PlanTransitionApplied,
            "A policy can explicitly abandon a held intention through the same accepted, versioned transition as creating one");

        using var invalidObservation = Seat(new Immediate(Hold));
        invalidObservation.Observe(Frame()); await invalidObservation.EvaluateAsync();
        invalidObservation.Report(invalidObservation.TakeCommand()!, AiExecutionReceipt.Accepted);
        bool chronological = false;
        try { invalidObservation.Observe(Frame(2, turn: 6)); } catch (ArgumentException) { chronological = true; }
        check(chronological && invalidObservation.Plan is { Version: 1 } && invalidObservation.PlanVersion == 1
                && invalidObservation.Observe(Frame(2)),
            "Rejecting backward-turn knowledge cannot mutate retained strategy, revoke its current frame or advance the session's observation watermark");
    }

    private static async Task ReplayMemory(Action<bool, string> check)
    {
        AiDecisionReplayCase? recorded = null;
        var policy = new Immediate(request => {
            if (request.Plan != null) recorded = AiDecisionReplayCase.Capture("held-answer", "strategic-memory-probe.v1", request);
            return Hold(request);
        });
        using var live = Seat(policy);
        live.Observe(Frame()); await live.EvaluateAsync(); live.Report(live.TakeCommand()!, AiExecutionReceipt.Accepted);
        live.Observe(Frame(2)); await live.EvaluateAsync();
        var restored = JsonSerializer.Deserialize<AiDecisionReplayCase>(JsonSerializer.Serialize(recorded!))!;
        var replay = await AiDecisionReplay.EvaluateAsync(restored, new Immediate(request => request.Plan is { Purpose: AiPlanPurpose.HoldInteraction, Version: 1 }
                && request.PlanVersion == 1 && request.Plan.Reservations.Single().ManaSourceIds.SequenceEqual(["own-mana"])
            ? Hold(request) : new AiProposal.Unavailable("retained-plan", "This comparison requires its captured strategic memory.")));
        check(replay.Status == AiSeatStatus.Ready && replay.Evidence is { PlanVersion: 1, RetainedPlan.Version: 1,
                PlanTransitionApplied: false, ProposedPlanTransition.ExpectedVersion: 1, ProposedPlanTransition.Next.Version: 2 },
            "Private JSON replay restores the exact retained strategy and memory version through production session validation without claiming acceptance");
        bool rejected = false;
        try { await AiDecisionReplay.EvaluateAsync(restored with { Plan = restored.Plan! with { SeatId = 7 } }, policy); }
        catch (ArgumentException) { rejected = true; }
        check(rejected, "A private replay cannot import another seat's retained strategic plan into its captured observation");
        rejected = false;
        try { await AiDecisionReplay.EvaluateAsync(restored with { PlanVersion = 0 }, policy); }
        catch (ArgumentException) { rejected = true; }
        check(rejected, "Replay rejects a retained plan whose optimistic memory version does not match the captured session");
    }

    private static AiProposal.Answer Answer(AiDecisionRequest request)
        => new(request.Frame.Task switch {
            AiTask.Payment => new PayMana(request.Frame.State.Id, request.Frame.Stamp.DecisionId),
            AiTask.Attack => new CompleteAttackers(request.Frame.State.Id, request.Frame.Stamp.DecisionId),
            _ => new PassPriority(request.Frame.State.Id, request.Frame.Stamp.DecisionId)
        }, "strategic-memory-probe");
    private static AiProposal.Answer Hold(AiDecisionRequest request)
        => Answer(request) with { PlanTransition = new(request.PlanVersion, Plan(request)) };
    private static AiTurnPlan Plan(AiDecisionRequest request)
        => new(request.Frame.State.Id, request.Frame.Stamp.SeatId, request.Frame.State.Turn, request.PlanVersion + 1,
            AiPlanPurpose.HoldInteraction, [Reservation()], ["own-blocker"]) { CreatedFrom = request.Frame.Stamp };
    private static AiResourceReservation Reservation()
        => new("response-occurrence", "counter-route", ["U"], 0) { SourceZone = "Hand", ManaSourceIds = ["own-mana"] };
    private static AiSeatSession Seat(IAiPolicy policy) => new("strategic-game", 0, policy, 27, Limits);
    private static ImmutableDictionary<string, int> Mana(params (string Color, int Count)[] entries)
        => entries.ToImmutableDictionary(entry => entry.Color, entry => entry.Count);

    private static AiDecisionFrame Frame(long revision = 1, bool tapMana = false, string responseZone = "Hand", bool removeMana = false,
        bool transferMana = false, int turn = 7, bool tapBlocker = false, int poolBlue = 0, DecisionPayload? payload = null, bool omitOffers = false)
    {
        var response = new Card { Key = "response-key-" + revision, VisualId = "response-occurrence", Type = "Instant", Name = "Held answer" };
        var source = new Card { Key = "mana-key-" + revision, VisualId = "own-mana", Type = "Land", Tapped = tapMana };
        var blocker = new Card { VisualId = "own-blocker", CombatId = "own-blocker", Type = "Creature", Power = 2, Toughness = 2, Tapped = tapBlocker };
        var opponentMana = new Card { VisualId = "opponent-mana", Type = "Land" };
        var battlefield = new[] { blocker }.Concat(removeMana || transferMana ? [] : new[] { source }).ToImmutableArray();
        var otherBattlefield = new[] { opponentMana }.Concat(transferMana ? new[] { source } : []).ToImmutableArray();
        var state = new GameSnapshot("strategic-game", "playing", null, null, 0, turn, "Main", "MAIN1", 0,
            [new(0, "Actor", 40, true, false, poolBlue > 0 ? Mana(("U", poolBlue)) : Mana(),
                [new("Battlefield", battlefield.Length, battlefield, null), new(responseZone, 1, [response], null)]),
             new(7, "Other", 40, false, false, Mana(), [new("Battlefield", otherBattlefield.Length, otherBattlefield, null)])], [], null,
            new("input-" + revision, new(), payload ?? new PriorityPayload(new("Pass", true)))) {
                Revision = revision, BoardRevision = revision, DecisionOwnerId = 0, PriorityPlayerId = 0,
                Combat = payload is AttackPayload or BlockPayload ? new([], [], null) : null };
        var offer = new SourceActionSet(state.Id, state.Decision!.Id, response.Key, response.VisualId,
            [new("response-offer-" + revision, "cast", "Cast held answer")]);
        var semantics = new ActionSemantics(offer.Actions[0].Id, ActionEffectKind.CounterSpell, [ActionEffectKind.CounterSpell], [], ActionAffordability.Payable) {
            StableId = "counter-route", Costs = [new(ActionCostKind.Mana, 0, false, "") { ManaSymbols = ["U"] }] };
        var sources = ImmutableArray.CreateBuilder<SourceActionSet>(); sources.Add(offer);
        var actions = ImmutableArray.CreateBuilder<ActionSemantics>(); actions.Add(semantics);
        if (!removeMana && !transferMana) {
            var manaOffer = new SourceActionSet(state.Id, state.Decision.Id, source.Key, source.VisualId,
                [new("blue-offer-" + revision, "activate", "Produce blue mana")]);
            sources.Add(manaOffer);
            actions.Add(new(manaOffer.Actions[0].Id, ActionEffectKind.Mana, [ActionEffectKind.Mana], [new(ActionCostKind.Tap, 1, true, "")], ActionAffordability.Payable) {
                StableId = "blue-route", ManaAbility = true, ManaColors = ["U"], ManaAmount = 1,
                Production = new(SemanticKnowledge.Known, [new(Mana(("U", 1)))]) });
        }
        DecisionActionDomain domain = state.Decision.Payload is PriorityPayload or PaymentPayload
            ? new(1, ActionDomainCoverage.Complete, omitOffers ? [] : sources.ToImmutable(), omitOffers ? [] : actions.ToImmutable()) : DecisionActionDomain.None;
        return new(state, domain: domain);
    }

    private sealed class Immediate(Func<AiDecisionRequest, AiProposal> answer) : IAiPolicy
    {
        public AiPolicyDescriptor Descriptor { get; } = new("strategic-memory-probe.v1", AiTasks.All);
        public Task<AiProposal> EvaluateAsync(AiDecisionRequest request, CancellationToken cancellation) => Task.FromResult(answer(request));
    }
    private sealed class Controlled : IAiPolicy
    {
        private readonly ConcurrentQueue<Entry> requests = new();
        public AiPolicyDescriptor Descriptor { get; } = new("delayed-strategic-probe.v1", AiTasks.All);
        public Task<AiProposal> EvaluateAsync(AiDecisionRequest request, CancellationToken cancellation)
        {
            var entry = new Entry(request, new(TaskCreationOptions.RunContinuationsAsynchronously)); requests.Enqueue(entry); return entry.Result.Task;
        }
        internal async Task<Entry> Request(int count)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (requests.Count < count) await Task.Delay(1, deadline.Token);
            return requests.ToArray()[count - 1];
        }
    }
    private sealed record Entry(AiDecisionRequest Context, TaskCompletionSource<AiProposal> Result);
}
