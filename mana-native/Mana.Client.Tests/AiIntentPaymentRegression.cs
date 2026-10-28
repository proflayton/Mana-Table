using System.Collections.Immutable;
using Mana.AI;
using Mana.Contracts;

internal static class AiIntentPaymentRegression
{
    private static readonly DecisionReply Yes = new("Continue", true), No = new("Not yet", false);

    internal static async Task RunAsync(Action<bool, string> check)
    {
        // The fixture injects an already retained intention. All payment
        // selection, scope validation and accepted memory updates are production.
        using var seat = new AiSeatSession("intent-payment", 0, new RetainedAnswerFixture(), 17,
            new(TimeSpan.FromSeconds(3), 4096));
        var initial = Frame(1, priority: true);
        seat.Observe(initial); await seat.EvaluateAsync(); var held = seat.TakeCommand();
        check(held?.Command is PassPriority && seat.Report(held, AiExecutionReceipt.Accepted)
                && seat.Plan is { Purpose: AiPlanPurpose.HoldInteraction, Reservations.Length: 1 },
            "The payment continuation begins with an accepted seat-owned intention preserving the only blue producer");

        var generic = Frame(2);
        seat.Observe(generic); await seat.EvaluateAsync(); var first = seat.TakeCommand();
        check(first?.Command is UseOfferedAction { Card: "colorless-key-2", ActionId: "mana-colorless-2", ManaOutput: { } output }
                && output.Colors.GetValueOrDefault("C") == 1 && generic.Allows(first.Command)
                && first.ReasonCode == "payment-follow-assignment-source",
            "The current generic bill uses its actual colorless-source witness instead of consuming the blue producer reserved for an answer");
        check(first != null && seat.Report(first, AiExecutionReceipt.Accepted) && seat.Plan?.Reservations.Single().ManaSourceIds.SequenceEqual(["blue"]) == true,
            "Accepting the generic funding command retains the same blue interaction reservation without inventing a new resource");

        // The authoritative next input changes the remaining bill, and the
        // first source is now tapped. A stored nominal intention cannot veto it.
        var changed = Frame(3, unpaid: ["{U}"], colorlessTapped: true);
        seat.Observe(changed); await seat.EvaluateAsync(); var second = seat.TakeCommand();
        check(second?.Command is UseOfferedAction { Card: "blue-key-3", ActionId: "mana-blue-3", ManaOutput: { } blue }
                && blue.Colors.GetValueOrDefault("U") == 1 && changed.Allows(second.Command),
            "A changed current blue bill is paid from the fresh offered blue source instead of reusing the spent colorless offer or refusing the real cost");
        check(second != null && seat.Report(second, AiExecutionReceipt.Accepted)
                && seat.Plan is { Purpose: AiPlanPurpose.Develop, Reservations.IsEmpty: true }
                && seat.PlanVersion == 2 && seat.History.Decisions.Last().PlanTransitionApplied,
            "Funding the changed bill explicitly abandons the old reservation through the accepted versioned memory transition");

        var pool = Frame(2, pool: true);
        var reservation = Reservation(pool: true);
        var plan = new AiTurnPlan(pool.State.Id, 0, pool.State.Turn, 1, AiPlanPurpose.HoldInteraction, [reservation], []) {
            CreatedFrom = pool.Stamp };
        var result = await Replay(pool, plan);
        check(result.Proposal is SpendPoolMana { Color: "C" } && result.Evidence?.ReasonCode == "payment-follow-assignment-pool"
                && result.Evidence.RetainedPlan?.Reservations.Single().PoolMana.GetValueOrDefault("U") == 1,
            "A current generic pool payment subtracts reserved blue units and spends an authorized colorless unit");
        var blueBill = Frame(3, unpaid: ["{U}"], pool: true);
        var abandoned = await Replay(blueBill, plan);
        check(abandoned.Proposal is SpendPoolMana { Color: "U" }
                && abandoned.Evidence?.ProposedPlanTransition?.Next is { Purpose: AiPlanPurpose.Develop, Reservations.IsEmpty: true },
            "A current blue pool bill can consume the previously reserved unit only alongside an explicit abandonment proposal");
    }

    private static Task<AiDecisionReplayResult> Replay(AiDecisionFrame frame, AiTurnPlan plan)
        => AiDecisionReplay.EvaluateAsync(new(1, "intent-payment-pool", "casual-commander.v3", frame.State, frame.Domain,
            frame.Semantics, new([], []), 17) { Plan = plan, PlanVersion = 1, RemainingWork = 4096 }, new CasualCommanderPolicy());

    private static AiResourceReservation Reservation(bool pool = false)
        => new("held-answer", "counter-route", ["{U}"], 0) { SourceZone = "Hand",
            ManaSourceIds = pool ? [] : ["blue"], PoolMana = pool ? Mana(("U", 1)) : Mana() };

