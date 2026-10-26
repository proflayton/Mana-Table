using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Mana.AI;
using Mana.Contracts;

internal static class AiFrameworkRegression
{
    private static readonly AiSeatLimits Limits = new(TimeSpan.FromSeconds(10));
    internal static async Task RunAsync(Action<bool, string> check)
    {
        check(typeof(AiSeatSession).Assembly.GetReferencedAssemblies().Where(a => a.Name!.StartsWith("Mana."))
                .Select(a => a.Name).SequenceEqual(["Mana.Contracts"]),
            "The AI framework depends only on neutral contracts, with no Forge, renderer, client UI or updater dependency");
        bool missing = false;
        try { _ = new AiPolicyDescriptor("targeting-only", AiTasks.All.Remove(AiTask.Payment)); }
        catch (ArgumentException) { missing = true; }
        check(missing, "A partial targeting policy cannot claim to be a whole-seat AI policy");
        bool absentCombat = false;
        try { _ = new AiDecisionFrame(State(new AttackPayload(new("Attack", true))) with { Combat = null }); }
        catch (ArgumentException) { absentCombat = true; }
        check(absentCombat, "An enabled completion button cannot stand in for missing combat constraints");
        var tasks = new HashSet<AiTask>();
        foreach (var (payload, answer) in Cases()) {
            var state = State(payload);
            var task = AiTasks.From(payload); tasks.Add(task);
            using var seat = Session(new Immediate(_ => new AiProposal.Answer(answer, "scripted-contract")));
            seat.Observe(new(state, actionCoverage: payload is PriorityPayload or PaymentPayload ? AiActionCoverage.Complete : AiActionCoverage.NotApplicable));
            var result = await seat.EvaluateAsync();
            var handoff = seat.TakeCommand();
            check(result.Status == AiSeatStatus.Ready && handoff?.Command == answer && seat.TakeCommand() == null,
                $"The {task} task preserves the actual typed answer and permits exactly one handoff");
            check(seat.Report(handoff!, AiExecutionReceipt.Accepted) && !seat.Report(handoff!, AiExecutionReceipt.Accepted)
                    && (await seat.EvaluateAsync()).Status == AiSeatStatus.WaitingForObservation,
                $"The {task} engine receipt is recorded once without replaying the same input");
            if (answer is SelectPlayer) {
                using var trace = JsonDocument.Parse(JsonSerializer.Serialize(seat.History));
                check(trace.RootElement.GetProperty("Decisions").EnumerateArray().Last().GetProperty("AnswerEvidence").GetProperty("Player").GetInt32() == 1,
                    "Diagnostic evidence retains the concrete target and scope rather than serializing only the command's base fields");
            }
        }
        check(tasks.SetEquals(AiTasks.All) && tasks.Count == typeof(DecisionPayload).Assembly.GetTypes()
                .Count(type => type.IsSealed && typeof(DecisionPayload).IsAssignableFrom(type)),
            "AI conformance covers every closed engine decision family; a new family requires explicit framework coverage");
        await SourceAuthority(check);
        AdditionalSourceAuthority(check);
        await CancellationAndBudgets(check);
        await ExecutionAndKnowledge(check);
    }

