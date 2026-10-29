using System.Collections.Immutable;
using Mana.AI;
using Mana.Contracts;

internal static class AiActionOutcomeRegression
{
    private const string PhysicalSource = "physical-source", DrawSource = "draw-source";
    private const string AlternateCast = "alternate-cast", PermanentCast = "permanent-cast", DrawCast = "draw-cast";

    internal static async Task RunAsync(Action<bool, string> check)
    {
        foreach (long seed in new long[] { 0, 1, 17, 91 })
        foreach (bool renamed in new[] { false, true })
        foreach (int body in new[] { 6, 40 }) {
            var frame = Frame(body, body, renamed: renamed);
            var answer = await Evaluate(frame, seed);
            check(answer is UseOfferedAction { ActionId: DrawCast } && frame.Allows(answer),
                $"A known one-card draw outranks an incomplete nonpermanent cast without borrowing its physical creature source's {body}/{body} body (seed {seed}, renamed {renamed})");
        }

        foreach (long seed in new long[] { 0, 1, 17, 91 }) {
            var alone = Frame(6, 6, drawAmount: null);
            var exploration = await Evaluate(alone, seed);
            check(exploration is UseOfferedAction { ActionId: AlternateCast } && alone.Allows(exploration),
                $"An incomplete nonpermanent cast remains an authorized exploration candidate when it is the only offered play (seed {seed})");

            var small = Frame(0, 1, drawAmount: 5, permanent: true);
            check(await Evaluate(small, seed) is UseOfferedAction { ActionId: DrawCast },
                $"Five known draws outweigh a small actual permanent outcome (seed {seed})");
            var large = Frame(6, 6, drawAmount: 5, permanent: true);
            check(await Evaluate(large, seed) is UseOfferedAction { ActionId: PermanentCast },
                $"The actual permanent route retains its larger body value against the same known draw alternative (seed {seed})");
            var moreCards = Frame(6, 6, drawAmount: 6, permanent: true);
            check(await Evaluate(moreCards, seed) is UseOfferedAction { ActionId: DrawCast },
                $"Six known draws outweigh that same permanent without counting its physical source body a second time (seed {seed})");
        }
    }

    private static async Task<DecisionCommand?> Evaluate(AiDecisionFrame frame, long seed)
    {
        using var session = new AiSeatSession(frame.State.Id, frame.State.ViewerId, new CasualCommanderPolicy(), seed,
            new(TimeSpan.FromSeconds(5), workUnits: 1024));
        session.Observe(frame);
        await session.EvaluateAsync();
        return session.TakeCommand()?.Command;
    }

    private static AiDecisionFrame Frame(int power, int toughness, int? drawAmount = 1, bool renamed = false, bool permanent = false)
    {
        // An alternate spell shares its physical creature host. The provider
        // supplies no separate SourceAction.Card when the host is unchanged.
        var physical = new Card {
            Key = PhysicalSource + ":current", VisualId = PhysicalSource, CombatId = PhysicalSource,
            Name = renamed ? "Unrelated translated creature" : "Disclosed creature source", Type = "Creature",
            Power = power, Toughness = toughness,
            OtherFace = new(renamed ? "Another translated spell" : "Alternate spell", "Sorcery Adventure", "", "", null, null, "", "back")
        };
        var draw = new Card {
            Key = DrawSource + ":current", VisualId = DrawSource, CombatId = DrawSource,
            Name = renamed ? "Another unrelated printed name" : "Disclosed draw source", Type = "Sorcery"
        };
        ImmutableArray<Card> hand = renamed ? [draw, physical] : [physical, draw];
        ImmutableArray<Player> players = [
            new(0, renamed ? "Other actor name" : "Acting seat", 40, true, false,
                ImmutableDictionary<string, int>.Empty, [new("Hand", hand.Length, hand, null), new("Library", 60, [], null)]),
            new(1, "First opponent", 40, false, false, ImmutableDictionary<string, int>.Empty, []),
            new(2, "Second opponent", 40, false, false, ImmutableDictionary<string, int>.Empty, []),
            new(3, "Third opponent", 40, false, false, ImmutableDictionary<string, int>.Empty, [])
        ];
        if (renamed) players = players.Reverse().ToImmutableArray();
        var state = new GameSnapshot("action-outcome-game", "playing", null, null, 0, 4, "Main", "MAIN1", 0,
            players, [], null, new("current-action-outcome", new(), new PriorityPayload(new("Pass", true)))) {
            Revision = 1, BoardRevision = 1, DecisionOwnerId = 0, PriorityPlayerId = 0
        };
        string actionId = permanent ? PermanentCast : AlternateCast;
        var effect = permanent ? ActionEffectKind.Permanent : ActionEffectKind.MoveCard;
        var alternateGraph = new ActionOperationGraph(SemanticKnowledge.Unknown, 0, [new(0, ActionEffectKind.MoveCard) {
            Knowledge = SemanticKnowledge.Unknown, ActorPlayerId = 0,
            Sources = Source(PhysicalSource), Recipients = EffectRecipients.Unknown,
            OriginZone = "Battlefield", DestinationZone = "Library"
        }]);
        var physicalAction = new SourceAction(actionId, "cast", renamed ? "Unrelated translated action" : "Cast the offered route");
        ImmutableArray<SourceActionSet> sources = [new(state.Id, state.Decision!.Id, physical.Key, PhysicalSource, [physicalAction])];
        ImmutableArray<ActionSemantics> semantics = [Semantic(actionId, effect) with {
            OperationGraph = permanent ? ActionOperationGraph.Unknown : alternateGraph
        }];
        if (drawAmount is { } amount) {
            sources = sources.Add(new(state.Id, state.Decision!.Id, draw.Key, DrawSource,
                [new(DrawCast, "cast", renamed ? "Other translated action" : "Cast the known draw")]));
            semantics = semantics.Add(Semantic(DrawCast, ActionEffectKind.Draw) with {
                EffectAmount = amount,
                OperationGraph = new(SemanticKnowledge.Known, 0, [new(0, ActionEffectKind.Draw) {
                    Knowledge = SemanticKnowledge.Known, ActorPlayerId = 0, Amount = amount,
                    AmountKnowledge = SemanticKnowledge.Known, Sources = Source(DrawSource),
                    Recipients = new(SemanticKnowledge.Known, EffectRecipientBinding.Actor, null,
                        [new("player:0", SemanticEntityKind.Player, PlayerId: 0, ControllerId: 0)])
                }])
            });
        }
        if (renamed) { sources = sources.Reverse().ToImmutableArray(); semantics = semantics.Reverse().ToImmutableArray(); }
        return new(state, domain: new(1, ActionDomainCoverage.Complete, sources, semantics));
    }

    private static ActionSemantics Semantic(string id, ActionEffectKind effect)
        => new(id, effect, [effect], [], ActionAffordability.Payable) { StableId = id };
    private static EffectRecipients Source(string id)
        => new(SemanticKnowledge.Known, EffectRecipientBinding.Source, null,
            [new("source:" + id, SemanticEntityKind.Card, id, ControllerId: 0, OwnerId: 0)]);
}
