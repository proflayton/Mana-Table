using System.Collections.Immutable;
using Mana.AI;
using Mana.Contracts;

internal static class AiCombatSurvivalRegression
{
    private const string FirstCommander = "public-lineage-a", PartnerCommander = "public-lineage-b";

    // Authored legal offers exercise production policy and current command
    // admission. The real-engine journey separately proves these public facts.
    internal static async Task RunAsync(Action<bool, string> check)
    {
        foreach (long seed in new long[] { 0, 17, 91 })
        foreach (bool reverse in new[] { false, true }) {
            string context = $" (seed {seed}, reversed {reverse})";
            var huge = Creature("large-attacker", 22, 22) with { CommanderId = FirstCommander };
            var small = Creature("small-blocker", 1, 1); var large = Creature("large-blocker", 4, 4);
            var original = Blocking([huge], [small, large], life: 28, reverse: reverse);
            check((await Evaluate(original, seed)).Proposal is ToggleBlock { Blocker: "small-blocker:current" },
                "At 28 life the bot sacrifices its offered 1/1 to stop 22 damage from one commander rather than completing without blocks" + context);

            var prior = Blocking([huge with { Power = 11, Toughness = 11 }], [small, large], life: 40,
                firstDamage: 10, reverse: reverse);
            check((await Evaluate(prior, seed)).Proposal is ToggleBlock,
                "Prior damage joins only the attacking commander's exact public lineage before choosing a necessary block" + context);
            var renamed = prior with { Players = prior.Players.Select(player => player with {
                Name = "Different visible player", CommanderDamage = player.CommanderDamage.Select(row => row with {
                    Name = "Unrelated printed title", Owner = "Different registered owner", OwnerId = 0 }).ToImmutableArray(),
                Zones = player.Zones.Select(zone => zone with { Cards = zone.Cards.Select(card => card with {
                    Name = "Another printed title", CommanderTax = 0 }).ToImmutableArray() }).ToImmutableArray()
            }).ToImmutableArray() };
            check((await Evaluate(renamed, seed)).Proposal is ToggleBlock,
                "A renamed commander controlled by an opponent still uses its registered lineage after control changes" + context);

            var partners = Blocking([huge with { Power = 11, Toughness = 11 },
                Creature("partner-attacker", 10, 10) with { CommanderId = PartnerCommander }], [small, large],
                life: 40, reverse: reverse);
            check((await Evaluate(partners, seed)).Proposal is CompleteBlockers,
                "Eleven and ten damage from distinct partners do not combine into one commander defeat" + context);
            var otherPrior = Blocking([huge with { Power = 11, Toughness = 11 }], [small, large], life: 40,
                partnerDamage: 20, reverse: reverse);
            check((await Evaluate(otherPrior, seed)).Proposal is CompleteBlockers,
                "Prior damage from the other partner cannot make the current eleven-damage commander lethal" + context);

            var ordinary = Blocking([huge with { CommanderId = "", CommanderTax = 80 }], [small, large], life: 28, reverse: reverse);
            check((await Evaluate(ordinary, seed)).Proposal is CompleteBlockers,
                "A same-name noncommander copy and nonzero tax do not manufacture commander damage" + context);
            check((await Evaluate(original with { CommanderCombat = null }, seed)).Proposal is CompleteBlockers,
                "An older provider's missing commander rules retain unknown commander coverage instead of assuming a threshold" + context);
            check((await Evaluate(original with { CommanderCombat = new(null) }, seed)).Proposal is CompleteBlockers,
                "A provider-declared inapplicable commander rule does not penalize ordinary nonlethal damage" + context);
            check((await Evaluate(original with { CommanderCombat = new(31) }, seed)).Proposal is CompleteBlockers,
                "The policy consumes the provider's loss threshold rather than hardcoding the ordinary Commander limit" + context);
            var missing = original with { Players = original.Players.Select(player => player.Id != 0 ? player
                : player with { CommanderDamage = [] }).ToImmutableArray() };
            check((await Evaluate(missing, seed)).Proposal is CompleteBlockers,
                "Missing cumulative damage is unknown and never silently replaced with zero" + context);

            var trample = Blocking([Traits(huge, "trample")], [small, large], life: 28, firstDamage: 1, reverse: reverse);
            check((await Evaluate(trample, seed)).Proposal is ToggleBlock { Blocker: "large-blocker:current" },
                "Trample prevention that actually avoids the known commander defeat outranks a cheaper insufficient chump" + context);
            var doubleStrike = Blocking([Traits(huge with { Power = 11 }, "double strike")], [small, large], life: 28, reverse: reverse);
            check((await Evaluate(doubleStrike, seed)).Proposal is ToggleBlock,
                "Unblocked double strike contributes twice to the same commander lineage" + context);

            var expensive = Blocking([Traits(Creature("tiny-attacker", 1, 1), "first strike", "deathtouch")],
                [Creature("costly-blocker", 1000, 1000)], life: 1, reverse: reverse);
            check((await Evaluate(expensive, seed)).Proposal is ToggleBlock,
                "Avoiding modeled immediate life defeat outranks losing even a creature valued above the old finite survival bonus" + context);

            var retained = Attacking(Creature("only-defender", 4, 4), huge with { Tapped = true }, reverse);
            check((await Evaluate(retained, seed)).Proposal is CompleteAttackers,
                "A profitable optional attack retains the only blocker against a tapped commander that can untap and deal 22" + context);
            var noCommander = retained with { Players = retained.Players.Select(player => player with {
                Zones = player.Zones.Select(zone => zone with { Cards = zone.Cards.Select(card => card with {
                    CommanderId = "" }).ToImmutableArray() }).ToImmutableArray()
            }).ToImmutableArray() };
            check((await Evaluate(noCommander, seed)).Proposal is ToggleAttackPlayer,
                "The paired ordinary 22 damage exposure at 28 life does not reserve a phantom commander defense" + context);
        }
        await StagedPartners(check);
        await UnsupportedCoverage(check);
    }

