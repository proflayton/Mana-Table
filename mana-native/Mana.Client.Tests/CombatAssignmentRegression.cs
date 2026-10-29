using System.Collections.Immutable;
using Mana.Client;
using Mana.Contracts;

internal static class CombatAssignmentRegression
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var state = State(2);
        // The unrestricted blocker sorts first. Greedy pairing would strand the
        // ground-only blocker; the reviewed plan must retain the flying edge.
        state = state with { Combat = state.Combat! with { Attackers = [
            state.Combat.Attackers[0] with { EligibleBlockerIds = ["b000", "b001"] },
            state.Combat.Attackers[1] with { EligibleBlockerIds = ["b000"] }] } };
        var preview = CombatAssignmentPreview.PairOneEach(state, ["b000", "b001"], ["a000", "a001"]);
        check(preview is { Proposed.Length: 2 } && preview.Proposed.Contains(new("a001", "b000"))
                && preview.Proposed.Contains(new("a000", "b001")),
            "A reviewed group block uses current pair eligibility to preserve the constrained flying edge instead of greedily wasting a blocker");
        check(CombatAssignmentPreview.PairOneEach(state, ["b000"], ["a000", "a001"]) is { UnpairedAttackers: 1 }
                && CombatAssignmentPreview.PairOneEach(state, ["b000", "b001"], ["a000"]) is { UnpairedBlockers: 1 },
            "A group preview exposes unmatched attackers and blockers instead of claiming every selected creature is covered");
        check(CombatAssignmentIntent.TryArm(state with { Combat = state.Combat with { DeclarationId = "" } }, preview!.Desired) == null
                && CombatAssignmentPreview.PairOneEach(state, ["missing"], ["a000"]) == null
                && CombatAssignmentIntent.TryArm(state, [new("a000", "foreign")]) == null,
            "Legacy declarations, missing creatures and another seat's blocker cannot arm a group assignment");
        await Hundred(check);
        await PreserveAndReplace(check);
        await Interruptions(check);
    }

    private static async Task Hundred(Action<bool, string> check)
    {
        var initial = State(100);
        var preview = CombatAssignmentPreview.PairOneEach(initial, Blockers(100), Attackers(100))!;
        var intent = CombatAssignmentIntent.TryArm(initial, preview.Desired)!;
        var connection = new Assigning(initial);
        var session = new ClientSession(connection); session.Accept(initial);
        int publications = 0, batches = 0; bool bounded = true;
        session.Changed += (_, _) => publications++;
        try {
            while (intent.IsActive && batches++ < 20) {
                int before = connection.Commands.Count;
                check(intent.Advance(session, (_, _) => false), "Reviewed combat assignments use the ordinary serialized session lane");
                await Pump(session);
                bounded &= connection.Commands.Count - before is > 0 and <= 32;
            }
            check(intent.Completed && intent.Remaining == 0 && bounded && batches >= 4
                    && connection.Commands.Count == 100 && publications == 100 && connection.Observations == 100,
                "One reviewed 100-creature arrangement applies all exact block pairs, streams each result and yields the session within 32 commands");
            check(connection.Commands.All(command => command is ToggleBlock) && session.State!.Decision!.Payload is BlockPayload
                    && session.State.Combat!.Attackers.All(attack => attack.BlockerIds.Length == 1),
                "Group assignment never confirms combat; the player can review every staged block before choosing Confirm blocks");
            check(connection.Commands.Cast<ToggleBlock>().Select(command => command.DecisionId).Distinct().Count() == 100
                    && connection.Commands.Cast<ToggleBlock>().All(command => command.Attacker.StartsWith(command.DecisionId + ":", StringComparison.Ordinal)
                        && command.Blocker.StartsWith(command.DecisionId + ":", StringComparison.Ordinal)),
                "Every block toggle uses the newly observed prompt and current card handles instead of replaying the first declaration command");
        } finally { session.Close(); }
    }

    private static async Task PreserveAndReplace(Action<bool, string> check)
    {
        var existing = State(3, [new("a000", "b000"), new("a002", "foreign")]);
        var preview = CombatAssignmentPreview.PairOneEach(existing, Blockers(3), Attackers(3))!;
        check(preview.Proposed.SequenceEqual([new BlockAssignment("a001", "b001")])
                && preview.Desired.Contains(new("a000", "b000")) && !preview.Desired.Any(pair => pair.BlockerId == "foreign"),
            "Pairing leaves current blocks intact, skips already covered attackers, and gives no authority over another seat's assignments");
        var connection = new Assigning(existing);
        var session = new ClientSession(connection); session.Accept(existing);
        try {
            var intent = CombatAssignmentIntent.TryArm(existing, [new("a001", "b000")])!;
            intent.Advance(session, (_, _) => false); await Pump(session);
            check(intent.Completed && connection.Commands.Count == 2
                    && session.State!.Combat!.Attackers[0].BlockerIds.IsEmpty
                    && session.State.Combat.Attackers[1].BlockerIds.SequenceEqual(["b000"])
                    && session.State.Combat.Attackers[2].BlockerIds.SequenceEqual(["foreign"]),
                "An explicit replacement first removes the old own assignment, then adds the fresh legal pair while preserving the foreign block");
        } finally { session.Close(); }
    }

    private static async Task Interruptions(Action<bool, string> check)
    {
        var initial = State(3); var desired = new[] { new BlockAssignment("a000", "b000"), new("a001", "b001") };
        var monotonic = CombatAssignmentIntent.TryArm(initial, desired)!;
        check(monotonic.CanContinue(State(3, revision: 2))
                && ((IFiniteDecisionPolicy)monotonic).NextCommand(initial) == null,
            "A retained older declaration frame cannot generate block commands after the intent has observed a newer revision");
        foreach (var changed in new[] {
            State(3, revision: 2) with { Turn = 9 },
            State(3, revision: 2) with { Combat = initial.Combat! with { DeclarationId = "another-declaration" } },
            State(3, revision: 2) with { Decision = new("required", new(), new PaymentPayload(new("Pay", true), new("Cancel", false))) },
            State(3, revision: 2) with { Combat = initial.Combat! with { Attackers = initial.Combat.Attackers.RemoveAt(1) } },
            State(3, [new("a002", "b002")], revision: 2)
        }) {
            var intent = CombatAssignmentIntent.TryArm(initial, desired)!;
            check(!intent.CanContinue(changed) && !intent.IsActive,
                "Changed turn, declaration, required choice, attacker membership or unexpected own assignments revoke the reviewed combat plan");
        }
        var lostEligibility = initial with { Combat = initial.Combat! with { Attackers = initial.Combat.Attackers
            .Select(attack => attack with { EligibleBlockerIds = [] }).ToImmutableArray() } };
        var noRoute = new Assigning(lostEligibility); var noRouteSession = new ClientSession(noRoute); noRouteSession.Accept(lostEligibility);
        try {
            var intent = CombatAssignmentIntent.TryArm(lostEligibility, desired)!;
            intent.Advance(noRouteSession, (_, _) => false); await Pump(noRouteSession);
            check(!intent.IsActive && !intent.Completed && noRoute.Commands.Count == 0,
                "An unoffered current block stops for review before submitting even one toggle");
        } finally { noRouteSession.Close(); }
        foreach (bool stop in new[] { false, true }) {
            var connection = new Assigning(initial, hold: true); var session = new ClientSession(connection); session.Accept(initial);
            var intent = CombatAssignmentIntent.TryArm(initial, desired)!;
            try {
                intent.Advance(session, (_, _) => false);
                await connection.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (stop) intent.Stop(); else intent.Pause();
                connection.Release.TrySetResult(); await Pump(session);
                check(connection.Commands.Count == 1 && connection.Observations == 1
                        && session.State!.Combat!.Attackers[0].BlockerIds.Contains("b000") && !intent.Completed,
                    "Pause or Take control reconciles an already sent block and never dispatches the next pair");
            } finally { session.Close(); }
        }
        var echo = new Assigning(initial, echo: true); var echoSession = new ClientSession(echo); echoSession.Accept(initial);
        try {
            var intent = CombatAssignmentIntent.TryArm(initial, desired)!;
            intent.Advance(echoSession, (_, _) => false); await Pump(echoSession);
            check(echo.Commands.Count == 1 && !intent.IsActive && !intent.Completed,
                "A refreshed declaration that did not apply its pending block stops without toggling it twice");
        } finally { echoSession.Close(); }
    }

    private static IEnumerable<string> Blockers(int count) => Enumerable.Range(0, count).Select(index => "b" + index.ToString("D3"));
    private static IEnumerable<string> Attackers(int count) => Enumerable.Range(0, count).Select(index => "a" + index.ToString("D3"));
    private static GameSnapshot State(int count, ImmutableArray<BlockAssignment> assignments = default, long revision = 1)
    {
        string decision = "block-" + revision;
        Card Creature(string id) => new() { VisualId = id, CombatId = id, Key = decision + ":" + id, Name = "Token", Type = "Creature", Power = 1, Toughness = 1 };
        var own = Blockers(count).Select(Creature).ToImmutableArray();
        var incoming = Attackers(count).Select(Creature).ToImmutableArray();
        var pairs = assignments.IsDefault ? [] : assignments;
        return new("combat-batch", "playing", null, null, 0, 8, "Declare blockers", "COMBAT_DECLARE_BLOCKERS", 1,
            [new(0, "You", 40, false, false, ImmutableDictionary<string, int>.Empty, [new("Battlefield", own.Length, own, null)]),
             new(1, "Attacking player", 40, false, false, ImmutableDictionary<string, int>.Empty, [new("Battlefield", incoming.Length, incoming, null)]),
             new(2, "Other defender", 40, false, false, ImmutableDictionary<string, int>.Empty, [new("Battlefield", 1, [Creature("foreign")], null)])],
            [], new(incoming.Select(card => new Attack(card.CombatId, 0,
                pairs.Where(pair => pair.AttackerId == card.CombatId).Select(pair => pair.BlockerId).ToImmutableArray(),
                Blockers(count).ToImmutableArray())).ToImmutableArray(), [], null) { DeclarationId = "declaration-8" },
            new(decision, new(), new BlockPayload(new("Confirm blocks", true)))) {
                Revision = revision, BoardRevision = revision, DecisionOwnerId = 0, ActionClass = "blocks"
            };
    }
    private static async Task Pump(ClientSession session)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (session.Busy && DateTime.UtcNow < deadline) { session.Pump(); await Task.Delay(1); }
        session.Pump(); if (session.Busy) throw new TimeoutException("Combat assignment did not release its serial lane.");
    }
    private sealed class Assigning(GameSnapshot initial, bool hold = false, bool echo = false) : IMatchConnection
    {
        internal GameSnapshot State { get; private set; } = initial;
        internal List<GameCommand> Commands { get; } = [];
        internal int Observations;
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task ExecuteAsync(GameCommand command)
        {
            if (!GameCommandPermissions.Allows(State, command) || command is not ToggleBlock toggle)
                throw new InvalidOperationException("A group assignment attempted an unavailable command.");
            var attacker = CardViews.FindZoneActionSource(State, toggle.Attacker)!.Value.Card.CombatId;
            var blocker = CardViews.FindZoneActionSource(State, toggle.Blocker)!.Value.Card.CombatId;
            Commands.Add(command); Started.TrySetResult(); if (hold) await Release.Task;
            var pairs = State.Combat!.Attackers.SelectMany(attack => attack.BlockerIds.Select(id => new BlockAssignment(attack.CardId, id))).ToList();
            var pair = new BlockAssignment(attacker, blocker);
            if (!echo) { if (!pairs.Remove(pair)) pairs.Add(pair); }
            State = CombatAssignmentRegression.State(State.Combat.Attackers.Length, pairs.ToImmutableArray(), State.Revision + 1);
        }
        public Task<GameSnapshot?> ObserveAsync() { Observations++; return Task.FromResult<GameSnapshot?>(State); }
        public Task ConcedeAsync(string gameId) => Task.CompletedTask;
    }
}
