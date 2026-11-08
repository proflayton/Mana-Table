using System.Collections.Immutable;
using Mana.AI;
using Mana.Contracts;

internal static class AiCombatContextRegression
{
    // Authored current assignments and legal offers, not a combat rules solver.
    internal static async Task RunAsync(Action<bool, string> check)
    {
        foreach (long seed in new long[] { 0, 17, 91 })
        foreach (bool reverse in new[] { false, true }) {
            string context = $" (seed {seed}, reversed {reverse})";
            check(await Evaluate(Blocks(reverse), seed) is ToggleBlock { Attacker: "second-attacker:current", Blocker: "free-blocker:current" },
                "At ten life, four assigned trample damage plus eight unblocked damage requires the second 4/4 block to survive at two life" + context);
            check(await Evaluate(Blocks(reverse, trample: false), seed) is CompleteBlockers,
                "An existing ordinary block prevents the first eight damage, so the optional losing second block is unnecessary at ten life" + context);
            check(await Evaluate(Blocks(reverse, staged: false), seed) is ToggleBlock,
                "Before either block is assigned, the same sixteen incoming trample damage requires a current legal defensive assignment" + context);
            check(await Evaluate(Blocks(reverse, trample: false, complete: false), seed) is ToggleBlock { Attacker: "second-attacker:current", Blocker: "free-blocker:current" },
                "A disabled completion still supplies the engine-required legal block despite its losing material exchange" + context);
            check(await Evaluate(Attacks(reverse, permanent: true), seed) is CompleteAttackers,
                "Two damage to a four-loyalty planeswalker cannot eliminate its one-life controller or justify exposing the two-life actor to the tapped 3/3" + context);
            check(await Evaluate(Attacks(reverse, permanent: false), seed) is ToggleAttackPlayer { Attacker: "retained-blocker:current", Player: 1 },
                "The paired attack at the last one-life player remains a legitimate nominal finish instead of unnecessarily retaining the 2/2" + context);
        }
        await StagedFinishingDamage(check);
    }

