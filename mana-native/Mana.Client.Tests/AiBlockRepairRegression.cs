using System.Collections.Immutable;
using Mana.AI;
using Mana.Contracts;

internal static class AiBlockRepairRegression
{
    private const string GameId = "block-repair-game", Episode = "same-block-declaration";
    private static readonly ImmutableArray<BlockAssignment> ClearOwn = [];

    // Authored current authority and rules-certified removal witnesses. These
    // checks do not infer menace, requirements or legal blocks from card prose.
    internal static async Task RunAsync(Action<bool, string> check)
    {
        foreach (long seed in new long[] { 0, 91 })
        foreach (bool reverse in new[] { false, true }) {
            string context = $" (seed {seed}, reversed {reverse})";
            ImmutableArray<BlockAssignment> current = [Pair(0, 0), Pair(1, 1), Pair(1, 2)];
            ImmutableArray<BlockAssignment> desired = [Pair(1, 1), Pair(1, 2)];
            using var seat = Session(seed);
            var invalid = Frame(current, desired, bodyCount: 3, reverse: reverse);
            seat.Observe(invalid); await seat.EvaluateAsync(); var remove = seat.TakeCommand();
            check(remove?.Command is ToggleBlock toggle && Assignment(invalid, toggle) == Pair(0, 0)
                    && invalid.Allows(toggle) && remove.ReasonCode == "combat-block-repair-remove"
                    && seat.Report(remove, AiExecutionReceipt.Accepted),
                "A typed block repair removes only the undesired own singleton, retaining the valid two-blocker assignment and another player's block without parsing translated warnings" + context);

            var valid = Frame(desired, null, revision: 2, invalid: false, bodyCount: 3, reverse: reverse);
            seat.Observe(valid); await seat.EvaluateAsync(); var finish = seat.TakeCommand();
            check(finish?.Command is CompleteBlockers && valid.Allows(finish.Command)
                    && !valid.Allows(remove?.Command) && valid.State.Combat!.Attackers.Single(attack => attack.CardId == "foreign-attacker").BlockerIds.SequenceEqual(["foreign-blocker"])
                    && seat.Report(finish, AiExecutionReceipt.Accepted),
                "The accepted repair completes the same declaration across fresh input IDs and keys instead of readding the removed block or disturbing retained and foreign assignments" + context);
        }

        await ReceiptAndEpisode(check, AiExecutionReceipt.Rejected, sameEpisode: true);
        await ReceiptAndEpisode(check, AiExecutionReceipt.Accepted, sameEpisode: false);
        await RejectedRetry(check);
        await ManyRemovals(check);
        await UnknownAndAuthority(check);
        InvalidWitnesses(check);
    }

    private static async Task ReceiptAndEpisode(Action<bool, string> check, AiExecutionReceipt receipt, bool sameEpisode)
    {
        using var seat = Session();
        var invalid = Frame([Pair(0, 0)], ClearOwn);
        seat.Observe(invalid); await seat.EvaluateAsync(); var repair = seat.TakeCommand();
        check(repair?.Command is ToggleBlock && seat.Report(repair, receipt),
            "The block-repair lifecycle records the actual fixture receipt before testing its completion boundary: " + receipt);
        var valid = Frame([], null, revision: 2, invalid: false, episode: sameEpisode ? Episode : "unrelated-block-declaration");
        seat.Observe(valid); await seat.EvaluateAsync(); var next = seat.TakeCommand();
        check(next?.Command is ToggleBlock toggle && Assignment(valid, toggle) == Pair(0, 0) && valid.Allows(toggle)
                && next.ReasonCode != "combat-block-repair-remove",
            sameEpisode
                ? "A rejected repair cannot establish an accepted-completion shortcut when a fresh observation offers a profitable ordinary block"
                : "An accepted repair in a different causal episode cannot suppress a profitable ordinary block in this declaration");
    }

    private static async Task RejectedRetry(Action<bool, string> check)
    {
        using var seat = Session();
        var first = Frame([Pair(0, 0)], ClearOwn);
        seat.Observe(first); await seat.EvaluateAsync(); var rejected = seat.TakeCommand();
        check(rejected?.Command is ToggleBlock && seat.Report(rejected, AiExecutionReceipt.Rejected),
            "A rejected current repair removal is retained as rejected evidence");
        var refreshed = Frame([Pair(0, 0)], ClearOwn, revision: 2);
        seat.Observe(refreshed); await seat.EvaluateAsync(); var retried = seat.TakeCommand();
        check(retried?.Command is ToggleBlock toggle && Assignment(refreshed, toggle) == Pair(0, 0)
                && refreshed.Allows(toggle) && !refreshed.Allows(rejected?.Command)
                && retried.ReasonCode == "combat-block-repair-remove" && seat.Report(retried, AiExecutionReceipt.Accepted),
            "An unchanged invalid declaration retries its certified removal using fresh authority rather than treating rejection as progress or reusing the old command");
    }

