using System.Collections.Immutable;
using Mana.AI;
using Mana.Contracts;

internal static class AiIntentPolicyRegression
{
    private static readonly DecisionReply Yes = new("Continue", true);

    internal static async Task RunAsync(Action<bool, string> check)
    {
        await PreserveAnAnswer(check);
        await PendingConsequences(check);
        await ExactPendingOccurrence(check);
        await StackRecipientPurpose(check);
        await PreserveDefense(check);
    }

    private static async Task StackRecipientPurpose(Action<bool, string> check)
    {
        var pending = Damage("purpose-hazard", "purpose-source", 1, 0, 5);
        var state = Priority(1, pending: [pending], ownLife: 5).State;
        var target = Spell(pending);
        var metadata = ImmutableArray.Create(new DecisionOptionSemantics(17, ActionEffectKind.Unknown) { Target = target });
        AiDecisionFrame Choices(DecisionSemantics semantics, int minimum = 0)
            => new(state with { Decision = new("stack-recipient-choice", new(), new OptionsPayload(
                new([new(17, "An offered stack recipient", null)], new(minimum, 1)))) },
                domain: DecisionActionDomain.None, semantics: semantics with { Options = metadata });

        check((await Evaluate(Choices(new(DecisionIntent.Counter)))).Proposal is ChooseOptions counter
                && counter.Choices.SequenceEqual([17]),
            "An optional stack recipient chosen explicitly for countering is valued by preventing its known harmful pending effect");
        check((await Evaluate(Choices(new()))).Proposal is ChooseOptions unknown && unknown.Choices.IsEmpty
                && (await Evaluate(Choices(new(DecisionIntent.Benefit)))).Proposal is ChooseOptions other && other.Choices.IsEmpty,
            "The same optional stack recipient with an unknown or different purpose does not inherit counterspell avoidance value");

        var graph = CounterGraph([target]);
        check((await Evaluate(Choices(new() { OperationGraph = graph, OperationId = 0 }))).Proposal is ChooseOptions active
                && active.Choices.SequenceEqual([17]),
            "An exact active known counter operation supplies recipient purpose even when the legacy decision intent is absent");
        var draw = Known(1, ActionEffectKind.Draw, 0,
            new(SemanticKnowledge.Known, EffectRecipientBinding.Actor, null, [PlayerEntity(0)]), 1);
        var sequence = graph with { Operations = [graph.Operations[0] with { Links = [new(1, OperationLinkKind.Sequence)] }, draw] };
        check((await Evaluate(Choices(new() { OperationGraph = sequence, OperationId = 1 }))).Proposal is ChooseOptions downstream
                && downstream.Choices.IsEmpty,
            "A counter elsewhere in a sequence cannot establish the purpose of a different active operation's stack-recipient choice");
        var required = Choices(new(), minimum: 1);
        var fallback = await Evaluate(required);
        check(fallback.Proposal is ChooseOptions legal && legal.Choices.SequenceEqual([17]) && required.Allows(legal),
            "An unknown required stack-recipient purpose still supplies a legal minimum answer without pretending its effect was understood");
    }

    private static async Task PreserveAnAnswer(Action<bool, string> check)
    {
        var one = Priority(1, includeDraw: true);
        var held = await Evaluate(one);
        check(held.Status == AiSeatStatus.Ready && held.Proposal is PassPriority
                && held.Evidence?.ProposedPlanTransition?.Next is { Purpose: AiPlanPurpose.HoldInteraction, Reservations.Length: 1 },
            "With one blue resource the casual player preserves a known future instant answer instead of spending its last blue on an optional draw");
        var two = await Evaluate(Priority(2, includeDraw: true));
        check(two.Proposal is UseOfferedAction { ActionId: "draw-offer" },
            "A second genuine blue resource permits the same optional draw while retaining the capacity to answer later");

        var lethal = Damage("pending-damage", "damage-source", 1, 0, 5);
        var response = await Evaluate(Priority(1, includeDraw: true, pending: [lethal], ownLife: 5));
        check(response.Proposal is UseOfferedAction { ActionId: "counter-offer" }
                && response.Evidence?.ProposedPlanTransition?.Next is { Purpose: AiPlanPurpose.Respond, Reservations.IsEmpty: true },
            "Facing known pending lethal damage, the same last blue resource funds the available counter rather than a draw activation");

        var wrongColor = one.State with { Players = one.State.Players.Select(player => player.Id == 0 ? player with {
            Zones = player.Zones.Select(zone => zone.Name == "Battlefield" ? zone with {
                Cards = zone.Cards.Where(card => card.Type != "Land").ToImmutableArray(), Count = zone.Cards.Count(card => card.Type != "Land") } : zone).ToImmutableArray(),
            Mana = ImmutableDictionary<string, int>.Empty.Add("C", 2) } : player).ToImmutableArray() };
        var noBlue = PriorityDomain(wrongColor, includeDraw: true, targets: []);
        check((await Evaluate(new(wrongColor, domain: noBlue))).Proposal is PassPriority,
            "Two colorless units neither fund the blue draw nor create an invented blue response reservation");
    }

