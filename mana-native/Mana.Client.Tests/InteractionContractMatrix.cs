using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mana.Client;
using Mana.Conformance;
using Mana.Contracts;
using Mana.Magic;

// Interaction-contract coverage: transport, authority rejection and continuation.
// The controlled provider is a finite scripted machine, not a Magic rules engine.
internal static class InteractionContractMatrix
{
    private sealed record Case(string Name, ReplyAction Action, DecisionIntent Intent, DecisionPurpose Purpose);
    private static readonly Case[] Cases = [
        new("keep-opening-hand", ReplyAction.Confirm, DecisionIntent.General, DecisionPurpose.OpeningHand),
        new("cancel-targeting", ReplyAction.Cancel, DecisionIntent.Selection, DecisionPurpose.TargetSelection),
        new("select-current-card", ReplyAction.SelectCard, DecisionIntent.Selection, DecisionPurpose.TargetSelection),
        new("select-offered-player", ReplyAction.SelectPlayer, DecisionIntent.Selection, DecisionPurpose.TargetSelection),
        new("assign-attacker", ReplyAction.AssignAttack, DecisionIntent.Attack, DecisionPurpose.Unspecified),
        new("assign-blocker", ReplyAction.AssignBlock, DecisionIntent.Block, DecisionPurpose.Unspecified),
        new("automatic-priority", ReplyAction.AutoPass, DecisionIntent.Priority, DecisionPurpose.Unspecified),
        new("skip-optional-choice", ReplyAction.Skip, DecisionIntent.Selection, DecisionPurpose.Unspecified),
        new("choose-ability", ReplyAction.Choose, DecisionIntent.General, DecisionPurpose.AbilitySelection),
        new("ordered-selection", ReplyAction.Choose, DecisionIntent.General, DecisionPurpose.Unspecified),
        new("bounded-value", ReplyAction.Value, DecisionIntent.General, DecisionPurpose.Unspecified),
        new("allocate-damage", ReplyAction.Allocate, DecisionIntent.General, DecisionPurpose.TargetSelection),
        new("ignore-one-ability", ReplyAction.SetAbilityAutoPass, DecisionIntent.Priority, DecisionPurpose.Unspecified),
        new("place-eligible-hand-card", ReplyAction.UseHandPlacement, DecisionIntent.Priority, DecisionPurpose.Unspecified),
        new("yield-public-window", ReplyAction.PassUntilPublicAction, DecisionIntent.Priority, DecisionPurpose.Unspecified),
        new("source-action-during-payment", ReplyAction.UseSourceAction, DecisionIntent.Mana, DecisionPurpose.Unspecified)
    ];

    internal static async Task RunAsync(Action<bool, string> check)
    {
        var timer = Stopwatch.StartNew();
        check(Covers(Cases.Select(row => row.Action)) && Covers(Cases.Select(row => row.Intent))
                && Covers(Cases.Select(row => row.Purpose)),
            "The interaction-contract matrix names every ReplyAction, DecisionIntent and DecisionPurpose; new enum values require new cases");
        foreach (var row in Cases) await RunCase(row, check);
        SeededPermissions(check);
        await SemanticAliases(check);
        Console.WriteLine($"Interaction-contract matrix: {Cases.Length} named cases, 48 seeded transitions; {timer.Elapsed.TotalMilliseconds:F1} ms (no JVM/GPU; not exhaustive Magic rules coverage).");
    }

    private static bool Covers<T>(IEnumerable<T> covered) where T : struct, Enum =>
        covered.Distinct().Order().SequenceEqual(Enum.GetValues<T>().Distinct().Order());

