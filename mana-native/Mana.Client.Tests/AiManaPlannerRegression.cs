using System.Collections.Immutable;
using Mana.AI;
using Mana.Contracts;

internal static class AiManaPlannerRegression
{
    internal static void Run(Action<bool, string> check)
    {
        var dual = Producer("dual", ("u", Output(("U", 1))), ("g", Output(("G", 1))));
        var colorless = Producer("colorless", ("c", Output(("C", 1))));
        check(AiManaPlanner.Evaluate(Frame(dual), ["{U}", "{G}"]).Status == AiManaPlanStatus.Shortfall,
            "One dual land cannot be assigned to both exact colored shards");
        check(AiManaPlanner.Evaluate(Frame(dual, colorless), ["{U}", "{G}"]).Status == AiManaPlanStatus.Shortfall,
            "A spare colorless source cannot repair competing uses of one dual land");
        var bundle = Producer("bundle", ("both", Output(("U", 1), ("G", 1))));
        var bundled = AiManaPlanner.Evaluate(Frame(bundle), ["{U}", "{G}"]);
        check(bundled.Status == AiManaPlanStatus.Funded && bundled.Steps.OfType<AiManaStep.Source>().Count() == 1,
            "A simultaneous multicolor bundle funds both shards with exactly one source activation");
        var blue = Producer("dedicated", ("blue", Output(("U", 1))));
        var plan = AiManaPlanner.Evaluate(Frame(dual, blue), ["{U}", "{G}"]);
        check(plan.Status == AiManaPlanStatus.Funded && plan.Steps.OfType<AiManaStep.Source>().Any(step => step.ActionId == "g")
                && plan.Steps.OfType<AiManaStep.Source>().Any(step => step.ActionId == "blue"),
            "A complete funding witness reserves the flexible source for green and assigns the dedicated blue source");
        check(AiManaPlanner.Evaluate(Frame(dual, colorless), ["{U}"], generic: 1).Status == AiManaPlanStatus.Funded,
            "Generic demand accepts colorless while keeping an exact colored assignment");
        check(AiManaPlanner.Evaluate(Frame(blue), ["{C}"]).Status == AiManaPlanStatus.Shortfall,
            "True colorless demand cannot be satisfied by otherwise spare colored production");
        check(AiManaPlanner.Evaluate(Frame(blue), ["{U/G}"]).Status == AiManaPlanStatus.Funded
                && AiManaPlanner.Evaluate(Frame(colorless), ["{2/U}"]).Status == AiManaPlanStatus.Shortfall,
            "Hybrid alternatives preserve one-of colors and the actual two-generic alternative cost");
        check(AiManaPlanner.Evaluate(Frame(Producer("ring", ("two", Output(("C", 2))))), ["{2/U}"]).Status == AiManaPlanStatus.Funded,
            "A two-generic hybrid alternative can use one genuine two-mana bundle");
        check(AiManaPlanner.Evaluate(Frame(colorless), ["{U/P}"]).Status == AiManaPlanStatus.Unknown,
            "Failure to fund a phyrexian color does not deny its unmodeled life-payment alternative");
        check(AiManaPlanner.Evaluate(Frame(blue), ["{X}", "{U}"]).Status == AiManaPlanStatus.Unknown
                && AiManaPlanner.Evaluate(Frame(blue), ["{S}"]).Status == AiManaPlanStatus.Unknown,
            "Unchosen X and snow provenance cannot silently become a complete zero-cost bill");
        var unknown = dual with { Actions = dual.Actions.Select(action => action with { Production = ManaProduction.Unknown }).ToImmutableArray() };
        check(AiManaPlanner.Evaluate(Frame(unknown), ["{G}"]).Status == AiManaPlanStatus.Unknown,
            "An incomplete production projection remains unknown rather than a fabricated funding proof or shortfall");
        var zero = Producer("empty-resource", ("empty", Output()));
        var bluePair = Producer("blue-pair", ("pair", Output(("U", 2))));
        check(AiManaPlanner.Evaluate(Frame(zero), ["{1}"]).Status == AiManaPlanStatus.Shortfall
                && AiManaPlanner.Evaluate(Frame(zero, bluePair), ["{G}", "{G}"]).Status == AiManaPlanStatus.Shortfall,
            "Known zero output contributes neither capacity nor uncertainty about a missing color");
        var unknownZero = zero with { Actions = zero.Actions.Select(action => action with { Production = ManaProduction.Unknown }).ToImmutableArray() };
        check(AiManaPlanner.Evaluate(Frame(unknownZero, bluePair), ["{G}", "{G}"]).Status == AiManaPlanStatus.Unknown
                && AiManaPlanner.Evaluate(Frame(zero, bluePair), ["{U}", "{U}"]).Status == AiManaPlanStatus.Funded,
            "Legacy zero metadata cannot replace unknown production, while a known empty bundle does not interfere with a real funding witness");
        var free = blue with { Actions = blue.Actions.Select(action => action with { Costs = [] }).ToImmutableArray() };
        check(AiManaPlanner.Evaluate(Frame(free), ["{U}", "{U}"]).Status == AiManaPlanStatus.Unknown,
            "A free producer's unmodeled reuse cannot be declared insufficient after assuming it activates only once");
        var filter = bundle with { Actions = bundle.Actions.Select(action => action with {
            Costs = [new(ActionCostKind.Mana, 1, false, "")] }).ToImmutableArray() };
        check(AiManaPlanner.Evaluate(Frame(filter, colorless), ["{U}", "{G}"]).Status == AiManaPlanStatus.Unknown,
            "A source activation's mana dependency is not counted as free production");
        var lens = Producer("lens", ("plain", Output(("C", 1))), ("filter", Output(("U", 1))));
        lens = lens with { Actions = lens.Actions.Select(action => action.ActionId == "filter" ? action with {
            Costs = action.Costs.Add(new(ActionCostKind.Mana, 1, false, "")) } : action).ToImmutableArray() };
        check(AiManaPlanner.Evaluate(Frame(lens), ["{3}"]).Status == AiManaPlanStatus.Shortfall,
            "One tapped physical source cannot produce three nominal units even when its paid filter dependency is unmodeled");
        check(AiManaPlanner.Evaluate(Frame(lens, colorless), ["{U}"]).Status == AiManaPlanStatus.Unknown,
            "A sufficient capacity bound is only a necessary condition and never pretends to solve the filter dependency");
        var stone = Producer("stone", ("stone-mana", Output(("C", 1))));
        stone = stone with { Card = stone.Card with { Type = "Artifact" } };
        var draw = new ActionSemantics("stone-draw", ActionEffectKind.Draw, [ActionEffectKind.Draw],
            [new(ActionCostKind.Mana, 1, false, ""), new(ActionCostKind.Tap, 1, true, ""), new(ActionCostKind.Sacrifice, 1, true, "")],
            ActionAffordability.Unknown);
        stone = stone with { Offers = stone.Offers.Add(new("stone-draw", "activate", "Draw")), Actions = stone.Actions.Add(draw) };
        check(AiManaPlanner.EvaluateAction(Frame(stone), draw).Status == AiManaPlanStatus.Shortfall,
            "A permanent reserved for an activation's tap cost cannot also be tapped to fund that activation");
        check(AiManaPlanner.EvaluateAction(Frame(stone, colorless), draw).Status == AiManaPlanStatus.Funded,
            "Another physical producer funds the same activation without spending its reserved tap resource twice");
        var waiting = Propose(Frame(stone));
        var funded = Propose(Frame(stone, colorless));
        check(waiting is PassPriority && funded is UseOfferedAction { ActionId: "stone-draw" },
            "The policy waits on an unfunded tap-and-sacrifice activation, then uses it when another physical producer can fund the bill");
        var scarce = AiManaPlanner.Evaluate(Frame(dual, blue), ["{U}", "{G}"], workLimit: 1);
        check(scarce.Status == AiManaPlanStatus.BudgetExceeded && scarce.WorkSpent == 1 && scarce.Steps.IsEmpty,
            "Exhausted search returns an explicit bounded result without publishing a partial assignment as funded");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        bool cancelled = false;
        try { AiManaPlanner.Evaluate(Frame(blue), ["{U}"], cancellation: cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        check(cancelled, "Cancellation interrupts planning before any hypothetical source can become a command");
        Matrix(check);
        EnumerationAndPresentation(check);
        ConsumingResources(check);

        static DecisionCommand? Propose(AiDecisionFrame frame)
        {
            var recorded = new AiDecisionReplayCase(1, "activation-resource-conflict", "fixture.v1",
                frame.State, frame.Domain, frame.Semantics, new([], []), 731);
            return AiDecisionReplay.EvaluateAsync(recorded, new CasualCommanderPolicy()).GetAwaiter().GetResult().Proposal;
        }
    }

    private static void ConsumingResources(Action<bool, string> check)
    {
        var token = Producer("consumable", ("sacrifice", Output(("C", 1))));
        token = token with { Actions = token.Actions.Select(action => action with {
            Costs = [new(ActionCostKind.Sacrifice, 1, true, "")], Affordability = ActionAffordability.Unknown
        }).ToImmutableArray() };
        var land = Producer("persistent", ("tap", Output(("C", 1))));
        var exact = AiManaPlanner.Evaluate(Frame(token, land), [], generic: 2);
        check(exact.Status == AiManaPlanStatus.Funded && exact.Steps.OfType<AiManaStep.Source>().Select(step => step.SourceCardId).ToHashSet().SetEquals(["consumable", "persistent"]),
            "A disclosed self-sacrifice producer and a distinct tap producer fund exactly two units without inventing repeatable production");
        check(AiManaPlanner.Evaluate(Frame(token, land), [], generic: 4).Status == AiManaPlanStatus.Shortfall,
            "Two known one-use resources cannot start a four-mana cast merely because one resource is consumed");
        check(AiManaPlanner.Evaluate(Frame(token, land), [], generic: 1).Steps.OfType<AiManaStep.Source>().Single().SourceCardId == "persistent",
            "A simple sufficient tap producer is preserved ahead of unnecessarily sacrificing a separate mana resource");
        var alternatives = token with { Offers = token.Offers.Add(new("other-color", "activate", "Alternative production")),
            Actions = token.Actions.Add(token.Actions[0] with { ActionId = "other-color", StableId = "other-color",
                Production = new(SemanticKnowledge.Known, [Output(("G", 1))]) }) };
        check(AiManaPlanner.Evaluate(Frame(alternatives), ["{C}", "{G}"]).Status == AiManaPlanStatus.Shortfall,
            "Alternative outputs of a self-sacrifice source cannot consume the same occurrence twice");
        var tapAndSacrifice = token with { Actions = token.Actions.Select(action => action with {
            Costs = [new(ActionCostKind.Tap, 1, true, ""), new(ActionCostKind.Sacrifice, 1, true, "")]
        }).ToImmutableArray() };
        check(AiManaPlanner.Evaluate(Frame(tapAndSacrifice), [], generic: 1).Status == AiManaPlanStatus.Funded
                && AiManaPlanner.Evaluate(Frame(tapAndSacrifice), [], generic: 2).Status == AiManaPlanStatus.Shortfall,
            "One route that both taps and sacrifices its source still supplies its single disclosed bundle only once");
        var mixed = token with { Offers = token.Offers.Add(new("tap-before-sacrifice", "activate", "Tap for mana")),
            Actions = token.Actions.Add(token.Actions[0] with { ActionId = "tap-before-sacrifice", StableId = "tap-before-sacrifice",
                Costs = [new(ActionCostKind.Tap, 1, true, "")] }) };
        var sequence = AiManaPlanner.Evaluate(Frame(mixed), [], generic: 2);
        check(sequence.Status == AiManaPlanStatus.Funded && sequence.Steps.OfType<AiManaStep.Source>().Select(step => step.ActionId)
                .SequenceEqual(["tap-before-sacrifice", "sacrifice"])
                && AiManaPlanner.Evaluate(Frame(mixed), [], generic: 3).Status == AiManaPlanStatus.Shortfall,
            "A source can tap then sacrifice through distinct routes without being tapped or consumed twice");
        var colored = mixed with { Actions = mixed.Actions.Select(action => action.ActionId == "sacrifice" ? action with {
            Production = new(SemanticKnowledge.Known, [Output(("G", 1))])
        } : action).ToImmutableArray() };
        check(AiManaPlanner.Evaluate(Frame(colored), ["{G}", "{C}"]).Status == AiManaPlanStatus.Funded,
            "Funding can preserve the tap output for a later shard before consuming that source for the currently scarce color");
        var combined = mixed with { Offers = mixed.Offers.Add(new("tap-and-sacrifice", "activate", "Combined route")),
            Actions = mixed.Actions.Add(mixed.Actions[0] with { ActionId = "tap-and-sacrifice", StableId = "tap-and-sacrifice",
                Costs = [new(ActionCostKind.Tap, 1, true, ""), new(ActionCostKind.Sacrifice, 1, true, "")],
                Production = new(SemanticKnowledge.Known, [Output(("C", 3))]) }) };
        check(AiManaPlanner.Evaluate(Frame(combined), [], generic: 3).Status == AiManaPlanStatus.Funded
                && AiManaPlanner.Evaluate(Frame(combined), [], generic: 4).Status == AiManaPlanStatus.Shortfall,
            "Tap-plus-sacrifice is an alternative to tapping then sacrificing, so its three units cannot be added to that same source's earlier tap");
        var paid = token with { Actions = token.Actions.Select(action => action with {
            Costs = [new(ActionCostKind.Sacrifice, 1, true, ""), new(ActionCostKind.Mana, 1, false, "")]
        }).ToImmutableArray() };
        check(AiManaPlanner.Evaluate(Frame(paid), [], generic: 1).Status == AiManaPlanStatus.Unknown,
            "A paid consuming producer cannot fund its own prerequisite from output that has not been created");
        var foreign = token with { Actions = token.Actions.Select(action => action with {
            Costs = [new(ActionCostKind.Sacrifice, 1, false, "")]
        }).ToImmutableArray() };
        check(AiManaPlanner.Evaluate(Frame(foreign), [], generic: 1).Status == AiManaPlanStatus.Unknown,
            "A sacrifice of an unspecified donor is not silently treated as consuming only the producer");
        var draw = new ActionSemantics("consume-for-draw", ActionEffectKind.Draw, [ActionEffectKind.Draw],
            [new(ActionCostKind.Mana, 1, false, ""), new(ActionCostKind.Sacrifice, 1, true, "")], ActionAffordability.Unknown);
        var donor = land with { Offers = land.Offers.Add(new(draw.ActionId, "activate", "Draw")), Actions = land.Actions.Add(draw) };
        check(AiManaPlanner.EvaluateAction(Frame(donor), draw).Status == AiManaPlanStatus.Shortfall
                && AiManaPlanner.EvaluateAction(Frame(donor, token), draw).Status == AiManaPlanStatus.Funded,
            "An initiating sacrifice reserves that source while a separate consuming producer can fund its mana prerequisite");
        int cases = 0, mismatches = 0;
        string[] colors = ["U", "G", "C"];
        foreach (string tapColor in colors) foreach (string sacrificeColor in colors) foreach (string combinedColor in colors) {
            var resource = Producer("ledger", ("tap", Output((tapColor, 1))), ("sacrifice", Output((sacrificeColor, 1))),
                ("combined", Output((combinedColor, 3))));
            resource = resource with { Actions = resource.Actions.Select(action => action with { Costs = action.ActionId switch {
                "sacrifice" => [new(ActionCostKind.Sacrifice, 1, true, "")],
                "combined" => [new(ActionCostKind.Tap, 1, true, ""), new(ActionCostKind.Sacrifice, 1, true, "")],
                _ => [new(ActionCostKind.Tap, 1, true, "")]
            } }).ToImmutableArray() };
            // Independent finite outcome set: either the combined activation,
            // or an optional tap followed by an optional pure sacrifice.
            var outcomes = new[] {
                new[] { tapColor }, new[] { sacrificeColor }, new[] { tapColor, sacrificeColor },
                new[] { combinedColor, combinedColor, combinedColor }
            };
            foreach (string first in colors) foreach (string second in colors) for (int generic = 0; generic <= 2; generic++) {
                bool expected = outcomes.Any(units => {
                    var remaining = units.ToList();
                    return remaining.Remove(first) && remaining.Remove(second) && remaining.Count >= generic;
                });
                var assessed = AiManaPlanner.Evaluate(Frame(resource), ["{" + first + "}", "{" + second + "}"], generic);
                if (assessed.Status != (expected ? AiManaPlanStatus.Funded : AiManaPlanStatus.Shortfall)) mismatches++;
                cases++;
            }
        }
        check(cases == 729 && mismatches == 0,
            "The physical resource ledger agrees with 729 independently enumerated tap, sacrifice, combined-cost, exact-color and generic funding outcomes");
    }

    private static void Matrix(Action<bool, string> check)
    {
        // Independent exhaustive oracle for two one-unit producers. These small
        // assignments have a complete answer; no policy scores are the oracle.
        string[] colors = ["U", "G", "C"];
        int mismatches = 0, cases = 0;
        for (int first = 1; first < 8; first++) for (int second = 1; second < 8; second++)
            foreach (string a in colors) foreach (string b in colors) {
                var left = Enumerable.Range(0, 3).Where(index => (first & 1 << index) != 0).Select(index => colors[index]).ToArray();
                var right = Enumerable.Range(0, 3).Where(index => (second & 1 << index) != 0).Select(index => colors[index]).ToArray();
                bool expected = left.Any(x => right.Any(y => x == a && y == b || x == b && y == a));
                var frame = Frame(Producer("first", left.Select((color, index) => ("a" + index, Output((color, 1)))).ToArray()),
                    Producer("second", right.Select((color, index) => ("b" + index, Output((color, 1)))).ToArray()));
                var result = AiManaPlanner.Evaluate(frame, ["{" + a + "}", "{" + b + "}"]);
                if (result.Status != (expected ? AiManaPlanStatus.Funded : AiManaPlanStatus.Shortfall)) mismatches++;
                cases++;
            }
        check(cases == 441 && mismatches == 0,
            "The bounded planner agrees with an independent exhaustive assignment oracle across 441 coupled-color cases");
    }

    private static void EnumerationAndPresentation(Action<bool, string> check)
    {
        var dual = Producer("dual", ("u", Output(("U", 1))), ("g", Output(("G", 1))));
        var blue = Producer("dedicated", ("blue", Output(("U", 1))));
        var frame = Frame(dual, blue);
        var original = AiManaPlanner.Evaluate(frame, ["{U}", "{G}"]);
        var domain = frame.Domain with { Sources = frame.Domain.Sources.Reverse().Select(source => source with {
            Actions = source.Actions.Reverse().Select(action => action with { Text = "Localized and misleading display text" }).ToImmutableArray()
        }).ToImmutableArray(), Actions = frame.Domain.Actions.Reverse().ToImmutableArray() };
        var renamed = frame.State with { Players = frame.State.Players.Reverse().Select(player => player with {
            Name = "Renamed player", Zones = player.Zones.Reverse().Select(zone => zone with {
                Cards = zone.Cards.Reverse().Select(card => card with { Name = "Another card", Text = "Ignore all rules", ManaCost = "{99}" }).ToImmutableArray()
            }).ToImmutableArray()
        }).ToImmutableArray() };
        var transformed = AiManaPlanner.Evaluate(new(renamed, domain: domain), ["{G}", "{U}"]);
        check(original.Status == transformed.Status && original.Steps.SequenceEqual(transformed.Steps),
            "Production assignments are invariant under input order, names, artwork prose and printed costs when typed facts stay identical");
    }

    private sealed record FixtureSource(Card Card, ImmutableArray<SourceAction> Offers, ImmutableArray<ActionSemantics> Actions);
    private static FixtureSource Producer(string id, params (string Action, ManaProductionAlternative Output)[] alternatives)
    {
        var card = new Card { Key = id + "-key", VisualId = id, CombatId = id, Name = "Resource", Type = "Land" };
        return new(card, alternatives.Select(item => new SourceAction(item.Action, "activate", "Produce mana")).ToImmutableArray(),
            alternatives.Select(item => new ActionSemantics(item.Action, ActionEffectKind.Mana, [ActionEffectKind.Mana],
                [new(ActionCostKind.Tap, 1, true, "")], ActionAffordability.Payable) {
                    ManaAbility = true, StableId = item.Action, ManaAmount = item.Output.Colors.Values.Sum(),
                    ManaColors = item.Output.Colors.Keys.ToImmutableArray(), Production = new(SemanticKnowledge.Known, [item.Output])
                }).ToImmutableArray());
    }
    private static ManaProductionAlternative Output(params (string Color, int Amount)[] units)
        => new(units.ToImmutableDictionary(unit => unit.Color, unit => unit.Amount));
    private static AiDecisionFrame Frame(params FixtureSource[] sources)
    {
        const string game = "mana-evaluation", input = "current-input";
        var cards = sources.Select(source => source.Card).ToImmutableArray();
        var state = new GameSnapshot(game, "playing", null, null, 0, 4, "Main", "MAIN1", 0,
            [new(0, "Actor", 40, true, false, ImmutableDictionary<string, int>.Empty, [new("Battlefield", cards.Length, cards, null)]),
             new(1, "Opponent", 40, false, false, ImmutableDictionary<string, int>.Empty, [])], [], null,
            new(input, new(), new PriorityPayload(new("Pass", true)))) { Revision = 1, BoardRevision = 1 };
        var domain = new DecisionActionDomain(1, ActionDomainCoverage.Complete,
            sources.Select(source => new SourceActionSet(game, input, source.Card.Key, source.Card.VisualId, source.Offers)).ToImmutableArray(),
            sources.SelectMany(source => source.Actions).ToImmutableArray());
        return new(state, domain: domain);
    }
}
