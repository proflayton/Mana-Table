using System.Collections.Immutable;
using Mana.AI;
using Mana.Contracts;

internal static class AiSwarmCombatRegression
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        await Stage(check, Enumerable.Repeat(2, 8).ToArray(), 2, 9, expected: 7);
        // Public turn-69 counts/printed-independent current powers retained by
        // the timed-out campaign. Capacity facts below are authored, not patched
        // into the old trace which did not publish this new field.
        int[] observedPowers = [.. Enumerable.Repeat(3, 30), .. Enumerable.Repeat(4, 29), .. Enumerable.Repeat(7, 6),
            .. Enumerable.Repeat(8, 6), 4, 5, 7, 5, 5, 5, 3, 8, 4];
        await Stage(check, observedPowers, 11, 23, expected: 15, ownBodies: 92, enemyBodies: 20, ownOther: 18, enemyOther: 24);
        check((await Evaluate(Frame([2, 2], 2, 9))).Proposal is CompleteAttackers,
            "An insufficient swarm does not donate bodies merely because aggregate attacks are available");
        var losingRace = Frame(Enumerable.Repeat(2, 6).ToArray(), 2, 9, ownLife: 1);
        check((await Evaluate(losingRace)).Proposal is CompleteAttackers,
            "A nonlethal group in a losing race does not enter the final-duel swarm finishing path");
        var protectedFrame = Frame(Enumerable.Repeat(2, 8).ToArray(), 2, 9);
        var reserved = new AiTurnPlan(protectedFrame.State.Id, 0, 7, 1, AiPlanPurpose.Defend, [], ["attacker-000", "attacker-001"]) {
            CreatedFrom = protectedFrame.Stamp };
        check((await Evaluate(protectedFrame, reserved)).Proposal is CompleteAttackers,
            "Existing retained defenders are excluded even when spending them would create a nominal lethal swarm");
        var oneReserved = reserved with { RetainedBlockerIds = ["attacker-000"] };
        check((await Evaluate(protectedFrame, oneReserved)).Proposal is ToggleAttackPlayer { Attacker: not "attacker-000-key-1" },
            "A spare cohort can stage its current legal attack while preserving the exact held defender");
        check((await Evaluate(Frame(Enumerable.Repeat(2, 8).ToArray(), 2, 9, missingCapacity: true))).Proposal is CompleteAttackers,
            "Legacy omitted blocking capacity does not silently become a one-block assumption");
        check((await Evaluate(Frame(Enumerable.Repeat(2, 8).ToArray(), 1, 9, capacity: new(null, true)))).Proposal is CompleteAttackers,
            "An unlimited blocker prevents a false aggregate finishing estimate");
        check((await Evaluate(Frame([2, 2, 2], 1, 3, capacity: new(2)))).Proposal is CompleteAttackers,
            "A creature with two actual block slots is not reused as if it supplied only one slot");
        check((await Evaluate(Frame(Enumerable.Repeat(2, 8).ToArray(), 2, 9, keyword: "lifelink"))).Proposal is CompleteAttackers,
            "Known lifelink on the blocking side falls back rather than claiming an ordinary-damage finish");
        check((await Evaluate(Frame(Enumerable.Repeat(2, 8).ToArray(), 2, 9, keyword: "custom"))).Proposal is CompleteAttackers,
            "An unsupported current keyword keeps the aggregate forecast incomplete");
        var changed = Frame(Enumerable.Repeat(2, 8).ToArray(), 2, 9, capacity: new(null, true),
            assigned: ["attacker-000"], revision: 2);
        check((await Evaluate(changed)).Evidence?.ReasonCode != "combat-stage-swarm-finish",
            "A changed current blocker capacity cannot inherit an older finite-capacity cohort forecast");
        var multi = Frame(Enumerable.Repeat(2, 8).ToArray(), 2, 9);
        var third = new Player(2, "Other opponent", 40, false, false, ImmutableDictionary<string, int>.Empty, []);
        check((await Evaluate(new(multi.State with { Players = multi.State.Players.Add(third) }, domain: DecisionActionDomain.None,
                semantics: multi.Semantics))).Proposal is CompleteAttackers,
            "The finishing shortcut does not widen multiplayer aggression or assume an eliminated table");
    }

    private static async Task Stage(Action<bool, string> check, int[] powers, int blockers, int enemyLife,
        int expected, int ownBodies = 0, int enemyBodies = 0, int ownOther = 0, int enemyOther = 0)
    {
        using var seat = new AiSeatSession("swarm-regression", 0, new CasualCommanderPolicy(), 17,
            new(TimeSpan.FromSeconds(10), 4096));
        var assigned = new List<string>(); bool completed = false;
        for (int step = 0; step <= 24; step++) {
            var frame = Frame(powers, blockers, enemyLife, assigned: assigned, revision: step + 1,
                ownBodies: ownBodies, enemyBodies: enemyBodies, ownOther: ownOther, enemyOther: enemyOther);
            seat.Observe(frame); await seat.EvaluateAsync(); var proposal = seat.TakeCommand();
            check(proposal != null && frame.Allows(proposal.Command) && seat.View.Status != AiSeatStatus.BudgetExceeded,
                "Every staged swarm command retains fresh current authority within its one shared 4096-unit combat allowance");
            if (proposal == null) throw new InvalidOperationException("The staged swarm lost its current command.");
            if (proposal.Command is ToggleAttackPlayer attack) {
                var source = frame.State.Viewer!.Zone("Battlefield").Cards.Single(card => card.Key == attack.Attacker);
                check(attack.Player == 1 && !assigned.Contains(source.VisualId) && proposal.ReasonCode == "combat-stage-swarm-finish",
                    "The aggregate finish adds one distinct current offered attacker instead of toggling or recycling a stale handle");
                assigned.Add(source.VisualId);
            } else if (proposal.Command is CompleteAttackers) {
                completed = true;
                check(proposal.ReasonCode == "combat-complete-swarm-finish", "The whole bounded cohort completes through the aggregate path");
            } else throw new InvalidOperationException("Unexpected swarm command: " + proposal.Command.GetType().Name);
            check(seat.Report(proposal, AiExecutionReceipt.Accepted), "The staged forecast advances only after each current command receives its accepted fixture receipt");
            if (completed) break;
        }
        check(completed && assigned.Count == expected && assigned.Count < powers.Length,
            $"The {powers.Length}-candidate position stages its minimal {expected}-body finishing cohort and completes without spending the whole board or exceeding the shared budget");
        long lower = assigned.Select(id => powers[int.Parse(id.AsSpan("attacker-".Length))]).OrderDescending().Skip(blockers).Sum();
        check(lower >= enemyLife, "Distinct blocking slots erase the largest incoming hits once; the remaining ordinary damage meets the finishing threshold");
    }

    private static Task<AiDecisionReplayResult> Evaluate(AiDecisionFrame frame, AiTurnPlan? plan = null)
        => AiDecisionReplay.EvaluateAsync(new(1, "swarm-boundary", "casual-commander.v3", frame.State, frame.Domain,
            frame.Semantics, new([], []), 17) { Plan = plan, PlanVersion = plan?.Version ?? 0 }, new CasualCommanderPolicy());

    private static AiDecisionFrame Frame(int[] powers, int blockers, int enemyLife, int ownLife = 40,
        BlockingCapacity? capacity = default, string keyword = "", List<string>? assigned = null, int revision = 1, bool missingCapacity = false,
        int ownBodies = 0, int enemyBodies = 0, int ownOther = 0, int enemyOther = 0)
    {
        capacity ??= missingCapacity ? null : new(1);
        var own = powers.Select((power, i) => Creature("attacker-" + i.ToString("D3"), power, revision)).ToList();
        for (int i = powers.Length; i < ownBodies; i++) own.Add(Creature("noncandidate-" + i, 1, revision) with { Tapped = true });
        for (int i = 0; i < ownOther; i++) own.Add(new() { VisualId = "own-land-" + i, Type = "Land", Name = "Unrelated permanent" });
        var enemy = Enumerable.Range(0, Math.Max(blockers, enemyBodies)).Select(i => Creature("blocker-" + i, i == 0 ? 65 : 10, revision) with {
            Tapped = i >= blockers, BlockingCapacity = capacity, CombatKeywords = keyword.Length > 0 ? [keyword] : [],
            States = keyword.Length > 0 ? [new("keyword", keyword, "ability", 1, null, "", keyword)] : [] }).ToList();
        for (int i = 0; i < enemyOther; i++) enemy.Add(new() { VisualId = "enemy-land-" + i, Type = "Land", Name = "Unrelated permanent" });
        var targets = new Defender("player", "1", 1, "Opponent");
        var attacks = (assigned ?? []).Select(id => new Attack(id, 1, [], []) { Defender = targets }).ToImmutableArray();
        var state = new GameSnapshot("swarm-regression", "playing", null, null, 0, 7, "Declare attackers", "COMBAT_DECLARE_ATTACKERS", 0,
            [new(0, "Actor", ownLife, false, false, ImmutableDictionary<string, int>.Empty, [new("Battlefield", own.Count, own.ToImmutableArray(), null)]),
             new(1, "Opponent", enemyLife, false, false, ImmutableDictionary<string, int>.Empty, [new("Battlefield", enemy.Count, enemy.ToImmutableArray(), null)])], [],
            new(attacks, powers.Select((_, index) => new AttackOption("attacker-" + index.ToString("D3"), [targets])).ToImmutableArray(), null),
            new("attack-input-" + revision, new(), new AttackPayload(new("Attack", true)))) {
            Revision = revision, BoardRevision = revision, DecisionOwnerId = 0, ActionClass = "attack" };
        return new(state, domain: DecisionActionDomain.None, semantics: new() { EpisodeId = "same-authored-attack-declaration" });
    }

    private static Card Creature(string id, int power, int revision) => new() { VisualId = id, CombatId = id,
        Key = id + "-key-" + revision, Name = "Disclosed creature", Type = "Creature", Power = power, Toughness = Math.Max(1, power), States = [], BlockingCapacity = new(1) };
}
