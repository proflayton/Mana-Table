using System.Collections.Immutable;
using Mana.AI;
using Mana.Contracts;

internal static class AiTeamRelationsRegression
{
    private static readonly DecisionReply Done = new("Done", true), Required = new("Done", false);

    internal static async Task RunAsync(Action<bool, string> check)
    {
        var state = Targets();
        var threats = CasualTableAssessment.Assess(state, new([], []));
        check(threats.Select(threat => threat.PlayerId).Order().SequenceEqual([2, 3]),
            "A large allied battlefield is not assessed as an opposing threat");
        foreach (var effect in new[] { ActionEffectKind.Draw, ActionEffectKind.LifeGain }) {
            check(Estimate(state, effect, PlayerEntity(1), 1).Value > 0
                    && Estimate(state, effect, PlayerEntity(2), 1).Value < 0,
                "Public ally benefits and enemy benefits have opposite strategic value: " + effect);
        }
        check(Estimate(state, ActionEffectKind.Damage, CardEntity(state, 1), 5).Value < 0
                && Estimate(state, ActionEffectKind.Damage, CardEntity(state, 2), 5).Value > 0,
            "Damage to an allied creature is harmful while removing an enemy creature is useful");
        var lethalAlly = state with { Players = state.Players.Select(player => player.Id == 1 ? player with { Life = 1 } : player).ToImmutableArray() };
        check(Estimate(lethalAlly, ActionEffectKind.Damage, PlayerEntity(1), 1).Value <= -1000,
            "An allied player's publicly lethal damage does not receive enemy elimination value");

        foreach (bool reverse in new[] { false, true }) {
            var ordered = reverse ? state with { Players = state.Players.Reverse().ToImmutableArray() } : state;
            for (int seed = 0; seed < 4; seed++) {
                check(await Choose(ordered, TargetSemantics(ordered, ActionEffectKind.Damage), seed) is SelectCard { Card: "enemy-key" },
                    "Required damage selects the useful enemy over a much larger allied creature across order and seed changes");
                check(await Choose(ordered, TargetSemantics(ordered, ActionEffectKind.AddCounters), seed) is SelectCard { Card: "ally-key" },
                    "A positive targeted counter effect can support an ally from the engine's current choices");
            }
        }
        var playerChoice = state with { Decision = state.Decision! with { Payload = new TargetSelectionPayload(Required, Required, new(1, 1), [1, 2]) },
            Players = state.Players.Select(player => player with { Zones = [] }).ToImmutableArray() };
        check(await Choose(playerChoice, new(DecisionIntent.Benefit)) is SelectPlayer { Player: 1 },
            "A beneficial player choice supports an ally when self is not an offered recipient");

        var allyOnly = Targets(optional: true, enemySelectable: false);
        check(await Choose(allyOnly, TargetSemantics(allyOnly, ActionEffectKind.Damage)) is CompleteSelection,
            "The casual player declines discretionary damage when only its ally is offered");
        var requiredAlly = allyOnly with { Decision = allyOnly.Decision! with { Payload = new TargetSelectionPayload(Required, Required, new(1, 1), []) } };
        check(await Choose(requiredAlly, TargetSemantics(requiredAlly, ActionEffectKind.Damage)) is SelectCard { Card: "ally-key" },
            "Team preferences never remove the engine's only required legal target");

        var cost = state with { Players = state.Players.Select(player => player with { Zones = [new("Battlefield", 1,
            player.Id == 0 ? [Face("own", 40, true)] : player.Id == 1 ? [Face("ally", 1, true)] : [Face("enemy", 1, false)], null)] }).ToImmutableArray() };
        check(await Choose(cost, new(DecisionIntent.Sacrifice) { SelectionIsCost = true }) is SelectCard { Card: "own-key" },
            "Ally support does not make allied resources the acting seat's own cost resources");

        await UnknownAndLegacy(check);
        await Combat(check);
        await Allocation(check);
    }