    private static async Task ManyRemovals(Action<bool, string> check)
    {
        const int count = 64;
        var current = Enumerable.Range(0, count).Select(index => Pair(index, index)).ToImmutableArray();
        using var seat = Session(work: 1024);
        int remaining = 1024;
        bool ready = true, distinct = true, accepted = true, bounded = true, stale = true;
        var removed = new HashSet<BlockAssignment>();
        DecisionCommand? previous = null;
        for (int step = 0; step < count; step++) {
            var frame = Frame(current, ClearOwn, revision: step + 1, bodyCount: count, reverse: step % 2 == 0);
            seat.Observe(frame);
            ready &= (await seat.EvaluateAsync()).Status == AiSeatStatus.Ready;
            var submission = seat.TakeCommand();
            if (submission?.Command is not ToggleBlock toggle) { ready = false; break; }
            var pair = Assignment(frame, toggle);
            distinct &= current.Contains(pair) && removed.Add(pair) && frame.Allows(toggle);
            stale &= previous == null || !frame.Allows(previous);
            int available = Remaining(seat, submission);
            bounded &= available < remaining && available > 0;
            remaining = available;
            accepted &= seat.Report(submission, AiExecutionReceipt.Accepted);
            current = current.Remove(pair);
            previous = toggle;
        }
        var final = Frame(current, null, revision: count + 1, invalid: false, bodyCount: count);
        seat.Observe(final); var completed = await seat.EvaluateAsync(); var finish = seat.TakeCommand();
        check(ready && distinct && accepted && bounded && stale && removed.Count == count && current.IsEmpty
                && completed.Status == AiSeatStatus.Ready && finish?.Command is CompleteBlockers && final.Allows(finish.Command)
                && Remaining(seat, finish) < remaining && seat.Report(finish, AiExecutionReceipt.Accepted),
            "Sixty-four required own-block removals and final completion share one 1024-unit causal allowance, consume fresh accepted commands once, and never reopen optional block comparisons");
    }

    private static async Task UnknownAndAuthority(Action<bool, string> check)
    {
        var unknown = Frame([Pair(0, 0)], null);
        using (var seat = Session()) {
            seat.Observe(unknown); var status = await seat.EvaluateAsync();
            check(status.Status == AiSeatStatus.Unavailable && seat.TakeCommand() == null,
                "An invalid all-assigned declaration with an unknown witness cannot fabricate a removal or complete through a disabled control");
        }
        var certified = Frame([Pair(0, 0)], ClearOwn, complete: true);
        using (var seat = Session()) {
            seat.Observe(certified); await seat.EvaluateAsync(); var repair = seat.TakeCommand();
            check(repair?.Command is ToggleBlock toggle && certified.Allows(toggle),
                "A nonempty rules problem with a certified empty own repair cannot be mistaken for valid completion even if the authored completion control is enabled");
        }
        var stale = Frame([Pair(0, 0)], ClearOwn, revision: 2);
        check(!stale.Allows(new ToggleBlock(GameId, "block-input-1", Key("attack-000", 1), Key("blocker-000", 1)))
                && !stale.Allows(new ToggleBlock(GameId, "block-input-2", Key("attack-000", 1), Key("blocker-000", 1)))
                && !stale.Allows(new ToggleBlock(GameId, "block-input-2", Key("attack-000", 2), Key("foreign-blocker", 2))),
            "Block repair retains exact current input, current card keys and offered attacker-blocker membership instead of broadening command authority");
        bool absentRejected = false;
        try { _ = Frame([Pair(0, 0)], ClearOwn, removalOffered: false); }
        catch (ArgumentException) { absentRejected = true; }
        check(absentRejected,
            "A repair witness cannot cross the current authority boundary when its required removal is absent from the offered block domain");
    }

    private static void InvalidWitnesses(Action<bool, string> check)
    {
        ImmutableArray<BlockAssignment> current = [Pair(0, 0), Pair(1, 1), Pair(2, 2)];
        foreach (var (repair, label) in new (ImmutableArray<BlockAssignment> Repair, string Label)[] {
            (default, "uninitialized array"), (current, "no removal"),
            ([Pair(0, 0), Pair(0, 0)], "duplicate assignment"),
            ([Pair(0, 1)], "unassigned own pair"),
            ([new("foreign-attacker", "foreign-blocker")], "another player's block"),
            ([new("missing-attacker", "blocker-000")], "missing attacker")
        }) {
            bool rejected = false;
            try { _ = Frame(current, repair, bodyCount: 3); }
            catch (ArgumentException) { rejected = true; }
            check(rejected, "A block-repair witness must be an initialized, strictly smaller complete subset of the current own assignments: " + label);
        }
        bool validRejected = false;
        try { _ = Frame([], ClearOwn, invalid: false); }
        catch (ArgumentException) { validRejected = true; }
        check(validRejected, "A valid declaration cannot publish an actionable removal witness");
    }