    private static void AdditionalSourceAuthority(Action<bool, string> check)
    {
        var state = State(new PriorityPayload(new("Pass", true)));
        var card = new Card { Key = "stack-source", VisualId = "live-stack-id", Name = "Current stack spell" };
        var occurrence = new ActionSourceOccurrence(state.Decision!.Id, "Stack", state.ViewerId, card);
        var stacked = new StackItem(card.Name, "Public rules", StackActionKind.Spell, card with { Key = "" }) { Id = "stack-item" };
        var offer = new SourceActionSet(state.Id, state.Decision.Id, card.Key, card.VisualId, [new("stack-route", "activate", "Current stack action")]);
        var metadata = new ActionSemantics("stack-route", ActionEffectKind.AddCounters, [ActionEffectKind.AddCounters], [], ActionAffordability.Payable);
        var domain = new DecisionActionDomain(1, ActionDomainCoverage.Complete, [offer], [metadata]);
        var current = state with { Stack = [stacked], ActionSources = [occurrence] };
        var frame = new AiDecisionFrame(current, domain: domain);
        check(frame.Allows(new UseOfferedAction(state.Id, state.Decision.Id, card.Key, "stack-route"))
                && !frame.Allows(new UseOfferedAction(state.Id, state.Decision.Id, card.Key, "invented")),
            "A separately authorized current stack occurrence permits only its exact offered route");
        var historical = state with { Stack = [stacked with { Card = card }], ActionSources = [] };
        check(CardViews.FindActionSource(historical, card.Key) == null
                && !GameCommandPermissions.Allows(historical, new UseOfferedAction(state.Id, state.Decision.Id, card.Key, "stack-route")),
            "A keyed historical stack face still grants no current source authority");
        var revoked = current with { Decision = new("next-input", new(), state.Decision.Payload) };
        check(CardViews.FindActionSource(revoked, card.Key) == null
                && !GameCommandPermissions.Allows(revoked, new UseOfferedAction(state.Id, "next-input", card.Key, "stack-route")),
            "A source occurrence cannot survive replacement of the decision that disclosed it");
        var sideboard = current with { Stack = [], ActionSources = [occurrence with { Zone = "Sideboard" }] };
        check(new AiDecisionFrame(sideboard, domain: domain).Allows(new UseOfferedAction(state.Id, state.Decision.Id, card.Key, "stack-route"))
                && CardViews.FindActionSource(sideboard with { ActionSources = [occurrence with { Zone = "Sideboard", PlayerId = 1 }] }, card.Key) == null,
            "An explicit sideboard source belongs to the observing seat instead of opening another player's hidden sideboard");
        foreach (var malformed in new[] {
            current with { ActionSources = [occurrence, occurrence] },
            current with { ActionSources = [occurrence with { DecisionId = "old-input" }] },
            current with { Stack = [stacked with { Kind = StackActionKind.TriggeredAbility }] },
            current with { ActionSources = [occurrence with { Card = card with { Selectable = true } }] }
        }) {
            bool rejected = false;
            try { _ = new AiDecisionFrame(malformed, domain: domain); } catch (ArgumentException) { rejected = true; }
            check(rejected, "Malformed, stale or uncorroborated additional source authority is rejected before policy evaluation");
        }
        bool invalidMetadata = false;
        try { _ = new AiDecisionFrame(current, domain: domain with { Actions = [metadata with { Costs = default }] }); }
        catch (ArgumentException) { invalidMetadata = true; }
        check(invalidMetadata, "Malformed structured action metadata is rejected before entering a policy's cost evaluation");
    }

    private static async Task SourceAuthority(Action<bool, string> check)
    {
        var state = State(new PriorityPayload(new("Pass", true)));
        var offer = new SourceActionSet(state.Id, state.Decision!.Id, "source-key", "source-id", [new("offered", "cast", "Cast the source")]);
        var partial = new AiDecisionFrame(state, sourceActions: [offer]);
        check(partial.Allows(new UseOfferedAction(state.Id, state.Decision.Id, "source-key", "offered"))
                && !partial.Allows(new UseOfferedAction(state.Id, state.Decision.Id, "source-key", "invented"))
                && !partial.Allows(new PlayCard(state.Id, state.Decision.Id, "source-key"))
                && !partial.Allows(new PassPriority(state.Id, state.Decision.Id)),
            "Partial action discovery permits an exact offered route but cannot authorize invented actions, unspecified play clicks or automatic passing");
        bool forged = false;
        try { _ = new AiDecisionFrame(state, sourceActions: [offer with { SourceCardId = "another-occurrence" }]); }
        catch (ArgumentException) { forged = true; }
        check(forged, "A source catalog cannot transfer action authority from another occurrence");
        var policy = new Controlled();
        using var seat = Session(policy);
        seat.Observe(partial); var first = seat.EvaluateAsync(); var old = await policy.Request(1);
        var replacement = new AiDecisionFrame(state, offerVersion: 1, sourceActions: [offer with { Actions = [new("replacement", "cast", "Current route")] }]);
        seat.Observe(replacement); var next = seat.EvaluateAsync(); var current = await policy.Request(2);
        old.Result.SetResult(new AiProposal.Answer(new UseOfferedAction(state.Id, state.Decision.Id, "source-key", "offered"), "old"));
        current.Result.SetResult(new AiProposal.Answer(new UseOfferedAction(state.Id, state.Decision.Id, "source-key", "replacement"), "current"));
        await first; await next;
        check(seat.TakeCommand()?.Command is UseOfferedAction { ActionId: "replacement" }
                && !seat.Observe(partial) && seat.History.Decisions.Any(record => record.Outcome == AiDecisionOutcome.Cancelled
                    && record.Stamp.OfferVersion == 0),
            "A refreshed offer version revokes the previous catalog even when the game/input revision is unchanged");
        using var invented = Session(new Immediate(_ => new AiProposal.Answer(new SelectPlayer(state.Id, state.Decision.Id, 99), "fabricated-target")));
        invented.Observe(partial);
        check((await invented.EvaluateAsync()).Status == AiSeatStatus.Invalid && invented.TakeCommand() == null,
            "Policy output outside the current typed task and candidate constraints never reaches the connection");
        using var pass = Session(new Immediate(_ => new AiProposal.Answer(new PassPriority(state.Id, state.Decision.Id), "no-known-action")));
        pass.Observe(new(state with { Decision = new(state.Decision.Id, new(), new PriorityPayload(new("Pass", true), CanAutoPass: true)) }));
        check((await pass.EvaluateAsync()).Status == AiSeatStatus.Invalid && pass.TakeCommand() == null,
            "An auto-pass hint is not promoted into a complete AI action space");
    }