    private static async Task StagedPartners(Action<bool, string> check)
    {
        var first = Creature("first", 11, 11) with { CommanderId = FirstCommander };
        var second = Creature("second", 11, 11) with { CommanderId = PartnerCommander };
        var one = Creature("one", 1, 1); var two = Creature("two", 4, 4);
        var state = Blocking([first, second], [one, two], 40, firstDamage: 10, partnerDamage: 10);
        var picked = (await Evaluate(state, 17)).Proposal as ToggleBlock;
        check(picked != null, "Two independently lethal partners first receive an available current defensive assignment");
        if (picked == null) return;
        string attacker = state.Players.SelectMany(player => player.Zone("Battlefield").Cards).Single(card => card.Key == picked.Attacker).VisualId;
        string blocker = state.Viewer!.Zone("Battlefield").Cards.Single(card => card.Key == picked.Blocker).VisualId;
        var next = state with { Revision = 2, BoardRevision = 2, Decision = state.Decision! with { Id = "second-block-input" },
            Combat = state.Combat! with { Attackers = state.Combat.Attackers.Select(attack => attack.CardId == attacker
                ? attack with { Blocked = true, BlockerIds = [blocker] }
                : attack with { EligibleBlockerIds = attack.EligibleBlockerIds.Where(id => id != blocker).ToImmutableArray() }).ToImmutableArray() } };
        check((await Evaluate(next, 17)).Proposal is ToggleBlock remaining && remaining.Attacker != picked.Attacker && remaining.Blocker != picked.Blocker,
            "A fresh staged declaration preserves each partner's separate incoming damage and blocks the remaining known lethal source");
    }

    private static async Task UnsupportedCoverage(Action<bool, string> check)
    {
        var small = Creature("small", 1, 1);
        var infected = Traits(Creature("infected", 12, 12), "infect");
        var unknown = Blocking([infected], [small], life: 1);
        check((await Evaluate(unknown, 17)).Proposal is CompleteBlockers,
            "Unsupported infect is not misreported as twelve ordinary life loss and established life defeat");
        var commander = Blocking([infected with { CommanderId = FirstCommander, Power = 22, Toughness = 22 }], [small], life: 40);
        check((await Evaluate(commander, 17)).Proposal is ToggleBlock,
            "Unknown poison outcomes do not erase a separately understood lethal commander-damage contribution");
        var trample = Traits(Creature("trampling", 22, 22), "trample") with { CommanderId = FirstCommander };
        var field = Blocking([trample, Creature("other", 2, 2)], [small, Creature("used-a", 1, 1), Creature("used-b", 1, 1)], 1,
            firstDamage: 0);
        field = field with { Combat = field.Combat! with { Attackers = field.Combat.Attackers.Select(attack => attack.CardId == "trampling"
            ? attack with { Blocked = true, BlockerIds = ["used-a", "used-b"] }
            : attack with { EligibleBlockerIds = [small.VisualId] }).ToImmutableArray() } };
        check((await Evaluate(field, 17)).Proposal is CompleteBlockers,
            "Unsupported multi-block trample stays unknown rather than inventing a zero contribution and certainty about the remaining combat");
    }

