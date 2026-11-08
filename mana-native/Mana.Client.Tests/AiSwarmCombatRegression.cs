using System.Collections.Immutable;
using Mana.AI;
using Mana.Contracts;

internal static class AiSwarmCombatRegression
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        await Stage(check, Enumerable.Repeat(2, 8).ToArray(), 2, 9, expected: 7);
        await Stage(check, [2, 2, .. Enumerable.Repeat(1, 32)], 7, 23, expected: 30,
            ownBodies: 42, enemyBodies: 11, ownOther: 7, enemyOther: 16);
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
        var heldResponse = reserved with { RetainedBlockerIds = [], Reservations = [
            new("attacker-000", "held-response", [], 0) { SourceZone = "Battlefield", ManaSourceIds = ["attacker-001"] }
        ] };
        var protectedResponse = await Evaluate(protectedFrame, heldResponse);
        check(protectedResponse.Proposal is CompleteAttackers && protectedResponse.Evidence?.RetainedPlan == heldResponse,
            "The final-duel forecast preserves both a valid retained response source and its separate funding witness instead of spending either to complete a lethal group");
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
            "Legacy absent team facts do not establish the explicit FFA model needed for multiplayer elimination");
        await MultiplayerElimination(check);
    }

    private static async Task MultiplayerElimination(Action<bool, string> check)
    {
        await StageFfa(check, 3, []);
        await StageFfa(check, 4, [2, 3]);
        var first = FfaFrame(3, []);
        var reversed = Change(first, first.State with { Players = first.State.Players.Reverse().Select(player => player with {
            Zones = player.Zones.Select(zone => zone with { Cards = zone.Cards.Reverse().ToImmutableArray() }).ToImmutableArray()
        }).ToImmutableArray(), Combat = first.State.Combat! with { AttackOptions = first.State.Combat.AttackOptions.Reverse().ToImmutableArray() } });
        var ordinary = await Evaluate(first); var reordered = await Evaluate(reversed);
        check(ordinary.Proposal is ToggleAttackPlayer { Player: 1 } && reordered.Proposal == ordinary.Proposal,
            "An explicit three-player FFA stages a nominal elimination which the old blanket multiplayer refusal missed, independently of provider order");
        var later = FfaFrame(3, [], blockers: 0, enemyLife: 40);
        later = Change(later, later.State with {
            Players = later.State.Players.Select(player => player.Id != 2 ? player : player with { Life = 9 }).ToImmutableArray(),
            Combat = later.State.Combat! with { AttackOptions = later.State.Combat.AttackOptions.Select(option => option with {
                Defenders = option.Defenders.Add(new("player", "2", 2, "Other opponent"))
            }).ToImmutableArray() }
        });
        check((await Evaluate(later)).Proposal is ToggleAttackPlayer { Player: 2 },
            "The bounded per-defender search can eliminate a later offered opponent rather than assuming the first opponent is its target");

        async Task Declines(AiDecisionFrame frame, string reason, AiTurnPlan? plan = null, int work = 4096)
        {
            var result = await Evaluate(frame, plan, work);
            if (plan != null) check(result.Evidence?.RetainedPlan == plan,
                "The refusal evaluates the exact valid retained plan rather than discarding its reserved resources");
            check(result.Status == AiSeatStatus.Ready && result.Proposal != null && frame.Allows(result.Proposal)
                    && result.Evidence?.ReasonCode != "combat-stage-swarm-finish"
                    && result.Evidence?.ReasonCode != "combat-complete-swarm-finish",
                "The multiplayer aggregate forecast declines " + reason);
        }
        var risky = FfaFrame(3, [40]);
        await Declines(risky, "a lethal known return attack even though those creatures are currently tapped");
        var phasedReturn = FfaFrame(3, [20], ownLife: 10);
        await Declines(Change(phasedReturn, phasedReturn.State with { Players = phasedReturn.State.Players.Select(player => player.Id != 2 ? player
            : player with { Zones = player.Zones.Select(zone => zone with { Cards = zone.Cards.Select(card => card with {
                PhasedOut = true
            }).ToImmutableArray() }).ToImmutableArray() }).ToImmutableArray() }), "a lethal phased-out return creature that may phase in before its next combat");
        await Declines(FfaFrame(4, [12, 12], ownLife: 20),
            "combined return damage from two surviving opponents rather than considering each alone");
        var commander = FfaFrame(3, [1]);
        await Declines(Change(commander, commander.State with { Players = commander.State.Players.Select(player => player.Id != 0 ? player
            : player with { CommanderDamage = player.CommanderDamage.Select(row => row.OwnerId == 2 ? row with { Damage = 20 } : row).ToImmutableArray() }).ToImmutableArray() }),
            "a one-damage return attack with only one commander-damage point of headroom");
        await Declines(FfaFrame(3, [21]), "possible commander-lethal return damage even at forty life");
        await Declines(Change(commander, commander.State with { Players = commander.State.Players.Select(player => player.Id != 0 ? player
            : player with { CommanderDamage = [] }).ToImmutableArray() }), "missing prior commander-damage coverage while return damage is possible");
        await Declines(Change(first, first.State with { Players = first.State.Players.Select(player => player.Id != 2 ? player
            : player with { TeamId = 1 }).ToImmutableArray() }), "shared opposing teams and their unmodeled life/blocking relationships");
        await Declines(Change(first, first.State with { Players = first.State.Players.Select(player => player.Id != 2 ? player
            : player with { TeamId = 0 }).ToImmutableArray() }), "an allied seat rather than extending a duel assumption to team combat");
        await Declines(Change(first, first.State with { Players = first.State.Players.Select(player => player.Id != 2 ? player
            : player with { TeamId = null }).ToImmutableArray() }), "an unknown current player relationship");
        await Declines(Change(first, first.State with { Players = first.State.Players.Select(player => player.Id != 2 ? player
            : player with { States = [new("unmodeled", "Unknown", "unknown", 1, null, "")] }).ToImmutableArray() }), "an unsupported current player state");
        await Declines(Change(first, first.State with { Players = first.State.Players.Select(player => player.Id != 0 ? player
            : player with { Counters = ImmutableDictionary<string, int>.Empty.Add("Poison", 1) }).ToImmutableArray() }), "an unmodeled player counter instead of assuming ordinary life is the only terminal resource");

        var incomplete = WithEnemyCards(first, cards => cards.Select(card => card with { BlockingCapacity = null }).ToImmutableArray());
        await Declines(incomplete, "missing current blocking capacity");
        await Declines(WithEnemyCards(first, cards => cards.Select(card => card with { BlockingCapacity = new(null, true) }).ToImmutableArray()),
            "unlimited blocking capacity");
        var multiBlock = FfaFrame(3, [], powers: [2, 2, 2], blockers: 1, enemyLife: 3);
        await Declines(WithEnemyCards(multiBlock, cards => cards.Select(card => card with { BlockingCapacity = new(2) }).ToImmutableArray()),
            "a defender whose two real block slots erase too much of the candidate group");
        foreach (string keyword in new[] { "lifelink", "unmodeled" }) {
            await Declines(WithEnemyCards(first, cards => cards.Select(card => card with {
                CombatKeywords = [keyword], States = [new(keyword, keyword, "ability", 1, null, "", keyword)]
            }).ToImmutableArray()), "unsupported " + keyword + " on a possible blocker");
        }
        var returnKeyword = FfaFrame(3, [1]);
        await Declines(Change(returnKeyword, returnKeyword.State with { Players = returnKeyword.State.Players.Select(player => player.Id != 2 ? player
            : player with { Zones = player.Zones.Select(zone => zone with { Cards = zone.Cards.Select(card => card with {
                CombatKeywords = ["lifelink"], States = [new("lifelink", "Lifelink", "ability", 1, null, "", "lifelink")]
            }).ToImmutableArray() }).ToImmutableArray() }).ToImmutableArray() }), "unsupported return-combat characteristics even on a tapped creature");

        var reservation = new AiTurnPlan(first.State.Id, 0, first.State.Turn, 1, AiPlanPurpose.Defend, [], ["attacker-000", "attacker-001"]) {
            CreatedFrom = first.Stamp };
        await Declines(first, "a group that needs existing retained blockers", reservation);
        var manaReservation = reservation with { RetainedBlockerIds = [], Reservations = [
            new("attacker-000", "held-response", [], 0) { SourceZone = "Battlefield", ManaSourceIds = ["attacker-001"] }
        ] };
        await Declines(first, "a group that consumes the retained response source or its mana witness", manaReservation);
        var sourceOnly = manaReservation with { Reservations = [manaReservation.Reservations[0] with { ManaSourceIds = [] }] };
        var spare = await Evaluate(first, sourceOnly);
        check(spare.Proposal is ToggleAttackPlayer { Attacker: "attacker-001-key-1", Player: 1 }
                && spare.Evidence?.ReasonCode == "combat-stage-swarm-finish" && spare.Evidence?.RetainedPlan == sourceOnly,
            "A feasible spare FFA cohort preserves the exact held response source and stages a different current attacker");
        await Declines(first, "insufficient remaining shared work to finish later stages", work: 128);
        var changed = FfaFrame(3, [], assigned: ["attacker-000"], revision: 2);
        changed = WithEnemyCards(changed, cards => cards.Select(card => card with { BlockingCapacity = new(null, true) }).ToImmutableArray());
        await Declines(changed, "changed blocker facts after an earlier accepted attack");
        check(!changed.Allows(ordinary.Proposal!),
            "An older aggregate proposal cannot retain its prior decision or card key after fresh combat publication");
        var split = FfaFrame(3, [], assigned: ["attacker-000"], revision: 2);
        split = Change(split, split.State with { Combat = split.State.Combat! with { Attackers = split.State.Combat.Attackers.Add(
            new("attacker-001", 2, [], []) { Defender = new("player", "2", 2, "Other opponent") }) } });
        await Declines(split, "an already split declaration rather than redirecting or reusing its staged assignments");
    }

    private static async Task StageFfa(Action<bool, string> check, int players, int[] returnPowers)
    {
        using var seat = new AiSeatSession("swarm-regression", 0, new CasualCommanderPolicy(), 17, new(TimeSpan.FromSeconds(10), 4096));
        var assigned = new List<string>(); bool completed = false; DecisionCommand? prior = null;
        for (int step = 0; step <= 8; step++) {
            var frame = FfaFrame(players, returnPowers, assigned: assigned, revision: step + 1);
            seat.Observe(frame); await seat.EvaluateAsync(); var ticket = seat.TakeCommand();
            check(ticket != null && frame.Allows(ticket.Command) && seat.View.Status != AiSeatStatus.BudgetExceeded
                    && (prior == null || !frame.Allows(prior)),
                "Every staged FFA elimination answers a fresh current offer within one shared input budget");
            if (ticket == null) throw new InvalidOperationException("The FFA elimination did not retain a current answer.");
            if (ticket.Command is ToggleAttackPlayer attack) {
                var card = frame.State.Viewer!.Zone("Battlefield").Cards.Single(card => card.Key == attack.Attacker);
                bool distinct = !assigned.Contains(card.VisualId);
                if (distinct) assigned.Add(card.VisualId);
                check(attack.Player == 1 && distinct && ticket.ReasonCode == "combat-stage-swarm-finish",
                    "A nominal FFA elimination stages one new offered attacker at its chosen defender");
            } else if (ticket.Command is CompleteAttackers) {
                completed = true;
                check(ticket.ReasonCode == "combat-complete-swarm-finish", "The nominal FFA elimination completes its bounded current cohort");
            } else throw new InvalidOperationException("Unexpected FFA elimination answer.");
            check(seat.Report(ticket, AiExecutionReceipt.Accepted), "Only an accepted scoped fixture receipt advances the staged FFA declaration");
            prior = ticket.Command;
            if (completed) break;
        }
        check(completed && assigned.Count == 7 && assigned.Count < 8,
            $"The explicit {players}-player FFA eliminates one nominal nine-life defender without spending the entire army or assuming the game ends");
    }

    private static AiDecisionFrame FfaFrame(int players, int[] returnPowers, int[]? powers = null,
        int blockers = 2, int enemyLife = 9, int ownLife = 40, List<string>? assigned = null, int revision = 1)
    {
        var duel = Frame(powers ?? Enumerable.Repeat(2, 8).ToArray(), blockers, enemyLife,
            ownLife, assigned: assigned, revision: revision);
        var seats = duel.State.Players.ToList();
        for (int id = 2; id < players; id++) {
            var cards = returnPowers.Length < id - 1 ? ImmutableArray<Card>.Empty
                : ImmutableArray.Create(Creature("return-" + id, returnPowers[id - 2], revision) with { Tapped = true });
            seats.Add(new(id, "Other opponent", 40, false, false, ImmutableDictionary<string, int>.Empty,
                [new("Battlefield", cards.Length, cards, null)]));
        }
        var rows = seats.Select(player => new CommanderDamage("Public commander", player.Id, "Owner", 0)).ToImmutableArray();
        return Change(duel, duel.State with { Players = seats.Select(player => player with {
            TeamId = player.Id, CommanderDamage = player.Id == 0 ? rows : []
        }).ToImmutableArray() });
    }

    private static AiDecisionFrame Change(AiDecisionFrame frame, GameSnapshot state) => new(state, domain: frame.Domain, semantics: frame.Semantics);
    private static AiDecisionFrame WithEnemyCards(AiDecisionFrame frame, Func<ImmutableArray<Card>, ImmutableArray<Card>> change)
        => Change(frame, frame.State with { Players = frame.State.Players.Select(player => player.Id != 1 ? player : player with {
            Zones = player.Zones.Select(zone => zone.Name != "Battlefield" ? zone : zone with { Cards = change(zone.Cards) }).ToImmutableArray()
        }).ToImmutableArray() });

    private static async Task Stage(Action<bool, string> check, int[] powers, int blockers, int enemyLife,
        int expected, int ownBodies = 0, int enemyBodies = 0, int ownOther = 0, int enemyOther = 0)
    {
        using var seat = new AiSeatSession("swarm-regression", 0, new CasualCommanderPolicy(), 17,
            new(TimeSpan.FromSeconds(10), 4096));
        var assigned = new List<string>(); bool completed = false;
        for (int step = 0; step <= 32; step++) {
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

    private static Task<AiDecisionReplayResult> Evaluate(AiDecisionFrame frame, AiTurnPlan? plan = null, int work = 4096)
        => AiDecisionReplay.EvaluateAsync(new(1, "swarm-boundary", "casual-commander.v3", frame.State, frame.Domain,
            frame.Semantics, new([], []), 17) { Plan = plan, PlanVersion = plan?.Version ?? 0, RemainingWork = work }, new CasualCommanderPolicy());

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
