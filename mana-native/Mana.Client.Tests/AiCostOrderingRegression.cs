using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mana.AI;
using Mana.Contracts;

internal static class AiCostOrderingRegression
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        // The labels are deliberately unrelated to the physical costs. The
        // provider owns the default sequence, including tap before sacrifice.
        var choices = ImmutableArray.Create(new Choice(17, "Untrusted first label", null),
            new Choice(29, "Untrusted second label", null), new Choice(5, "Untrusted third label", null));
        foreach (var offered in new[] { choices, ImmutableArray.Create(choices[1], choices[2], choices[0]) }) {
            var frame = Frame(offered);
            foreach (long seed in new long[] { 17, 901 }) {
                var answer = await Evaluate(frame, seed);
                check(answer.Proposal is OrderOptions order && order.Choices.SequenceEqual(offered.Select(choice => choice.Index))
                        && frame.Allows(answer.Proposal),
                    "Typed cost ordering preserves the actual provider sequence across arbitrary indices, offered permutations and policy seeds");
            }
        }

        var typed = Frame(choices);
        var json = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter(allowIntegerValues: false) } };
        var roundTrip = JsonSerializer.Deserialize<DecisionSemantics>(JsonSerializer.Serialize(typed.Semantics, json), json)!;
        check(roundTrip.OrderingPurpose == DecisionOrderingPurpose.CostPayment
                && roundTrip.DefaultOrder.SequenceEqual([17, 29, 5])
                && new AiDecisionFrame(typed.State, semantics: roundTrip).Semantics.SelectionIsCost,
            "The additive cost-order wire preserves its explicit purpose and exact default indices");
        var legacy = JsonSerializer.Deserialize<DecisionSemantics>("{}", json)!;
        check(legacy.OrderingPurpose == DecisionOrderingPurpose.Unknown && legacy.DefaultOrder.IsEmpty,
            "An older producer without cost-order metadata stays explicitly unknown");

        bool Rejected(DecisionSemantics semantics, GameSnapshot? state = null) {
            try { _ = new AiDecisionFrame(state ?? typed.State, semantics: semantics); return false; }
            catch (ArgumentException) { return true; }
        }
        foreach (var invalid in new[] {
            typed.Semantics with { DefaultOrder = default },
            typed.Semantics with { DefaultOrder = [] },
            typed.Semantics with { DefaultOrder = [17, 29] },
            typed.Semantics with { DefaultOrder = [17, 29, 29] },
            typed.Semantics with { DefaultOrder = [17, 29, 999] },
            typed.Semantics with { DefaultOrder = [5, 17, 29] },
            typed.Semantics with { DefaultOrder = [-1, 29, 5] },
            typed.Semantics with { SelectionIsCost = false },
            typed.Semantics with { OrderingPurpose = (DecisionOrderingPurpose)99 },
            typed.Semantics with { OrderingPurpose = DecisionOrderingPurpose.Unknown }
        }) check(Rejected(invalid), "Cost-order metadata rejects absent, partial, duplicate, invented or reordered defaults and mismatched purpose");
        var decision = typed.State.Decision!;
        check(Rejected(typed.Semantics, typed.State with { Decision = decision with {
                    Payload = new OptionsPayload(new(choices, new(3, 3))) } })
                && Rejected(typed.Semantics, typed.State with { Decision = decision with {
                    Payload = new OrderingPayload(new(choices, new(0, 3))) } })
                && Rejected(typed.Semantics, typed.State with { Decision = decision with {
                    Payload = new OrderingPayload(new(choices, new(2, 2))) } })
                && Rejected(typed.Semantics with { DefaultOrder = [] }, typed.State with { Decision = decision with {
                    Payload = new OrderingPayload(new([], new(0, 0))) } }),
            "A cost default requires an actual nonempty ordering prompt that selects every offered part exactly once");

        var small = new Card { Name = "Small", Type = "Creature", ManaCost = "{1}", Power = 1, Toughness = 1 };
        var large = new Card { Name = "Large", Type = "Creature", ManaCost = "{6}", Power = 6, Toughness = 6 };
        ImmutableArray<Choice> cards = [new(17, "Small", small), new(29, "Large", large)];
        var ordinary = Frame(cards, new());
        var cardAnswer = await Evaluate(ordinary);
        check(cardAnswer.Proposal is OrderOptions cardOrder && cardOrder.Choices.SequenceEqual([29, 17])
                && ordinary.Allows(cardAnswer.Proposal),
            "Ordinary card ordering retains public card-value ranking rather than using an unrelated cost default");
        var costCards = Frame(cards, new() { SelectionIsCost = true });
        var costCardAnswer = await Evaluate(costCards);
        check(costCardAnswer.Proposal is OrderOptions costOrder && costOrder.Choices.SequenceEqual([17, 29])
                && costCards.Allows(costCardAnswer.Proposal),
            "A card ordering during a cost keeps its ordinary opportunity-cost ranking when the provider did not identify a CostPart ordering");
    }

    private static Task<AiDecisionReplayResult> Evaluate(AiDecisionFrame frame, long seed = 17)
        => AiDecisionReplay.EvaluateAsync(new(1, "typed-cost-order", "casual-commander.v3", frame.State,
            frame.Domain, frame.Semantics, new([], []), seed), new CasualCommanderPolicy());

    private static AiDecisionFrame Frame(ImmutableArray<Choice> choices, DecisionSemantics? semantics = null)
    {
        var state = new GameSnapshot("cost-order", "playing", null, null, 0, 2, "Main", "MAIN1", 0,
            [new(0, "Actor", 40, true, false, ImmutableDictionary<string, int>.Empty, []),
             new(1, "Opponent", 40, false, false, ImmutableDictionary<string, int>.Empty, [])], [], null,
            new("order", new(), new OrderingPayload(new(choices, new(choices.Length, choices.Length))))) {
            Revision = 1, BoardRevision = 1, DecisionOwnerId = 0 };
        return new(state, semantics: semantics ?? new() { SelectionIsCost = true,
            OrderingPurpose = DecisionOrderingPurpose.CostPayment, DefaultOrder = choices.Select(choice => choice.Index).ToImmutableArray() });
    }
}