    private static async Task<AiDecisionReplayResult> Evaluate(GameSnapshot state, long seed)
    {
        var frame = new AiDecisionFrame(state);
        var result = await AiDecisionReplay.EvaluateAsync(new(1, "authored-combat-survival", "casual-commander.v5", state,
            DecisionActionDomain.None, new() { EpisodeId = "authored-combat-survival" }, new([], []), seed) {
                RemainingTime = TimeSpan.FromSeconds(5), RemainingWork = 2048
            }, new CasualCommanderPolicy());
        if (result.Proposal != null && !frame.Allows(result.Proposal))
            throw new InvalidOperationException("Combat survival proposed a command outside current authority.");
        return result;
    }

    private static GameSnapshot Blocking(ImmutableArray<Card> enemy, ImmutableArray<Card> own, int life,
        int firstDamage = 0, int partnerDamage = 0, bool reverse = false)
    {
        var blockers = own.Select(card => card.VisualId).ToImmutableArray();
        var attacks = enemy.Select(card => new Attack(card.VisualId, 0, [], blockers) {
            Defender = new("player", "0", 0, "Actor") }).ToImmutableArray();
        return State(own, enemy, new(attacks, [], null), new BlockPayload(new("Done", true)), life,
            firstDamage, partnerDamage, reverse);
    }

    private static GameSnapshot Attacking(Card own, Card enemy, bool reverse)
        => State([own], [enemy], new([], [new(own.VisualId, [new("player", "1", 1, "Opponent")])], null),
            new AttackPayload(new("Done", true)), 28, 0, 0, reverse);

    private static GameSnapshot State(ImmutableArray<Card> own, ImmutableArray<Card> enemy, Combat combat,
        DecisionPayload payload, int life, int firstDamage, int partnerDamage, bool reverse)
    {
        if (reverse) { own = own.Reverse().ToImmutableArray(); enemy = enemy.Reverse().ToImmutableArray();
            combat = combat with { Attackers = combat.Attackers.Reverse().Select(attack => attack with {
                EligibleBlockerIds = attack.EligibleBlockerIds.Reverse().ToImmutableArray() }).ToImmutableArray() }; }
        ImmutableArray<Player> players = [
            new(0, "Actor", life, true, false, ImmutableDictionary<string, int>.Empty, [new("Battlefield", own.Length, own, null)]) {
                TeamId = 0, CommanderDamage = [new("Same printed title", 1, "Opponent", firstDamage) { CommanderId = FirstCommander },
                    new("Same printed title", 1, "Opponent", partnerDamage) { CommanderId = PartnerCommander }] },
            new(1, "Opponent", 40, false, false, ImmutableDictionary<string, int>.Empty, [new("Battlefield", enemy.Length, enemy, null)]) { TeamId = 1 }
        ];
        if (reverse) players = players.Reverse().ToImmutableArray();
        bool blocking = payload is BlockPayload;
        return new("combat-survival-game", "playing", null, null, 0, 35,
            blocking ? "Declare blockers" : "Declare attackers", blocking ? "COMBAT_DECLARE_BLOCKERS" : "COMBAT_DECLARE_ATTACKERS",
            blocking ? 1 : 0, players, [], combat with { DeclarationId = "combat-survival-declaration" },
            new("combat-survival-input", new(), payload)) {
                Revision = 1, BoardRevision = 1, DecisionOwnerId = 0, PriorityPlayerId = 0,
                ActionClass = blocking ? "block" : "attack", CommanderCombat = new(21)
            };
    }

    private static Card Creature(string id, int power, int toughness) => new() {
        Key = id + ":current", VisualId = id, CombatId = id, Name = "Same printed title", Type = "Creature",
        Power = power, Toughness = toughness, States = []
    };
    private static Card Traits(Card card, params string[] traits) => card with {
        CombatKeywords = traits.ToImmutableArray(), States = traits.Select(key => new PublicState(key, key, "ability", 1, null, "", key)).ToImmutableArray()
    };
}
