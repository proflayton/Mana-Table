using System.Collections.Immutable;
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
    private sealed record Case(string Name, Type CommandType, DecisionTask Task);
    private static readonly Case[] Cases = [
        new("pass-priority", typeof(PassPriority), DecisionTask.Priority),
        new("automatic-priority", typeof(AutoPassPriority), DecisionTask.Priority),
        new("keep-opening-hand", typeof(KeepOpeningHand), DecisionTask.OpeningHand),
        new("mulligan-opening-hand", typeof(MulliganOpeningHand), DecisionTask.OpeningHand),
        new("pay-mana", typeof(PayMana), DecisionTask.Payment),
        new("cancel-payment", typeof(CancelPayment), DecisionTask.Payment),
        new("complete-attackers", typeof(CompleteAttackers), DecisionTask.Attack),
        new("complete-blockers", typeof(CompleteBlockers), DecisionTask.Block),
        new("complete-selection", typeof(CompleteSelection), DecisionTask.EntitySelection),
        new("cancel-targeting", typeof(CancelSelection), DecisionTask.TargetSelection),
        new("accept-confirmation", typeof(AcceptConfirmation), DecisionTask.Confirmation),
        new("decline-confirmation", typeof(DeclineConfirmation), DecisionTask.Confirmation),
        new("acknowledge-decision", typeof(AcknowledgeDecision), DecisionTask.Confirmation),
        new("acknowledge-reveal", typeof(AcknowledgeReveal), DecisionTask.Reveal),
        new("undo-last-action", typeof(UndoLastAction), DecisionTask.Priority),
        new("pass-turn", typeof(PassTurn), DecisionTask.Priority),
        new("select-current-card", typeof(SelectCard), DecisionTask.TargetSelection),
        new("play-hand-card", typeof(PlayCard), DecisionTask.Priority),
        new("activate-current-card", typeof(ActivateCard), DecisionTask.Priority),
        new("use-mana-source", typeof(UseManaSource), DecisionTask.Payment),
        new("select-offered-player", typeof(SelectPlayer), DecisionTask.TargetSelection),
        new("assign-attacker", typeof(ToggleAttackPlayer), DecisionTask.Attack),
        new("attack-permanent", typeof(ToggleAttackPermanent), DecisionTask.Attack),
        new("assign-blocker", typeof(ToggleBlock), DecisionTask.Block),
        new("choose-ability", typeof(ChooseOptions), DecisionTask.AbilityChoice),
        new("choose-option", typeof(ChooseOptions), DecisionTask.Options),
        new("ordered-selection", typeof(OrderOptions), DecisionTask.Ordering),
        new("bounded-value", typeof(ChooseNumber), DecisionTask.Number),
        new("enter-text", typeof(EnterText), DecisionTask.Text),
        new("allocate-damage", typeof(AllocateAmounts), DecisionTask.Allocation),
        new("skip-optional-allocation", typeof(SkipAllocation), DecisionTask.Allocation),
        new("ignore-one-ability", typeof(IgnorePriorityAbility), DecisionTask.Priority),
        new("restore-one-ability", typeof(RestorePriorityAbility), DecisionTask.Priority),
        new("place-eligible-hand-card", typeof(PlaceFromHand), DecisionTask.Priority),
        new("yield-public-window", typeof(PassForNow), DecisionTask.Priority),
        new("source-action-during-payment", typeof(UseOfferedAction), DecisionTask.Payment)
    ];

    internal static async Task RunAsync(Action<bool, string> check)
    {
        var timer = Stopwatch.StartNew();
        var commandTypes = typeof(GameCommand).Assembly.GetTypes().Where(type => !type.IsAbstract && typeof(GameCommand).IsAssignableFrom(type)).OrderBy(type => type.FullName);
        check(Cases.Select(row => row.CommandType).Distinct().OrderBy(type => type.FullName).SequenceEqual(commandTypes)
                && Cases.Select(row => row.Task).Distinct().Order().SequenceEqual(Enum.GetValues<DecisionTask>().Where(task => task != DecisionTask.Unspecified).Order()),
            "The finite interaction-contract matrix names every closed command variant and supported decision task; it is not exhaustive rules coverage");
        foreach (var row in Cases) await RunCase(row, check);
        SeededPermissions(check);
        CurrentZoneAuthority(check);
        await SourceAuthorityLifetime(check);
        await SemanticAliases(check);
        Console.WriteLine($"Interaction-contract matrix: {Cases.Length} named cases, 48 seeded transitions; {timer.Elapsed.TotalMilliseconds:F1} ms (no JVM/GPU; not exhaustive Magic rules coverage).");
    }

    private static void CurrentZoneAuthority(Action<bool, string> check)
    {
        var fixture = Fixture.Create(Cases.Single(item => item.CommandType == typeof(UseOfferedAction)), "zone-authority");
        var reported = fixture.Focus;
        var current = reported with { Key = "refreshed-source-key", CombatId = "alternate-occurrence", Power = 7 };
        var state = fixture.Before with {
            Players = [fixture.Before.Viewer! with { Zones = [new("Library", 20, [], current)] }],
            Decision = fixture.Before.Decision! with { SourceCard = reported, SourceCardId = current.CombatId },
            Stack = [new(reported.Name, "A source face is not a current location", true, reported)]
        };
        var source = (UseOfferedAction)fixture.Expected;
        check(CardViews.Source(state, state.Decision!) == current && CardViews.InZones(state).Single() is { PlayerId: 0, Zone: "Library" }
            && GameCommandPermissions.Allows(state, source with { Card = current.Key })
            && !GameCommandPermissions.Allows(state, source),
            "A disclosed library top supplies current state and source authority through its exact new key; a retained stack source cannot revive the old key");
        check(CardViews.Current(state, reported) == current
            && CardViews.Current(state, reported with { VisualId = "", CombatId = "", Key = current.Key }) != current
            && CardViews.Current(state, reported, reported.Key) == reported,
            "Occurrence links refresh reported faces independently of action keys; matching a key or name cannot refresh an anonymous or explicit unrelated occurrence");
        var masked = current with { FaceDown = true, Name = "Face-down card", Text = "", ArtName = "", OtherFace = null };
        var hidden = state with { Players = [state.Viewer! with { Zones = [new("Library", 20, [], masked)] }] };
        check(CardViews.Source(hidden, hidden.Decision!) == masked
            && !GameCommandPermissions.Allows(hidden, source with { Card = masked.Key })
            && CardViews.Source(state, state.Decision!) == current,
            "A masked current envelope replaces historical identity and revokes source authority without rewriting the earlier snapshot");
    }

    private static async Task SourceAuthorityLifetime(Action<bool, string> check)
    {
        var row = Cases.Single(item => item.CommandType == typeof(UseOfferedAction));
        var baseline = Fixture.Create(row, "source-authority");
        var context = new SourceActionContext(baseline.Before.Id, baseline.Before.Decision!.Id, baseline.Before.Revision,
            baseline.Before.ViewerId, baseline.Focus.Key, CardViews.Identity(baseline.Focus));
        var withoutPort = new ClientSession(baseline.Provider); withoutPort.Accept(baseline.Before);
        withoutPort.SourceActions.Observe(context);
        check(withoutPort.SourceActions.View.Status == SourceActionStatus.Unsupported && !withoutPort.Submit(baseline.Expected)
                && baseline.Provider.Calls == 0,
            "A match connection without an explicit source-action port cannot authorize a source command");
        withoutPort.Close();

        var changedOccurrence = ChangeCard(baseline.Before, baseline.Focus.VisualId,
            card => card with { VisualId = card.VisualId + ":new-occurrence" });
        var historicalOnly = baseline.Before with {
            Players = baseline.Before.Players.Select(player => player with { Zones = player.Zones.Select(zone => zone with {
                Cards = zone.Cards.Where(card => card.Key != baseline.Focus.Key).ToImmutableArray()
            }).ToImmutableArray() }).ToImmutableArray(),
            Decision = baseline.Before.Decision! with { SourceCard = baseline.Focus, Choices = [new(5, baseline.Focus.Name, baseline.Focus)] }
        };
        foreach (var (name, current) in new (string, GameSnapshot)[] {
            ("snapshot revision", baseline.Before with { Revision = baseline.Before.Revision + 1 }),
            ("local seat", baseline.Before with { ViewerId = 1 }),
            ("physical occurrence", changedOccurrence),
            ("historical face only", historicalOnly),
            ("stack source face only", historicalOnly with { Stack = [new(baseline.Focus.Name, "Retained ability source", true, baseline.Focus)] }),
            ("revoked face", ChangeCard(baseline.Before, baseline.Focus.VisualId, card => card with { FaceDown = true })),
            ("decision", baseline.Before with { Decision = baseline.Before.Decision! with { Id = "later-decision" } }),
            ("game", baseline.Before with { Id = "later-game" }) }) {
            var client = new ClientSession(baseline.Provider, sourceActions: baseline.Provider); client.Accept(baseline.Before);
            await ReadSourceActions(client, context);
            check(client.SourceActions.View.Status == SourceActionStatus.Ready, "A current explicit source catalog is available before changing " + name);
            client.Accept(current);
            bool revoked = client.SourceActions.View.Status == SourceActionStatus.Idle;
            // Even an old query explicitly reintroduced by a caller cannot
            // establish authority over the session's newer physical state.
            await ReadSourceActions(client, context);
            bool accepted = client.Submit(Rebind(baseline.Expected, current.Id, current.Decision!.Id));
            check(revoked && !accepted && baseline.Provider.Calls == 0,
                "The session independently rejects a catalog bound to an obsolete " + name);
            client.Close();
        }
        var stable = new ClientSession(baseline.Provider, sourceActions: baseline.Provider); stable.Accept(baseline.Before);
        await ReadSourceActions(stable, context);
        long generation = stable.SourceActions.View.Generation;
        stable.Accept(baseline.Before with { });
        check(stable.SourceActions.View.Status == SourceActionStatus.Ready && stable.SourceActions.View.Generation == generation,
            "An unchanged observed state preserves its ready source catalog without a second lifecycle");
        stable.Reset();
        check(stable.SourceActions.View.Status == SourceActionStatus.Idle && !stable.Submit(baseline.Expected),
            "Reset clears the session-owned source authority with its match state");
        stable.Close(); stable.SourceActions.Observe(context);
        check(stable.SourceActions.View.Status == SourceActionStatus.Idle && !stable.SourceActions.Supported
                && !stable.Accept(baseline.Before) && !stable.Submit(baseline.Expected),
            "Closing the client seals source queries, accepted snapshots and command authority together");
    }

    private static async Task ReadSourceActions(ClientSession client, SourceActionContext context)
    {
        client.SourceActions.Observe(context);
        long deadline = Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency;
        while (client.SourceActions.View.Status == SourceActionStatus.Loading && Stopwatch.GetTimestamp() < deadline) {
            client.Pump(); await Task.Yield();
        }
        client.Pump();
        if (client.SourceActions.View.Status == SourceActionStatus.Loading)
            throw new Exception("The session-owned source query did not complete within its bounded wait");
    }

    private static async Task RunCase(Case row, Action<bool, string> check)
    {
        var fixture = Fixture.Create(row, "matrix:" + row.Name);
        var provider = fixture.Provider;
        var client = new ClientSession(provider, sourceActions: provider);
        client.Accept(fixture.Before);
        Exception? failure = null;
        client.Failed += error => failure = error;

        // Source commands must belong to the session's exact current catalog.
        // Remaining published constraints are checked before provider dispatch;
        // the provider still rejects any well-formed input it did not offer.
        var reply = row.CommandType == typeof(UseOfferedAction) ? await fixture.BuildExecuteAsync(client) : null;
        bool allowedLocally = fixture.Unoffered is not UseOfferedAction
            && GameCommandPermissions.Allows(fixture.Before, fixture.Unoffered);
        bool submitted = client.Submit(fixture.Unoffered);
        await Drain(client);
        check(submitted == allowedLocally && (allowedLocally ? failure is EngineCommandRejectedException && provider.Rejected == 1 : failure == null && provider.Calls == 0) && provider.Accepted == 0
                && !client.NeedsRecovery && !client.UncertainCommand && Same(client.State, fixture.Before),
            $"{row.Name}: unoffered input is rejected by published permissions or the authority, preserving the exact answerable decision");
        if (row.CommandType == typeof(UseOfferedAction)) {
            int refused = 0;
            foreach (var query in new[] {
                (Game: "foreign-game", Decision: fixture.Before.Decision!.Id, Key: fixture.Focus.Key),
                (Game: fixture.Before.Id, Decision: "foreign-decision", Key: fixture.Focus.Key),
                (Game: fixture.Before.Id, Decision: fixture.Before.Decision!.Id, Key: "private-source-key") }) {
                try { await provider.SourceActionsAsync(query.Game, query.Decision, query.Key); }
                catch (EngineCommandRejectedException) { refused++; }
            }
            check(refused == 3 && provider.Calls == 0 && Same(provider.Current, fixture.Before) && Same(client.State, fixture.Before),
                "The scripted source authority rejects foreign game/prompt and undisclosed source queries without fabricating a catalog or changing state");
        }

        reply ??= await fixture.BuildExecuteAsync(client);
        check(reply != null && Same(reply, fixture.Expected),
            $"{row.Name}: the current interaction constructs the exact offered command");
        bool accepted = client.Submit(reply!);
        bool duplicate = client.Submit(reply!);
        await Drain(client);
        check(accepted && !duplicate && provider.Accepted == 1 && provider.Rejected == (allowedLocally ? 1 : 0) && Same(client.State, fixture.After),
            $"{row.Name}: one serialized input reaches its authored next state/required continuation");
        if (row.CommandType == typeof(UseOfferedAction)) {
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
        check(!client.Submit(reply!) && !client.Submit(Rebind(reply!, "retired-game", later.Decision!.Id))
                && provider.Calls == calls,
            $"{row.Name}: obsolete prompt/game input is blocked before provider dispatch");
        client.Close();
        check(!client.Submit(Rebind(reply!, reply!.GameId, later.Decision!.Id)) && provider.Calls == calls,
            $"{row.Name}: closing the session revokes a captured current command");
    }

    private static void SeededPermissions(Action<bool, string> check)
    {
        var random = new Random(0x51A7E);
        var source = Cases.Single(row => row.CommandType == typeof(SelectCard));
        for (int i = 0; i < 48; i++) {
            var f = Fixture.Create(source, "seed:" + random.Next().ToString("x"));
            var original = f.Focus;
            bool visible = random.Next(2) == 0, permitted = random.Next(2) == 0;
            var current = original with { Key = original.Key + ":new", Selectable = permitted };
            var state = f.Before with { Revision = i + 20, Decision = f.Before.Decision! with { Id = "seed-prompt:" + i },
                Players = f.Before.Players.Select(player => player.Id == 0 ? player with {
                    Zones = player.Zones.Select(zone => zone.Name == "Battlefield" ? zone with {
                        Cards = visible ? [current, f.Other] : [f.Other], Count = visible ? 2 : 1 } : zone).ToImmutableArray()
                } : player).ToImmutableArray() };
            var result = new InteractionController().CardClick(state, original);
            bool correct = visible && permitted
                ? result.Reply is SelectCard reply && reply.Card == current.Key && reply.DecisionId == state.Decision!.Id
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

        var row = Cases.Single(item => item.CommandType == typeof(UseOfferedAction));
        var first = Fixture.Create(row, "foreign-provider");
        var second = Fixture.Create(row, "current-provider");
        var client = new ClientSession(second.Provider, sourceActions: second.Provider); client.Accept(second.Before);
        await second.BuildExecuteAsync(client);
        client.Submit(Rebind(first.Expected, second.Before.Id, second.Before.Decision!.Id));
        await Drain(client);
        check(second.Provider.Calls == 0 && second.Provider.Rejected == 0 && second.Provider.Accepted == 0 && Same(client.State, second.Before),
            "Rebinding game/prompt strings cannot authorize an undisclosed source from another provider");
        client.Close();
    }

    private static async Task Drain(ClientSession client)
    {
        long deadline = Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency;
        while (client.Busy && Stopwatch.GetTimestamp() < deadline) { client.Pump(); await Task.Yield(); }
        client.Pump();
        if (client.Busy) throw new Exception("Interaction-contract provider did not complete within its bounded wait");
    }
    private static bool Same<T>(T? left, T? right) => JsonNode.DeepEquals(
        left == null ? null : JsonSerializer.SerializeToNode(left, left.GetType()),
        right == null ? null : JsonSerializer.SerializeToNode(right, right.GetType()));
    private static GameCommand Rebind(GameCommand command, string game, string decision) => command is DecisionCommand scoped
        ? scoped with { GameId = game, DecisionId = decision } : command with { GameId = game };

    private sealed record Fixture(Case Row, GameSnapshot Before, GameSnapshot After, GameCommand Expected,
        GameCommand Unoffered, Card Focus, Card Other, Card Hand, Card Enemy, ScriptedProvider Provider)
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
            bool Is<T>() => row.CommandType == typeof(T);
            var decision = new Decision { Id = prefix + ":decision", Task = row.Task,
                OkEnabled = true, CancelEnabled = Is<CancelSelection>() || Is<CancelPayment>() || Is<MulliganOpeningHand>()
                    || Is<AcceptConfirmation>() || Is<DeclineConfirmation>() || Is<UndoLastAction>() || Is<PassTurn>(),
                CanAutoPass = Is<AutoPassPriority>(), MaySkip = Is<SkipAllocation>(),
                PriorityAlternative = Is<UndoLastAction>() ? PriorityAlternative.UndoLastAction : Is<PassTurn>() ? PriorityAlternative.PassTurn : PriorityAlternative.None,
                QuietYieldToken = prefix + ":yield", PriorityAbilities = [ability],
                PlayerChoices = Is<SelectPlayer>() ? [1] : [],
                Choices = [new(7, "First ability", null), new(9, "Second ability", null)],
                Min = Is<ChooseNumber>() ? 2 : 1, Max = Is<ChooseNumber>() ? 6 : 2,
                Amount = 3, Limits = [2, 3] };
            if (Is<ChooseOptions>()) decision = decision with { Max = 1 };
            if (Is<OrderOptions>()) decision = decision with { Task = DecisionTask.Ordering, Min = 2, Max = 2,
                Choices = [new(7, focus.Name, focus) { CardId = focus.VisualId }, new(9, other.Name, other) { CardId = other.VisualId }] };
            if (Is<ChooseNumber>()) decision = decision with { Task = DecisionTask.Number };
            if (Is<EnterText>()) decision = decision with { Task = DecisionTask.Text };
            if (Is<AcknowledgeReveal>()) decision = decision with { Task = DecisionTask.Reveal };
            if (Is<AllocateAmounts>() || Is<SkipAllocation>()) decision = decision with { Task = DecisionTask.Allocation, AllocationKind = "damage",
                Choices = [new(0, focus.Name, focus) { CardId = focus.VisualId, Maximum = 2 },
                    new(1, enemy.Name, enemy) { CardId = enemy.VisualId, Maximum = 3 }] };
            hand = hand with { Selectable = Is<PlayCard>() };
            var players = Enumerable.Range(0, 4).Select(id => new Player(id, id == 0 ? "You" : "Opponent " + id, 40,
                id == 0, false, ImmutableDictionary<string, int>.Empty, id == 0 ? [new("Battlefield", 2, [focus, other], null), new("Hand", 1, [hand], null)]
                    : [new("Battlefield", id == 1 ? 1 : 0, id == 1 ? [enemy] : [], null), new("Hand", 2, [], null)])).ToArray();
            Combat? combat = Is<ToggleAttackPlayer>() ? new([], [new(focus.CombatId, [new("player", "opponent-1", 1, "Opponent 1")])], null)
                : Is<ToggleAttackPermanent>() ? new([], [new(focus.CombatId, [new("card", enemy.CombatId, 1, enemy.Name)])], null)
                : Is<ToggleBlock>() ? new([new(enemy.CombatId, 0, [], [focus.CombatId])], [], null) : null;
            var before = new GameSnapshot(prefix + ":game", "playing", null, null, 0, 3, "Main phase", "MAIN1", 1,
players.ToImmutableArray(), [], combat, decision) { Revision = 8, BoardRevision = 4, PriorityPlayerId = 0, DecisionOwnerId = 0,
                IgnoredPriorityAbilities = Is<RestorePriorityAbility>() ? [ability with { IgnoredUntilNextTurn = true }] : [] };
            GameCommand expected = row.CommandType.Name switch {
                nameof(PassPriority) => new PassPriority(before.Id, decision.Id),
                nameof(AutoPassPriority) => new AutoPassPriority(before.Id, decision.Id),
                nameof(KeepOpeningHand) => new KeepOpeningHand(before.Id, decision.Id),
                nameof(MulliganOpeningHand) => new MulliganOpeningHand(before.Id, decision.Id),
                nameof(PayMana) => new PayMana(before.Id, decision.Id),
                nameof(CancelPayment) => new CancelPayment(before.Id, decision.Id),
                nameof(CompleteAttackers) => new CompleteAttackers(before.Id, decision.Id),
                nameof(CompleteBlockers) => new CompleteBlockers(before.Id, decision.Id),
                nameof(CompleteSelection) => new CompleteSelection(before.Id, decision.Id),
                nameof(CancelSelection) => new CancelSelection(before.Id, decision.Id),
                nameof(AcceptConfirmation) => new AcceptConfirmation(before.Id, decision.Id),
                nameof(DeclineConfirmation) => new DeclineConfirmation(before.Id, decision.Id),
                nameof(AcknowledgeDecision) => new AcknowledgeDecision(before.Id, decision.Id),
                nameof(AcknowledgeReveal) => new AcknowledgeReveal(before.Id, decision.Id),
                nameof(UndoLastAction) => new UndoLastAction(before.Id, decision.Id),
                nameof(PassTurn) => new PassTurn(before.Id, decision.Id),
                nameof(SelectCard) => new SelectCard(before.Id, decision.Id, focus.Key),
                nameof(PlayCard) => new PlayCard(before.Id, decision.Id, hand.Key),
                nameof(ActivateCard) => new ActivateCard(before.Id, decision.Id, focus.Key),
                nameof(UseManaSource) => new UseManaSource(before.Id, decision.Id, focus.Key),
                nameof(SelectPlayer) => new SelectPlayer(before.Id, decision.Id, 1),
                nameof(ToggleAttackPlayer) => new ToggleAttackPlayer(before.Id, decision.Id, focus.Key, 1),
                nameof(ToggleAttackPermanent) => new ToggleAttackPermanent(before.Id, decision.Id, focus.Key, enemy.Key),
                nameof(ToggleBlock) => new ToggleBlock(before.Id, decision.Id, enemy.Key, focus.Key),
                nameof(ChooseOptions) => new ChooseOptions(before.Id, decision.Id, [9]),
                nameof(OrderOptions) => new OrderOptions(before.Id, decision.Id, [9, 7]),
                nameof(ChooseNumber) => new ChooseNumber(before.Id, decision.Id, 4),
                nameof(EnterText) => new EnterText(before.Id, decision.Id, "named value"),
                nameof(AllocateAmounts) => new AllocateAmounts(before.Id, decision.Id, [2, 1]),
                nameof(SkipAllocation) => new SkipAllocation(before.Id, decision.Id),
                nameof(IgnorePriorityAbility) => new IgnorePriorityAbility(before.Id, decision.Id, ability.Id),
                nameof(RestorePriorityAbility) => new RestorePriorityAbility(before.Id, ability.Id),
                nameof(PlaceFromHand) => new PlaceFromHand(before.Id, decision.Id, hand.Key, ability.Id),
                nameof(PassForNow) => new PassForNow(before.Id, decision.Id, decision.QuietYieldToken),
                nameof(UseOfferedAction) => new UseOfferedAction(before.Id, decision.Id, focus.Key, prefix + ":source-action"),
                _ => throw new InvalidOperationException("Missing command fixture: " + row.CommandType.Name)
            };
            GameCommand invalid = expected switch {
                SelectCard value => value with { Card = prefix + ":private-opponent-key" },
                PlayCard value => value with { Card = enemy.Key },
                ActivateCard value => value with { Card = hand.Key },
                UseManaSource value => value with { Card = prefix + ":private-opponent-key" },
                SelectPlayer value => value with { Player = 3 },
                ToggleAttackPlayer value => value with { Player = 3 },
                ToggleAttackPermanent value => value with { DefenderCard = other.Key },
                ToggleBlock value => value with { Blocker = other.Key },
                ChooseOptions value => value with { Choices = [99] },
                OrderOptions value => value with { Choices = [99, 7] },
                ChooseNumber value => value with { Value = 7 },
                EnterText value => new ChooseNumber(value.GameId, value.DecisionId, 4),
                AllocateAmounts value => value with { Amounts = [3, 0] },
                IgnorePriorityAbility value => value with { AbilityId = prefix + ":unoffered" },
                RestorePriorityAbility value => value with { AbilityId = prefix + ":unoffered" },
                UseOfferedAction value => value with { ActionId = prefix + ":unoffered" },
                PlaceFromHand value => value with { Card = enemy.Key },
                PassForNow value => value with { YieldToken = prefix + ":expired-yield" },
                PassPriority or AutoPassPriority or UndoLastAction or PassTurn => new KeepOpeningHand(before.Id, decision.Id),
                _ => new PassPriority(before.Id, decision.Id)
            };
            var after = before with { Revision = 9, BoardRevision = 5, Decision = null, ActionClass = row.Name };
            after = expected switch {
                SelectCard or ActivateCard or UseManaSource or UseOfferedAction => ChangeCard(after, focus.VisualId, card => card with { Counters = ImmutableDictionary<string, int>.Empty.Add("fixture", 1) }),
                SelectPlayer => after with { Players = players.Select(p => p.Id == 1 ? p with { Life = 37 } : p).ToImmutableArray() },
                ToggleAttackPlayer => after with { Combat = new([new(focus.CombatId, 1, [], [])], [], null) },
                ToggleAttackPermanent => after with { Combat = new([new(focus.CombatId, 1, [], []) { Defender = new("card", enemy.CombatId, 1, enemy.Name) }], [], null) },
                ToggleBlock => after with { Combat = new([new(enemy.CombatId, 0, [focus.CombatId], []) { Blocked = true }], [], null) },
                AutoPassPriority or PassForNow => after with { PriorityPlayerId = 1, DecisionOwnerId = null },
                ChooseOptions or OrderOptions => after with { Decision = new() { Id = prefix + ":target-continuation", Task = DecisionTask.TargetSelection,
                    PlayerChoices = [1] }, Notices = [Is<OrderOptions>() ? "order:9,7" : "ability:9"] },
                ChooseNumber => after with { Stack = [new("Fixture spell", "", false, focus) { Effect = new() { Kind = "damage", Amount = 4 } }] },
                AllocateAmounts => ChangeCard(ChangeCard(after, focus.VisualId, card => card with { Damage = 2 }), enemy.VisualId, card => card with { Damage = 1 }),
                IgnorePriorityAbility => after with { Decision = decision, IgnoredPriorityAbilities = [ability with { IgnoredUntilNextTurn = true }] },
                RestorePriorityAbility => after with { IgnoredPriorityAbilities = [] },
                PlaceFromHand => after with { Decision = new() { Id = prefix + ":placement-continuation", Task = DecisionTask.TargetSelection,
                    Min = 1, Max = 1, SourceCard = focus, SourceCardId = focus.VisualId,
                    Choices = [new(4, hand.Name, hand) { CardId = hand.VisualId }] } },
                _ => after
            };
            return new(row, before, after, expected, invalid, focus, other, hand, enemy, new(before, after, expected));
        }
        internal async Task<GameCommand?> BuildExecuteAsync(ClientSession client)
        {
            switch (Expected) {
                case SelectCard: case ActivateCard: case UseManaSource: return new InteractionController().CardClick(Before, Focus).Reply;
                case PlayCard: return InteractionController.Play(Before, Hand);
                case SelectPlayer: return new InteractionController().PlayerClick(Before, 1).Reply;
                case ToggleAttackPlayer: return InteractionController.Assign(Before, Focus.CombatId, 1);
                case ToggleAttackPermanent: return InteractionController.Assign(Before, Focus.CombatId, targetCard: Enemy.CombatId);
                case ToggleBlock: return InteractionController.Assign(Before, Focus.CombatId, targetCard: Enemy.CombatId);
                case AutoPassPriority: return new AutoPriority().Update(Before, true, false, new HashSet<string>(), false, false);
                case PlaceFromHand placement: return HandPlacements.Start(Before, Hand, placement.AbilityId);
                case AllocateAmounts: return AllocationGuide.Reply(Before, [2, 1]);
                case OrderOptions:
                    var gestures = new InteractionController();
                    var last = gestures.CardClick(Before, Other);
                    var first = gestures.CardClick(Before, Focus);
                    int[] order = [last.ChoiceIndex ?? -1, first.ChoiceIndex ?? -1];
                    return TableChoices.CanConfirm(Before, order) ? new OrderOptions(Before.Id, Before.Decision!.Id, order.ToImmutableArray()) : null;
                case UseOfferedAction action:
                    var context = new SourceActionContext(Before.Id, Before.Decision!.Id, Before.Revision, Before.ViewerId, Focus.Key, Focus.VisualId);
                    await ReadSourceActions(client, context);
                    return client.SourceActions.TryCreateCommand(context, action.ActionId);
                default: return Expected; // Generic task rows exercise session/authority, not renderer input.
            }
        }
    }

    private static GameSnapshot ChangeCard(GameSnapshot state, string id, Func<Card, Card> update) => state with {
        Players = state.Players.Select(player => player with { Zones = player.Zones.Select(zone => zone with {
            Cards = zone.Cards.Select(card => card.VisualId == id ? update(card) : card).ToImmutableArray() }).ToImmutableArray() }).ToImmutableArray()
    };

    private sealed class ScriptedProvider(GameSnapshot before, GameSnapshot after, GameCommand offered) : IMatchConnection, ISourceActions
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
        public Task ExecuteAsync(GameCommand reply)
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
                [new(((UseOfferedAction)offered).ActionId, "activate", "Increment the fixture counter")]));
        }
        public Task ConcedeAsync(string gameId) => throw new InvalidOperationException("Concede is not a game decision and has separate existing coverage.");
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
            fixture = Fixture.Create(Cases.Single(row => row.CommandType == typeof(UseOfferedAction)), name);
            client = new(fixture.Provider, sourceActions: fixture.Provider); client.Accept(fixture.Before); return Task.FromResult(Checkpoint());
        }
        public async Task<Checkpoint> ApplyAsync(SemanticCommand command, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (command.Seat != 0 || command.Kind != "use-source" || command.Arguments["source"]?.GetValue<string>() != "focus"
                || command.Arguments["action"]?.GetValue<string>() != "increment") throw new ArgumentException("Unknown semantic fixture alias.");
            var reply = await fixture.BuildExecuteAsync(client) ?? throw new Exception("Current fixture action unavailable");
            SentKey = ((UseOfferedAction)reply).Card;
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