    private static async Task CancellationAndBudgets(Action<bool, string> check)
    {
        var clock = new Clock(); var policy = new Controlled();
        using var seat = Session(policy, clock, new(TimeSpan.FromSeconds(5), workUnits: 3));
        var state = State(new OpeningHandPayload(new("Keep", true), new("Mulligan", true)));
        var original = new AiDecisionFrame(state);
        check(seat.Observe(original) && !seat.Observe(original), "Identical observations do not start another AI evaluation");
        var first = seat.EvaluateAsync(); var before = await policy.Request(1);
        check(ReferenceEquals(first, seat.EvaluateAsync()) && before.Context.Work.TrySpend(2),
            "Concurrent evaluation requests share one computation and one bounded work allowance");
        clock.Advance(TimeSpan.FromSeconds(2));
        var refreshed = new AiDecisionFrame(state with { Revision = 2, BoardRevision = 2 });
        seat.Observe(refreshed); var second = seat.EvaluateAsync(); var after = await policy.Request(2);
        check(after.Context.RemainingTime <= TimeSpan.FromSeconds(3) && after.Context.Seed == before.Context.Seed
                && ReferenceEquals(before.Context.Work, after.Context.Work) && !after.Context.Work.TrySpend(2),
            "A refreshed input preserves its original deadline, deterministic seed and consumed work instead of renewing the budget");
        before.Result.SetResult(new AiProposal.Answer(new KeepOpeningHand(state.Id, state.Decision!.Id), "stale"));
        clock.Advance(TimeSpan.FromSeconds(4));
        after.Result.SetResult(new AiProposal.Answer(new KeepOpeningHand(state.Id, state.Decision!.Id), "late"));
        await first; await second;
        check(seat.View.Status == AiSeatStatus.BudgetExceeded && seat.TakeCommand() == null,
            "Both stale and over-budget answers lose authority even when the policy ignored cancellation");

        var hanging = new Controlled();
        using var timed = Session(hanging, limits: new(TimeSpan.FromMilliseconds(80)));
        timed.Observe(original);
        var timeout = timed.EvaluateAsync(); var blocked = await hanging.Request(1);
        check((await timeout.WaitAsync(TimeSpan.FromSeconds(3))).Status == AiSeatStatus.BudgetExceeded && timed.TakeCommand() == null,
            "An unresponsive policy cannot hold the decision await indefinitely or gain an automatic fallback action");
        blocked.Result.SetResult(new AiProposal.Answer(new KeepOpeningHand(state.Id, state.Decision!.Id), "too-late"));

        var closing = new Controlled(); var closed = Session(closing);
        closed.Observe(original); var wait = closed.EvaluateAsync(); var inFlight = await closing.Request(1);
        closed.Dispose(); inFlight.Result.SetResult(new AiProposal.Answer(new KeepOpeningHand(state.Id, state.Decision!.Id), "closed"));
        await wait;
        check(closed.View.Status == AiSeatStatus.Closed && closed.TakeCommand() == null,
            "Closing a seat revokes pending model work and never sends a last command");
    }