    private static async Task StagedFinishingDamage(Action<bool, string> check)
    {
        const string finish = "combat-staged-damage-finish";
        foreach (long seed in new long[] { 0, 17, 91 })
        foreach (bool reverse in new[] { false, true })
        foreach (bool rename in new[] { false, true }) {
            var state = Staged(reverse, rename);
            var result = await Forecast(state, seed);
            check(result.Status == AiSeatStatus.Ready && result.Proposal is ToggleAttackPlayer { Player: 1, Attacker: "candidate:current-1" }
                    && result.Evidence?.ReasonCode == finish,
                "Five already-staged flying damage plus the current one-power flyer finishes the six-life defender instead of spreading damage; names/provider order/seed do not supply that arithmetic");
        }
        var initial = Staged();
        async Task NoBonus(GameSnapshot state, string reason, AiTurnPlan? plan = null, int work = 3436)
        {
            var result = await Forecast(state, 17, plan, work);
            check(result.Status == AiSeatStatus.Ready && result.Proposal != null && result.Evidence?.ReasonCode != finish,
                "The staged-damage bonus declines " + reason + " while preserving a legal current answer");
            if (plan != null) check(result.Evidence?.RetainedPlan == plan,
                "The held-resource control uses the exact validated retained plan rather than dropping it");
        }
        var other = initial with { Combat = initial.Combat! with { Attackers = initial.Combat.Attackers.Select((attack, index) => index != 0 ? attack
            : attack with { DefendingPlayerId = 2, Defender = new("player", "2", 2, "Other opponent") }).ToImmutableArray() } };
        await NoBonus(other, "damage committed to another defender");
        await NoBonus(initial with { Combat = initial.Combat! with { Attackers = initial.Combat.Attackers.Select((attack, index) => index != 0 ? attack
            : attack with { Blocked = true, BlockerIds = ["ground-a"] }).ToImmutableArray() } }, "a blocked staged body");
        foreach (string trait in new[] { "infect", "assign combat damage using toughness", "custom", "unmodeled" }) {
            await NoBonus(ChangeCard(initial, "staged-four", card => Traits(card, "flying", trait)),
                "an unsupported staged " + trait + " body, leaving only one independently supported damage before the candidate");
        }
        await NoBonus(ChangeCard(initial, "staged-four", card => card with { Power = null }), "missing current staged power");
        await NoBonus(ChangeCard(initial, "staged-four", card => card with { FaceDown = true, BlockingCapacity = null }), "a face-down staged body");
        await NoBonus(ChangeCard(initial, "staged-four", card => card with { States = null }), "legacy hints without authoritative current damage states");
        await NoBonus(ChangeCard(initial, "staged-four", card => card with { VisualId = "", CombatId = "" }), "a missing physical staged identity");
        await NoBonus(ChangeField(initial, 0, cards => cards.Where(card => card.VisualId != "staged-four").ToImmutableArray()), "a departed staged occurrence");
        var foreign = initial.Players.First(player => player.Id == 0).Zone("Battlefield").Cards.First(card => card.VisualId == "staged-four");
        var moved = ChangeField(initial, 0, cards => cards.Where(card => card.VisualId != "staged-four").ToImmutableArray());
        await NoBonus(ChangeField(moved, 2, cards => cards.Add(foreign)), "a currently foreign-controlled staged occurrence");
        var walker = new Card { VisualId = "walker", CombatId = "walker", Key = "walker:current-1", Name = "Disclosed permanent", Type = "Planeswalker", States = [] };
        var permanent = ChangeField(initial, 1, cards => cards.Add(walker));
        await NoBonus(permanent with { Combat = permanent.Combat! with { Attackers = permanent.Combat.Attackers.Select((attack, index) => index != 0 ? attack
            : attack with { Defender = new("planeswalker", "walker", 1, walker.Name) }).ToImmutableArray() } }, "damage assigned to a permanent rather than its controller");
        await NoBonus(ChangeCard(initial, "ground-a", card => Traits(card, "reach")), "a current possible reach blocker");
        await NoBonus(ChangeCard(initial, "ground-a", card => Traits(card, "custom")), "an unmodeled potential blocker's characteristics");
        await NoBonus(ChangeCard(initial, "candidate", card => Traits(card, "flying", "infect")), "non-life damage from the proposed contributor");
        await NoBonus(ChangeCard(initial, "staged-four", card => card with { Power = 5 }), "an already nominally lethal group receiving an additional group bonus");
        var duplicate = initial.Players.First(player => player.Id == 0).Zone("Battlefield").Cards.First(card => card.VisualId == "staged-four");
        var duplicateRows = ChangeField(initial, 0, cards => cards.Add(duplicate));
        // Legacy relationships reach the ordinary comparison path without the
        // separate FFA forecast's dictionary rejecting this malformed row first.
        duplicateRows = duplicateRows with { Players = duplicateRows.Players.Select(player => player with { TeamId = null }).ToImmutableArray() };
        await NoBonus(duplicateRows, "a repeated physical row even when both entries share one immutable object");
        await NoBonus(initial with { Combat = initial.Combat! with { Attackers = initial.Combat.Attackers.Add(initial.Combat.Attackers[0]) } },
            "a duplicated staged physical assignment");
        var alias = ChangeCard(initial, "staged-four", card => card with { CombatId = "staged-four-alias" });
        alias = alias with { Combat = alias.Combat! with { AttackOptions = alias.Combat.AttackOptions.Select(option => option.CardId != "staged-four" ? option
            : option with { CardId = "staged-four-alias" }).ToImmutableArray() } };
        var retarget = await Forecast(alias, 17);
        check(retarget.Proposal is ToggleAttackPlayer { Attacker: "candidate:current-1", Player: 1 }
                && retarget.Evidence?.ReasonCode == finish,
            "A staged body's other current identity alias cannot count that same damage again as a new contributor or displace the actual finishing candidate");

        var frame = new AiDecisionFrame(initial);
        foreach (bool funding in new[] { false, true }) {
            var held = new AiTurnPlan(initial.Id, 0, initial.Turn, 1, AiPlanPurpose.HoldInteraction,
                [new(funding ? "staged-one" : "candidate", "retained-response", [], 0) {
                    SourceZone = "Battlefield", ManaSourceIds = funding ? ["candidate"] : []
                }], []) { CreatedFrom = frame.Stamp };
            await NoBonus(initial, funding ? "a candidate held as the response's funding witness" : "a candidate held as the response source", held);
        }
        var required = ChangeCard(initial, "staged-four", card => card with { Power = null });
        required = required with { Decision = required.Decision! with { Payload = new AttackPayload(new("Done", false)) } };
        var mandatory = await Forecast(required, 17);
        check(mandatory.Status == AiSeatStatus.Ready && mandatory.Proposal is ToggleAttackPlayer,
            "Unsupported staged knowledge does not remove a current engine-required attack answer");
        var crowded = ChangeField(initial, 0, cards => cards.AddRange(Enumerable.Range(0, 64).Select(index => new Card {
            Key = "spare-" + index + ":current-1", VisualId = "spare-" + index, CombatId = "spare-" + index,
            Name = "Disclosed artifact", Type = "Artifact", States = []
        })));
        var limited = await Forecast(crowded, 17, work: 96);
        check(limited.Status == AiSeatStatus.Ready && limited.Evidence is { RemainingWork: >= 64 }
                && limited.Evidence.ReasonCode != finish,
            "Incomplete optional indexing contributes no staged-damage bonus and retains the existing mechanical work reserve");
        var answer = await Forecast(initial, 17);
        var fresh = Staged(revision: 2);
        check(answer.Proposal != null && !new AiDecisionFrame(fresh).Allows(answer.Proposal)
                && (await Forecast(fresh, 17)).Proposal is ToggleAttackPlayer { Player: 1, Attacker: "candidate:current-2" },
            "A fresh publication requires its fresh decision and card key even when the public staged-damage arithmetic is unchanged");
    }

