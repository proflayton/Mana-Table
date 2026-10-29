using System.Collections.Immutable;
using Mana.AI;
using Mana.Contracts;

internal static class AiDecisionQualityRegression
{
    private static readonly DecisionReply Yes = new("Yes", true), No = new("No", true);
    private static readonly EffectRecipients Actor = new(SemanticKnowledge.Known, EffectRecipientBinding.Actor, null,
        [new("player:0", SemanticEntityKind.Player, PlayerId: 0)]);

    internal static async Task RunAsync(Action<bool, string> check)
    {
        var confirmation = State(new ConfirmationPayload(Yes, No), life: 2);
        var harmful = Graph(Known(0, ActionEffectKind.Draw, 2) with { Links = [new(1, OperationLinkKind.Sequence)] },
            Known(1, ActionEffectKind.LifeLoss, 2));
        var harmfulSemantics = new DecisionSemantics(DecisionIntent.Benefit, 2) { OperationId = 0, OperationGraph = harmful };
        check(await Evaluate(confirmation, harmfulSemantics) is DeclineConfirmation,
            "An optional confirmation respects a complete known draw-two-plus-lose-two consequence at two life instead of accepting its beneficial headline");
        check(await Evaluate(confirmation, new(DecisionIntent.Benefit, 2) { OperationId = 0,
                OperationGraph = Graph(Known(0, ActionEffectKind.Draw, 2)) }) is AcceptConfirmation,
            "A complete known favorable optional confirmation is still accepted");
        check(await Evaluate(confirmation, new(DecisionIntent.Benefit, 0) { OperationId = 0,
                OperationGraph = Graph(Known(0, ActionEffectKind.Draw, 0)) }) is DeclineConfirmation,
            "A complete known zero-value optional confirmation is declined without manufacturing benefit from its headline");
        var required = confirmation with { Decision = confirmation.Decision! with { Payload = new ConfirmationPayload(Yes) } };
        check(await Evaluate(required, harmfulSemantics) is AcknowledgeDecision,
            "A required one-button acknowledgment remains an acknowledgment even when its context graph is unfavorable");
        var locked = confirmation with { Decision = confirmation.Decision! with {
            Payload = new ConfirmationPayload(Yes, No with { Enabled = false }) } };
        check(await Evaluate(locked, harmfulSemantics) is AcceptConfirmation,
            "A disabled decline cannot cause an otherwise required confirmation to submit an illegal refusal");
        var conditional = harmful with { Knowledge = SemanticKnowledge.Unknown,
            Operations = harmful.Operations.Select(node => node.Id != 0 ? node : node with {
                Conditional = true, Knowledge = SemanticKnowledge.Unknown }).ToImmutableArray() };
        var conditionalSemantics = harmfulSemantics with { OperationGraph = conditional };
        var conditionalAnswer = await Evaluate(confirmation, conditionalSemantics);
        check(CasualActionAssessment.Evaluate(confirmation, conditional, 128).Status == CasualAssessmentStatus.Incomplete
                && new AiDecisionFrame(confirmation, semantics: conditionalSemantics).Allows(conditionalAnswer),
            "A conditional confirmation retains an explicit legal fallback while its affirmative branch remains unknown; it is not promoted to a complete known consequence");

        foreach (long seed in new long[] { 0, 17, 91 })
        foreach (bool reverse in new[] { false, true })
        foreach (bool rename in new[] { false, true }) {
            ImmutableArray<Choice> choices = [new(7, rename ? "Unrelated translated first choice" : "Draw mode", null),
                new(19, rename ? "Unrelated translated second choice" : "Scry mode", null)];
            if (reverse) choices = choices.Reverse().ToImmutableArray();
            var state = State(new OptionsPayload(new(choices, new(1, 1))));
            var semantics = ModeSemantics(reverse);
            check(await Evaluate(state, semantics, seed) is ChooseOptions chosen && chosen.Choices.SequenceEqual([19]),
                $"A required mode prefers a completed favorable scry estimate over an incomplete draw headline with an unknown sibling (seed {seed}, reversed {reverse}, renamed {rename})");
        }
        var optional = State(new OptionsPayload(new([new(7, "Incomplete draw mode", null), new(19, "Known scry mode", null)], new(0, 2))));
        check(await Evaluate(optional, ModeSemantics()) is ChooseOptions optionalModes && optionalModes.Choices.SequenceEqual([19]),
            "Optional mode selection includes only the modeled favorable mode, rather than counting it then taking a higher unmodeled headline from a different ordering");
        var unknown = State(new OptionsPayload(new([new(7, "First unknown mode", null), new(19, "Second unknown mode", null)], new(1, 2))));
        var unknownSemantics = new DecisionSemantics { Options = [new(7, ActionEffectKind.Unknown), new(19, ActionEffectKind.Unknown)] };
        var unknownAnswer = await Evaluate(unknown, unknownSemantics);
        check(unknownAnswer is ChooseOptions { Choices.Length: 1 } && new AiDecisionFrame(unknown, semantics: unknownSemantics).Allows(unknownAnswer),
            "When every required mode is unmodeled, the policy still supplies the engine's legal minimum without claiming modeled benefit");

        Card small = Face("small-card", 1), large = Face("large-card", 8);
        ImmutableArray<Choice> faces = [new(7, "Small disclosed card", small), new(19, "Large disclosed card", large)];
        var search = State(new OptionsPayload(new(faces, new(0, 1))));
        check(await Evaluate(search, new(DecisionIntent.Search)) is ChooseOptions found && found.Choices.SequenceEqual([19]),
            "Public card-search choices retain their disclosed-body comparison without requiring a spell-mode operation graph");
        var discard = State(new OptionsPayload(new(faces, new(1, 1))));
        discard = discard with { Players = discard.Players.Select(player => player.Id != 0 ? player : player with {
            Zones = player.Zones.Select(zone => zone.Name != "Hand" ? zone : zone with {
                Count = 3, Cards = [Source(), small, large] }).ToImmutableArray()
        }).ToImmutableArray() };
        check(await Evaluate(discard, new(DecisionIntent.Discard) { SelectionIsCost = true }) is ChooseOptions lost
                && lost.Choices.SequenceEqual([7]),
            "Required discard retains the cheaper disclosed resource instead of treating card choices as unknown spell modes");
        var mana = State(new OptionsPayload(new([new Choice(7, "First color", null) { Mana = "R" },
            new Choice(19, "Second color", null) { Mana = "U" }], new(1, 1))));
        check(await Evaluate(mana, new() { UnpaidMana = ["{U}"] }) is ChooseOptions paid && paid.Choices.SequenceEqual([19]),
            "A current mana-color choice still follows the unpaid blue requirement rather than a generic unknown-mode fallback");
    }