    private static async Task ExecutionAndKnowledge(Action<bool, string> check)
    {
        var state = State(new OpeningHandPayload(new("Keep", true), new("Mulligan", true)));
        var policy = new Immediate(_ => new AiProposal.Answer(new KeepOpeningHand(state.Id, state.Decision!.Id), "keep"));
        using var seat = Session(policy, limits: new(TimeSpan.FromSeconds(10), historyCapacity: 2));
        seat.Observe(new(state)); await seat.EvaluateAsync(); var taken = seat.TakeCommand()!;
        seat.Report(taken, AiExecutionReceipt.Unknown);
        var next = state with { Revision = 2, BoardRevision = 2, Decision = new("next", new(), state.Decision!.Payload) };
        seat.Observe(new(next));
        check((await seat.EvaluateAsync()).Status == AiSeatStatus.NeedsRecovery && seat.TakeCommand() == null,
            "A lost execution acknowledgement blocks further AI commands across refreshed observations; it is never retried as a safe rejection");
        bool foreign = false;
        try { seat.Observe(new(next with { ViewerId = 1, DecisionOwnerId = 1 })); } catch (ArgumentException) { foreign = true; }
        check(foreign, "A session cannot accept another seat's private observation");
        bool anotherGame = false;
        try { seat.Observe(new(next with { Id = "another-game" })); } catch (ArgumentException) { anotherGame = true; }
        check(anotherGame, "A game change requires a new seat session instead of carrying old private history or handles");

        using var knowledge = Session(new Immediate(_ => new AiProposal.Unavailable("semantic-effects", "No rules guesses.")), limits: new(TimeSpan.FromSeconds(10), historyCapacity: 2));
        ActivityEntry Event(long id) => new(id, 2, "MAIN1", "damage", 1, "PRIVATE-MESSAGE", "opaque-card", "PRIVATE-NAME") {
            Impact = new("damage", 3, CardName: "PRIVATE-IMPACT", PlayerId: 0),
            Cause = new("opaque-cause", "PRIVATE-CAUSE", "PRIVATE-TEXT", 1, Id: "event-cause") };
        knowledge.Observe(new(state with { Activity = [Event(1), Event(2), Event(3)] }));
        var unavailable = await knowledge.EvaluateAsync();
        knowledge.Observe(new(state with { Revision = 2, BoardRevision = 2, Activity = [Event(1), Event(2), Event(3), Event(4)] }));
        var history = knowledge.History;
        string evidence = JsonSerializer.Serialize(history);
        check(unavailable.Status == AiSeatStatus.Unavailable && knowledge.TakeCommand() == null
                && history.Events.Select(e => e.Id).SequenceEqual([3L, 4L]) && history.Events.All(e => e.Impact?.Amount == 3)
                && !evidence.Contains("PRIVATE-", StringComparison.Ordinal),
            "Seat memory deduplicates and bounds observed facts, strips printed identity/prose, and keeps unsupported policy work explicit");
        check(history.Events.All(e => e.CauseControllerId == 1) && history.Decisions.All(d => d.Outcome != AiDecisionOutcome.Accepted),
            "Only observed event facts establish damage/causes; policy proposals do not manufacture resolved outcomes");

        using var early = Session(policy);
        early.Observe(new(state)); await early.EvaluateAsync(); var original = early.TakeCommand()!;
        long originalSeed = early.History.Decisions.Last().Seed;
        early.Observe(new(next));
        early.Report(original, AiExecutionReceipt.Accepted);
        var receipt = early.History.Decisions.Last();
        check(receipt.Stamp == original.Stamp && receipt.Answer == original.Command && receipt.Task == AiTask.OpeningHand && receipt.Seed == originalSeed
                && early.View.Status == AiSeatStatus.Idle,
            "An observation arriving before its command acknowledgement cannot reassign the receipt to the next input or pretend the effect resolved");

        long firstSeed = 0, otherSeed = 0;
        using var firstSeat = Session(new Immediate(request => { firstSeed = request.Seed; return new AiProposal.Unavailable("fixture", ""); }));
        using var otherSeat = new AiSeatSession(state.Id, 1,
            new Immediate(request => { otherSeed = request.Seed; return new AiProposal.Unavailable("fixture", ""); }), 17, Limits);
        firstSeat.Observe(new(state)); otherSeat.Observe(new(state with { ViewerId = 1, DecisionOwnerId = 1 }));
        await firstSeat.EvaluateAsync(); await otherSeat.EvaluateAsync();
        check(firstSeed != otherSeed && otherSeat.History.Decisions.All(record => record.Stamp.SeatId == 1),
            "A shared game seed still produces independent deterministic seat seeds and private decision histories");
    }