    private static async Task RunCase(Case row, Action<bool, string> check)
    {
        var fixture = Fixture.Create(row, "matrix:" + row.Name);
        var provider = fixture.Provider;
        var client = new ClientSession(provider);
        client.Accept(fixture.Before);
        Exception? failure = null;
        client.Failed += error => failure = error;

        // Deliberately well-formed but unoffered input must reach the authority,
        // which rejects it explicitly without manufacturing a successful result.
        bool submitted = client.Submit(fixture.Unoffered);
        await Drain(client);
        check(submitted && failure is EngineCommandRejectedException && provider.Accepted == 0 && provider.Rejected == 1
                && !client.NeedsRecovery && !client.UncertainCommand && Same(client.State, fixture.Before),
            $"{row.Name}: unoffered input is explicitly rejected, preserving the exact answerable decision");
        if (row.Action == ReplyAction.UseSourceAction) {
            int refused = 0;
            foreach (var query in new[] {
                (Game: "foreign-game", Decision: fixture.Before.Decision!.Id, Key: fixture.Focus.Key),
                (Game: fixture.Before.Id, Decision: "foreign-decision", Key: fixture.Focus.Key),
                (Game: fixture.Before.Id, Decision: fixture.Before.Decision!.Id, Key: "private-source-key") }) {
                try { await provider.SourceActionsAsync(query.Game, query.Decision, query.Key); }
                catch (EngineCommandRejectedException) { refused++; }
            }
            check(refused == 3 && provider.Calls == 1 && Same(provider.Current, fixture.Before) && Same(client.State, fixture.Before),
                "The scripted source authority rejects foreign game/prompt and undisclosed source queries without fabricating a catalog or changing state");
        }

        var reply = await fixture.BuildReplyAsync();
        check(reply != null && Same(reply, fixture.Expected),
            $"{row.Name}: the current interaction constructs the exact offered command");
        bool accepted = client.Submit(reply!);
        bool duplicate = client.Submit(reply!);
        await Drain(client);
        check(accepted && !duplicate && provider.Accepted == 1 && provider.Rejected == 1 && Same(client.State, fixture.After),
            $"{row.Name}: one serialized input reaches its authored next state/required continuation");
        if (row.Action == ReplyAction.UseSourceAction) {
            bool refused = false;
            try { await provider.SourceActionsAsync(fixture.Before.Id, fixture.Before.Decision!.Id, fixture.Focus.Key); }
            catch (EngineCommandRejectedException) { refused = true; }
            check(refused && Same(provider.Current, fixture.After) && Same(client.State, fixture.After),
                "A consumed scripted decision cannot return a fresh source catalog for its former handles");
        }

        // Preferences can legitimately retain a prompt, so explicitly publish a
        // later decision instead of assuming every action consumes its prompt.
        var later = fixture.After with { Revision = fixture.After.Revision + 1,
            Decision = fixture.Before.Decision! with { Id = fixture.Before.Decision!.Id + ":later" } };
        client.Accept(later);
        int calls = provider.Calls;
        check(!client.Submit(reply!) && !client.Submit(reply! with { GameId = "retired-game", DecisionId = later.Decision!.Id })
                && provider.Calls == calls,
            $"{row.Name}: obsolete prompt/game input is blocked before provider dispatch");
        client.Close();
        check(!client.Submit(reply! with { DecisionId = later.Decision!.Id }) && provider.Calls == calls,
            $"{row.Name}: closing the session revokes a captured current command");
    }

    private static void SeededPermissions(Action<bool, string> check)
    {
        var random = new Random(0x51A7E);
        var source = Cases.Single(row => row.Action == ReplyAction.SelectCard);
        for (int i = 0; i < 48; i++) {
            var f = Fixture.Create(source, "seed:" + random.Next().ToString("x"));
            var original = f.Focus;
            bool visible = random.Next(2) == 0, permitted = random.Next(2) == 0;
            var current = original with { Key = original.Key + ":new", Selectable = permitted };
            var state = f.Before with { Revision = i + 20, Decision = f.Before.Decision! with { Id = "seed-prompt:" + i },
                Players = f.Before.Players.Select(player => player.Id == 0 ? player with {
                    Zones = player.Zones.Select(zone => zone.Name == "Battlefield" ? zone with {
                        Cards = visible ? [current, f.Other] : [f.Other], Count = visible ? 2 : 1 } : zone).ToArray()
                } : player).ToArray() };
            var result = new InteractionController().CardClick(state, original);
            bool correct = visible && permitted
                ? result.Reply is { Action: ReplyAction.SelectCard } reply && reply.Card == current.Key && reply.DecisionId == state.Decision!.Id
                : result.Reply == null;
            if (!correct) throw new Exception($"Seeded interaction-contract transition {i}: visibility={visible}, permission={permitted}");
            var hidden = new Card { Name = "Private opponent face", VisualId = "private:" + i, Key = "private-key:" + i, Selectable = true };
            if (new InteractionController().CardClick(state, hidden).Reply != null
                || new InteractionController().PlayerClick(state, 3).Reply != null)
                throw new Exception($"Seeded interaction-contract transition {i} used unprojected private/candidate input");
        }
        check(true, "Seeded visibility/permission changes use fresh projected handles or inspection only; hidden faces and unoffered players never create replies");
    }