    private static async Task PendingConsequences(Action<bool, string> check)
    {
        var own = Card("own-value", "Creature", 5, 5);
        var threat = Card("opposing-threat", "Creature", 12, 12);
        var beneficialRemoval = Removal("pending-removal", "removal-source", 1, threat, 2);
        var beneficial = await Evaluate(Priority(1, ownCards: [own], enemyCards: [threat], pending: [beneficialRemoval]));
        check(beneficial.Proposal is PassPriority,
            "An opponent removing another opponent's large threat does not provoke a counter merely because an opposing spell is on the stack");
        var harmfulRemoval = Removal("pending-removal", "removal-source", 1, own, 0);
        var protection = await Evaluate(Priority(1, ownCards: [own], enemyCards: [threat], pending: [harmfulRemoval]));
        check(protection.Proposal is UseOfferedAction { ActionId: "counter-offer" },
            "Changing the committed victim to the actor's own useful creature makes that same counter a meaningful protective response");
        var unknown = beneficialRemoval with { OperationGraph = ActionOperationGraph.Unknown };
        check((await Evaluate(Priority(1, ownCards: [own], enemyCards: [threat], pending: [unknown]))).Proposal is PassPriority,
            "A spell with unknown consequences does not receive a fabricated positive counter value from its controller or printed name");
    }

    private static async Task ExactPendingOccurrence(Action<bool, string> check)
    {
        // Copies can share a physical source while having different committed
        // recipients. Only StackItemId identifies the pending effect to stop.
        var dangerous = Damage("harmful-copy", "shared-source", 1, 0, 5);
        var other = Damage("other-copy", "shared-source", 1, 2, 5);
        var state = Priority(1, pending: [dangerous, other], ownLife: 5).State;
        var harmOnly = CounterGraph([Spell(dangerous)]);
        var otherOnly = CounterGraph([Spell(other)]);
        var saved = CasualActionAssessment.Evaluate(state, harmOnly, 512);
        var wasted = CasualActionAssessment.Evaluate(state, otherOnly, 512);
        check(saved is { Status: CasualAssessmentStatus.Estimated, Value: > 100 }
                && wasted is { Status: CasualAssessmentStatus.Estimated, Value: < 0 },
            "Counter assessment follows the exact committed stack copy rather than reusing the first effect with the same source card");
        var counterOther = new AiDecisionFrame(state, domain: PriorityDomain(state, false, [Spell(other)]));
        var counterDangerous = new AiDecisionFrame(state, domain: PriorityDomain(state, false, [Spell(dangerous)]));
        check((await Evaluate(counterOther)).Proposal is PassPriority
                && (await Evaluate(counterDangerous)).Proposal is UseOfferedAction { ActionId: "counter-offer" },
            "The casual player distinguishes a beneficial copy from the lethal copy even while both same-source spells remain pending");
        var ambiguous = Spell(dangerous) with { Id = "unresolved-spell", StackItemId = "" };
        check(CasualActionAssessment.Evaluate(state, CounterGraph([ambiguous]), 512).Status == CasualAssessmentStatus.Incomplete,
            "A legacy source-only reference shared by two pending copies stays incomplete instead of selecting one by list order");
    }