    private static DecisionSemantics ModeSemantics(bool reverse = false)
    {
        var incomplete = Graph(Known(0, ActionEffectKind.Draw, 2) with { Links = [new(1, OperationLinkKind.Sequence)] },
            Known(1, ActionEffectKind.Unknown, 0) with { Knowledge = SemanticKnowledge.Unknown }) with { Knowledge = SemanticKnowledge.Unknown };
        ImmutableArray<DecisionOptionSemantics> options = [new(7, ActionEffectKind.Draw, 2) { OperationGraph = incomplete },
            new(19, ActionEffectKind.Scry, 2) { OperationGraph = Graph(Known(0, ActionEffectKind.Scry, 2)) }];
        return new() { Options = reverse ? options.Reverse().ToImmutableArray() : options };
    }

    private static async Task<DecisionCommand?> Evaluate(GameSnapshot state, DecisionSemantics semantics, long seed = 17)
    {
        using var session = new AiSeatSession(state.Id, state.ViewerId, new CasualCommanderPolicy(), seed,
            new(TimeSpan.FromSeconds(5), workUnits: 1024));
        session.Observe(new(state, semantics: semantics));
        await session.EvaluateAsync();
        return session.TakeCommand()?.Command;
    }

    private static ActionOperation Known(int id, ActionEffectKind effect, int amount) => new(id, effect) {
        Knowledge = SemanticKnowledge.Known, ActorPlayerId = 0, Amount = amount, AmountKnowledge = SemanticKnowledge.Known,
        Sources = new(SemanticKnowledge.Known, EffectRecipientBinding.Source, null,
            [new("decision-source", SemanticEntityKind.Card, "decision-source", ControllerId: 0, OwnerId: 0)]), Recipients = Actor
    };
    private static ActionOperationGraph Graph(params ActionOperation[] nodes) => new(SemanticKnowledge.Known, 0, nodes.ToImmutableArray());
    private static Card Face(string id, int body) => new() {
        Key = id + ":current", VisualId = id, CombatId = id, Name = "Disclosed body", Type = "Creature", Power = body, Toughness = body
    };
    private static Card Source() => Face("decision-source", 2) with { Type = "Instant", Power = null, Toughness = null };

    private static GameSnapshot State(DecisionPayload payload, int life = 40)
    {
        var source = Source();
        return new("decision-quality-game", "playing", null, null, 0, 4, "Main", "MAIN1", 0, [
            new(0, "Acting seat", life, false, false, ImmutableDictionary<string, int>.Empty,
                [new("Hand", 1, [source], null), new("Battlefield", 0, [], null), new("Library", 20, [], null)]),
            new(1, "Other seat", 40, false, false, ImmutableDictionary<string, int>.Empty, []),
            new(2, "Third seat", 40, false, false, ImmutableDictionary<string, int>.Empty, []),
            new(3, "Fourth seat", 40, false, false, ImmutableDictionary<string, int>.Empty, [])
        ], [], null, new("current-quality-decision", new(Source: new(source.VisualId, source, 0, "Hand")), payload)) {
            Revision = 1, BoardRevision = 1, DecisionOwnerId = 0
        };
    }
}