    private static async Task SemanticAliases(Action<bool, string> check)
    {
        // This is the complete oracle for this finite contract fixture: a single
        // pending command, a counter and four fixed private values. No Magic
        // rules-state completeness or real-provider equivalence is implied.
        var scenario = new Scenario("finite-interaction-aliases", "interaction-contract.v1", new(),
            [new(0, "use-source", new() { ["source"] = "focus", ["action"] = "increment" })], [], Players: 4);
        await using var a = new AliasMachine("provider-a");
        await using var b = new AliasMachine("provider-b");
        var result = await ConformanceRunner.RunAsync(scenario, a, b);
        check(result.Passed && result.Checkpoints.Length == 2 && a.SentKey != b.SentKey,
            "Finite scripted contract providers reach the same canonical continuation and all four seat views using their own semantic-alias translation");
        int previous = b.Commands;
        bool rejected = false;
        try { await b.ApplyAsync(new(0, "use-source", new() { ["source"] = a.SentKey, ["action"] = "increment" }), default); }
        catch (ArgumentException) { rejected = true; }
        check(rejected && b.Commands == previous,
            "A replay's raw foreign provider handle is rejected as an alias before any command crosses the second provider boundary");

        var row = Cases.Single(item => item.Action == ReplyAction.UseSourceAction);
        var first = Fixture.Create(row, "foreign-provider");
        var second = Fixture.Create(row, "current-provider");
        var client = new ClientSession(second.Provider); client.Accept(second.Before);
        client.Submit(first.Expected with { GameId = second.Before.Id, DecisionId = second.Before.Decision!.Id });
        await Drain(client);
        check(second.Provider.Rejected == 1 && second.Provider.Accepted == 0 && Same(client.State, second.Before),
            "Even rebinding game/prompt strings cannot authorize another provider's source/action handles");
        client.Close();
    }

    private static async Task Drain(ClientSession client)
    {
        long deadline = Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency;
        while (client.Busy && Stopwatch.GetTimestamp() < deadline) { client.Pump(); await Task.Yield(); }
        client.Pump();
        if (client.Busy) throw new Exception("Interaction-contract provider did not complete within its bounded wait");
    }
    private static bool Same<T>(T? left, T? right) => JsonNode.DeepEquals(JsonSerializer.SerializeToNode(left), JsonSerializer.SerializeToNode(right));