    private static async Task UnknownAndLegacy(Action<bool, string> check)
    {
        var state = Targets(optional: true, enemySelectable: false);
        foreach (int? invalid in new int?[] { null, -1 }) {
            var unknown = state with { Players = state.Players.Select(player => player.Id == 1 ? player with { TeamId = invalid } : player).ToImmutableArray() };
            check(PlayerRelations.ForViewer(unknown, 1) == PlayerRelation.Unknown
                    && Estimate(unknown, ActionEffectKind.Damage, CardEntity(unknown, 1), 5).Value == 0
                    && await Choose(unknown, TargetSemantics(unknown, ActionEffectKind.Damage)) is CompleteSelection,
                "Missing or malformed team facts cannot create discretionary enemy damage value");
            check(await Choose(unknown, TargetSemantics(unknown, ActionEffectKind.AddCounters)) is CompleteSelection,
                "An unknown relationship cannot manufacture an ally benefit either");
        }
        var legacy = state with { Players = state.Players.Select(player => player with { TeamId = null }).ToImmutableArray() };
        check(PlayerRelations.ForViewer(legacy, 1) == PlayerRelation.Unknown
                && await Choose(legacy, TargetSemantics(legacy, ActionEffectKind.Damage)) is SelectCard { Card: "ally-key" },
            "Only the policy's explicit all-absent legacy FFA compatibility preserves prior enemy preferences; neutral relations stay Unknown");
        var duplicated = legacy with { Players = legacy.Players.Add(legacy.Players[1]) };
        check(await Choose(duplicated, TargetSemantics(duplicated, ActionEffectKind.Damage)) is CompleteSelection,
            "Duplicate participant IDs cannot activate the legacy FFA relationship fallback");
    }

    private static async Task Combat(Action<bool, string> check)
    {
        var attack = Attack();
        check(await Choose(attack) is ToggleAttackPlayer { Player: 2 },
            "A lethal attack on the final known enemy may spend a defender even with a live ally");
        foreach (int? team in new int?[] { 1, null }) {
            var unfinished = attack with { Players = attack.Players.Select(player => player.Id == 3
                ? player with { Eliminated = false, TeamId = team } : player).ToImmutableArray() };
            check(await Choose(unfinished) is CompleteAttackers,
                "A second enemy or an unknown live seat prevents claiming that an elimination wins the encounter");
        }
        var alliedTarget = attack with { Combat = attack.Combat! with { AttackOptions = [new("own", [new("player", "1", 1, "Ally")])] } };
        check(await Choose(alliedTarget) is CompleteAttackers,
            "A legal offer to attack an ally does not create a discretionary attack opportunity");
        var forced = alliedTarget with { Decision = alliedTarget.Decision! with { Payload = new AttackPayload(Required) } };
        check(await Choose(forced) is ToggleAttackPlayer { Player: 1 },
            "A required attack still follows the engine's actual offered defender instead of stalling on a team preference");
        var block = Targets() with { Decision = new("team-block", new(), new BlockPayload(Done)),
            Players = attack.Players.Select(player => player.Id == 1 ? player with { Life = 1 } : player.Id == 2
                ? player with { Zones = [new("Battlefield", 1, [Face("enemy", 6)], null)] } : player).ToImmutableArray(),
            Combat = new([new("enemy", 1, [], ["own"])], [], null) };
        check(await Choose(block) is ToggleBlock { Blocker: "own-key" },
            "An explicitly offered own blocker can protect an ally from publicly lethal damage");
        var unoffered = block with { Combat = block.Combat! with { Attackers = [new("enemy", 1, [], [])] } };
        check(await Choose(unoffered) is CompleteBlockers,
            "An ally needing help never authorizes an unoffered block");
    }

    private static async Task Allocation(Action<bool, string> check)
    {
        var state = Targets() with { Decision = new("team-allocate", new(), new AllocationPayload(5, AllocationKind.Generic, "", false,
            [new(0, "ally", "Ally", null, "", 1, 1, 1, 0, 5), new(1, "enemy", "Enemy", null, "", 2, 2, 2, 0, 5)])) };
        check(await Choose(state, new(DecisionIntent.Damage)) is AllocateAmounts harm && harm.Amounts.SequenceEqual([0, 5]),
            "A harmful allocation uses the enemy's offered capacity before an allied recipient");
        check(await Choose(state, new(DecisionIntent.Benefit)) is AllocateAmounts benefit && benefit.Amounts.SequenceEqual([5, 0]),
            "A beneficial allocation supports the allied recipient rather than reusing enemy threat ordering");
    }