    private static async Task PreserveDefense(Action<bool, string> check)
    {
        var vulnerable = await Evaluate(Attack());
        check(vulnerable.Proposal is CompleteAttackers
                && vulnerable.Evidence?.ProposedPlanTransition?.Next is { Purpose: AiPlanPurpose.Defend } plan
                && plan.RetainedBlockerIds.Contains("sole-defender"),
            "At five life, the casual player keeps its only blocker against a visible six-power retaliation instead of taking a free three-point attack elsewhere");
        check((await Evaluate(Attack(vigilance: true))).Proposal is ToggleAttackPlayer { Player: 2 },
            "Vigilance allows the same attack because the retained defender can remain available for the next opposing combat");
        check((await Evaluate(Attack(incomingThreat: false))).Proposal is ToggleAttackPlayer { Player: 2 },
            "Removing the visible retaliation threat releases that defensive reservation rather than making the player generically passive");
        check((await Evaluate(Attack(threatKeyword: "flying"))).Proposal is ToggleAttackPlayer { Player: 2 }
                && (await Evaluate(Attack(threatKeyword: "unblockable"))).Proposal is ToggleAttackPlayer { Player: 2 },
            "A creature that cannot block the visible evasive threat is not hoarded as an imaginary way to prevent lethal damage");
        check((await Evaluate(Attack(tappedThreat: true))).Proposal is CompleteAttackers,
            "A currently tapped opposing six-power body still warrants retaining a relevant blocker for its next turn's untap and combat");
        check((await Evaluate(Attack(defenderLife: 1))).Proposal is CompleteAttackers,
            "Eliminating one exposed opponent does not justify opening lethal retaliation from another seat still in the game");
        check((await Evaluate(Attack(tappedThreat: true, defenderLife: 1, finalOpponent: true))).Proposal is ToggleAttackPlayer { Player: 2 },
            "A visible attack that wins against the final opponent can spend a defender because no later opposing turn remains to defend");
        check((await Evaluate(Attack(threatKeyword: "menace"))).Proposal is ToggleAttackPlayer { Player: 2 },
            "One creature is not hoarded as an imaginary legal block against a visible menace attacker");
        var pair = await Evaluate(Attack(threatKeyword: "menace", spareBlockers: 1));
        check(pair.Proposal is CompleteAttackers
                && pair.Evidence?.ProposedPlanTransition?.Next is { Purpose: AiPlanPurpose.Defend } paired
                && paired.RetainedBlockerIds.Contains("sole-defender"),
            "At five life against six-power menace, the casual player keeps the second blocker needed for a legal pair");
        check((await Evaluate(Attack(threatKeyword: "menace", spareBlockers: 2))).Proposal is ToggleAttackPlayer { Player: 2 },
            "A third available blocker permits an attack while leaving a genuine two-creature menace block");
        var mixed = Attack(threatKeyword: "menace", spareBlockers: 1, trampleThreat: true);
        var defense = new AiTurnPlan(mixed.State.Id, mixed.State.ViewerId, mixed.State.Turn, 1,
            AiPlanPurpose.Defend, [], ["sole-defender"]) { CreatedFrom = mixed.Stamp };
        check((await Evaluate(mixed, defense)).Proposal is CompleteAttackers,
            "The incomplete menace-and-trample forecast preserves an existing defensive intention instead of applying a fictional one-blocker exchange");
    }

    private static AiDecisionFrame Priority(int blue, bool includeDraw = false, ImmutableArray<StackItem> pending = default,
        int ownLife = 40, ImmutableArray<Card> ownCards = default, ImmutableArray<Card> enemyCards = default)
    {
        var mana = Enumerable.Range(0, blue).Select(index => Card("blue-" + index, "Land")).ToImmutableArray();
        var battlefield = mana.Add(Card("draw-source", "Artifact")).AddRange(ownCards.IsDefault ? [] : ownCards);
        var stack = pending.IsDefault ? [] : pending;
        var state = State(new PriorityPayload(Yes), ownLife, battlefield, enemyCards.IsDefault ? [] : enemyCards) with { Stack = stack };
        return new(state, domain: PriorityDomain(state, includeDraw, stack.Select(Spell).ToImmutableArray()));
    }

