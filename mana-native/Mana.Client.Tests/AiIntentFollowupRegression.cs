using System.Collections.Immutable;
using Mana.AI;
using Mana.Contracts;

internal static class AiIntentFollowupRegression
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        await ForeignMemoryScope(check);
        // The actual golden's 3U then 2G bills can lose all three Forests to
        // generic payment. Ordered IDs force that allocation without a plan;
        // six blue sources include the first land play beside the five set up.
        using var session = new AiSeatSession("commander-followup", 0, new CasualCommanderPolicy(), 17,
            new(TimeSpan.FromSeconds(5), 4096));
        var initial = Frame(1);
        session.Observe(initial); await session.EvaluateAsync(); var first = session.TakeCommand();
        bool accepted = first?.Command is UseOfferedAction { ActionId: "primary-offer-1" }
            && session.Report(first, AiExecutionReceipt.Accepted);
        check(accepted && session.Plan is { Purpose: AiPlanPurpose.Develop, Reservations.Length: 1 } development
                && development.Reservations.Single() is { SourceCardId: "companion", SourceZone: "Command", GenericMana: 2,
                    Purpose: AiReservationPurpose.FollowUp } followup && followup.ManaSymbols.SequenceEqual(["{G}"])
                && followup.ManaSourceIds.Contains("a-green-0"),
            "Choosing a three-generic blue commander retains the currently funded two-generic green companion as a same-turn development intention");

        var tapped = ImmutableHashSet<string>.Empty;
        var unpaid = ImmutableArray.Create("{U}", "{1}", "{1}", "{1}");
        bool funded = true;
        long revision = 2;
        for (int step = 0; step < 4; step++) {
            var paying = Frame(revision++, primaryStarted: true, unpaid: unpaid, tapped: tapped);
            session.Observe(paying); await session.EvaluateAsync(); var answer = session.TakeCommand();
            var producer = answer?.Command is UseOfferedAction command
                ? paying.SourceActions.FirstOrDefault(source => source.SourceCardKey == command.Card) : null;
            if (answer?.Command is not UseOfferedAction payment || producer == null
                || !producer.SourceCardId.StartsWith("z-blue-", StringComparison.Ordinal)
                || payment.ManaOutput?.Colors.GetValueOrDefault("U") != 1 || !paying.Allows(payment)) {
                funded = false; break;
            }
            funded &= session.Report(answer, AiExecutionReceipt.Accepted);
            tapped = tapped.Add(producer.SourceCardId);
            int shard = unpaid.IndexOf("{U}");
            unpaid = unpaid.RemoveAt(shard >= 0 ? shard : 0);
        }
        check(funded && unpaid.IsEmpty && tapped.Count == 4 && session.PlanVersion == 1
                && session.Plan?.Reservations.Single().Purpose == AiReservationPurpose.FollowUp,
            "Fresh authoritative payment frames pay the first commander entirely from blue sources while preserving the green follow-up witness");
        if (!funded) return;

        var complete = Frame(revision++, primaryStarted: true, unpaid: [], tapped: tapped);
        session.Observe(complete); await session.EvaluateAsync(); var paymentCommit = session.TakeCommand();
        bool paid = paymentCommit?.Command is PayMana && session.Report(paymentCommit, AiExecutionReceipt.Accepted);
        var next = Frame(revision, primaryStarted: true, primaryResolved: true, tapped: tapped);
        session.Observe(next); await session.EvaluateAsync(); var companion = session.TakeCommand();
        check(paid && companion?.Command is UseOfferedAction selected && selected.ActionId == "companion-offer-" + revision
                && next.Allows(selected) && session.Report(companion, AiExecutionReceipt.Accepted)
                && session.Plan is { Purpose: AiPlanPurpose.Develop, Reservations.IsEmpty: true },
            "After the first payment and authoritative resolution, the same session selects the companion's fresh cast offer and consumes its follow-up intention without calling it an instant response");
    }

    private static async Task ForeignMemoryScope(Action<bool, string> check)
    {
        foreach (bool counter in new[] { false, true }) {
            var frame = ForeignFrame(counter);
            using var session = new AiSeatSession(frame.State.Id, 0, new CasualCommanderPolicy(), 17,
                new(TimeSpan.FromSeconds(5), 4096));
            session.Observe(frame); var evaluated = await session.EvaluateAsync(); var answer = session.TakeCommand();
            string expected = counter ? "companion-offer-1" : "primary-offer-1";
            check(evaluated.Status == AiSeatStatus.Ready && answer?.Command is UseOfferedAction action
                    && action.ActionId == expected && frame.Allows(action) && session.Report(answer, AiExecutionReceipt.Accepted)
                    && session.Plan?.Reservations.All(reservation => reservation.SourceCardId != "companion") == true,
                counter
                    ? "A currently authorized foreign-exile instant can counter known lethal damage without proposing unsupported owned-source memory or rejecting its legal cast"
                    : "An owned primary cast stays ready when a beneficial foreign-exile follow-up is offered but cannot be retained by the owned-source memory model");
        }
    }

    private static AiDecisionFrame ForeignFrame(bool counter)
    {
        var original = Frame(1);
        var foreign = original.State.Viewer!.Zone("Command").Cards.Single(card => card.VisualId == "companion") with {
            Type = "Instant", Power = null, Toughness = null };
        var pending = Card("pending-source", 1, "Instant") with { Key = "" };
        EffectRecipients Source(string id, int controller) => new(SemanticKnowledge.Known, EffectRecipientBinding.Source, null,
            [new("source:" + id, SemanticEntityKind.Card, id, ControllerId: controller, OwnerId: controller)]);
        var actor = new SemanticEntity("actor", SemanticEntityKind.Player, PlayerId: 0, ControllerId: 0);
        var target = new SemanticEntity("pending-target", SemanticEntityKind.Spell, pending.VisualId, ControllerId: 1) {
            StackItemId = "pending-hazard" };
        var action = new ActionOperation(0, counter ? ActionEffectKind.CounterSpell : ActionEffectKind.Draw) {
            Knowledge = SemanticKnowledge.Known, ActorPlayerId = 0, Sources = Source(foreign.VisualId, 1),
            Recipients = counter ? new(SemanticKnowledge.Known, EffectRecipientBinding.Targets, 0, [])
                : new(SemanticKnowledge.Known, EffectRecipientBinding.Actor, null, [actor]),
            Targets = counter ? new(SemanticKnowledge.Known, 1, 1, [target]) : null,
            Amount = counter ? null : 1, AmountKnowledge = counter ? SemanticKnowledge.Unknown : SemanticKnowledge.Known };
        var hazard = new StackItem("Pending hazard", "Public pending damage", StackActionKind.Spell, pending) {
            Id = "pending-hazard", ControllerId = 1,
            OperationGraph = new(SemanticKnowledge.Known, 0, [new(0, ActionEffectKind.Damage) {
                Knowledge = SemanticKnowledge.Known, ActorPlayerId = 1, Sources = Source(pending.VisualId, 1),
                Recipients = new(SemanticKnowledge.Known, EffectRecipientBinding.Explicit, null, [actor]),
                Amount = 5, AmountKnowledge = SemanticKnowledge.Known }]) };
        var state = original.State with {
            Players = original.State.Players.Select(player => player.Id == 0 ? player with {
                Life = counter ? 5 : player.Life,
                Zones = player.Zones.Select(zone => zone.Name == "Command" ? zone with {
                    Count = 1, Cards = zone.Cards.Where(card => card.VisualId != "companion").ToImmutableArray() } : zone).ToImmutableArray()
            } : player with { Zones = [new("Exile", 1, [foreign], null)] }).ToImmutableArray(),
            Stack = counter ? [hazard] : [],
            PlayAccesses = [new(1, "Exile", foreign.VisualId, [new("foreign-cast-permission", PlayAccessKind.Cast, "", "", "Current cast access", true)])]
        };
        var domain = original.Domain with {
            Sources = original.SourceActions.Where(source => !counter || source.SourceCardId != "primary").ToImmutableArray(),
            Actions = original.Domain.Actions.Where(facts => !counter || facts.ActionId != "primary-offer-1")
                .Select(facts => facts.ActionId == "companion-offer-1" ? facts with {
                    Effect = counter ? ActionEffectKind.CounterSpell : ActionEffectKind.Draw,
                    Effects = [counter ? ActionEffectKind.CounterSpell : ActionEffectKind.Draw],
                    Targeted = counter, TargetsAvailable = true,
                    OperationGraph = new(SemanticKnowledge.Known, 0, [action]) } : facts).ToImmutableArray()
        };
        return new(state, domain: domain);
    }

    private static AiDecisionFrame Frame(long revision, bool primaryStarted = false, bool primaryResolved = false,
        ImmutableArray<string> unpaid = default, ImmutableHashSet<string>? tapped = null)
    {
        tapped ??= ImmutableHashSet<string>.Empty;
        var primary = Card("primary", revision, "Legendary Creature", 5, 5) with { ManaCost = "{3}{U}" };
        var companion = Card("companion", revision, "Legendary Creature", 1, 1) with { ManaCost = "{2}{G}" };
        var mana = Enumerable.Range(0, 3).Select(index => Card("a-green-" + index, revision, "Basic Land - Forest"))
            .Concat(Enumerable.Range(0, 6).Select(index => Card("z-blue-" + index, revision, "Basic Land - Island")))
            .Select(card => card with { Tapped = tapped.Contains(card.VisualId) }).ToImmutableArray();
        bool priority = !primaryStarted || primaryResolved;
        var battlefield = primaryResolved ? mana.Add(primary with { Sick = true }) : mana;
        var commanders = primaryStarted ? ImmutableArray.Create(companion) : [primary, companion];
        var stack = primaryStarted && !primaryResolved ? ImmutableArray.Create(new StackItem("Primary commander", "Public permanent", StackActionKind.Spell,
            primary with { Key = "" }) { Id = "pending-primary", ControllerId = 0, OperationGraph = Permanent(primary) }) : [];
        var state = new GameSnapshot("commander-followup", "playing", null, null, 0, 4, "Main", "MAIN1", 0,
            [new(0, "Actor", 40, true, false, ImmutableDictionary<string, int>.Empty,
                [new("Battlefield", battlefield.Length, battlefield, null), new("Command", commanders.Length, commanders, null),
                 new("Hand", 0, [], null), new("Library", 60, [], null)]) {
                    LandPlaysUsed = 1, LandPlayLimit = 1 },
             new(1, "Opponent", 40, false, false, ImmutableDictionary<string, int>.Empty, [])], stack, null,
            new("followup-input-" + revision, priority ? new() : new(Source: new("primary")),
                priority ? new PriorityPayload(new("Pass", true))
                    : new PaymentPayload(new("Pay", unpaid.IsEmpty), new("Cancel", true)))) {
                Revision = revision, BoardRevision = revision, DecisionOwnerId = 0, PriorityPlayerId = priority ? 0 : null };
        var offers = new List<(Card Card, SourceAction Offer, ActionSemantics Facts)>();
        if (priority) foreach (var commander in commanders) {
            string id = commander.VisualId + "-offer-" + revision;
            offers.Add((commander, new(id, "cast", "Cast the offered commander"),
                new(id, ActionEffectKind.Permanent, [ActionEffectKind.Permanent],
                    [new(ActionCostKind.Mana, commander.VisualId == "primary" ? 3 : 2, false, "") {
                        ManaSymbols = commander.VisualId == "primary" ? ["{U}"] : ["{G}"] }], ActionAffordability.Unknown) {
                    StableId = commander.VisualId + "-route", ManaCostKnowledge = SemanticKnowledge.Unknown,
                    OperationGraph = Permanent(commander) }));
        }
        foreach (var land in mana.Where(card => !card.Tapped)) {
            string color = land.VisualId.StartsWith("a-green-", StringComparison.Ordinal) ? "G" : "U";
            string id = "mana-" + land.VisualId + "-" + revision;
            bool usable = priority || unpaid.Any(symbol => symbol == "{" + color + "}" || symbol == "{1}");
            offers.Add((land, new(id, "activate", "Produce the current mana"),
                new(id, ActionEffectKind.Mana, [ActionEffectKind.Mana], [new(ActionCostKind.Tap, 1, true, "")], ActionAffordability.Payable) {
                    StableId = "mana-" + land.VisualId, ManaAbility = true, ManaAmount = 1, ManaColors = [color],
                    PaymentManaColors = priority ? null : usable ? [color] : [],
                    Production = new(SemanticKnowledge.Known, [new(ImmutableDictionary<string, int>.Empty.Add(color, 1))]) }));
        }
        var domain = new DecisionActionDomain(revision, ActionDomainCoverage.Complete,
            offers.Select(item => new SourceActionSet(state.Id, state.Decision!.Id, item.Card.Key, item.Card.VisualId, [item.Offer])).ToImmutableArray(),
            offers.Select(item => item.Facts).ToImmutableArray());
        return new(state, domain: domain, semantics: priority ? new() : new() {
            UnpaidMana = unpaid, Payment = new(PaymentPurpose.SpellCost, 0, "primary", false) });
    }

    private static ActionOperationGraph Permanent(Card card)
    {
        var source = new SemanticEntity("source:" + card.VisualId, SemanticEntityKind.Card, card.VisualId, ControllerId: 0, OwnerId: 0);
        var binding = new EffectRecipients(SemanticKnowledge.Known, EffectRecipientBinding.Source, null, [source]);
        return new(SemanticKnowledge.Known, 0, [new(0, ActionEffectKind.Permanent) {
            Knowledge = SemanticKnowledge.Known, ActorPlayerId = 0, Sources = binding, Recipients = binding }]);
    }
    private static Card Card(string id, long revision, string type, int? power = null, int? toughness = null)
        => new() { Key = id + "-key-" + revision, VisualId = id, CombatId = id, Name = "Disclosed permanent", Type = type,
            Power = power, Toughness = toughness };
}
