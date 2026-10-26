using System.Collections.Immutable;
using Mana.AI;
using Mana.Contracts;

internal static class AiPolicyRegression
{
    private static readonly DecisionReply Yes = new("Continue", true), No = new("Cancel", false);
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var policy = new CasualCommanderPolicy();
        check(policy.Descriptor.Tasks.SetEquals(AiTasks.All), "The casual policy declares the complete seat rather than a targeting-only replacement");
        await EveryTask(check);
        await ActionsAndCosts(check);
        await TargetsAndSocialMemory(check);
        await CombatAndAllocation(check);
        await TacticalExchangesAndResourceCosts(check);
        await OperationGraphs(check);
        await BoundedGraphOptimization(check);
        await BoundedCombatOptimization(check);
        await CausalEpisodes(check);
    }

    private static async Task EveryTask(Action<bool, string> check)
    {
        var choices = new ChoiceSelection([new(17, "First", null), new(29, "Second", null)], new(1, 1));
        var cases = new (DecisionPayload Payload, Type Command, DecisionSemantics? Semantics)[] {
            (new PriorityPayload(Yes), typeof(PassPriority), null),
            (new OpeningHandPayload(Yes, Yes), typeof(KeepOpeningHand), null),
            (new PaymentPayload(Yes, Yes), typeof(PayMana), null),
            (new AttackPayload(Yes), typeof(CompleteAttackers), null),
            (new BlockPayload(Yes), typeof(CompleteBlockers), null),
            (new TargetSelectionPayload(No, No, new(1, 1), [1]), typeof(SelectPlayer), new(DecisionIntent.Damage)),
            (new EntitySelectionPayload(No, No, [0], new(1, 1)), typeof(SelectPlayer), new(DecisionIntent.Search)),
            (new ConfirmationPayload(Yes, Yes), typeof(AcceptConfirmation), null),
            (new AbilityChoicePayload(choices), typeof(ChooseOptions), null),
            (new OptionsPayload(choices), typeof(ChooseOptions), null),
            (new OrderingPayload(choices), typeof(OrderOptions), null),
            (new RevealPayload([]), typeof(AcknowledgeReveal), null),
            (new NumberPayload("999", new(2, 7)), typeof(ChooseNumber), null),
            (new TextPayload(""), typeof(EnterText), new() { LegalTextValues = ["A legal rule value"] }),
            (new AllocationPayload(3, AllocationKind.Generic, "", false, [new(9, "recipient", "", null, "", 1, 1, 1, 0, 3)]), typeof(AllocateAmounts), null)
        };
        foreach (var item in cases) {
            var frame = Frame(State(item.Payload), semantics: item.Semantics);
            var answer = await Evaluate(frame);
            check(answer.Command?.GetType() == item.Command && frame.Allows(answer.Command),
                $"The casual policy produces an authorized ordinary {AiTasks.From(item.Payload)} decision");
        }
        var unsupported = await Evaluate(Frame(State(new TextPayload("Arbitrary user hint"))));
        check(unsupported.Status == AiSeatStatus.Unavailable && unsupported.Command == null,
            "Unrestricted text without a legal rules domain is reported explicitly rather than inventing a card name");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        using var seat = Session(); seat.Observe(Frame(State(new PriorityPayload(Yes))));
        bool cancellationObserved;
        try { cancellationObserved = (await seat.EvaluateAsync(cancelled.Token)).Status == AiSeatStatus.Cancelled; }
        catch (OperationCanceledException) { cancellationObserved = true; }
        check(cancellationObserved, "An already cancelled policy evaluation does not consume or submit a game decision");
    }

    private static async Task ActionsAndCosts(Action<bool, string> check)
    {
        var state = State(new PriorityPayload(Yes));
        var land = new SourceAction("land", "play", "Untrusted display text");
        var draw = new SourceAction("draw", "activate", "Untrusted display text");
        var landSemantic = Semantic("land", ActionEffectKind.Land) with { StableId = "land-route" };
        var drawSemantic = Semantic("draw", ActionEffectKind.Draw) with { StableId = "draw-route" };
        var domain = Domain(state, ("hand-land", land, landSemantic), ("own-creature", draw, drawSemantic));
        var answer = await Evaluate(Frame(state, domain));
        check(answer.Command is UseOfferedAction { ActionId: "land" }, "Land sequencing uses the concrete legal route before optional activations");

        using var session = Session();
        var drawOnly = Domain(state, ("own-creature", draw, drawSemantic));
        session.Observe(Frame(state, drawOnly)); await session.EvaluateAsync(); var first = session.TakeCommand()!;
        session.Report(first, AiExecutionReceipt.Accepted);
        var later = state with { Revision = 2, BoardRevision = 2, Decision = state.Decision! with { Id = "later" } };
        session.Observe(Frame(later, Domain(later, ("own-creature", draw with { Id = "new-offer" }, drawSemantic with { ActionId = "new-offer" }))));
        await session.EvaluateAsync();
        check(session.TakeCommand()?.Command is PassPriority,
            "Refreshed action UUIDs cannot restart the same repeatable activation forever in one turn");

        using var bounded = new AiSeatSession(state.Id, 0, new CasualCommanderPolicy(), 17, new(TimeSpan.FromSeconds(5), 1024, historyCapacity: 2));
        bounded.Observe(Frame(state, drawOnly)); await bounded.EvaluateAsync(); var attempted = bounded.TakeCommand()!;
        bounded.Report(attempted, AiExecutionReceipt.Accepted);
        for (int revision = 2; revision <= 4; revision++) {
            var acknowledge = state with { Revision = revision, BoardRevision = revision,
                Decision = new("ack" + revision, new(), new ConfirmationPayload(Yes)) };
            bounded.Observe(Frame(acknowledge)); await bounded.EvaluateAsync(); bounded.Report(bounded.TakeCommand()!, AiExecutionReceipt.Accepted);
        }
        var retried = later with { Revision = 5, BoardRevision = 5 };
        bounded.Observe(Frame(retried, Domain(retried, ("own-creature", draw, drawSemantic)))); await bounded.EvaluateAsync();
        var passed = bounded.TakeCommand();
        check(passed?.Command is PassPriority && bounded.History.AttemptedRoutes.Count == 1
                && bounded.History.Decisions.All(record => record.Command != nameof(UseOfferedAction)),
            "Evicting diagnostic history cannot erase a current-turn retry guard or reopen a repeatable activation loop");
        bounded.Report(passed!, AiExecutionReceipt.Accepted);
        var nextTurn = retried with { Turn = 5, Revision = 6, BoardRevision = 6, Decision = retried.Decision! with { Id = "next-turn" } };
        bounded.Observe(Frame(nextTurn, Domain(nextTurn, ("own-creature", draw, drawSemantic)))); await bounded.EvaluateAsync();
        var nextAttempt = bounded.TakeCommand();
        check(nextAttempt?.Command is UseOfferedAction && bounded.History.AttemptedRoutes.IsEmpty,
            "A genuine turn boundary resets policy route attempts without reviving old action handles");
        var advanced = nextTurn with { Turn = 6, Revision = 7, BoardRevision = 7, Decision = nextTurn.Decision! with { Id = "advanced" } };
        bounded.Observe(Frame(advanced, Domain(advanced, ("own-creature", draw, drawSemantic))));
        bounded.Report(nextAttempt!, AiExecutionReceipt.Accepted);
        check(bounded.History.AttemptedRoutes.IsEmpty, "A delayed receipt from a prior turn cannot populate the new turn's route memory");

        var manaState = State(new PaymentPayload(No, Yes));
        var colorless = Semantic("colorless", ActionEffectKind.Mana) with { ManaAbility = true, ManaColors = ["C"], ManaAmount = 2,
            Production = new(SemanticKnowledge.Known, [new(ImmutableDictionary<string, int>.Empty.Add("C", 2))]) };
        var blue = Semantic("blue", ActionEffectKind.Mana) with { ManaAbility = true, ManaColors = ["U"], ManaAmount = 1,
            Production = new(SemanticKnowledge.Known, [new(ImmutableDictionary<string, int>.Empty.Add("U", 1))]) };
        var manaDomain = Domain(manaState, ("own-creature", new("colorless", "activate", "Tap"), colorless),
            ("own-land", new("blue", "activate", "Tap"), blue));
        var payment = await Evaluate(Frame(manaState, manaDomain, new() { UnpaidMana = ["{U}"] }));
        check(payment.Command is UseOfferedAction { ActionId: "blue" },
            "A remaining blue shard chooses its offered blue source rather than a larger colorless producer");

        var colorlessPayment = await Evaluate(Frame(manaState, manaDomain, new() { UnpaidMana = ["{C}"] }));
        check(colorlessPayment.Command is UseOfferedAction { ActionId: "colorless" }, "True colorless requirements remain distinct from colored and generic mana");
        var sacrifice = colorless with { ActionId = "sacrifice", Costs = [new(ActionCostKind.Sacrifice, 1, true, "")] };
        var safe = colorless with { ActionId = "safe", ManaAmount = 1,
            Production = new(SemanticKnowledge.Known, [new(ImmutableDictionary<string, int>.Empty.Add("C", 1))]) };
        payment = await Evaluate(Frame(manaState, Domain(manaState,
            ("own-creature", new("sacrifice", "activate", "Tap"), sacrifice), ("own-land", new("safe", "activate", "Tap"), safe)), new() { UnpaidMana = ["{1}"] }));
        check(payment.Command is UseOfferedAction { ActionId: "safe" }, "Funding generic costs preserves permanents when an ordinary tap route is offered");

        var noFunding = await Evaluate(Frame(manaState));
        check(noFunding.Command is CancelPayment, "A payment without funding cancels explicitly and cannot manufacture mana or silently pass");
        var restricted = blue with { ActionId = "restricted", PaymentManaColors = [] };
        payment = await Evaluate(Frame(manaState, Domain(manaState,
            ("own-creature", new("restricted", "activate", "Mana"), restricted), ("own-land", new("blue", "activate", "Mana"), blue)), new() { UnpaidMana = ["{U}"] }));
        check(payment.Command is UseOfferedAction { ActionId: "blue" },
            "A mana ability which produces blue but cannot spend it on this spell is excluded by its exact payment applicability");
        var poolState = manaState with { Players = manaState.Players.Select(player => player.Id == 0
            ? player with { Mana = ImmutableDictionary<string, int>.Empty.Add("U", 1) } : player).ToImmutableArray() };
        payment = await Evaluate(Frame(poolState, Domain(poolState, ("own-land", new("blue", "activate", "Mana"), blue)) with { PoolManaColors = ["U"] },
            new() { UnpaidMana = ["{U}"] }));
        check(payment.Command is SpendPoolMana { Color: "U" }, "Already floating applicable mana is spent before another permanent is tapped");
        var colorChoice = State(new OptionsPayload(new([new(8, "Localized red", null) { Mana = "{R}" },
            new(31, "Localized blue", null) { Mana = "{U}" }], new(1, 1))));
        var color = await Evaluate(Frame(colorChoice, semantics: new() { UnpaidMana = ["{U}"] }));
        check(color.Command is ChooseOptions colorIndices && colorIndices.Choices.SequenceEqual([31]),
            "An any-color production choice follows the current unpaid blue shard using typed mana symbols and original option indices");
        var noTargets = drawSemantic with { Effect = ActionEffectKind.Damage, TargetsAvailable = false };
        check((await Evaluate(Frame(state, Domain(state, ("own-creature", draw, noTargets))))).Command is PassPriority,
            "A route whose targeting minimum has no legal candidates is not repeatedly initiated");
        var blueSpell = Semantic("blue-spell", ActionEffectKind.Draw) with { Affordability = ActionAffordability.Unknown, ManaCostKnowledge = SemanticKnowledge.Known,
            Costs = [new(ActionCostKind.Mana, 0, false, "") { ManaSymbols = ["{U}"] }] };
        var onlyColorless = Domain(state, ("hand-spell", new("blue-spell", "cast", "Spell"), blueSpell),
            ("own-land", new("colorless", "activate", "Mana"), colorless));
        check((await Evaluate(Frame(state, onlyColorless))).Command is PassPriority,
            "Colorless production cannot be mistaken for funding a blue spell during sequencing");
        var blueFunded = Domain(state, ("hand-spell", new("blue-spell", "cast", "Spell"), blueSpell),
            ("own-land", new("blue", "activate", "Mana"), blue));
        check((await Evaluate(Frame(state, blueFunded))).Command is UseOfferedAction { ActionId: "blue-spell" },
            "The same spell becomes a policy candidate when a genuine blue production route is disclosed");
        var uncertainCost = blueSpell with { ManaCostKnowledge = SemanticKnowledge.Unknown };
        var uncertainDomain = Domain(state, ("hand-spell", new("blue-spell", "cast", "Spell"), uncertainCost),
            ("own-land", new("colorless", "activate", "Mana"), colorless));
        check((await Evaluate(Frame(state, uncertainDomain))).Command is PassPriority
                && uncertainDomain.Actions.Any(action => action.ActionId == "blue-spell" && action.Affordability == ActionAffordability.Unknown),
            "The strategy waits on nominally unfunded optional casts while preserving the offered route and unknown effective affordability");
        var discounted = uncertainCost with { Affordability = ActionAffordability.Payable };
        check((await Evaluate(Frame(state, Domain(state, ("hand-spell", new("blue-spell", "cast", "Spell"), discounted),
            ("own-land", new("colorless", "activate", "Mana"), colorless))))).Command is UseOfferedAction { ActionId: "blue-spell" },
            "Authoritative payable knowledge overrides the nominal shortfall heuristic, including a reduced or alternative cost");
        var expensive = uncertainCost with { Costs = [new(ActionCostKind.Mana, 3, false, "") { ManaSymbols = ["{G}", "{G}"] }] };
        var green = blue with { ActionId = "green", ManaColors = ["G"], Costs = [new(ActionCostKind.Tap, 1, true, "")],
            Production = new(SemanticKnowledge.Known, [new(ImmutableDictionary<string, int>.Empty.Add("G", 1))]) };
        check((await Evaluate(Frame(state, Domain(state, ("hand-spell", new("blue-spell", "cast", "Spell"), expensive),
            ("own-land", new("green", "activate", "Mana"), green))))).Command is PassPriority,
            "A turn-one single green source does not initiate an unknown-effective-cost spell with nominal three-generic two-green demand");

        var donor = Card("donor") with { Key = "", Selectable = false };
        var counterPayment = new CounterPaymentChoice(1, new("donor", donor, 0, "Battlefield"),
            [new(5, "P1P1", "Positive counters", 1), new(19, "CHARGE", "Charge counters", 4)]);
        var counterState = State(new OptionsPayload(new(counterPayment.Options.Select(option => new Choice(option.Index, option.Label, null)).ToImmutableArray(), new(1, 1)), counterPayment));
        var counter = await Evaluate(Frame(counterState));
        check(counter.Command is ChooseOptions counterChoice && counterChoice.Choices.SequenceEqual([19]),
            "Counter payment uses an actual offered counter type with surplus rather than an invented donor or text-derived choice");
        var partial = new AiDecisionFrame(State(new PriorityPayload(Yes)));
        check((await Evaluate(partial)).Status == AiSeatStatus.Unavailable, "A complete policy still refuses to treat partial action discovery as permission to pass");
    }

    private static async Task TargetsAndSocialMemory(Action<bool, string> check)
    {
        var payload = new TargetSelectionPayload(No, No, new(1, 1), [0, 1, 2, 3]);
        var state = State(payload);
        // Seat one is at 3 life; seat two has the substantial public battlefield.
        state = state with { Players = state.Players.Select(player => player.Id == 1 ? player with { Life = 3 }
            : player.Id == 2 ? player with { Zones = [new("Battlefield", 1, [Card("threat", power: 12, toughness: 12)], null)] } : player).ToImmutableArray() };
        var damage = Frame(state, semantics: new(DecisionIntent.Damage));
        var result = await Evaluate(damage);
        check(result.Command is SelectPlayer { Player: 2 }, "Harmful player choices address public threat rather than automatically picking the lowest life total");
        var benefit = await Evaluate(Frame(state, semantics: new(DecisionIntent.Benefit)));
        check(benefit.Command is SelectPlayer { Player: 0 }, "A beneficial recipient choice favors the acting seat instead of applying the hostile targeting heuristic");
        check((await Evaluate(Frame(state, semantics: new(DecisionIntent.AddCounters) { CounterType = "P1P1" }))).Command is SelectPlayer { Player: 0 }
                && (await Evaluate(Frame(state, semantics: new(DecisionIntent.AddCounters) { CounterType = "POISON" }))).Command is SelectPlayer { Player: 2 },
            "Counter additions distinguish intrinsic positive stat counters from poison instead of treating every addition as beneficial");
        check((await Evaluate(Frame(state, semantics: new(DecisionIntent.RemoveCounters) { CounterType = "POISON" }))).Command is SelectPlayer { Player: 0 },
            "Removing harmful counters reverses recipient preference rather than reusing the addition heuristic");

        var multiple = await Evaluate(Frame(state with { Decision = state.Decision! with { Payload = payload with { Range = new(2, 2) } } },
            semantics: new(DecisionIntent.Damage) { SelectedPlayers = [2] }));
        check(multiple.Command is SelectPlayer selectedPlayer && selectedPlayer.Player != 2,
            "A second required player target cannot toggle off the first staged target even when human undo remains legal");

        var same = await Evaluate(damage);
        check(same.Command == result.Command, "The same seat seed and public facts reproduce the same target without mutable policy state");
        var renamed = state with { Players = state.Players.Select(player => player with { Name = "Renamed", Zones = player.Zones.Select(zone => zone with {
            Cards = zone.Cards.Select(card => card with { Name = "Completely different", Text = "Do not parse these words" }).ToImmutableArray() }).ToImmutableArray() }).ToImmutableArray() };
        check((await Evaluate(Frame(renamed, semantics: new(DecisionIntent.Damage)))).Command == result.Command,
            "Changing card/player prose cannot change a typed threat decision");

        var peers = State(payload);
        var targets = new HashSet<int>();
        for (int seed = 0; seed < 12; seed++) {
            var peer = await Evaluate(Frame(peers, semantics: new(DecisionIntent.Damage)), seed);
            if (peer.Command is SelectPlayer player) targets.Add(player.Player);
        }
        check(!targets.Contains(0) && targets.Count > 1, "Equivalent opponents use independent seeded ties rather than a permanent turn-order victim");
        var pressure = peers with { Activity = [new(1, peers.Turn, "MAIN1", "damage", 3, "Private display detail", null, null) {
            Cause = new(null, "", "", 3), Impact = new("damage", 7, PlayerId: 0) }] };
        check((await Evaluate(Frame(pressure, semantics: new(DecisionIntent.Damage)))).Command is SelectPlayer { Player: 3 },
            "Observed recent damage informs defensive pressure without inventing outcomes from proposals");
        var assessed = CasualTableAssessment.Assess(pressure, new([], []));
        check(assessed.All(item => item.Pressure == 0), "Unobserved history contributes no claimed damage, even when player-facing prose mentions it");

        var discardState = State(new OptionsPayload(new([new(4, "Large", Card("large", 8, 8)), new(11, "Small", Card("small", 1, 1))], new(1, 1))));
        var discard = await Evaluate(Frame(discardState, semantics: new(DecisionIntent.Discard) { SelectionIsCost = true }));
        check(discard.Command is ChooseOptions selected && selected.Choices.SequenceEqual([11]),
            "Required discard preserves the stronger disclosed card and keeps the engine's original option index");

        var masked = Card("opaque-recipient", 2, 2) with {
            VisualId = null!, FaceDown = true, Selectable = true, Name = "Face-down card", Text = "", ManaCost = "" };
        var maskedTarget = Battlefield(State(new TargetSelectionPayload(No, Yes, new(1, 1), [])), 1, [masked]);
        var maskedGraph = Graph(Known(0, ActionEffectKind.Damage,
            new(SemanticKnowledge.Known, EffectRecipientBinding.Targets, 0, []), 4) with {
                Targets = new(SemanticKnowledge.Known, 1, 1, [CardEntity(masked.CombatId, 1)]) });
        var maskedSemantics = new DecisionSemantics(DecisionIntent.Damage, 4) { OperationId = 0,
            OperationGraph = maskedGraph with { Knowledge = SemanticKnowledge.Unknown } };
        check((await Evaluate(Frame(maskedTarget, semantics: maskedSemantics))).Command is SelectCard maskedAnswer && maskedAnswer.Card == masked.Key,
            "A masked creature with no visual face identity remains a legal required target when effect knowledge is incomplete, using its opaque occurrence rather than hashing a null face");
        var knownMasked = Frame(maskedTarget, semantics: maskedSemantics with { OperationGraph = maskedGraph });
        check((await Evaluate(knownMasked)).Command is SelectCard knownMaskedAnswer && knownMaskedAnswer.Card == masked.Key,
            "Known effect graphs bind a masked recipient through the same public combat occurrence without requiring or recovering its concealed printed identity");
        var maskedCost = Battlefield(State(new EntitySelectionPayload(No, No, [], new(1, 1))), 0, [masked]);
        check((await Evaluate(Frame(maskedCost, semantics: new(DecisionIntent.Sacrifice) { SelectionIsCost = true }))).Command
                is SelectCard maskedCostAnswer && maskedCostAnswer.Card == masked.Key,
            "Required entity costs also accept masked current occurrences instead of faulting while resolving a public face-down permanent");
    }

    private static async Task CombatAndAllocation(Action<bool, string> check)
    {
        var state = State(new AttackPayload(Yes));
        state = state with { Combat = new([], [new("own-creature", [new("player", "1", 1, "Other")])], null) };
        check((await Evaluate(Frame(state))).Command is ToggleAttackPlayer { Attacker: "own-creature-key", Player: 1 },
            "Combat attacks use exact offered defenders and current occurrence handles");
        var attacked = state with { Combat = state.Combat with { Attackers = [new("own-creature", 1, [], [])] } };
        check((await Evaluate(Frame(attacked))).Command is CompleteAttackers, "A staged attacker is not toggled off when its declaration is republished");
        var defend = State(new BlockPayload(Yes));
        var opponent = Card("attacker", 2, 1);
        defend = defend with { Players = defend.Players.Select(player => player.Id == 1 ? player with {
            Zones = [new("Battlefield", 1, [opponent], null)] } : player).ToImmutableArray(),
            Combat = new([new("attacker", 0, [], ["own-creature"])], [], null) };
        check((await Evaluate(Frame(defend))).Command is ToggleBlock { Attacker: "attacker-key", Blocker: "own-creature-key" },
            "A profitable defensive trade is assigned through the same legal block command as a human");
        var stagedBlock = defend with { Combat = defend.Combat! with {
            Attackers = [new("attacker", 0, ["own-creature"], ["own-creature", "reserve"])] },
            Players = defend.Players.Select(player => player.Id == 0 ? player with {
                Zones = player.Zones.Select(zone => zone.Name == "Battlefield" ? zone with { Cards = zone.Cards.Add(Card("reserve", 8, 8)), Count = zone.Count + 1 } : zone).ToImmutableArray()
            } : player).ToImmutableArray() };
        check((await Evaluate(Frame(stagedBlock))).Command is CompleteBlockers,
            "A valid staged block completes instead of committing every spare creature to the same already-blocked attacker");

        var allocation = new AllocationPayload(7, AllocationKind.Combat, "damage", false,
            [new(5, "blocker", "", null, "", null, 1, 1, 0, 7, 3), new(2, "defender", "", null, "", 1, 1, 1, 0, 7, null, true)]);
        var result = await Evaluate(Frame(State(allocation), semantics: new(DecisionIntent.CombatDamage)));
        check(result.Command is AllocateAmounts amounts && amounts.Amounts.SequenceEqual([3, 4])
                && AllocationConstraints.Check(allocation, amounts.Amounts) == null,
            "Damage allocation respects lethal-before-defender constraints and never substitutes printed toughness for authoritative bounds");
        var mandatory = new AllocationPayload(5, AllocationKind.Generic, "", false,
            [new(8, "one", "", null, "", 1, 1, 1, 2, 3), new(1, "two", "", null, "", 2, 2, 2, 1, 4)]);
        var allocated = await Evaluate(Frame(State(mandatory)));
        check(allocated.Command is AllocateAmounts exact && exact.Amounts.Sum() == 5 && AllocationConstraints.Check(mandatory, exact.Amounts) == null,
            "Allocation preserves each minimum, maximum and exact total even when recipient indices are sparse");
        var impossible = new AllocationPayload(9, AllocationKind.Generic, "", false, [new(0, "one", "", null, "", 1, 1, 1, 0, 3)]);
        check((await Evaluate(Frame(State(impossible)))).Status == AiSeatStatus.Unavailable,
            "Contradictory required allocation domains fail visibly instead of emitting an invalid or partial assignment");
    }

    private static async Task CausalEpisodes(Action<bool, string> check)
    {
        var state = State(new PriorityPayload(Yes));
        using var limited = new AiSeatSession(state.Id, 0, new CasualCommanderPolicy(), 17, new(TimeSpan.FromSeconds(5), workUnits: 1));
        limited.Observe(Frame(state, semantics: new() { EpisodeId = "same-operation" })); await limited.EvaluateAsync();
        var first = limited.TakeCommand()!; limited.Report(first, AiExecutionReceipt.Accepted);
        var refreshed = state with { Revision = 2, BoardRevision = 2, Decision = state.Decision! with { Id = "replacement-input" } };
        limited.Observe(Frame(refreshed, semantics: new() { EpisodeId = "same-operation" }));
        check((await limited.EvaluateAsync()).Status == AiSeatStatus.BudgetExceeded && limited.TakeCommand() == null,
            "Replacing an input object cannot renew the work allowance of the same causal rules operation");
        var next = refreshed with { Revision = 3, BoardRevision = 3, Decision = refreshed.Decision! with { Id = "new-operation-input" } };
        limited.Observe(Frame(next, semantics: new() { EpisodeId = "next-operation" }));
        check((await limited.EvaluateAsync()).Status == AiSeatStatus.Ready,
            "A distinct causal operation receives a fresh allowance while keeping answer scopes separate");

        var attack = State(new AttackPayload(Yes));
        attack = attack with { Players = attack.Players.Select(player => player.Id == 0 ? player with {
            Zones = player.Zones.Select(zone => zone.Name == "Battlefield" ? zone with { Cards = zone.Cards.Add(Card("required", 1, 1)), Count = zone.Count + 1 } : zone).ToImmutableArray()
        } : player).ToImmutableArray(), Combat = new([new("own-creature", 1, [], [])],
            [new("own-creature", [new("player", "1", 1, "")]), new("required", [new("player", "1", 1, "")])], null) {
                AttackValid = false, AttackRepair = [new("required", new("player", "1", 1, ""))] } };
        var semantics = new DecisionSemantics { EpisodeId = "combat-turn4-number1-actor0" };
        using var repair = Session();
        repair.Observe(Frame(attack, semantics: semantics)); await repair.EvaluateAsync(); var remove = repair.TakeCommand();
        check(remove?.Command is ToggleAttackPlayer { Attacker: "own-creature-key", Player: 1 },
            "An invalid joint declaration removes assignments absent from the rules engine's legal requirement witness");
        repair.Report(remove!, AiExecutionReceipt.Accepted);
        var missing = attack with { Revision = 2, BoardRevision = 2, Decision = attack.Decision! with { Id = "repair-missing" },
            Combat = attack.Combat! with { Attackers = [] } };
        repair.Observe(Frame(missing, semantics: semantics)); await repair.EvaluateAsync(); var add = repair.TakeCommand();
        check(add?.Command is ToggleAttackPlayer { Attacker: "required-key", Player: 1 },
            "Repair selects the exact forced assignment from current engine facts rather than passing or parsing a warning");
        repair.Report(add!, AiExecutionReceipt.Accepted);
        var valid = missing with { Revision = 3, BoardRevision = 3, Decision = missing.Decision! with { Id = "repair-complete" },
            Combat = missing.Combat! with { Attackers = [new("required", 1, [], [])], AttackValid = true, AttackRepair = [] } };
        repair.Observe(Frame(valid, semantics: semantics)); await repair.EvaluateAsync();
        check(repair.TakeCommand()?.Command is CompleteAttackers,
            "A legal repaired declaration completes across refreshed input IDs instead of reintroducing the optional assignment which broke it");
        var contradictory = missing with { Combat = missing.Combat! with { AttackRepair = [] } };
        check((await Evaluate(Frame(contradictory, semantics: semantics))).Status == AiSeatStatus.Unavailable,
            "An invalid declaration without a repair witness cannot become an endless series of accepted completion gestures");
    }

    private static async Task TacticalExchangesAndResourceCosts(Action<bool, string> check)
    {
        var equal = AttackCase(Card("own-creature", 3, 3), Card("defender", 3, 3));
        check((await Evaluate(Frame(equal))).Command is CompleteAttackers,
            "An optional attack does not give up an equal body for a trade with no estimated damage or material advantage");
        var first = AttackCase(Card("own-creature", 3, 3) with { CombatKeywords = ["first strike"] }, Card("defender", 3, 3));
        check((await Evaluate(Frame(first))).Command is ToggleAttackPlayer,
            "A first-strike attacker recognizes a profitable exchange before the defender can deal ordinary combat damage");
        var touch = AttackCase(Card("own-creature", 1, 1) with { CombatKeywords = ["deathtouch"] }, Card("defender", 8, 8));
        check((await Evaluate(Frame(touch))).Command is ToggleAttackPlayer,
            "A small deathtouch creature can trade up against a stronger public body instead of being evaluated on power alone");
        var trap = AttackCase(Card("own-creature", 4, 4), Card("defender", 1, 1) with { CombatKeywords = ["deathtouch"] });
        check((await Evaluate(Frame(trap))).Command is CompleteAttackers,
            "A large attacker avoids donating itself to a much smaller deathtouch blocker");
        var strikeBeforeTouch = AttackCase(Card("own-creature", 4, 4) with { CombatKeywords = ["first strike"] },
            Card("defender", 1, 1) with { CombatKeywords = ["deathtouch"] });
        check((await Evaluate(Frame(strikeBeforeTouch))).Command is ToggleAttackPlayer,
            "First-strike timing removes the deathtouch blocker before it can return ordinary damage");
        var immune = AttackCase(Card("own-creature", 1, 1) with { CombatKeywords = ["deathtouch"] },
            Card("defender", 8, 8) with { CombatKeywords = ["indestructible"] });
        check((await Evaluate(Frame(immune))).Command is CompleteAttackers,
            "Deathtouch is not mistaken for destroying an indestructible blocker");
        var doubleStrike = AttackCase(Card("own-creature", 2, 4) with { CombatKeywords = ["double strike"] }, Card("defender", 3, 4));
        var ordinary = AttackCase(Card("own-creature", 2, 4), Card("defender", 3, 4));
        check((await Evaluate(Frame(doubleStrike))).Command is ToggleAttackPlayer
                && (await Evaluate(Frame(ordinary))).Command is CompleteAttackers,
            "Double strike carries first-step damage into the normal step; an ordinary creature does not invent that second hit");
        var trampler = AttackCase(Card("own-creature", 8, 8) with { CombatKeywords = ["trample"] },
            Card("defender", 1, 1) with { CombatKeywords = ["indestructible"] });
        check((await Evaluate(Frame(trampler))).Command is ToggleAttackPlayer,
            "Trample can assign excess damage past indestructible after assigning lethal damage to its current toughness");

        var badBlock = BlockCase(Card("attacker", 3, 3) with { CombatKeywords = ["first strike"] }, Card("own-creature", 3, 3));
        check((await Evaluate(Frame(badBlock))).Command is CompleteBlockers,
            "A nonlethal first-strike attack does not induce a losing equal-body block");
        var goodBlock = BlockCase(Card("attacker", 3, 3), Card("own-creature", 3, 3) with { CombatKeywords = ["first strike"] });
        check((await Evaluate(Frame(goodBlock))).Command is ToggleBlock,
            "The same first-strike exchange is profitable when the defending creature has first strike");
        var immortal = BlockCase(Card("attacker", 8, 8), Card("own-creature", 1, 1) with { CombatKeywords = ["indestructible"] });
        check((await Evaluate(Frame(immortal))).Command is ToggleBlock,
            "An indestructible blocker can prevent ordinary damage without inventing a body loss from marked damage");
        var powerless = BlockCase(Card("attacker", 8, 8), Card("own-creature", 0, 1) with { CombatKeywords = ["deathtouch"] });
        check((await Evaluate(Frame(powerless))).Command is CompleteBlockers,
            "Zero combat damage does not apply deathtouch or turn a losing nonlethal block into a profitable trade");
        var lethal = BlockCase(Card("attacker", 1, 1) with { CombatKeywords = ["deathtouch"] }, Card("own-creature", 8, 8));
        lethal = lethal with { Players = lethal.Players.Select(player => player.Id == 0 ? player with { Life = 1 } : player).ToImmutableArray() };
        check((await Evaluate(Frame(lethal))).Command is ToggleBlock,
            "Preventing immediate lethal damage takes precedence over preserving a stronger creature");
        var permanentAttack = lethal with { Combat = lethal.Combat! with { Attackers = [lethal.Combat.Attackers[0] with {
            Defender = new("planeswalker", "walker", 0, "") }] } };
        permanentAttack = Battlefield(permanentAttack, 0, permanentAttack.Viewer!.Zone("Battlefield").Cards.Add(Card("walker") with {
            Type = "Planeswalker", Power = null, Toughness = null }));
        check((await Evaluate(Frame(permanentAttack))).Command is CompleteBlockers,
            "Damage aimed at an owned planeswalker is not treated as immediate lethal damage to its low-life controller");
        var doubleLethal = BlockCase(Card("attacker", 2, 2) with { CombatKeywords = ["double strike"] }, Card("own-creature", 1, 1));
        doubleLethal = doubleLethal with { Players = doubleLethal.Players.Select(player => player.Id == 0 ? player with { Life = 3 } : player).ToImmutableArray() };
        check((await Evaluate(Frame(doubleLethal))).Command is ToggleBlock,
            "Unblocked double-strike damage is counted twice when deciding whether a defensive sacrifice prevents lethal");
        var trampleLethal = BlockCase(Card("attacker", 8, 8) with { CombatKeywords = ["trample"] },
            Card("own-creature", 1, 1) with { CombatKeywords = ["indestructible"] });
        trampleLethal = Battlefield(trampleLethal, 0, trampleLethal.Viewer!.Zone("Battlefield").Cards.Add(Card("large-blocker", 8, 8)))
            with { Combat = trampleLethal.Combat! with { Attackers = [new("attacker", 0, [], ["own-creature", "large-blocker"])] } };
        trampleLethal = trampleLethal with { Players = trampleLethal.Players.Select(player => player.Id == 0 ? player with { Life = 5 } : player).ToImmutableArray() };
        check((await Evaluate(Frame(trampleLethal))).Command is ToggleBlock { Blocker: "large-blocker-key" },
            "Lethal trample selects the body that actually absorbs the damage rather than treating a tiny indestructible blocker as complete prevention");

        var animated = Card("animated", 8, 8) with { Type = "Land Creature", Selectable = true };
        var removal = State(new TargetSelectionPayload(No, No, new(1, 1), []));
        removal = Battlefield(removal, 1, [animated, Card("ordinary", 2, 2) with { Selectable = true }]);
        check((await Evaluate(Frame(removal, semantics: new(DecisionIntent.Destroy)))).Command is SelectCard { Card: "animated-key" },
            "An animated land retains its effective creature-body threat instead of being valued as an ordinary half-point land");

        var land = Card("hand-land") with { Type = "Basic Land", Power = null, Toughness = null };
        var spell = Card("hand-spell", 2, 3);
        var discard = State(new OptionsPayload(new([new(3, "Resource", land), new(8, "Body", spell)], new(1, 1))));
        check((await Evaluate(Frame(discard, semantics: new(DecisionIntent.Discard) { SelectionIsCost = true }))).Command is ChooseOptions keepLand
                && keepLand.Choices.SequenceEqual([8]),
            "Discard opportunity cost preserves the lands required for early development instead of blindly throwing away the lowest public threat");
        var abundant = Battlefield(discard, 0, Enumerable.Range(0, 6).Select(index => land with { VisualId = "developed" + index, Key = "developed" + index + "-key" }).ToImmutableArray());
        check((await Evaluate(Frame(abundant, semantics: new(DecisionIntent.Discard) { SelectionIsCost = true }))).Command is ChooseOptions discardLand
                && discardLand.Choices.SequenceEqual([3]),
            "With six developed lands the same discard retains the useful body and sheds an excess land");
        var sacrifice = State(new EntitySelectionPayload(No, No, [], new(1, 1)));
        sacrifice = Battlefield(sacrifice, 0, [Card("own-land") with { Type = "Land", Power = null, Toughness = null, Selectable = true },
            Card("own-creature", 1, 1) with { Selectable = true }]);
        check((await Evaluate(Frame(sacrifice, semantics: new(DecisionIntent.Sacrifice) { SelectionIsCost = true }))).Command is SelectCard { Card: "own-creature-key" },
            "A required sacrifice protects the only developed land over a small replaceable creature");

        var authoritative = first with { Players = first.Players.Select(player => player.Id == 0 ? player with {
            Zones = player.Zones.Select(zone => zone.Name == "Battlefield" ? zone with { Cards = zone.Cards.Select(card => card.VisualId == "own-creature"
                ? card with { States = [] } : card).ToImmutableArray() } : zone).ToImmutableArray() } : player).ToImmutableArray() };
        check((await Evaluate(Frame(authoritative))).Command is CompleteAttackers,
            "An authoritative empty effective-keyword list overrides stale printed first strike during tactical evaluation");
    }

    private static GameSnapshot AttackCase(Card attacker, Card blocker)
    {
        var state = State(new AttackPayload(Yes));
        state = Battlefield(state, 0, [attacker]);
        state = Battlefield(state, 1, [blocker]);
        return state with { Combat = new([], [new(attacker.VisualId, [new("player", "1", 1, "")])], null) };
    }
    private static GameSnapshot BlockCase(Card attacker, Card blocker)
    {
        var state = State(new BlockPayload(Yes));
        state = Battlefield(state, 0, [blocker]);
        state = Battlefield(state, 1, [attacker]);
        return state with { Combat = new([new(attacker.VisualId, 0, [], [blocker.VisualId])], [], null) };
    }
    private static GameSnapshot Battlefield(GameSnapshot state, int owner, ImmutableArray<Card> cards)
        => state with { Players = state.Players.Select(player => player.Id == owner ? player with {
            Zones = player.Zones.Where(zone => zone.Name != "Battlefield").Append(new("Battlefield", cards.Length, cards, null)).ToImmutableArray()
        } : player).ToImmutableArray() };

    private static async Task OperationGraphs(Action<bool, string> check)
    {
        var state = State(new PriorityPayload(Yes));
        var actor = new EffectRecipients(SemanticKnowledge.Known, EffectRecipientBinding.Actor, null, [PlayerEntity(0)]);
        var targeted = new EffectRecipients(SemanticKnowledge.Known, EffectRecipientBinding.Targets, 0, []);
        var ownDomain = new EffectTargetDomain(SemanticKnowledge.Known, 1, 1, [CardEntity("own-creature", 0)]);
        var removal = Graph(Known(0, ActionEffectKind.Destroy, targeted) with { Targets = ownDomain });
        var route = new SourceAction("removal", "cast", "Localized description");
        var semantics = Semantic("removal", ActionEffectKind.Destroy) with { OperationGraph = removal };
        check((await Evaluate(Frame(state, Domain(state, ("hand-spell", route, semantics))))).Command is PassPriority,
            "A complete removal graph whose only legal victim is an owned permanent is declined instead of receiving a flat positive removal score");
        var enemy = Card("enemy", 8, 8);
        state = Battlefield(state, 1, [enemy]);
        var viable = removal with { Operations = [removal.Operations[0] with { Targets = ownDomain with {
            Candidates = [CardEntity("own-creature", 0), CardEntity("enemy", 1)] } }] };
        check((await Evaluate(Frame(state, Domain(state, ("hand-spell", route, semantics with { OperationGraph = viable }))))).Command is UseOfferedAction { ActionId: "removal" },
            "The same removal becomes worthwhile when its graph exposes an actual opposing victim");

        var players = Enumerable.Range(0, 4).Select(PlayerEntity).ToImmutableArray();
        var drawLoss = Graph(Known(0, ActionEffectKind.Draw, targeted, 2) with {
            Targets = new(SemanticKnowledge.Known, 1, 1, players), Links = [new(1, OperationLinkKind.Sequence)] },
            Known(1, ActionEffectKind.LifeLoss, targeted, 2));
        var targetState = State(new TargetSelectionPayload(No, No, new(1, 1), [0, 1, 2, 3]));
        check((await Evaluate(Frame(targetState, semantics: new(DecisionIntent.Damage) { OperationGraph = drawLoss, OperationId = 0 }))).Command is SelectPlayer { Player: 0 },
            "Targeted draw and life loss remain on one recipient: two cards outweigh two life for the healthy acting player despite the hostile legacy intent");
        var danger = targetState with { Players = targetState.Players.Select(player => player.Id == 0 ? player with { Life = 2 } : player).ToImmutableArray() };
        check((await Evaluate(Frame(danger, semantics: new(DecisionIntent.Benefit) { OperationGraph = drawLoss, OperationId = 0 }))).Command is SelectPlayer { Player: not 0 },
            "The same bundled target effect avoids killing its low-life actor instead of independently assigning the draw and loss to different players");
        var ownDrawLoss = drawLoss with { Operations = [drawLoss.Operations[0] with { Targets = new(SemanticKnowledge.Known, 1, 1, [PlayerEntity(0)]) }, drawLoss.Operations[1]] };
        var priorityDanger = danger with { Decision = danger.Decision! with { Payload = new PriorityPayload(Yes) } };
        var drawRoute = new SourceAction("draw-loss", "cast", "Different prose");
        check((await Evaluate(Frame(priorityDanger, Domain(priorityDanger, ("hand-spell", drawRoute,
            Semantic("draw-loss", ActionEffectKind.Draw) with { OperationGraph = ownDrawLoss }))))).Command is PassPriority,
            "Initiation declines a known self-lethal bundled draw rather than treating its root draw effect as an unconditional benefit");

        var actorSequence = Graph(Known(0, ActionEffectKind.Scry, actor, 2) with { Links = [new(1, OperationLinkKind.Sequence)] },
            Known(1, ActionEffectKind.Draw, actor, 2) with { Links = [new(2, OperationLinkKind.Sequence)] },
            Known(2, ActionEffectKind.LifeLoss, actor, 2));
        check(CasualActionAssessment.Evaluate(state, actorSequence, 256).Value > 0
                && CasualActionAssessment.Evaluate(priorityDanger, actorSequence, 256).Value < 0,
            "A sequence beginning with scry retains its later actor draw and life payment, including lethal loss at low life");
        var splitLoss = Graph(Known(0, ActionEffectKind.LifeLoss, actor, 1) with { Links = [new(1, OperationLinkKind.Sequence)] },
            Known(1, ActionEffectKind.LifeLoss, actor, 1));
        check(CasualActionAssessment.Evaluate(priorityDanger, splitLoss, 256).Value < -100,
            "Life loss is aggregated across a sequence before assessing lethal rather than pricing each individually nonlethal point in isolation");
        var recoveredLife = splitLoss with { Operations = [splitLoss.Operations[0] with { Amount = 2 },
            splitLoss.Operations[1] with { Effect = ActionEffectKind.LifeGain, Amount = 2 }] };
        check(CasualActionAssessment.Evaluate(priorityDanger, recoveredLife, 256) is { Status: CasualAssessmentStatus.Estimated, Value: 0 },
            "A supported loss-then-recovery sequence assesses net life before post-resolution lethal instead of inventing a mid-resolution defeat");

        var compensation = Graph(Known(0, ActionEffectKind.Destroy, targeted) with {
            Targets = new(SemanticKnowledge.Known, 1, 1, [CardEntity("own-creature", 0), CardEntity("enemy", 1)]),
            Links = [new(1, OperationLinkKind.Sequence)] },
            Known(1, ActionEffectKind.Draw, new(SemanticKnowledge.Known, EffectRecipientBinding.TargetControllers, 0, []), 2));
        var compensatedState = Battlefield(state with { Decision = targetState.Decision }, 0, [Card("own-creature", 1, 1) with { Selectable = true }]);
        compensatedState = Battlefield(compensatedState, 1, [enemy with { Selectable = true }]);
        compensatedState = compensatedState with { Decision = compensatedState.Decision! with { Payload = new TargetSelectionPayload(No, No, new(1, 1), []) } };
        check((await Evaluate(Frame(compensatedState, semantics: new(DecisionIntent.Destroy) { OperationGraph = compensation, OperationId = 0 }))).Command is SelectCard { Card: "own-creature-key" },
            "A compensating draw follows the destroyed creature's actual controller, so a small owned body may be a better exchange than enriching the large opposing body's controller");
        var tokenCompensation = compensation with { Knowledge = SemanticKnowledge.Unknown,
            Operations = [compensation.Operations[0], compensation.Operations[1] with { Effect = ActionEffectKind.Token, Knowledge = SemanticKnowledge.Unknown }] };
        check(CasualActionAssessment.Evaluate(state, tokenCompensation, 256).Status == CasualAssessmentStatus.Incomplete,
            "A token compensation without authoritative token characteristics stays incomplete; the evaluator does not invent a token body to price the exchange");
        check((await Evaluate(Frame(state, Domain(state, ("hand-spell", route, semantics with { OperationGraph = tokenCompensation }))))).Command is UseOfferedAction,
            "An incomplete operation graph preserves conservative legal cast exploration rather than being mislabeled a known harmful action");
        check((await Evaluate(Frame(state, Domain(state, ("hand-spell", route with { Kind = "activate" }, semantics with { OperationGraph = tokenCompensation }))))).Command is PassPriority,
            "An optional activation with an incomplete effect graph is declined conservatively instead of being upgraded to a profitable route by default");
        var ownToken = Graph(Known(0, ActionEffectKind.Token, actor, 1) with { Knowledge = SemanticKnowledge.Unknown }) with { Knowledge = SemanticKnowledge.Unknown };
        var tokenRoute = route with { Kind = "activate" };
        check((await Evaluate(Frame(state, Domain(state, ("hand-spell", tokenRoute, semantics with {
            Effect = ActionEffectKind.Token, OperationGraph = ownToken }))))).Command is UseOfferedAction,
            "Actor-only token creation remains a conservative exploration candidate when the token body is unknown; missing characteristics do not erase the resource-creation route");
        var enemyToken = ownToken with { Operations = [ownToken.Operations[0] with { Recipients = new(SemanticKnowledge.Known,
            EffectRecipientBinding.Explicit, null, [PlayerEntity(1)]) }] };
        var conditionalToken = ownToken with { Operations = [ownToken.Operations[0] with { Conditional = true }] };
        var tokenThenLoss = ownToken with { Operations = [ownToken.Operations[0] with { Links = [new(1, OperationLinkKind.Sequence)] },
            Known(1, ActionEffectKind.LifeLoss, actor, 2)] };
        check((await Evaluate(Frame(state, Domain(state, ("hand-spell", tokenRoute, semantics with { OperationGraph = enemyToken }))))).Command is PassPriority
                && (await Evaluate(Frame(state, Domain(state, ("hand-spell", tokenRoute, semantics with { OperationGraph = conditionalToken }))))).Command is PassPriority
                && (await Evaluate(Frame(state, Domain(state, ("hand-spell", tokenRoute, semantics with { OperationGraph = tokenThenLoss }))))).Command is PassPriority,
            "Unknown token exploration requires exact own recipients and excludes conditional creation or bundled harmful effects instead of assuming every token graph is favorable");

        var choose = Known(0, ActionEffectKind.Choose, actor) with { MinChoices = 1, MaxChoices = 2,
            Links = [new(1, OperationLinkKind.Choice), new(2, OperationLinkKind.Choice)] };
        var modes = Graph(choose, Known(1, ActionEffectKind.Draw, actor, 2), Known(2, ActionEffectKind.LifeLoss, actor, 40));
        check(Math.Abs(CasualActionAssessment.Evaluate(state, modes, 256).Value - 6) < .0001,
            "A finite modal graph honors optional maximum choices and excludes a harmful extra mode rather than summing every branch");
        var requiredModes = modes with { Operations = [choose with { MinChoices = 2 }, modes.Operations[1], modes.Operations[2]] };
        check(CasualActionAssessment.Evaluate(state, requiredModes, 256).Value < 0,
            "The published minimum mode count cannot be ignored to escape a required harmful branch");
        var deadRemoval = Known(2, ActionEffectKind.Destroy,
            new(SemanticKnowledge.Known, EffectRecipientBinding.Targets, 2, [])) with { Targets = new(SemanticKnowledge.Known, 1, 1, []) };
        var liveMode = Graph(choose with { MaxChoices = 1 }, modes.Operations[1], deadRemoval);
        check(CasualActionAssessment.Evaluate(state, liveMode, 256) is { Status: CasualAssessmentStatus.Estimated, Value: 6 },
            "Choosing a live draw mode does not demand a target for an unselected removal mode whose legal candidate domain is empty");
        check(CasualActionAssessment.Evaluate(state, liveMode with { Operations = [choose with { MinChoices = 2 }, modes.Operations[1], deadRemoval] }, 256).Status
                == CasualAssessmentStatus.Unsupported,
            "Requiring both modes cannot invent a target for the empty mandatory removal branch");
        check(CasualActionAssessment.Evaluate(state, liveMode with { Knowledge = SemanticKnowledge.Unknown,
            Operations = [liveMode.Operations[0], modes.Operations[1], deadRemoval with { Knowledge = SemanticKnowledge.Unknown }] }, 256).Status
                == CasualAssessmentStatus.Incomplete,
            "An unknown unselected branch still makes strategic comparison incomplete; it is not silently priced as zero");
        var repeated = Graph(choose with { MinChoices = 2, MaxChoices = 2, MayRepeatChoices = true,
            Links = [new(1, OperationLinkKind.Choice)] }, Known(1, ActionEffectKind.Draw, actor, 2));
        check(Math.Abs(CasualActionAssessment.Evaluate(state, repeated, 256).Value - 12) < .0001,
            "Explicitly repeatable modes may be selected twice, with the exact finite choice count controlling their total effect");
        var targetedRepeat = repeated with { Operations = [repeated.Operations[0],
            Known(1, ActionEffectKind.Destroy, new(SemanticKnowledge.Known, EffectRecipientBinding.Targets, 1, [])) with {
                Targets = new(SemanticKnowledge.Known, 1, 1, [CardEntity("enemy", 1)]) }] };
        check(CasualActionAssessment.Evaluate(state, targetedRepeat, 256).Status == CasualAssessmentStatus.Incomplete,
            "Repeated targeted mode recipes require independent target instances and cannot falsely reuse one target-group assignment for every copy");
        var uncertain = modes with { Operations = [choose, modes.Operations[1], modes.Operations[2] with { AmountKnowledge = SemanticKnowledge.Unknown }] };
        check(CasualActionAssessment.Evaluate(state, uncertain, 256).Status == CasualAssessmentStatus.Incomplete,
            "An unknown mode is not assigned zero value to manufacture a confident choice among incompletely modeled branches");
        var conditional = actorSequence with { Operations = actorSequence.Operations.Select(node => node.Id == 1 ? node with { Conditional = true } : node).ToImmutableArray() };
        check(CasualActionAssessment.Evaluate(state, conditional, 256).Status == CasualAssessmentStatus.Incomplete,
            "A future or hidden conditional branch stays incomplete instead of being assumed to execute");
        var recursive = Graph(Known(0, ActionEffectKind.Draw, actor, 1) with { Links = [new(0, OperationLinkKind.Sequence)] });
        check(CasualActionAssessment.Evaluate(state, recursive, 256).Status == CasualAssessmentStatus.Unsupported
                && CasualActionAssessment.Evaluate(state, viable, 1).Status == CasualAssessmentStatus.BudgetExceeded,
            "Cycles and exhausted finite-plan search return explicit statuses instead of unbounded recursion or a false negative action value");

        var options = State(new OptionsPayload(new([new(7, "Draw mode", null), new(19, "Scry mode", null)], new(1, 1))));
        options = options with { Players = options.Players.Select(player => player.Id == 0 ? player with { Life = 2 } : player).ToImmutableArray() };
        check((await Evaluate(Frame(options, semantics: new() { Options = [new(7, ActionEffectKind.Draw) { OperationGraph = ownDrawLoss },
            new(19, ActionEffectKind.Scry) { OperationGraph = Graph(Known(0, ActionEffectKind.Scry, actor, 2)) }] }))).Command is ChooseOptions chosen
                && chosen.Choices.SequenceEqual([19]),
            "A concrete mode choice compares full operation graphs and avoids self-lethal draw in favor of the useful scry option");
        var optionalOptions = State(new OptionsPayload(new([new(7, "Draw", null), new(19, "Lose life", null)], new(0, 2))));
        check((await Evaluate(Frame(optionalOptions, semantics: new() { Options = [new(7, ActionEffectKind.Draw) { OperationGraph = Graph(modes.Operations[1] with { Id = 0 }) },
            new(19, ActionEffectKind.LifeLoss) { OperationGraph = Graph(modes.Operations[2] with { Id = 0 }) }] }))).Command is ChooseOptions optionalChosen
                && optionalChosen.Choices.SequenceEqual([7]),
            "Optional concrete modes select useful modeled effects within the range without adding a harmful extra choice");
        check((await Evaluate(Frame(optionalOptions))).Command is ChooseOptions unknownOptional && unknownOptional.Choices.IsEmpty,
            "Seeded tie breakers do not manufacture positive value and fill optional slots whose effects are unknown");
        var zeroGraph = Graph(Known(0, ActionEffectKind.LifeGain, actor, 0));
        check((await Evaluate(Frame(state, Domain(state, ("hand-spell", route, semantics with { OperationGraph = zeroGraph }))))).Command is PassPriority,
            "Affordability and deterministic jitter cannot turn a completely modeled zero-value action into a profitable one");
        var damageGraph = Graph(Known(0, ActionEffectKind.Damage, targeted, 1) with { Targets = new(SemanticKnowledge.Known, 1, 1, players) });
        var threatened = Battlefield(targetState, 2, [Card("public-threat", 12, 12)]);
        check((await Evaluate(Frame(threatened, semantics: new() { OperationId = 0, OperationGraph = damageGraph }))).Command is SelectPlayer { Player: 2 },
            "When graph-modeled hostile player effects have equal material value, the policy still softly favors the publicly stronger opponent");
        var renamed = targetState with { Players = targetState.Players.Reverse().Select(player => player with { Name = "Other display name",
            Zones = player.Zones.Reverse().Select(zone => zone with { Cards = zone.Cards.Reverse().Select(card => card with {
                Name = "Other face name", Text = "Unrelated translated prose" }).ToImmutableArray() }).ToImmutableArray() }).ToImmutableArray() };
        var shuffled = drawLoss with { Operations = drawLoss.Operations.Reverse().Select(node => node.Targets == null ? node : node with {
            Targets = node.Targets with { Candidates = node.Targets.Candidates.Reverse().ToImmutableArray() } }).ToImmutableArray() };
        check((await Evaluate(Frame(renamed, semantics: new(DecisionIntent.Unknown) { OperationGraph = shuffled, OperationId = 0 }))).Command is SelectPlayer { Player: 0 }
                && CasualActionAssessment.Evaluate(state, modes with { Operations = modes.Operations.Reverse().Select(node => node.Id == 0
                    ? node with { Links = node.Links.Reverse().ToImmutableArray() } : node).ToImmutableArray() }, 256).Value == 6,
            "Graph recipient and mode estimates survive renamed prose, player/card enumeration changes, node reordering and reordered choice links");
    }
    private static SemanticEntity PlayerEntity(int id) => new("player:" + id, SemanticEntityKind.Player, PlayerId: id, ControllerId: id);
    private static SemanticEntity CardEntity(string id, int controller) => new(id, SemanticEntityKind.Card, id, ControllerId: controller, OwnerId: controller);
    private static ActionOperation Known(int id, ActionEffectKind effect, EffectRecipients recipients, int? amount = null)
        => new(id, effect) { Knowledge = SemanticKnowledge.Known, Recipients = recipients, ActorPlayerId = 0,
            Sources = new(SemanticKnowledge.Known, EffectRecipientBinding.Source, null, [CardEntity("hand-spell", 0)]),
            Amount = amount, AmountKnowledge = amount == null ? SemanticKnowledge.Unknown : SemanticKnowledge.Known };
    private static ActionOperationGraph Graph(params ActionOperation[] nodes) => new(SemanticKnowledge.Known, 0, nodes.ToImmutableArray());

    private static async Task BoundedGraphOptimization(Action<bool, string> check)
    {
        var state = Battlefield(State(new PriorityPayload(Yes)), 1,
            Enumerable.Range(0, 40).Select(index => Card("wide-" + index, 2, 3)).ToImmutableArray());
        var graph = Graph(Known(0, ActionEffectKind.Destroy,
            new(SemanticKnowledge.Known, EffectRecipientBinding.Targets, 0, [])) with {
                Targets = new(SemanticKnowledge.Known, 2, 2, state.Players.First(player => player.Id == 1).Zone("Battlefield").Cards
                    .Select(card => CardEntity(card.VisualId, 1)).ToImmutableArray()) });
        var probe = new GraphBudgetProbe(graph);
        using (var session = new AiSeatSession(state.Id, 0, probe, 17, new(TimeSpan.FromSeconds(5), 1024))) {
            session.Observe(Frame(state)); var result = await session.EvaluateAsync();
            check(probe.LocalIsolation && probe.Estimate is { Status: CasualAssessmentStatus.EstimatedPartial, Value: > 0 }
                    && !probe.GlobalExceeded && probe.Remaining >= 4 && result.Status == AiSeatStatus.Ready,
                "Large-target optimization retains a completed positive plan at its local limit without exhausting the shared decision or claiming a global optimum");
        }
        var semantic = Semantic("wide-removal", ActionEffectKind.Destroy) with { OperationGraph = graph };
        var frame = Frame(state, Domain(state, ("hand-spell", new("wide-removal", "cast", "Two targets"), semantic)));
        check((await Evaluate(frame)).Command is UseOfferedAction { ActionId: "wide-removal" },
            "A useful legal two-target route on a forty-creature board remains playable when comparison search is bounded");

        var targets = Enumerable.Range(0, 64).Select(index => Card("required-" + index, 2, 3) with { Selectable = true }).ToImmutableArray();
        var selection = Battlefield(State(new TargetSelectionPayload(No, No, new(64, 64), [])), 1, targets);
        var requiredGraph = graph with { Operations = [graph.Operations[0] with { Targets = new(SemanticKnowledge.Known, 64, 64,
            targets.Select(card => CardEntity(card.VisualId, 1)).ToImmutableArray()) }] };
        var semantics = new DecisionSemantics(DecisionIntent.Destroy) { OperationGraph = requiredGraph, OperationId = 0, EpisodeId = "sixty-four-required-targets" };
        using var required = new AiSeatSession(selection.Id, 0, new CasualCommanderPolicy(), 17, new(TimeSpan.FromSeconds(5), 128));
        var selected = new HashSet<string>();
        bool allReady = true, distinct = true;
        for (int step = 0; step < 64; step++) {
            var next = selection with { Revision = step + 1, BoardRevision = step + 1, Decision = selection.Decision! with { Id = "required-input-" + step } };
            next = Battlefield(next, 1, targets.Select(card => card with { Highlighted = selected.Contains(card.Key) }).ToImmutableArray());
            required.Observe(Frame(next, semantics: semantics));
            allReady &= (await required.EvaluateAsync()).Status == AiSeatStatus.Ready;
            var answer = required.TakeCommand();
            if (answer?.Command is not SelectCard selectionAnswer || !selected.Add(selectionAnswer.Card)) { distinct = false; break; }
            required.Report(answer, AiExecutionReceipt.Accepted);
        }
        var complete = selection with { Revision = 65, BoardRevision = 65,
            Decision = selection.Decision! with { Id = "required-complete", Payload = new TargetSelectionPayload(Yes, No, new(64, 64), []) } };
        complete = Battlefield(complete, 1, targets.Select(card => card with { Highlighted = true }).ToImmutableArray());
        required.Observe(Frame(complete, semantics: semantics));
        var completed = await required.EvaluateAsync();
        check(allReady && distinct && selected.Count == 64 && completed.Status == AiSeatStatus.Ready
                && required.TakeCommand()?.Command is CompleteSelection,
            "One causal allowance survives sixty-four required target selections and final confirmation by reserving mechanical progress instead of spending it all on comparisons");
    }

    private static async Task BoundedCombatOptimization(Action<bool, string> check)
    {
        var attackers = Enumerable.Range(0, 40).Select(index => Card("large-attacker-" + index.ToString("D2"), 10, 10)).ToImmutableArray();
        var defenders = Enumerable.Range(1, 3).Select(id => new Defender("player", id.ToString(), id, "Opponent")).ToImmutableArray();
        var attack = Battlefield(State(new AttackPayload(Yes)), 0, attackers);
        foreach (int id in Enumerable.Range(1, 3)) attack = Battlefield(attack, id,
            Enumerable.Range(0, 40).Select(index => Card("enemy-" + id + "-" + index.ToString("D2"), 1, 1)).ToImmutableArray());
        attack = attack with { Combat = new([], attackers.Select(card => new AttackOption(card.VisualId, defenders)).ToImmutableArray(), null) };
        check((await Evaluate(Frame(attack))).Command is ToggleAttackPlayer,
            "Forty attackers, three defenders and forty blockers per defender retain a fully assessed attack instead of exhausting the whole seat allowance on 4800 comparisons");

        var dangerous = attack;
        foreach (int id in Enumerable.Range(1, 3)) dangerous = Battlefield(dangerous, id,
            dangerous.Players.First(player => player.Id == id).Zone("Battlefield").Cards.Select((card, index) => index == 39
                ? card with { CombatKeywords = ["deathtouch"] } : card).ToImmutableArray());
        check((await Evaluate(Frame(dangerous))).Command is CompleteAttackers,
            "A capped attack comparison discards its unfinished defender rather than overlooking the last lethal blocker and inventing a safe attack");

        var staged = new List<Attack>();
        var attackSemantics = new DecisionSemantics { EpisodeId = "forty-wide-attack-and-repair" };
        using var attacking = Session();
        bool attacksReady = true, attackCompleted = false;
        int revision = 0;
        for (int step = 0; step <= 40; step++) {
            var next = attack with { Revision = ++revision, BoardRevision = revision,
                Decision = attack.Decision! with { Id = "large-attack-" + revision }, Combat = attack.Combat! with { Attackers = staged.ToImmutableArray() } };
            attacking.Observe(Frame(next, semantics: attackSemantics));
            attacksReady &= (await attacking.EvaluateAsync()).Status == AiSeatStatus.Ready;
            var answer = attacking.TakeCommand();
            if (answer?.Command is CompleteAttackers) { attacking.Report(answer, AiExecutionReceipt.Accepted); attackCompleted = true; break; }
            if (answer?.Command is not ToggleAttackPlayer toggle) { attacksReady = false; break; }
            staged.Add(new(attackers.Single(card => card.Key == toggle.Attacker).VisualId, toggle.Player, [], []));
            attacking.Report(answer, AiExecutionReceipt.Accepted);
        }
        // The same causal work allowance must still service an authoritative
        // joint requirement witness after optional comparison work has stopped.
        var witness = attackers.Select(card => new AttackAssignment(card.VisualId, defenders[2])).ToImmutableArray();
        bool repairReady = true, repairCompleted = false;
        for (int step = 0; step <= 80; step++) {
            bool valid = staged.Count == 40 && staged.All(item => item.DefendingPlayerId == 3);
            var next = attack with { Revision = ++revision, BoardRevision = revision,
                Decision = attack.Decision! with { Id = "large-repair-" + revision }, Combat = attack.Combat! with {
                    Attackers = staged.ToImmutableArray(), AttackValid = valid, AttackRepair = valid ? [] : witness } };
            attacking.Observe(Frame(next, semantics: attackSemantics));
            repairReady &= (await attacking.EvaluateAsync()).Status == AiSeatStatus.Ready;
            var answer = attacking.TakeCommand();
            if (answer?.Command is CompleteAttackers) { repairCompleted = valid; break; }
            if (answer?.Command is not ToggleAttackPlayer toggle) { repairReady = false; break; }
            string id = attackers.Single(card => card.Key == toggle.Attacker).VisualId;
            int current = staged.FindIndex(item => item.CardId == id);
            if (current >= 0) staged.RemoveAt(current); else staged.Add(new(id, toggle.Player, [], []));
            attacking.Report(answer, AiExecutionReceipt.Accepted);
        }
        check(attacksReady && attackCompleted && repairReady && repairCompleted,
            "Large attack optimization preserves one shared episode's mechanical allowance for refreshed declarations, forty required witness assignments and final completion");

        var blockers = Enumerable.Range(0, 40).Select(index => Card("large-blocker-" + index.ToString("D2"), 3, 4)
            with { CombatKeywords = ["first strike"] }).ToImmutableArray();
        var enemies = Enumerable.Range(0, 40).Select(index => Card("incoming-" + index.ToString("D2"), 2, 3)).ToImmutableArray();
        var block = Battlefield(Battlefield(State(new BlockPayload(No)), 0, blockers), 1, enemies);
        var blockAssignments = enemies.Select(card => new Attack(card.VisualId, 0, [], blockers.Select(item => item.VisualId).ToImmutableArray())).ToImmutableArray();
        using var blocking = Session();
        var blockSemantics = new DecisionSemantics { EpisodeId = "forty-staged-required-blocks" };
        bool blocksReady = true, distinct = true;
        var used = new HashSet<string>();
        for (int step = 0; step < 40; step++) {
            var next = block with { Revision = step + 1, BoardRevision = step + 1,
                Decision = block.Decision! with { Id = "large-block-" + step }, Combat = new(blockAssignments, [], null) };
            blocking.Observe(Frame(next, semantics: blockSemantics));
            blocksReady &= (await blocking.EvaluateAsync()).Status == AiSeatStatus.Ready;
            var answer = blocking.TakeCommand();
            if (answer?.Command is not ToggleBlock toggle || !used.Add(toggle.Blocker)) { distinct = false; break; }
            string attacker = enemies.Single(card => card.Key == toggle.Attacker).VisualId;
            string blocker = blockers.Single(card => card.Key == toggle.Blocker).VisualId;
            blockAssignments = blockAssignments.Select(item => item.CardId == attacker ? item with { BlockerIds = [blocker] } : item).ToImmutableArray();
            blocking.Report(answer, AiExecutionReceipt.Accepted);
        }
        var final = block with { Revision = 41, BoardRevision = 41,
            Decision = block.Decision! with { Id = "large-block-complete", Payload = new BlockPayload(Yes) }, Combat = new(blockAssignments, [], null) };
        blocking.Observe(Frame(final, semantics: blockSemantics));
        var completed = await blocking.EvaluateAsync();
        check(blocksReady && distinct && used.Count == 40 && blockAssignments.All(item => item.BlockerIds.Length == 1)
                && completed.Status == AiSeatStatus.Ready && blocking.TakeCommand()?.Command is CompleteBlockers,
            "Forty successive required block assignments and completion share 1024 work units without an optional comparison search starving the remaining legal commands");
    }

    private sealed class GraphBudgetProbe(ActionOperationGraph graph) : IAiPolicy
    {
        public AiPolicyDescriptor Descriptor { get; } = new("graph-budget-probe", AiTasks.All);
        internal CasualActionEstimate? Estimate;
        internal bool LocalIsolation, GlobalExceeded;
        internal int Remaining;
        public Task<AiProposal> EvaluateAsync(AiDecisionRequest request, CancellationToken cancellation)
        {
            int before = request.Work.Remaining;
            var local = request.Work.CreateSlice(2, 4);
            LocalIsolation = local.TrySpend(2) && !local.TrySpend() && local.WasExceeded
                && !request.Work.WasExceeded && request.Work.Remaining == before - 2;
            Estimate = CasualActionAssessment.Evaluate(request.Frame.State, graph, request.Work);
            GlobalExceeded = request.Work.WasExceeded; Remaining = request.Work.Remaining;
            return Task.FromResult<AiProposal>(new AiProposal.Answer(new PassPriority(request.Frame.State.Id, request.Frame.Stamp.DecisionId), "bounded-probe"));
        }
    }

    private static ActionSemantics Semantic(string id, ActionEffectKind effect) => new(id, effect, [effect], [], ActionAffordability.Payable) { StableId = id };
    private static DecisionActionDomain Domain(GameSnapshot state, params (string Source, SourceAction Action, ActionSemantics Semantics)[] offers)
        => new(1, ActionDomainCoverage.Complete, offers.GroupBy(item => item.Source).Select(group => {
            var source = CardViews.FindZoneOccurrence(state, group.Key)!.Value.Card;
            return new SourceActionSet(state.Id, state.Decision!.Id, source.Key, source.VisualId, group.Select(item => item.Action).ToImmutableArray());
        }).ToImmutableArray(), offers.Select(item => item.Semantics).ToImmutableArray());
    private static AiDecisionFrame Frame(GameSnapshot state, DecisionActionDomain? domain = null, DecisionSemantics? semantics = null)
        => new(state, domain: domain ?? (state.Decision!.Payload is PriorityPayload or PaymentPayload ? Domain(state) : DecisionActionDomain.None), semantics: semantics);
    private static async Task<(AiSeatStatus Status, DecisionCommand? Command)> Evaluate(AiDecisionFrame frame, long seed = 17)
    {
        using var session = Session(seed);
        session.Observe(frame); var evaluated = await session.EvaluateAsync();
        return (evaluated.Status, session.TakeCommand()?.Command);
    }
    private static AiSeatSession Session(long seed = 17) => new("policy-game", 0, new CasualCommanderPolicy(), seed, new(TimeSpan.FromSeconds(5), 1024));
    private static Card Card(string id, int power = 2, int toughness = 3) => new() {
        Key = id + "-key", VisualId = id, CombatId = id, Name = "Printed face", Power = power, Toughness = toughness, Type = "Creature" };
    private static GameSnapshot State(DecisionPayload payload)
    {
        var hand = ImmutableArray.Create(Card("hand-land") with { Type = "Basic Land", Power = null, Toughness = null },
            Card("hand-land-2") with { Type = "Basic Land", Power = null, Toughness = null }, Card("hand-spell"));
        return new("policy-game", "playing", null, null, 0, 4, "Main", "MAIN1", 0,
            [new(0, "You", 40, true, false, ImmutableDictionary<string, int>.Empty,
                [new("Battlefield", 2, [Card("own-creature"), Card("own-land") with { Type = "Basic Land", Power = null, Toughness = null }], null), new("Hand", hand.Length, hand, null)]),
             new(1, "One", 40, false, false, ImmutableDictionary<string, int>.Empty, []),
             new(2, "Two", 40, false, false, ImmutableDictionary<string, int>.Empty, []),
             new(3, "Three", 40, false, false, ImmutableDictionary<string, int>.Empty, [])], [],
            payload is AttackPayload or BlockPayload ? new([], [], null) : null, new("input", new(), payload)) {
                Revision = 1, BoardRevision = 1, DecisionOwnerId = 0, PriorityPlayerId = 0 };
    }
}
