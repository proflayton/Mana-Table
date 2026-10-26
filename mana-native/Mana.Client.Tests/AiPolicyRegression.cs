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
                && bounded.History.Decisions.All(record => record.Answer is not UseOfferedAction),
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
        var colorless = Semantic("colorless", ActionEffectKind.Mana) with { ManaAbility = true, ManaColors = ["C"], ManaAmount = 2 };
        var blue = Semantic("blue", ActionEffectKind.Mana) with { ManaAbility = true, ManaColors = ["U"], ManaAmount = 1 };
        var manaDomain = Domain(manaState, ("own-creature", new("colorless", "activate", "Tap"), colorless),
            ("own-land", new("blue", "activate", "Tap"), blue));
        var payment = await Evaluate(Frame(manaState, manaDomain, new() { UnpaidMana = ["{U}"] }));
        check(payment.Command is UseOfferedAction { ActionId: "blue" },
            "A remaining blue shard chooses its offered blue source rather than a larger colorless producer");

        var colorlessPayment = await Evaluate(Frame(manaState, manaDomain, new() { UnpaidMana = ["{C}"] }));
        check(colorlessPayment.Command is UseOfferedAction { ActionId: "colorless" }, "True colorless requirements remain distinct from colored and generic mana");
        var sacrifice = colorless with { ActionId = "sacrifice", Costs = [new(ActionCostKind.Sacrifice, 1, true, "")] };
        var safe = colorless with { ActionId = "safe", ManaAmount = 1 };
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
        var blueSpell = Semantic("blue-spell", ActionEffectKind.Draw) with { Affordability = ActionAffordability.Unknown,
            Costs = [new(ActionCostKind.Mana, 0, false, "") { ManaSymbols = ["{U}"] }] };
        var onlyColorless = Domain(state, ("hand-spell", new("blue-spell", "cast", "Spell"), blueSpell),
            ("own-land", new("colorless", "activate", "Mana"), colorless));
        check((await Evaluate(Frame(state, onlyColorless))).Command is PassPriority,
            "Colorless production cannot be mistaken for funding a blue spell during sequencing");
        var blueFunded = Domain(state, ("hand-spell", new("blue-spell", "cast", "Spell"), blueSpell),
            ("own-land", new("blue", "activate", "Mana"), blue));
        check((await Evaluate(Frame(state, blueFunded))).Command is UseOfferedAction { ActionId: "blue-spell" },
            "The same spell becomes a policy candidate when a genuine blue production route is disclosed");

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