    private sealed record Fixture(Case Row, GameSnapshot Before, GameSnapshot After, DecisionReply Expected,
        DecisionReply Unoffered, Card Focus, Card Other, Card Hand, Card Enemy, ScriptedProvider Provider)
    {
        internal static Fixture Create(Case row, string prefix)
        {
            var focus = new Card { Name = "Identical test creature", VisualId = prefix + ":focus", CombatId = prefix + ":focus",
                Key = prefix + ":focus-key", Selectable = true, Power = 2, Toughness = 2 };
            var other = focus with { VisualId = prefix + ":other", CombatId = prefix + ":other", Key = prefix + ":other-key" };
            var hand = new Card { Name = "Forest", Type = "Basic Land", VisualId = prefix + ":hand", Key = prefix + ":hand-key" };
            var enemy = focus with { VisualId = prefix + ":enemy", CombatId = prefix + ":enemy", Key = prefix + ":enemy-key" };
            var ability = new PriorityAbility(prefix + ":ability", focus.Key, focus.Name, "An offered exact ability", false) {
                EligibleHandKeys = [hand.Key], HandPlacementLimit = 1 };
            var decision = new Decision { Id = prefix + ":decision", Kind = "input", Intent = row.Intent, Purpose = row.Purpose,
                OkEnabled = row.Action is ReplyAction.Confirm or ReplyAction.AutoPass, CancelEnabled = row.Action == ReplyAction.Cancel,
                CanAutoPass = row.Action == ReplyAction.AutoPass, MaySkip = row.Action == ReplyAction.Skip,
                QuietYieldToken = prefix + ":yield", PriorityAbilities = [ability],
                PlayerChoices = row.Action == ReplyAction.SelectPlayer ? [1] : [],
                Choices = [new(7, "First ability", null), new(9, "Second ability", null)],
                Min = row.Action == ReplyAction.Value ? 2 : 1, Max = row.Action == ReplyAction.Value ? 6 : 2,
                Numeric = row.Action == ReplyAction.Value, Amount = 3, Limits = [2, 3], Ordered = row.Name == "ordered-selection" };
            if (row.Action == ReplyAction.Choose) decision = decision with { Kind = "choice", Max = 1 };
            if (row.Name == "ordered-selection") decision = decision with { Min = 2, Max = 2,
                Choices = [new(7, focus.Name, focus) { CardId = focus.VisualId }, new(9, other.Name, other) { CardId = other.VisualId }] };
            if (row.Action == ReplyAction.Allocate) decision = decision with { Kind = "allocate", AllocationKind = "damage",
                Choices = [new(0, focus.Name, focus) { CardId = focus.VisualId, Maximum = 2 },
                    new(1, enemy.Name, enemy) { CardId = enemy.VisualId, Maximum = 3 }] };
            var players = Enumerable.Range(0, 4).Select(id => new Player(id, id == 0 ? "You" : "Opponent " + id, 40,
                id == 0, false, [], id == 0 ? [new("Battlefield", 2, [focus, other], null), new("Hand", 1, [hand], null)]
                    : [new("Battlefield", id == 1 ? 1 : 0, id == 1 ? [enemy] : [], null), new("Hand", 2, [], null)])).ToArray();
            Combat? combat = row.Action == ReplyAction.AssignAttack ? new([], [new(focus.CombatId, [new("player", "opponent-1", 1, "Opponent 1")])], null)
                : row.Action == ReplyAction.AssignBlock ? new([new(enemy.CombatId, 0, [], [focus.CombatId])], [], null) : null;
            var before = new GameSnapshot(prefix + ":game", "playing", null, null, 0, 3, "Main phase", "MAIN1", 1,
                players, [], combat, decision) { Revision = 8, BoardRevision = 4, PriorityPlayerId = 0, DecisionOwnerId = 0 };
            var expected = new DecisionReply(before.Id, decision.Id, row.Action);
            expected = row.Action switch {
                ReplyAction.SelectCard => expected with { Card = focus.Key },
                ReplyAction.SelectPlayer => expected with { Player = 1 },
                ReplyAction.AssignAttack => expected with { Attacker = focus.Key, Player = 1 },
                ReplyAction.AssignBlock => expected with { Attacker = enemy.Key, Blocker = focus.Key },
                ReplyAction.Choose => expected with { Choices = row.Name == "ordered-selection" ? [9, 7] : [9] },
                ReplyAction.Value => expected with { Value = "4" },
                ReplyAction.Allocate => expected with { Amounts = [2, 1] },
                ReplyAction.SetAbilityAutoPass => expected with { AbilityId = ability.Id, Ignore = true },
                ReplyAction.UseHandPlacement => expected with { Card = hand.Key, AbilityId = ability.Id },
                ReplyAction.PassUntilPublicAction => expected with { YieldToken = decision.QuietYieldToken },
                ReplyAction.UseSourceAction => expected with { Card = focus.Key, AbilityId = prefix + ":source-action" },
                _ => expected
            };
            var invalid = row.Action switch {
                ReplyAction.SelectCard => expected with { Card = prefix + ":private-opponent-key" },
                ReplyAction.SelectPlayer or ReplyAction.AssignAttack => expected with { Player = 3 },
                ReplyAction.AssignBlock => expected with { Blocker = other.Key },
                ReplyAction.Choose => expected with { Choices = [99] },
                ReplyAction.Value => expected with { Value = "7" },
                ReplyAction.Allocate => expected with { Amounts = [3, 0] },
                ReplyAction.SetAbilityAutoPass or ReplyAction.UseSourceAction => expected with { AbilityId = prefix + ":unoffered" },
                ReplyAction.UseHandPlacement => expected with { Card = enemy.Key },
                ReplyAction.PassUntilPublicAction => expected with { YieldToken = prefix + ":expired-yield" },
                ReplyAction.Cancel => expected with { Action = ReplyAction.Confirm },
                _ => expected with { Action = ReplyAction.Cancel }
            };
            var after = before with { Revision = 9, BoardRevision = 5, Decision = null, ActionClass = row.Name };
            after = row.Action switch {
                ReplyAction.SelectCard or ReplyAction.UseSourceAction => ChangeCard(after, focus.VisualId, card => card with { Counters = new() { ["fixture"] = 1 } }),
                ReplyAction.SelectPlayer => after with { Players = players.Select(p => p.Id == 1 ? p with { Life = 37 } : p).ToArray() },
                ReplyAction.AssignAttack => after with { Combat = new([new(focus.CombatId, 1, [], [])], [], null) },
                ReplyAction.AssignBlock => after with { Combat = new([new(enemy.CombatId, 0, [focus.CombatId], []) { Blocked = true }], [], null) },
                ReplyAction.AutoPass or ReplyAction.PassUntilPublicAction => after with { PriorityPlayerId = 1, DecisionOwnerId = null },
                ReplyAction.Choose => after with { Decision = new() { Id = prefix + ":target-continuation", Intent = DecisionIntent.Selection,
                    Purpose = DecisionPurpose.TargetSelection, PlayerChoices = [1] }, Notices = [row.Name == "ordered-selection" ? "order:9,7" : "ability:9"] },
                ReplyAction.Value => after with { Stack = [new("Fixture spell", "", false, focus) { Effect = new() { Kind = "damage", Amount = 4 } }] },
                ReplyAction.Allocate => ChangeCard(ChangeCard(after, focus.VisualId, card => card with { Damage = 2 }), enemy.VisualId, card => card with { Damage = 1 }),
                ReplyAction.SetAbilityAutoPass => after with { Decision = decision, IgnoredPriorityAbilities = [ability with { IgnoredUntilNextTurn = true }] },
                ReplyAction.UseHandPlacement => after with { Decision = new() { Id = prefix + ":placement-continuation", Intent = DecisionIntent.Selection,
                    Purpose = DecisionPurpose.TargetSelection, Min = 1, Max = 1, SourceCard = focus, SourceCardId = focus.VisualId,
                    Choices = [new(4, hand.Name, hand) { CardId = hand.VisualId }] } },
                _ => after
            };
            return new(row, before, after, expected, invalid, focus, other, hand, enemy, new(before, after, expected));
        }
        internal async Task<DecisionReply?> BuildReplyAsync()
        {
            switch (Row.Action) {
                case ReplyAction.SelectCard: return new InteractionController().CardClick(Before, Focus).Reply;
                case ReplyAction.SelectPlayer: return new InteractionController().PlayerClick(Before, 1).Reply;
                case ReplyAction.AssignAttack: return InteractionController.Assign(Before, Focus.CombatId, 1);
                case ReplyAction.AssignBlock: return InteractionController.Assign(Before, Focus.CombatId, targetCard: Enemy.CombatId);
                case ReplyAction.AutoPass: return new AutoPriority().Update(Before, true, false, new HashSet<string>(), false, false);
                case ReplyAction.UseHandPlacement: return HandPlacements.Start(Before, Hand, Expected.AbilityId!);
                case ReplyAction.Allocate: return AllocationGuide.Reply(Before, [2, 1]);
                case ReplyAction.Choose when Row.Name == "ordered-selection":
                    var gestures = new InteractionController();
                    var last = gestures.CardClick(Before, Other);
                    var first = gestures.CardClick(Before, Focus);
                    int[] order = [last.ChoiceIndex ?? -1, first.ChoiceIndex ?? -1];
                    return TableChoices.CanConfirm(Before, order) ? new(Before.Id, Before.Decision!.Id, ReplyAction.Choose, Choices: order) : null;
                case ReplyAction.UseSourceAction:
                    var completions = new ConcurrentQueue<Action>();
                    var session = new SourceActionSession(Provider, completions.Enqueue);
                    var context = new SourceActionContext(Before.Id, Before.Decision!.Id, Before.Revision, Before.ViewerId, Focus.Key, Focus.VisualId);
                    session.Observe(context);
                    long deadline = Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency;
                    while (session.View.Status == SourceActionStatus.Loading && Stopwatch.GetTimestamp() < deadline) {
                        while (completions.TryDequeue(out var complete)) complete();
                        await Task.Yield();
                    }
                    var result = session.TryCreateReply(context, Expected.AbilityId!); session.Close(); return result;
                default: return Expected; // Generic button/value rows exercise session/authority, not renderer input.
            }
        }
    }