    private static async Task<AiDecisionReplayResult> Forecast(GameSnapshot state, long seed, AiTurnPlan? plan = null, int work = 3436)
    {
        var frame = new AiDecisionFrame(state);
        var result = await AiDecisionReplay.EvaluateAsync(new(1, "authored-staged-damage", "casual-commander.v5", state,
            DecisionActionDomain.None, new() { EpisodeId = "authored-staged-declaration" }, new([], []), seed) {
                RemainingTime = TimeSpan.FromSeconds(5), RemainingWork = work, Plan = plan, PlanVersion = plan?.Version ?? 0
            }, new CasualCommanderPolicy());
        if (result.Proposal != null && !frame.Allows(result.Proposal))
            throw new InvalidOperationException("Staged damage proposed an answer outside current combat authority.");
        return result;
    }

    private static GameSnapshot Staged(bool reverse = false, bool rename = false, int revision = 1)
    {
        Card Body(string id, int power, params string[] traits) => Traits(Creature(id, power, Math.Max(1, power)) with {
            Key = id + ":current-" + revision, States = [], BlockingCapacity = new(1), Name = rename ? "Different visible title" : "Disclosed creature"
        }, traits);
        ImmutableArray<Card> own = [Body("staged-four", 4, "flying", "prowess"), Body("staged-one", 1, "flying"), Body("candidate", 1, "flying")];
        ImmutableArray<Card> first = [Body("ground-a", 2), Body("ground-b", 4)];
        ImmutableArray<Card> second = [Body("other-ground", 3)];
        var target = new Defender("player", "1", 1, "Opponent"); var other = new Defender("player", "2", 2, "Other opponent");
        ImmutableArray<Attack> attacks = [new("staged-four", 1, [], []) { Defender = target }, new("staged-one", 1, [], []) { Defender = target }];
        ImmutableArray<AttackOption> offers = own.Select(card => new AttackOption(card.VisualId,
            reverse ? [other, target] : [target, other])).ToImmutableArray();
        if (reverse) { own = own.Reverse().ToImmutableArray(); first = first.Reverse().ToImmutableArray(); second = second.Reverse().ToImmutableArray();
            attacks = attacks.Reverse().ToImmutableArray(); offers = offers.Reverse().ToImmutableArray(); }
        ImmutableArray<Player> players = [
            new(0, rename ? "Another actor" : "Actor", 40, true, false, ImmutableDictionary<string, int>.Empty, [new("Battlefield", own.Length, own, null)]),
            new(1, rename ? "Different opponent" : "Opponent", 6, false, false, ImmutableDictionary<string, int>.Empty, [new("Battlefield", first.Length, first, null)]),
            new(2, rename ? "Renamed rival" : "Other opponent", 20, false, false, ImmutableDictionary<string, int>.Empty, [new("Battlefield", second.Length, second, null)])
        ];
        players = players.Select(player => player with { TeamId = player.Id }).ToImmutableArray();
        if (reverse) players = players.Reverse().ToImmutableArray();
        return new("staged-combat-context", "playing", null, null, 0, 4, "Declare attackers", "COMBAT_DECLARE_ATTACKERS", 0,
            players, [], new(attacks, offers, null) { DeclarationId = "authored-staged-declaration" },
            new("staged-input-" + revision, new(), new AttackPayload(new("Done", true)))) {
                Revision = revision, BoardRevision = revision, DecisionOwnerId = 0, PriorityPlayerId = 0, ActionClass = "attack"
            };
    }

    private static Card Traits(Card card, params string[] traits) => card with { CombatKeywords = traits.ToImmutableArray(),
        States = traits.Select(key => new PublicState(key, key, "ability", 1, null, "", key)).ToImmutableArray() };
    private static GameSnapshot ChangeCard(GameSnapshot state, string id, Func<Card, Card> change) => state with {
        Players = state.Players.Select(player => player with { Zones = player.Zones.Select(zone => zone with {
            Cards = zone.Cards.Select(card => card.VisualId == id ? change(card) : card).ToImmutableArray()
        }).ToImmutableArray() }).ToImmutableArray()
    };
    private static GameSnapshot ChangeField(GameSnapshot state, int playerId, Func<ImmutableArray<Card>, ImmutableArray<Card>> change) => state with {
        Players = state.Players.Select(player => player.Id != playerId ? player : player with { Zones = player.Zones.Select(zone => {
            if (zone.Name != "Battlefield") return zone;
            var cards = change(zone.Cards); return zone with { Cards = cards, Count = cards.Length };
        }).ToImmutableArray() }).ToImmutableArray()
    };

