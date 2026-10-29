using System.Collections.Immutable;
using Mana.AI;
using Mana.Contracts;

internal static class AiDamageTargetRegression
{
    private const string BodyId = "durable-body", CommanderId = "partner-commander", SourceId = "damage-source";
    private enum GraphShape { Absent, Incomplete, Known }

    internal static async Task RunAsync(Action<bool, string> check)
    {
        foreach (var shape in new[] { GraphShape.Known, GraphShape.Incomplete })
        foreach (long seed in new long[] { 0, 1, 17, 91 })
        foreach (bool reverse in new[] { false, true }) {
            var state = State(reverse: reverse);
            var answer = await Evaluate(state, Semantics(state, 5, shape), seed);
            check(answer is SelectCard { Card: CommanderId + ":current" },
                $"Five known recipient damage selects the lethal 1/1 partner over an unmarked 40/40 belonging to the same opponent ({shape}, seed {seed}, reversed {reverse})");
        }

        foreach (var shape in new[] { GraphShape.Known, GraphShape.Incomplete }) {
            var marked = State(body: Face(BodyId, 40, 40) with { Damage = 37 });
            check(await Evaluate(marked, Semantics(marked, 5, shape)) is SelectCard { Card: BodyId + ":current" },
                $"Public marked damage makes the 40/40's remaining toughness lethal and preferable to the 1/1 ({shape})");
            var indestructible = State(body: Face(BodyId, 2, 2), commander: Face(CommanderId, 4, 4) with {
                States = [new("indestructible", "Indestructible", "ability", 1, null, "", "indestructible")] });
            check(await Evaluate(indestructible, Semantics(indestructible, 5, shape)) is SelectCard { Card: BodyId + ":current" },
                $"An indestructible 4/4 cannot earn damage-removal value ahead of a killable 2/2 ({shape})");
            var zero = State(optional: true);
            check(await Evaluate(zero, Semantics(zero, 0, shape)) is CompleteSelection,
                $"Known zero damage declines an optional target instead of receiving full-removal value ({shape})");
            check(await Evaluate(zero, Semantics(zero, 0, shape) with { Intent = DecisionIntent.Unknown }) is CompleteSelection,
                $"The active damage operation declines a zero-value optional target even without legacy Damage intent ({shape})");
            var required = State();
            var semantics = Semantics(required, 0, shape);
            var requiredAnswer = await Evaluate(required, semantics);
            check(requiredAnswer is SelectCard && new AiDecisionFrame(required, semantics: semantics).Allows(requiredAnswer),
                $"Known zero damage still supplies an engine-required legal target without inventing completion ({shape})");
        }

        var mixed = State();
        mixed = mixed with { Players = mixed.Players.Select(player => player.Id != 1 ? player : player with { Life = 5 }).ToImmutableArray(),
            Decision = mixed.Decision! with { Payload = ((TargetSelectionPayload)mixed.Decision.Payload) with { Players = [1] } } };
        var mixedSemantics = Semantics(mixed, 5, GraphShape.Incomplete);
        mixedSemantics = mixedSemantics with { OperationGraph = mixedSemantics.OperationGraph with {
            Operations = mixedSemantics.OperationGraph.Operations.Select(node => node.Id != 0 ? node : node with {
                Targets = node.Targets! with { Candidates = node.Targets.Candidates.Add(
                    new("player:1", SemanticEntityKind.Player, PlayerId: 1, ControllerId: 1)) }
            }).ToImmutableArray()
        } };
        check(await Evaluate(mixed, mixedSemantics) is SelectPlayer { Player: 1 },
            "A partial known-five-damage comparison retains the provider-shaped player recipient and chooses lethal player damage ahead of killing its 1/1");

        var compensated = State(optional: true);
        var compensation = Semantics(compensated, 5, GraphShape.Known);
        var damage = compensation.OperationGraph.Operations[0];
        compensation = compensation with { OperationGraph = compensation.OperationGraph with { Operations = [
            damage with { Links = [new(1, OperationLinkKind.Sequence)] },
            new(1, ActionEffectKind.Draw) { Knowledge = SemanticKnowledge.Known, ActorPlayerId = 0,
                Sources = damage.Sources, Amount = 2, AmountKnowledge = SemanticKnowledge.Known,
                Recipients = new(SemanticKnowledge.Known, EffectRecipientBinding.TargetControllers, 0, []) }
        ] } };
        check(await Evaluate(compensated, compensation) is CompleteSelection,
            "A complete five-damage-plus-two-draw graph declines an optional creature target when compensation to that same target's controller outweighs removal; root-only damage cannot replace the known net consequence");

        foreach (string uncertainty in new[] { "legacy-total", "unknown-amount", "divided-recipient-unknown" })
        foreach (long seed in new long[] { 0, 1, 17, 91 }) {
            var before = State(body: Face(BodyId, 1, 1));
            DecisionSemantics Unknown(GameSnapshot state) {
                if (uncertainty != "divided-recipient-unknown")
                    return Semantics(state, uncertainty == "legacy-total" ? 5 : null, GraphShape.Absent);
                var known = Semantics(state, 5, GraphShape.Incomplete);
                return known with { OperationGraph = known.OperationGraph with {
                    Operations = known.OperationGraph.Operations.Select(node => node.Id != 0 ? node : node with {
                        Knowledge = SemanticKnowledge.Unknown, Amount = null, AmountKnowledge = SemanticKnowledge.Unknown
                    }).ToImmutableArray()
                } };
            }
            var original = await Evaluate(before, Unknown(before), seed);
            var selected = (original as SelectCard)?.Card;
            var grown = selected == BodyId + ":current" ? State(body: Face(BodyId, 1, 1), commander: Face(CommanderId, 40, 40))
                : State(body: Face(BodyId, 40, 40));
            var after = await Evaluate(grown, Unknown(grown), seed);
            check(original is SelectCard && after == original,
                $"Unknown recipient damage cannot become full removal merely because the unchosen body grows, with the same IDs and seed ({uncertainty}, seed {seed})");
        }

        var restricted = State(body: Face(BodyId, 80, 80) with { Selectable = false });
        restricted = restricted with { Players = restricted.Players.Select(player => player.Id != 0 ? player : player with {
            Zones = [new("Battlefield", 2, [Source(), Face("own-lethal-body", 99, 1)], null),
                new("Hand", 1, [new Card { FaceDown = true, Selectable = false }], null)]
        }).ToImmutableArray() };
        var restrictedSemantics = Semantics(restricted, 5, GraphShape.Incomplete);
        var safe = await Evaluate(restricted, restrictedSemantics);
        var restrictedFrame = new AiDecisionFrame(restricted, semantics: restrictedSemantics);
        check(safe is SelectCard { Card: CommanderId + ":current" } && restrictedFrame.Allows(safe)
                && !restrictedFrame.Allows(new SelectCard(restricted.Id, restricted.Decision!.Id, BodyId + ":current"))
                && !restrictedFrame.Allows(new SelectCard(restricted.Id, restricted.Decision!.Id, "")),
            "Damage estimates preserve exact target authority, avoid an owned lethal body, and cannot select a nonselectable public body or concealed face without a handle");
    }