    private static GameSnapshot ChangeCard(GameSnapshot state, string id, Func<Card, Card> update) => state with {
        Players = state.Players.Select(player => player with { Zones = player.Zones.Select(zone => zone with {
            Cards = zone.Cards.Select(card => card.VisualId == id ? update(card) : card).ToArray() }).ToArray() }).ToArray()
    };

    private sealed class ScriptedProvider(GameSnapshot before, GameSnapshot after, DecisionReply offered) : IMatchConnection, ISourceActions
    {
        private GameSnapshot current = before;
        private readonly string sourceCardId = before.Viewer!.Zone("Battlefield").Cards[0].VisualId;
        private readonly string sourceCardKey = before.Viewer!.Zone("Battlefield").Cards[0].Key;
        private int calls, accepted, rejected;
        internal GameSnapshot Current => current;
        internal int Calls => Volatile.Read(ref calls);
        internal int Accepted => Volatile.Read(ref accepted);
        internal int Rejected => Volatile.Read(ref rejected);
        public Task<GameSnapshot?> ObserveAsync() => Task.FromResult<GameSnapshot?>(current);
        public Task ReplyAsync(DecisionReply reply)
        {
            Interlocked.Increment(ref calls);
            if (Accepted > 0 || !Same(reply, offered)) {
                Interlocked.Increment(ref rejected);
                throw new EngineCommandRejectedException("The scripted authority did not offer this exact input.");
            }
            current = after; Interlocked.Increment(ref accepted); return Task.CompletedTask;
        }
        public Task<SourceActionSet> SourceActionsAsync(string game, string decision, string key)
        {
            if (Accepted > 0 || current.Id != game || current.Decision?.Id != decision || key != sourceCardKey)
                throw new EngineCommandRejectedException("The scripted source is not disclosed/actionable in this current decision.");
            return Task.FromResult(new SourceActionSet(current.Id, current.Decision!.Id, sourceCardKey, sourceCardId,
                [new(offered.AbilityId!, "activate", "Increment the fixture counter")]));
        }
        public Task ConcedeAsync(string gameId) => throw new InvalidOperationException("Concede is not a ReplyAction and has separate existing coverage.");
    }