    private static DecisionActionDomain PriorityDomain(GameSnapshot state, bool includeDraw, ImmutableArray<SemanticEntity> targets)
    {
        var offers = new List<(Card Source, SourceAction Offer, ActionSemantics Facts)>();
        var counter = state.Viewer!.Zone("Hand").Cards.Single();
        var counterFacts = Semantic("counter-offer", ActionEffectKind.CounterSpell) with {
            StableId = "held-counter-route", Affordability = ActionAffordability.Unknown, Targeted = true, TargetsAvailable = !targets.IsEmpty,
            OperationGraph = CounterGraph(targets), Costs = [BlueCost()] };
        offers.Add((counter, new("counter-offer", "cast", "Localized counter action"), counterFacts));
        if (includeDraw) offers.Add((state.Viewer.Zone("Battlefield").Cards.Single(card => card.VisualId == "draw-source"),
            new("draw-offer", "activate", "Localized draw action"), Semantic("draw-offer", ActionEffectKind.Draw) with {
                StableId = "draw-route", Affordability = ActionAffordability.Unknown, EffectAmount = 1, Costs = [BlueCost()], OperationGraph = Graph(Known(0, ActionEffectKind.Draw, 0,
                    new(SemanticKnowledge.Known, EffectRecipientBinding.Actor, null, [PlayerEntity(0)]), 1, "draw-source")) }));
        foreach (var land in state.Viewer.Zone("Battlefield").Cards.Where(card => card.Type == "Land")) {
            string id = "mana-" + land.VisualId;
            offers.Add((land, new(id, "activate", "Localized mana action"), Semantic(id, ActionEffectKind.Mana) with {
                ManaAbility = true, ManaColors = ["U"], ManaAmount = 1, Costs = [new(ActionCostKind.Tap, 1, true, "")],
                Production = new(SemanticKnowledge.Known, [new(ImmutableDictionary<string, int>.Empty.Add("U", 1))]) }));
        }
        return new(1, ActionDomainCoverage.Complete, offers.GroupBy(item => item.Source.VisualId).Select(group => {
            var source = group.First().Source;
            return new SourceActionSet(state.Id, state.Decision!.Id, source.Key, source.VisualId, group.Select(item => item.Offer).ToImmutableArray());
        }).ToImmutableArray(), offers.Select(item => item.Facts).ToImmutableArray());
    }

    private static AiDecisionFrame Attack(bool vigilance = false, bool incomingThreat = true,
        string threatKeyword = "", bool tappedThreat = false, int defenderLife = 40, bool finalOpponent = false,
        int spareBlockers = 0, bool trampleThreat = false)
    {
        var defender = Card("sole-defender", "Creature", 3, 3) with { CombatKeywords = vigilance ? ["vigilance"] : [] };
        var threat = Card("retaliation", "Creature", 6, 6) with {
            Tapped = tappedThreat, CombatKeywords = (threatKeyword.Length > 0 ? ImmutableArray.Create(threatKeyword) : [])
                .AddRange(trampleThreat ? ImmutableArray.Create("trample") : ImmutableArray<string>.Empty) };
        var own = ImmutableArray.Create(defender).AddRange(Enumerable.Range(0, spareBlockers)
            .Select(index => Card("spare-blocker-" + index, "Creature", 1, 1)));
        int threatSeat = finalOpponent ? 2 : 1;
        var baseState = State(new AttackPayload(Yes), 5, own, []);
        var state = baseState with {
            Phase = "Declare attackers", PhaseKey = "DECLARE_ATTACKERS",
            Players = baseState.Players.Select(player => player with {
                Life = player.Id == 2 ? defenderLife : player.Life,
                Eliminated = finalOpponent && player.Id is 1 or 3,
                Zones = player.Id == threatSeat ? [new("Battlefield", incomingThreat ? 1 : 0, incomingThreat ? [threat] : [], null)] : player.Zones
            }).ToImmutableArray(),
            Combat = new([], [new(defender.VisualId, [new("player", "2", 2, "Opponent")])], null) };
        return new(state, domain: DecisionActionDomain.None);
    }

    private static GameSnapshot State(DecisionPayload payload, int ownLife, ImmutableArray<Card> ownCards, ImmutableArray<Card> enemyCards)
    {
        var empty = ImmutableDictionary<string, int>.Empty;
        return new("intent-policy-game", "playing", null, null, 0, 7, "Main", "MAIN1", 0,
            [new(0, "Actor", ownLife, true, false, empty,
                [new("Battlefield", ownCards.Length, ownCards, null), new("Hand", 1, [Card("held-answer", "Instant") with { ManaCost = "{U}" }], null), new("Library", 60, [], null)]),
             new(1, "First opponent", 40, false, false, empty, []),
             new(2, "Second opponent", 40, false, false, empty, [new("Battlefield", enemyCards.Length, enemyCards, null)]),
             new(3, "Third opponent", 40, false, false, empty, [])], [], payload is AttackPayload ? new([], [], null) : null,
            new("intent-input", new(), payload)) { Revision = 1, BoardRevision = 1, DecisionOwnerId = 0, PriorityPlayerId = 0 };
    }