    private static async Task<DecisionCommand?> Evaluate(GameSnapshot state, DecisionSemantics semantics, long seed = 17)
    {
        using var session = new AiSeatSession(state.Id, state.ViewerId, new CasualCommanderPolicy(), seed,
            new(TimeSpan.FromSeconds(5), workUnits: 1024));
        session.Observe(new(state, semantics: semantics));
        await session.EvaluateAsync();
        return session.TakeCommand()?.Command;
    }

    private static DecisionSemantics Semantics(GameSnapshot state, int? amount, GraphShape shape)
    {
        var semantics = new DecisionSemantics(DecisionIntent.Damage, amount);
        if (shape == GraphShape.Absent) return semantics;
        var candidates = state.Players.SelectMany(player => player.Zone("Battlefield").Cards
            .Where(card => card.Selectable).Select(card => Entity(card.VisualId, player.Id))).ToImmutableArray();
        var source = new EffectRecipients(SemanticKnowledge.Known, EffectRecipientBinding.Source, null, [Entity(SourceId, 0)]);
        var operation = new ActionOperation(0, ActionEffectKind.Damage) {
            Knowledge = SemanticKnowledge.Known, ActorPlayerId = 0, Amount = amount,
            AmountKnowledge = amount == null ? SemanticKnowledge.Unknown : SemanticKnowledge.Known,
            Sources = source, Recipients = new(SemanticKnowledge.Known, EffectRecipientBinding.Targets, 0, []),
            Targets = new(SemanticKnowledge.Known, state.Decision!.Payload is TargetSelectionPayload { Range.Min: 0 } ? 0 : 1, 1, candidates)
        };
        var operations = ImmutableArray.Create(operation);
        if (shape == GraphShape.Incomplete) operations = [operation with { Links = [new(1, OperationLinkKind.Sequence)] },
            new(1, ActionEffectKind.Unknown) { Knowledge = SemanticKnowledge.Unknown, ActorPlayerId = 0,
                Sources = source, Recipients = new(SemanticKnowledge.Known, EffectRecipientBinding.Actor, null,
                    [new("player:0", SemanticEntityKind.Player, PlayerId: 0)]) }];
        return semantics with { OperationId = 0,
            OperationGraph = new(shape == GraphShape.Known ? SemanticKnowledge.Known : SemanticKnowledge.Unknown, 0, operations) };
    }

    private static SemanticEntity Entity(string id, int controller)
        => new(id, SemanticEntityKind.Card, id, ControllerId: controller, OwnerId: controller);
    private static Card Face(string id, int power, int toughness) => new() {
        Key = id + ":current", VisualId = id, CombatId = id, Name = id == CommanderId ? "Partner commander" : "Public creature",
        Type = id == CommanderId ? "Legendary Creature" : "Creature", Power = power, Toughness = toughness, Selectable = true
    };
    private static Card Source() => Face(SourceId, 2, 2) with { Selectable = false };

    private static GameSnapshot State(Card? body = null, Card? commander = null, bool optional = false, bool reverse = false)
    {
        ImmutableArray<Card> enemies = [body ?? Face(BodyId, 40, 40), commander ?? Face(CommanderId, 1, 1)];
        if (reverse) enemies = enemies.Reverse().ToImmutableArray();
        var source = Source();
        ImmutableArray<Player> players = [
            new(0, "Acting seat", 40, false, false, ImmutableDictionary<string, int>.Empty, [new("Battlefield", 1, [source], null)]),
            new(1, "Both target creatures' controller", 40, false, false, ImmutableDictionary<string, int>.Empty, [new("Battlefield", 2, enemies, null)]),
            new(2, "Other opponent", 40, false, false, ImmutableDictionary<string, int>.Empty, []),
            new(3, "Remaining opponent", 40, false, false, ImmutableDictionary<string, int>.Empty, [])
        ];
        if (reverse) players = players.Reverse().ToImmutableArray();
        return new("damage-target-game", "playing", null, null, 0, 4, "Main", "MAIN1", 0, players, [], null,
            new("current-damage-target", new(Source: new(SourceId, source, 0, "Battlefield")),
                new TargetSelectionPayload(new("Done", optional), new("Cancel", false), new(optional ? 0 : 1, 1), []))) {
                    Revision = 1, BoardRevision = 1, DecisionOwnerId = 0
                };
    }
}