    private sealed class RetainedAnswerFixture : IAiPolicy
    {
        private readonly CasualCommanderPolicy payment = new();
        public AiPolicyDescriptor Descriptor => payment.Descriptor;
        public Task<AiProposal> EvaluateAsync(AiDecisionRequest request, CancellationToken cancellation)
        {
            if (request.Frame.Task != AiTask.Priority) return payment.EvaluateAsync(request, cancellation);
            return Task.FromResult<AiProposal>(new AiProposal.Answer(new PassPriority(request.Frame.State.Id, request.Frame.Stamp.DecisionId), "fixture-retain-current-answer") {
                PlanTransition = new(request.PlanVersion, new(request.Frame.State.Id, 0, request.Frame.State.Turn, request.PlanVersion + 1,
                    AiPlanPurpose.HoldInteraction, [Reservation()], []) { CreatedFrom = request.Frame.Stamp }) });
        }
    }

    private static AiDecisionFrame Frame(long revision, bool priority = false, ImmutableArray<string> unpaid = default,
        bool colorlessTapped = false, bool pool = false)
    {
        var response = Card("held-answer", revision, "Instant") with { ManaCost = "{U}" };
        var development = Card("development", revision, "Sorcery");
        var blue = Card("blue", revision, "Land");
        var colorless = Card("colorless", revision, "Artifact") with { Tapped = colorlessTapped };
        var battlefield = pool ? ImmutableArray<Card>.Empty : [blue, colorless];
        var state = new GameSnapshot("intent-payment", "playing", null, null, 0, 7, "Main", "MAIN1", 0,
            [new(0, "Actor", 40, true, false, pool ? Mana(("U", 1), ("C", 1)) : Mana(),
                [new("Hand", 2, [response, development], null), new("Battlefield", battlefield.Length, battlefield, null)]),
             new(1, "Opponent", 40, false, false, Mana(), [])], [], null,
            new("input-" + revision, priority ? new() : new(Source: new("development")),
                priority ? new PriorityPayload(Yes) : new PaymentPayload(No, Yes))) {
            Revision = revision, BoardRevision = revision, DecisionOwnerId = 0, PriorityPlayerId = priority ? 0 : null };
        var offers = new List<(Card Card, SourceAction Offer, ActionSemantics Facts)>();
        if (priority) offers.Add((response, new("counter-offer", "cast", "Localized answer"),
            new("counter-offer", ActionEffectKind.CounterSpell, [ActionEffectKind.CounterSpell],
                [new(ActionCostKind.Mana, 0, false, "") { ManaSymbols = ["{U}"] }], ActionAffordability.Payable) {
                StableId = "counter-route", ManaCostKnowledge = SemanticKnowledge.Known }));
        if (!pool) foreach (var source in battlefield.Where(card => !card.Tapped)) {
            string color = source.VisualId == "blue" ? "U" : "C", id = "mana-" + source.VisualId + "-" + revision;
            bool usable = unpaid.IsDefault || unpaid.Contains("{1}") || unpaid.Contains("{" + color + "}");
            offers.Add((source, new(id, "activate", "Localized producer"), new(id, ActionEffectKind.Mana, [ActionEffectKind.Mana],
                [new(ActionCostKind.Tap, 1, true, "")], ActionAffordability.Payable) {
                StableId = "mana-" + source.VisualId, ManaAbility = true, ManaColors = [color], PaymentManaColors = usable ? [color] : [], ManaAmount = 1,
                Production = new(SemanticKnowledge.Known, [new(Mana((color, 1)))]) }));
        }
        var domain = new DecisionActionDomain(revision, ActionDomainCoverage.Complete,
            offers.Select(item => new SourceActionSet(state.Id, state.Decision!.Id, item.Card.Key, item.Card.VisualId, [item.Offer])).ToImmutableArray(),
            offers.Select(item => item.Facts).ToImmutableArray()) { PoolManaColors = pool ? unpaid.IsDefault ? ["U", "C"] : ["U"] : [] };
        return new(state, domain: domain, semantics: priority ? new() : new() {
            UnpaidMana = unpaid.IsDefault ? ["{1}"] : unpaid, Payment = new(PaymentPurpose.SpellCost, 0, "development", false) });
    }

    private static Card Card(string id, long revision, string type)
        => new() { VisualId = id, CombatId = id, Key = id + "-key-" + revision, Name = "Disclosed face", Type = type };
    private static ImmutableDictionary<string, int> Mana(params (string Color, int Count)[] entries)
        => entries.ToImmutableDictionary(entry => entry.Color, entry => entry.Count);
}