    private static AiSeatSession Session(long seed = 17, int work = 1024)
        => new(GameId, 0, new CasualCommanderPolicy(), seed, new(TimeSpan.FromSeconds(5), work));

    private static int Remaining(AiSeatSession seat, AiSubmission submission)
        => seat.History.Decisions.Last(record => record.Stamp == submission.Stamp
            && record.Outcome == AiDecisionOutcome.Submitted).RemainingWork;

    private static BlockAssignment Pair(int attacker, int blocker)
        => new("attack-" + attacker.ToString("D3"), "blocker-" + blocker.ToString("D3"));

    private static BlockAssignment Assignment(AiDecisionFrame frame, ToggleBlock toggle)
    {
        var cards = frame.State.Players.SelectMany(player => player.Zone("Battlefield").Cards).ToArray();
        return new(cards.Single(card => card.Key == toggle.Attacker).CombatId,
            cards.Single(card => card.Key == toggle.Blocker).CombatId);
    }

    private static AiDecisionFrame Frame(ImmutableArray<BlockAssignment> assigned, ImmutableArray<BlockAssignment>? repair,
        int revision = 1, bool invalid = true, int bodyCount = 1, bool reverse = false, string episode = Episode,
        bool? complete = null, bool removalOffered = true)
    {
        var own = Enumerable.Range(0, bodyCount).Select(index => Creature("blocker-" + index.ToString("D3"), 3, 4)).ToImmutableArray();
        var incoming = Enumerable.Range(0, bodyCount).Select(index => Creature("attack-" + index.ToString("D3"), 2, 2)).ToImmutableArray();
        var foreignAttacker = Creature("foreign-attacker", 2, 2);
        var foreignBlocker = Creature("foreign-blocker", 3, 4);
        var attacks = incoming.Select(card => new Attack(card.CombatId, 0,
            assigned.Where(pair => pair.AttackerId == card.CombatId).Select(pair => pair.BlockerId).ToImmutableArray(),
            removalOffered ? own.Select(blocker => blocker.CombatId).ToImmutableArray() : []) {
                Defender = new("player", "0", 0, "Actor"), Blocked = assigned.Any(pair => pair.AttackerId == card.CombatId)
            }).Append(new(foreignAttacker.CombatId, 2, [foreignBlocker.CombatId], [foreignBlocker.CombatId]) {
                Defender = new("player", "2", 2, "Other defender"), Blocked = true }).ToImmutableArray();
        ImmutableArray<Player> players = [
            new(0, "Actor", 40, false, false, ImmutableDictionary<string, int>.Empty, [new("Battlefield", own.Length, own, null)]),
            new(1, "Attacking player", 40, false, false, ImmutableDictionary<string, int>.Empty,
                [new("Battlefield", incoming.Length + 1, incoming.Add(foreignAttacker), null)]),
            new(2, "Other defender", 40, false, false, ImmutableDictionary<string, int>.Empty,
                [new("Battlefield", 1, [foreignBlocker], null)]),
            new(3, "Other player", 40, false, false, ImmutableDictionary<string, int>.Empty, [])
        ];
        if (reverse) { attacks = attacks.Reverse().ToImmutableArray(); players = players.Reverse().ToImmutableArray(); }
        var state = new GameSnapshot(GameId, "playing", null, null, 0, 46, "Declare blockers", "COMBAT_DECLARE_BLOCKERS", 1,
            players, [], new(attacks, [], invalid ? "Zuweisung muss berichtigt werden." : null) { BlockRepair = repair },
            new("block-input-" + revision, new(), new BlockPayload(new("Fertig", complete ?? !invalid)))) {
                Revision = revision, BoardRevision = revision, DecisionOwnerId = 0, ActionClass = "block"
            };
        return new(state, domain: DecisionActionDomain.None, semantics: new() { EpisodeId = episode });

        Card Creature(string id, int power, int toughness) => new() {
            Key = Key(id, revision), VisualId = id, CombatId = id, Name = "Unrelated translated creature",
            Type = "Creature", Power = power, Toughness = toughness, States = [], CombatKeywords = []
        };
    }

    private static string Key(string id, int revision) => id + ":key:" + revision;
}