    private static StackItem Damage(string id, string sourceId, int actor, int victim, int amount)
        => new("Pending action", "Display prose does not determine strategy", StackActionKind.Spell, Card(sourceId, "Instant") with { Key = "" }) {
            Id = id, ControllerId = actor, Targets = [new("player", victim.ToString(), "Recipient", victim)],
            OperationGraph = Graph(Known(0, ActionEffectKind.Damage, actor,
                new(SemanticKnowledge.Known, EffectRecipientBinding.Explicit, null, [PlayerEntity(victim)]), amount, sourceId)) };
    private static StackItem Removal(string id, string sourceId, int actor, Card victim, int controller)
        => new("Pending action", "Display prose does not determine strategy", StackActionKind.Spell, Card(sourceId, "Instant") with { Key = "" }) {
            Id = id, ControllerId = actor, Targets = [new("card", victim.VisualId, "Recipient", controller)],
            OperationGraph = Graph(Known(0, ActionEffectKind.Destroy, actor,
                new(SemanticKnowledge.Known, EffectRecipientBinding.Explicit, null,
                    [new("victim", SemanticEntityKind.Card, victim.VisualId, ControllerId: controller, OwnerId: controller)]), sourceId: sourceId)) };
    private static ActionOperationGraph CounterGraph(ImmutableArray<SemanticEntity> candidates)
        => Graph(Known(0, ActionEffectKind.CounterSpell, 0, new(SemanticKnowledge.Known, EffectRecipientBinding.Targets, 0, [])) with {
            Targets = new(SemanticKnowledge.Known, 1, 1, candidates) });
    private static ActionOperation Known(int id, ActionEffectKind effect, int actor, EffectRecipients recipients, int? amount = null, string sourceId = "held-answer")
        => new(id, effect) { Knowledge = SemanticKnowledge.Known, ActorPlayerId = actor, Recipients = recipients,
            Sources = new(SemanticKnowledge.Known, EffectRecipientBinding.Source, null,
                [new("source:" + sourceId, SemanticEntityKind.Card, sourceId, ControllerId: actor)]),
            Amount = amount, AmountKnowledge = amount == null ? SemanticKnowledge.Unknown : SemanticKnowledge.Known };
    private static ActionOperationGraph Graph(params ActionOperation[] nodes) => new(SemanticKnowledge.Known, nodes[0].Id, nodes.ToImmutableArray());
    private static SemanticEntity Spell(StackItem item)
        => new(item.Id, SemanticEntityKind.Spell, item.Card!.VisualId, ControllerId: item.ControllerId) { StackItemId = item.Id };
    private static SemanticEntity PlayerEntity(int player) => new("player:" + player, SemanticEntityKind.Player, PlayerId: player);
    private static ActionCost BlueCost() => new(ActionCostKind.Mana, 0, false, "") { ManaSymbols = ["{U}"] };
    private static ActionSemantics Semantic(string id, ActionEffectKind effect)
        => new(id, effect, [effect], [], ActionAffordability.Payable) { StableId = id, ManaCostKnowledge = SemanticKnowledge.Known };
    private static Card Card(string id, string type, int? power = null, int? toughness = null)
        => new() { Key = id + "-key", VisualId = id, CombatId = id, Type = type, Name = "Disclosed face", Power = power, Toughness = toughness };
    private static Task<AiDecisionReplayResult> Evaluate(AiDecisionFrame frame, AiTurnPlan? plan = null)
        => AiDecisionReplay.EvaluateAsync(new(1, "intent-policy-case", "casual-commander", frame.State, frame.Domain, frame.Semantics,
            new([], []), 17) { RemainingWork = 4096, Plan = plan, PlanVersion = plan?.Version ?? 0 }, new CasualCommanderPolicy());
}