    private static GameSnapshot Targets(bool optional = false, bool enemySelectable = true)
    {
        var empty = ImmutableDictionary<string, int>.Empty;
        return new("team-policy", "playing", null, null, 0, 7, "Main", "MAIN1", 0,
            [new(0, "Actor", 5, true, false, empty, [new("Battlefield", 1, [Face("own", 3)], null)]) { TeamId = 0 },
             new(1, "Ally", 40, false, false, empty, [new("Battlefield", 1, [Face("ally", 40, true)], null), new("Hand", 20, [], null)]) { TeamId = 0 },
             new(2, "Enemy", 40, false, false, empty, [new("Battlefield", 1, [Face("enemy", 2, enemySelectable)], null)]) { TeamId = 1 },
             new(3, "Other enemy", 40, false, false, empty, []) { TeamId = 1 }], [], null,
            new("team-target", new(), new TargetSelectionPayload(optional ? Done : Required, Required, new(optional ? 0 : 1, 1), []))) {
                Revision = 1, BoardRevision = 1, DecisionOwnerId = 0, PriorityPlayerId = 0 };
    }

    private static GameSnapshot Attack()
    {
        var state = Targets();
        return state with { Phase = "Declare attackers", PhaseKey = "DECLARE_ATTACKERS",
            Decision = new("team-attack", new(), new AttackPayload(Done)),
            Players = state.Players.Select(player => player with { Eliminated = player.Id == 3,
                Life = player.Id == 2 ? 1 : player.Life,
                Zones = [new("Battlefield", player.Id == 1 ? 0 : 1,
                    player.Id == 0 ? [Face("own", 3)] : player.Id == 2 ? [Face("enemy", 6) with { Tapped = true }] : [], null)] }).ToImmutableArray(),
            Combat = new([], [new("own", [new("player", "2", 2, "Enemy")])], null) };
    }

    private static Card Face(string id, int size, bool selectable = false) => new() {
        Key = id + "-key", VisualId = id, CombatId = id, Name = "Public face", Type = "Creature",
        Power = size, Toughness = size, Selectable = selectable
    };
    private static SemanticEntity PlayerEntity(int player) => new("player:" + player, SemanticEntityKind.Player, PlayerId: player);
    private static SemanticEntity CardEntity(GameSnapshot state, int player)
    {
        var card = state.Players.First(seat => seat.Id == player).Zone("Battlefield").Cards[0];
        return new(card.VisualId, SemanticEntityKind.Card, card.VisualId, ControllerId: player, OwnerId: player);
    }

    private static ActionOperation Operation(ActionEffectKind effect, EffectRecipients recipients, int amount = 5) => new(0, effect) {
        Knowledge = SemanticKnowledge.Known, ActorPlayerId = 0, Amount = amount, AmountKnowledge = SemanticKnowledge.Known,
        CounterType = "P1P1", Sources = new(SemanticKnowledge.Known, EffectRecipientBinding.Source, null,
            [new("own", SemanticEntityKind.Card, "own", ControllerId: 0)]), Recipients = recipients
    };
    private static CasualActionEstimate Estimate(GameSnapshot state, ActionEffectKind effect, SemanticEntity recipient, int amount)
        => CasualActionAssessment.Evaluate(state, new(SemanticKnowledge.Known, 0,
            [Operation(effect, new(SemanticKnowledge.Known, EffectRecipientBinding.Explicit, null, [recipient]), amount)]), 512);
    private static DecisionSemantics TargetSemantics(GameSnapshot state, ActionEffectKind effect)
    {
        var candidates = state.Players.SelectMany(player => player.Zone("Battlefield").Cards.Where(card => card.Selectable)
            .Select(card => new SemanticEntity(card.VisualId, SemanticEntityKind.Card, card.VisualId, ControllerId: player.Id))).DistinctBy(entity => entity.Id).ToImmutableArray();
        var payload = (TargetSelectionPayload)state.Decision!.Payload;
        var operation = Operation(effect, new(SemanticKnowledge.Known, EffectRecipientBinding.Targets, 0, [])) with {
            Targets = new(SemanticKnowledge.Known, payload.Range.Min, payload.Range.Max, candidates) };
        return new(effect == ActionEffectKind.Damage ? DecisionIntent.Damage : DecisionIntent.AddCounters) {
            OperationId = 0, CounterType = "P1P1", OperationGraph = new(SemanticKnowledge.Known, 0, [operation]) };
    }
    private static async Task<DecisionCommand?> Choose(GameSnapshot state, DecisionSemantics? semantics = null, long seed = 17)
    {
        using var session = new AiSeatSession(state.Id, state.ViewerId, new CasualCommanderPolicy(), seed,
            new(TimeSpan.FromSeconds(5), 4096));
        session.Observe(new(state, semantics: semantics));
        await session.EvaluateAsync();
        return session.TakeCommand()?.Command;
    }
}