    private static async Task<DecisionCommand?> Evaluate(GameSnapshot state, long seed)
    {
        var frame = new AiDecisionFrame(state);
        using var seat = new AiSeatSession(state.Id, state.ViewerId, new CasualCommanderPolicy(), seed,
            new(TimeSpan.FromSeconds(5), workUnits: 1024));
        seat.Observe(frame);
        await seat.EvaluateAsync();
        var command = seat.TakeCommand()?.Command;
        if (command != null && !frame.Allows(command))
            throw new InvalidOperationException("Combat context policy proposed a command outside the current authored authority.");
        return command;
    }

    private static GameSnapshot Blocks(bool reverse, bool trample = true, bool staged = true, bool complete = true)
    {
        var first = Creature("first-attacker", 8, 8, trample);
        var second = Creature("second-attacker", 8, 8, trample);
        var assigned = Creature("assigned-blocker", 4, 4);
        var free = Creature("free-blocker", 4, 4);
        ImmutableArray<string> available = staged ? [free.VisualId] : [assigned.VisualId, free.VisualId];
        if (reverse) available = available.Reverse().ToImmutableArray();
        ImmutableArray<Attack> attacks = [
            new(first.VisualId, 0, staged ? [assigned.VisualId] : [], staged ? [] : available) {
                Blocked = staged, Defender = new("player", "0", 0, "Actor") },
            new(second.VisualId, 0, [], available) { Defender = new("player", "0", 0, "Actor") }
        ];
        if (reverse) attacks = attacks.Reverse().ToImmutableArray();
        return State([assigned, free], [first, second], new(attacks, [], null),
            new BlockPayload(new("Done", complete)), ownLife: 10, enemyLife: 40, reverse: reverse);
    }

    private static GameSnapshot Attacks(bool reverse, bool permanent)
    {
        var own = Creature("retained-blocker", 2, 2);
        var enemy = Creature("next-turn-attacker", 3, 3) with { Tapped = true };
        var walker = new Card { Key = "walker:current", VisualId = "walker", CombatId = "walker", Name = "Disclosed planeswalker",
            Type = "Planeswalker", Counters = ImmutableDictionary<string, int>.Empty.Add("Loyalty", 4) };
        var defender = permanent ? new Defender("planeswalker", walker.VisualId, 1, walker.Name) : new("player", "1", 1, "Opponent");
        return State([own], [enemy, walker], new([], [new(own.VisualId, [defender])], null),
            new AttackPayload(new("Done", true)), ownLife: 2, enemyLife: 1, reverse: reverse);
    }

    private static GameSnapshot State(ImmutableArray<Card> own, ImmutableArray<Card> enemy, Combat combat,
        DecisionPayload payload, int ownLife, int enemyLife, bool reverse)
    {
        if (reverse) { own = own.Reverse().ToImmutableArray(); enemy = enemy.Reverse().ToImmutableArray(); }
        ImmutableArray<Player> players = [
            new(0, "Actor", ownLife, true, false, ImmutableDictionary<string, int>.Empty, [new("Battlefield", own.Length, own, null)]),
            new(1, "Opponent", enemyLife, false, false, ImmutableDictionary<string, int>.Empty, [new("Battlefield", enemy.Length, enemy, null)])
        ];
        if (reverse) players = players.Reverse().ToImmutableArray();
        bool blocking = payload is BlockPayload;
        return new("combat-context-game", "playing", null, null, 0, 4,
            blocking ? "Declare blockers" : "Declare attackers", blocking ? "COMBAT_DECLARE_BLOCKERS" : "COMBAT_DECLARE_ATTACKERS", blocking ? 1 : 0,
            players, [], combat, new("current-combat", new(), payload)) {
                Revision = 1, BoardRevision = 1, DecisionOwnerId = 0, PriorityPlayerId = 0, ActionClass = blocking ? "block" : "attack"
            };
    }

    private static Card Creature(string id, int power, int toughness, bool trample = false) => new() {
        Key = id + ":current", VisualId = id, CombatId = id, Name = "Disclosed creature", Type = "Creature", Power = power, Toughness = toughness,
        CombatKeywords = trample ? ["trample"] : [],
        States = trample ? [new("trample", "Trample", "ability", 1, null, "", "trample")] : null
    };
}
