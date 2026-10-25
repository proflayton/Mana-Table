using System.Collections.Immutable;
using Mana.Contracts;
using Mana.Forge;
using System.Text.Json;

namespace Mana.Table;

// Exercises the exact decoder and immutable observation gate used in production.
internal static class ForgeCommandBoundaryChecks
{
    private static JsonElement Prompt(string fields)
    {
        using var document = JsonDocument.Parse("{\"id\":\"wire-prompt\"," + fields + "}");
        return document.RootElement.Clone();
    }
    private static Decision Decode(string fields, params int[] players) => ForgeDecisionDecoder.Decode(Prompt(fields), players.ToImmutableHashSet());

    internal static void Run(Action<bool, string> check)
    {
        var legacyReady = JsonSerializer.SerializeToElement(new { protocolVersion = 2,
            capabilities = ForgeProtocol.RequiredCapabilities.Where(capability => capability != "explicit-deck-setup") });
        bool legacyRejected = false;
        try { ForgeProtocol.ReadyCapabilities(legacyReady); } catch (IOException) { legacyRejected = true; }
        check(legacyRejected, "The production ready validator rejects an adapter missing explicit deck setup");
        var currentReady = JsonSerializer.SerializeToElement(new { protocolVersion = 2,
            capabilities = ForgeProtocol.RequiredCapabilities.Add("source-actions") });
        check(ForgeProtocol.ReadyCapabilities(currentReady).Contains("source-actions"), "Ready retains optional provider capabilities");

        var selectionWire = Prompt("""
            "kind":"input","task":"selection","purpose":"targets","min":1,"max":1,
            "inputType":"UnrelatedProviderImplementation","context":"private-provider-metadata"
            """);
        string original = selectionWire.GetRawText();
        var selected = ForgeDecisionDecoder.Decode(selectionWire);
        check(selected.Payload is TargetSelectionPayload { Range: { Min: 1, Max: 1 } } && selectionWire.GetRawText() == original,
            "A direct immutable decode consumes authoritative semantics without editing JSON or exposing provider class names");
        foreach (string invalid in new[] {
            """ "kind":"input","task":"unknown-required-task" """,
            """ "kind":"choice","task":"mana" """,
            """ "kind":"input","task":"openingHand","purpose":"targets" """,
            """ "kind":"input","task":"mana","priorityAlternative":"undo" """,
            """ "kind":"input","task":"priority","ordered":true""",
            """ "kind":"input","task":"mana","canAutoPass":true""",
            """ "kind":"choice","task":"choice","purpose":"abilities","ordered":true,"min":0,"max":1,"choices":[{"index":0,"label":"Ability"}]""",
            """ "kind":"choice","task":"choice","min":2,"max":1,"choices":[]""",
            """ "kind":"choice","task":"choice","min":"one","max":1,"choices":[]""",
            """ "kind":"number","task":"number","min":0,"max":1.5""",
            """ "kind":"text","task":"text","numeric":true""",
            """ "kind":"choice","task":"choice","purpose":"abilities","min":0,"max":1,"choices":[{"index":0,"label":"Ability"}],"libraryCards":[{"index":0,"label":"Hidden card","card":{}}]""",
            """ "kind":"input","task":"confirmation","librarySearch":{"libraryOwnerId":99,"destination":"Hand","cardName":"Named card"}""",
            """ "kind":"input","task":"priority","librarySearch":{"libraryOwnerId":null,"destination":"Hand","cardName":""}""",
            """ "kind":"input","task":"confirmation","openingHand":{"handSize":7,"nextHandSize":7,"cardsToReturn":0,"freeMulligan":true}""",
            """ "kind":"input","task":"openingHand","purpose":"openingHand","openingHand":{"handSize":7,"nextHandSize":6,"cardsToReturn":1,"freeMulligan":true}""",
            """ "kind":"choice","task":"choice","min":0,"max":1,"choices":[{"index":0,"label":"A"},{"index":0,"label":"B"}]""",
            """ "kind":"choice","task":"choice","min":0,"max":0,"choices":[],"libraryCards":[{"index":null,"label":"Hidden","card":{"name":"Hidden","key":"not-an-action"}}]""",
            """ "kind":"allocate","task":"allocate","amount":2,"choices":[{"index":1,"label":"Wrong position"}]"""
        }) {
            bool failed = false;
            try { Decode(invalid, 1); } catch (IOException) { failed = true; }
            check(failed, "Unsupported, contradictory or unsafe provider shapes fail without acquiring a different task");
        }
        var offeredSearch = Decode("""
            "kind":"input","task":"confirmation","ok":"Search library","okEnabled":true,"cancel":"Don't search","cancelEnabled":true,
            "librarySearch":{"libraryOwnerId":1,"destination":"Hand","cardName":"Named card"}
            """, 1);
        check(offeredSearch.Payload is ConfirmationPayload { Search.LibraryOwnerId: 1, Decline: { Enabled: true } },
            "An optional search owns its actual library and explicit refusal without reporting hidden availability");
        var targeting = Decode("""
            "kind":"input","task":"selection","purpose":"targets","min":1,"max":1,"playerChoices":[1],
            "librarySearch":{"libraryOwnerId":null,"destination":"Hand","cardName":"Named card"}
            """, 1);
        check(targeting.Payload is TargetSelectionPayload { Search: { CardName: "Named card" }, Players.Length: 1 },
            "A targeted search remains a real unresolved player decision");
        check(Decode(""" "kind":"input","task":"selection" """).Payload is EntitySelectionPayload { Range: null },
            "A current entity input with no published selection bounds retains that absence");
        var ordering = Decode("""
            "kind":"choice","task":"choice","ordered":true,"min":2,"max":2,"choices":[{"index":7,"label":"First"},{"index":3,"label":"Second"}]
            """);
        check(ordering.Payload is OrderingPayload { Selection.Choices: var offered } && offered.Select(choice => choice.Index).SequenceEqual([7, 3]),
            "Ordering preserves exact provider indices without another writable task classifier");
        using (var numericFixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "numeric-text-prompt.json")))) {
            var textbox = ForgeDecisionDecoder.Decode(numericFixture.RootElement);
            check(textbox.Payload is NumberPayload { Initial: "-2", Range: null },
                "The fixture verified against Java's actual numeric textbox has no invented bounds and retains its initial negative integer");
        }
        check(Decode(""" "kind":"number","task":"number","min":-2,"max":3""").Payload is NumberPayload { Range: { Min: -2, Max: 3 } },
            "A bounded number input retains its actual negative bounds");
        check(Decode("""
            "kind":"input","task":"openingHand","purpose":"openingHand","openingHand":{"handSize":7,"nextHandSize":6,"cardsToReturn":0,"freeMulligan":false}
            """).Payload is OpeningHandPayload { Consequence: { NextHandSize: 6, CardsToReturn: 0 } },
            "A paid Vancouver redraw can draw fewer cards without a London bottom requirement");
        var allocation = Decode("""
            "kind":"allocate","task":"allocate","amount":5,"atLeastOne":true,"allocationKind":"combat","amountLabel":"damage","limits":[3,5],
            "choices":[{"index":0,"id":"recipient-a","label":"Creature","lethal":9},{"index":1,"id":"recipient-b","label":"Player","minimum":2,"maximum":4,"isDefender":true}]
            """);
        check(allocation.Payload is AllocationPayload { Recipients: var recipients }
            && recipients[0] is { Id: "recipient-a", Minimum: 1, Maximum: 3, Lethal: 9 }
            && recipients[1] is { Id: "recipient-b", Minimum: 2, Maximum: 4, IsDefender: true },
            "Allocation normalizes engine defaults and limits once while preserving positions, identity and an unattainable lethal threshold");
        var explicitAllocation = Decode("""
            "kind":"allocate","task":"allocate","amount":3,"atLeastOne":true,"limits":[2],
            "choices":[{"index":0,"id":"explicit-recipient","label":"Creature","minimum":0,"maximum":3}]
            """);
        check(explicitAllocation.Payload is AllocationPayload { Recipients: [{ Minimum: 0, Maximum: 3 }] },
            "Explicit recipient bounds take precedence over legacy allocation defaults, which apply only when the current bounds are absent");
        var reveal = Decode("""
            "kind":"reveal","task":"reveal","choices":[{"index":0,"label":"Read only","cardId":"public-occurrence","card":{"name":"Visible card"}}],"min":0,"max":0
            """);
        check(reveal.Payload is RevealPayload { Cards: [{ Index: null, CardId: "public-occurrence", Card.Key: "" }] },
            "A regular reveal keeps its public occurrence link without turning its display index into selection authority");

        var scope = new ForgeCommandScope();
        var source = new Card { VisualId = "visible-source", Key = "prompt:source", Name = "Source", Selectable = true };
        GameSnapshot State(DecisionPayload payload) => new("observed-game", "playing", null, null, 0, 2, "Main phase", "MAIN1", 1,
            [new(0, "You", 40, true, false, ImmutableDictionary<string, int>.Empty, [new("Battlefield", 1, [source], null)])], [], null,
            new("observed-prompt", new(), payload));
        void Reject(GameCommand command, string description)
        {
            bool rejected = false;
            try { scope.Encode(command); } catch (EngineCommandRejectedException) { rejected = true; }
            check(rejected, description);
        }
        var yes = new DecisionReply("Yes", true);
        var no = new DecisionReply("No", true);
        const string game = "observed-game", prompt = "observed-prompt";
        Reject(new KeepOpeningHand(game, prompt), "The boundary requires an observation before any decision can be translated");
        var opening = State(new OpeningHandPayload(yes, no));
        scope.Observe(opening);
        var keep = scope.Encode(new KeepOpeningHand(game, prompt));
        check((string)keep["expectedTask"]! == "openingHand" && (string)keep["action"]! == "ok",
            "Keeping an observed opening hand carries its exact game, prompt and private task");
        Reject(new PayMana(game, prompt), "A mana command cannot borrow opening hand's ok wire action");
        Reject(new PassPriority(game, prompt), "A priority command cannot consume an opening hand");
        Reject(new KeepOpeningHand("another-game", prompt), "Another game cannot reuse an observed prompt");
        Reject(new KeepOpeningHand(game, "retired-prompt"), "Retired prompt authority is rejected");
        scope.Observe(State(new PaymentPayload(yes, no)));
        var payment = scope.Encode(new PayMana(game, prompt));
        check((string)payment["expectedTask"]! == "mana" && (string)payment["action"]! == "ok"
            && (string)payment["sessionId"]! == game && (string)payment["promptId"]! == prompt,
            "Payment's shared ok wire operation retains its own typed meaning");
        Reject(new KeepOpeningHand(game, prompt), "An opening hand command cannot pay mana");
        var manaSource = scope.Encode(new UseManaSource(game, prompt, source.Key));
        check((string)manaSource["expectedTask"]! == "mana" && (string)manaSource["key"]! == source.Key,
            "Mana source gestures retain the exact disclosed source handle");
        (DecisionPayload Payload, GameCommand Command, string Task)[] replies = [
            (new AttackPayload(yes), new CompleteAttackers(game, prompt), "attack"),
            (new BlockPayload(yes), new CompleteBlockers(game, prompt), "block"),
            (new EntitySelectionPayload(yes, no, []), new CompleteSelection(game, prompt), "selection"),
            (new ConfirmationPayload(yes, no), new AcceptConfirmation(game, prompt), "confirmation"),
            (new ConfirmationPayload(yes), new AcknowledgeDecision(game, prompt), "confirmation"),
            (new EntitySelectionPayload(yes, no, []), new CancelSelection(game, prompt), "selection"),
            (new PaymentPayload(yes, no), new CancelPayment(game, prompt), "mana"),
            (new ConfirmationPayload(yes, no), new DeclineConfirmation(game, prompt), "confirmation")
        ];
        foreach (var row in replies) {
            scope.Observe(State(row.Payload));
            check((string)scope.Encode(row.Command)["expectedTask"]! == row.Task,
                row.Command.GetType().Name + " translates only its current closed payload");
            Reject(new KeepOpeningHand(game, prompt), "A payload cannot be replaced with another task's shared wire reply");
        }
        var priority = State(new PriorityPayload(yes, new(PriorityAlternative.PassTurn, no)));
        scope.Observe(priority);
        var passTurn = scope.Encode(new PassTurn(game, prompt));
        check((string)passTurn["action"]! == "priorityPassTurn" && (string)passTurn["expectedTask"]! == "priority",
            "Pass turn has its own wire command and does not fall through generic cancel");
        Reject(new UndoLastAction(game, prompt), "Pass turn cannot impersonate Undo");
        Reject(new SelectCard(game, prompt, source.Key), "Priority activation cannot impersonate target selection");
        Reject(new PlayCard(game, prompt, source.Key), "A battlefield source cannot impersonate hand play");
        check((string)scope.Encode(new ActivateCard(game, prompt, source.Key))["key"]! == source.Key,
            "Priority activation retains its original scoped source handle");
        check(scope.CanReadSource(game, prompt, source.Key) && !scope.CanReadSource(game, "old-prompt", source.Key),
            "Source queries use the same current game, prompt and disclosed occurrence as commands");
        int[] suppliedChoices = [3];
        scope.Observe(State(new OptionsPayload(new([new(3, "Offered", null)], new(1, 1)))));
        var choose = new ChooseOptions(game, prompt, suppliedChoices.ToImmutableArray());
        suppliedChoices[0] = 99;
        var chosen = scope.Encode(choose);
        check(((ImmutableArray<int>)chosen["choices"]!).SequenceEqual([3]) && (string)chosen["expectedTask"]! == "choice",
            "Option commands preserve exact immutable offered indices");
        Reject(new OrderOptions(game, prompt, [3]), "Unordered options cannot impersonate an ordering");
        scope.Observe(State(new NumberPayload("", new(2, 5))));
        check((string)scope.Encode(new ChooseNumber(game, prompt, 4))["value"]! == "4", "Number encoding preserves integer wire values");
        Reject(new EnterText(game, prompt, "4"), "Number prompts do not accept a text command");
        scope.Observe(State(new NumberPayload("-2")));
        check((string)scope.Encode(new ChooseNumber(game, prompt, int.MinValue))["expectedTask"]! == "number",
            "An unbounded integer textbox accepts the real Int32 domain without inventing a range");
        var ignored = new PriorityAbility("ignored-ability", source.Key, source.Name, "Ability", true);
        scope.Observe(priority with { Decision = null, IgnoredPriorityAbilities = [ignored] });
        var restore = scope.Encode(new RestorePriorityAbility(game, ignored.Id));
        check(!restore.ContainsKey("expectedTask") && (string)restore["promptId"]! == "" && (bool)restore["ignored"]! == false,
            "Restoring a match preference does not impersonate another player's private prompt");
        scope.Observe(priority with { Decision = null, IgnoredPriorityAbilities = [] });
        Reject(new RestorePriorityAbility(game, ignored.Id), "Removed preferences revoke captured Restore commands");

        JsonElement Observation(JsonElement rawPrompt, object[]? stack = null, object? combat = null,
            int? decisionOwner = null, int? priorityPlayer = null, string status = "playing") => JsonSerializer.SerializeToElement(new {
            id = game, status, viewerId = 0, turn = 2, phase = "Main phase", phaseKey = "MAIN1", activePlayerId = 1,
            players = priority.Players.Add(new(1, "Other seat", 40, false, false, ImmutableDictionary<string, int>.Empty, [])),
            stack = stack ?? [], combat, prompt = rawPrompt, decisionOwnerId = decisionOwner, priorityPlayerId = priorityPlayer,
            decision = new { task = 999, payload = "unsolicited-native-record" }
        });
        var observed = scope.ObserveRaw(Observation(Prompt(""" "kind":"input","task":"priority","okEnabled":true"""),
            [new { id = 17, name = "Spell", text = "Text", ability = false }], new {
                attackers = new[] { new { cardId = "attacker", defendingPlayerId = 0, blockerIds = Array.Empty<string>(),
                    eligibleBlockerIds = Array.Empty<string>(), defender = new { kind = "player", id = 0, playerId = 0, name = "You" } } },
                attackOptions = new[] { new { cardId = "attacker", defenders = new object[] {
                    new { kind = "player", id = (object)0, playerId = 0, name = "You" },
                    new { kind = "card", id = (object)"opaque-card", playerId = 0, name = "Planeswalker" } } } }
            }));
        check(observed is { Stack: [{ Id: "17" }], Decision.Payload: PriorityPayload, Combat: { } decodedCombat }
            && decodedCombat.Attackers[0].Defender?.Id == "0"
            && decodedCombat.AttackOptions[0].Defenders[1].Id == "opaque-card",
            "The private envelope maps numeric stack/player IDs and opaque card IDs explicitly while ignoring unsolicited native decisions");
        // MatchSession.markBusy publishes this exact lifecycle after answering
        // Keep/Mulligan and other inputs, before the next decision is available.
        var resolving = scope.ObserveRaw(Observation(JsonSerializer.SerializeToElement<object?>(null), status: "resolving"));
        check(resolving is { Status: "resolving", Decision: null }
                && resolving.Players.Select(player => player.Id).SequenceEqual(observed!.Players.Select(player => player.Id))
                && resolving.Viewer!.Zone("Battlefield").Cards.Single().VisualId == source.VisualId
                && scope.ObservedGameId == game && !scope.CanReadSource(game, "wire-prompt", source.Key),
            "The real provider's resolving envelope retains the last public board and clears input and source-query authority");
        Reject(new PassPriority(game, "wire-prompt"), "A captured command cannot answer the provider's between-decision resolving state");
        foreach (string status in new[] { "resolving", "unknown" }) {
            scope.Observe(priority);
            bool rejected = false;
            try { scope.ObserveRaw(Observation(Prompt(""" "kind":"input","task":"priority","okEnabled":true"""), status: status)); }
            catch (IOException) { rejected = true; }
            check(rejected && scope.ObservedGameId.Length == 0,
                "A resolving state cannot carry a stale prompt, and an unknown lifecycle never confers authority");
        }
        bool malformed = false;
        try { scope.ObserveRaw(Observation(Prompt(""" "kind":"input","task":"unsupported" """))); }
        catch (IOException) { malformed = true; }
        check(malformed && scope.ObservedGameId == "" && !scope.CanReadSource(game, "wire-prompt", source.Key),
            "A malformed authoritative observation revokes both command and source-query authority");
        Reject(new PassPriority(game, "wire-prompt"), "Malformed state cannot leave an earlier pass command valid");
        foreach (var ownership in new[] { (Chooser: (int?)1, Responder: (int?)null), (Chooser: (int?)null, Responder: (int?)1) }) {
            scope.Observe(priority);
            bool foreign = false;
            try { scope.ObserveRaw(Observation(Prompt(""" "kind":"input","task":"priority","okEnabled":true"""),
                decisionOwner: ownership.Chooser, priorityPlayer: ownership.Responder)); } catch (IOException) { foreign = true; }
            check(foreign && scope.ObservedGameId.Length == 0 && !scope.CanReadSource(game, prompt, source.Key),
                "An explicit foreign chooser or responder cannot lend a private prompt or retain command/source authority");
            Reject(new PassPriority(game, prompt), "Contradictory public ownership revokes previously observed priority");
        }
        check(scope.ObserveRaw(Observation(Prompt(""" "kind":"input","task":"priority","okEnabled":true"""),
                decisionOwner: 0, priorityPlayer: 0))?.Decision?.Payload is PriorityPayload,
            "A consistent explicit local owner and responder retain the private priority payload");
        scope.Observe(priority);
        scope.Clear();
        Reject(new PassPriority(game, prompt), "Clearing a process or match revokes observed commands");
        scope.Observe(priority);
        scope.ObserveRaw(JsonSerializer.SerializeToElement<object?>(null));
        Reject(new PassPriority(game, prompt), "A null match observation revokes the preceding game");
        bool replacementRejected = false;
        try { new ForgeCommandScope().Encode(new PassPriority(game, prompt)); } catch (EngineCommandRejectedException) { replacementRejected = true; }
        check(replacementRejected, "A replacement provider process cannot inherit command authority");
    }
}