    private sealed class AliasMachine(string name) : IConformanceEngine
    {
        private Fixture fixture = null!;
        private ClientSession client = null!;
        public string Name => name;
        internal string? SentKey { get; private set; }
        internal int Commands => fixture.Provider.Calls;
        public Task<Checkpoint> StartAsync(Scenario scenario, CancellationToken token)
        {
            fixture = Fixture.Create(Cases.Single(row => row.Action == ReplyAction.UseSourceAction), name);
            client = new(fixture.Provider); client.Accept(fixture.Before); return Task.FromResult(Checkpoint());
        }
        public async Task<Checkpoint> ApplyAsync(SemanticCommand command, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (command.Seat != 0 || command.Kind != "use-source" || command.Arguments["source"]?.GetValue<string>() != "focus"
                || command.Arguments["action"]?.GetValue<string>() != "increment") throw new ArgumentException("Unknown semantic fixture alias.");
            var reply = await fixture.BuildReplyAsync() ?? throw new Exception("Current fixture action unavailable");
            SentKey = reply.Card;
            if (!client.Submit(reply)) throw new Exception("Current fixture command rejected before dispatch");
            await Drain(client);
            if (!Same(client.State, fixture.After)) throw new Exception("The finite fixture's accepted transition was not observed by its client.");
            return Checkpoint();
        }
        private Checkpoint Checkpoint()
        {
            var authority = fixture.Provider.Current;
            var observed = client.State ?? throw new Exception("The finite fixture has no observed client state.");
            bool pending = authority.Decision != null;
            int authoritativeCounter = Counter(authority);
            int observedCounter = Counter(observed);
            var privateValues = new JsonArray(Enumerable.Range(0, 4).Select(i => (JsonNode?)JsonValue.Create("fixture-private-" + i)).ToArray());
            var rules = new JsonObject { ["fixture"] = "finite-interaction-contract.v1", ["pending"] = pending,
                ["counter"] = authoritativeCounter, ["privateValues"] = privateValues };
            var seats = observed.Players.ToDictionary(player => player.Id, player => new JsonObject {
                ["counter"] = observedCounter, ["ownPrivateValue"] = "fixture-private-" + player.Id,
                ["life"] = player.Life, ["canIncrement"] = observed.Decision != null && player.Id == observed.ViewerId });
            return new(pending ? "offered" : "completed", rules, seats, []);
            int Counter(GameSnapshot state) => CardViews.Visible(state).Single(card => card.VisualId == fixture.Focus.VisualId)
                .Counters.GetValueOrDefault("fixture");
        }
        public ValueTask DisposeAsync() { client?.Close(); return ValueTask.CompletedTask; }
    }
}