    private static IEnumerable<(DecisionPayload, DecisionCommand)> Cases()
    {
        const string game = "ai-game", decision = "input";
        var yes = new DecisionReply("Continue", true); var no = new DecisionReply("Decline", true);
        var choices = new ChoiceSelection([new(7, "One", null)], new(1, 1));
        yield return (new PriorityPayload(yes), new PassPriority(game, decision));
        yield return (new OpeningHandPayload(yes, no), new KeepOpeningHand(game, decision));
        yield return (new PaymentPayload(yes, no), new PayMana(game, decision));
        yield return (new AttackPayload(yes), new CompleteAttackers(game, decision));
        yield return (new BlockPayload(yes), new CompleteBlockers(game, decision));
        yield return (new TargetSelectionPayload(yes, no, new(1, 1), [1]), new SelectPlayer(game, decision, 1));
        yield return (new EntitySelectionPayload(yes, no, []), new SelectCard(game, decision, "source-key"));
        yield return (new ConfirmationPayload(yes, no), new AcceptConfirmation(game, decision));
        yield return (new AbilityChoicePayload(choices), new ChooseOptions(game, decision, [7]));
        yield return (new OptionsPayload(choices), new ChooseOptions(game, decision, [7]));
        yield return (new OrderingPayload(choices), new OrderOptions(game, decision, [7]));
        yield return (new RevealPayload([]), new AcknowledgeReveal(game, decision));
        yield return (new NumberPayload("2", new(2, 9)), new ChooseNumber(game, decision, 3));
        yield return (new TextPayload(""), new EnterText(game, decision, "Chosen value"));
        yield return (new AllocationPayload(2, AllocationKind.Generic, "counters", false,
            [new(0, "recipient", "Target", null, "", 1, 1, 1, 1, 2)]), new AllocateAmounts(game, decision, [2]));
    }

    private static GameSnapshot State(DecisionPayload payload) => new("ai-game", "playing", null, null, 0, 2, "Main", "MAIN1", 0,
        [new(0, "Seat", 40, true, false, ImmutableDictionary<string, int>.Empty,
            [new("Hand", 1, [new() { Key = "source-key", VisualId = "source-id", Name = "Source", Selectable = true }], null)]),
         new(1, "Other", 40, false, false, ImmutableDictionary<string, int>.Empty, [])], [], null, new("input", new(), payload))
        { Revision = 1, BoardRevision = 1, DecisionOwnerId = 0, PriorityPlayerId = 0,
            Combat = payload is AttackPayload or BlockPayload ? new([], [], null) : null };

    private static AiSeatSession Session(IAiPolicy policy, TimeProvider? time = null, AiSeatLimits? limits = null)
        => new("ai-game", 0, policy, 17, limits ?? Limits, time);
    private sealed class Immediate(Func<AiDecisionRequest, AiProposal> answer) : IAiPolicy
    {
        public AiPolicyDescriptor Descriptor { get; } = new("scripted-conformance.v1", AiTasks.All);
        public Task<AiProposal> EvaluateAsync(AiDecisionRequest request, CancellationToken cancellation) => Task.FromResult(answer(request));
    }
    private sealed class Controlled : IAiPolicy
    {
        private readonly ConcurrentQueue<RequestEntry> requests = new();
        public AiPolicyDescriptor Descriptor { get; } = new("controlled-conformance.v1", AiTasks.All);
        public Task<AiProposal> EvaluateAsync(AiDecisionRequest request, CancellationToken cancellation)
        {
            var entry = new RequestEntry(request, new(TaskCreationOptions.RunContinuationsAsynchronously)); requests.Enqueue(entry); return entry.Result.Task;
        }
        internal async Task<RequestEntry> Request(int count)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (requests.Count < count) await Task.Delay(1, deadline.Token);
            return requests.ToArray()[count - 1];
        }
    }
    private sealed record RequestEntry(AiDecisionRequest Context, TaskCompletionSource<AiProposal> Result);
    private sealed class Clock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        internal void Advance(TimeSpan elapsed) => ticks += elapsed.Ticks;
    }
}
