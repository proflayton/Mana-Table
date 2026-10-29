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
    }

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
