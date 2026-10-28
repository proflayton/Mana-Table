using System.Collections.Immutable;
using Mana.Client;
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
        LobbyPublicDeckIdentity(check);
        var legacyReady = JsonSerializer.SerializeToElement(new { protocolVersion = 2,
            capabilities = ForgeProtocol.RequiredCapabilities.Where(capability => capability != "explicit-deck-setup") });
        bool legacyRejected = false;
        try { ForgeProtocol.ReadyCapabilities(legacyReady); } catch (IOException) { legacyRejected = true; }
        check(legacyRejected, "The production ready validator rejects an adapter missing explicit deck setup");
        var currentReady = JsonSerializer.SerializeToElement(new { protocolVersion = 2,
            capabilities = ForgeProtocol.RequiredCapabilities.Add("source-actions") });
        check(ForgeProtocol.ReadyCapabilities(currentReady).Contains("source-actions"), "Ready retains optional provider capabilities");
        ObservationCompletionOrder(check).GetAwaiter().GetResult();

        var selectionWire = Prompt("""
            "kind":"input","task":"selection","purpose":"targets","min":1,"max":1,
            "inputType":"UnrelatedProviderImplementation","context":"private-provider-metadata"
            """);
        string original = selectionWire.GetRawText();
        var selected = ForgeDecisionDecoder.Decode(selectionWire);
        check(selected.Payload is TargetSelectionPayload { Range: { Min: 1, Max: 1 } } && selectionWire.GetRawText() == original,
            "A direct immutable decode consumes authoritative semantics without editing JSON or exposing provider class names");
        CurrentChoiceDisclosures(check);
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
        var counterProgress = Decode(""" "kind":"input","task":"selection","counterPayment":{"amount":2,"remaining":1} """);
        check(counterProgress.Payload is EntitySelectionPayload { Range: null, CounterPayment: { Amount: 2, Remaining: 1 } },
            "A non-mana counter payment preserves authoritative total and remaining counts without inventing target bounds");
        check(Decode(""" "kind":"input","task":"selection","counterPayment":{"amount":2,"remaining":0} """).Payload
                is EntitySelectionPayload { CounterPayment.Remaining: 0 },
            "A fully allocated counter cost retains its explicit zero remaining count");
        const string ordinaryCounterChoices = """ "kind":"choice","task":"choice","min":1,"max":1,"choices":[{"index":7,"label":"-1/-1"},{"index":3,"label":"Stun"}] """;
        const string donor = """ {"cardId":"donor-visible","card":{"visualId":"donor-visible","key":"","name":"Current donor","power":1,"toughness":2,"counters":{"-1/-1":3,"Stun":1}},"controllerId":0,"zone":"Battlefield"} """;
        const string counterOptions = """ [{"index":7,"type":"M1M1","label":"-1/-1","available":3},{"index":3,"type":"STUN","label":"Stun","available":1}] """;
        const string anonymousDonor = """ {"cardId":"anonymous-position","card":{"combatId":"anonymous-position","faceDown":true,"name":"Face-down or hidden card","type":"Creature","power":2,"toughness":2},"controllerId":0,"zone":"Battlefield"} """;
        string CounterChoice(string options = counterOptions, string permanent = donor, int amount = 1)
            => "\"counterPaymentChoice\":{\"amount\":" + amount + ",\"permanent\":" + permanent + ",\"options\":" + options + "}";
        var counterTypes = Decode(ordinaryCounterChoices + "," + CounterChoice()
            + """ ,"sourceCard":{"visualId":"activation-host","key":"","name":"Ability host"},"sourceZone":"Battlefield","sourceControllerId":0 """, 0);
        check(counterTypes.Payload is OptionsPayload { CounterPayment: { Amount: 1, Permanent: { CardId: "donor-visible", Card.Key: "" }, Options: var types } }
                && types.Select(option => (option.Index, option.Type, option.Available)).SequenceEqual([(7, "M1M1", 3), (3, "STUN", 1)])
                && counterTypes.Context.Source?.Card?.VisualId == "activation-host",
            "An ordinary counter-type choice retains its current keyless donor, exact debit and legal unallocated counts at original offered indices");
        check(Decode(ordinaryCounterChoices + "," + CounterChoice(counterOptions.Replace("\"available\":3", "\"available\":2")), 0).Payload
                is OptionsPayload { CounterPayment: { Amount: 1, Options: [{ Available: 2 }, { Available: 1 }] } },
            "An additional type choice keeps its one-counter debit and remaining legal availability instead of deriving them from the donor's unchanged total counters");
        check(Decode(ordinaryCounterChoices + "," + CounterChoice(permanent: anonymousDonor), 0).Payload is OptionsPayload {
                CounterPayment.Permanent: { CardId: "anonymous-position", Card: { FaceDown: true, VisualId: "", CombatId: "anonymous-position", Key: "" } } },
            "A face-down battlefield donor retains only its anonymous public position and current public characteristics without inventing a face identity");
        foreach (string invalid in new[] {
            """ "kind":"input","task":"priority","counterPayment":{"amount":1,"remaining":1} """,
            """ "kind":"input","task":"selection","purpose":"targets","min":1,"max":1,"counterPayment":{"amount":1,"remaining":1} """,
            """ "kind":"input","task":"selection","counterPayment":{"amount":0,"remaining":0} """,
            """ "kind":"input","task":"selection","counterPayment":{"amount":1,"remaining":-1} """,
            """ "kind":"input","task":"selection","counterPayment":{"amount":1,"remaining":2} """,
            ordinaryCounterChoices + ",\"ordered\":true," + CounterChoice(),
            ordinaryCounterChoices + ",\"purpose\":\"abilities\"," + CounterChoice(),
            ordinaryCounterChoices.Replace("\"min\":1", "\"min\":0") + "," + CounterChoice(),
            ordinaryCounterChoices.Replace("\"max\":1", "\"max\":2") + "," + CounterChoice(),
            ordinaryCounterChoices + "," + CounterChoice(amount: 0),
            ordinaryCounterChoices + "," + CounterChoice(amount: 2),
            ordinaryCounterChoices + "," + CounterChoice(counterOptions.Replace("\"index\":3", "\"index\":7")),
            ordinaryCounterChoices + "," + CounterChoice(counterOptions.Replace("\"index\":3", "\"index\":9")),
            ordinaryCounterChoices + "," + CounterChoice(counterOptions.Replace("\"available\":1", "\"available\":0")),
            ordinaryCounterChoices + "," + CounterChoice(counterOptions.Replace("\"type\":\"STUN\"", "\"type\":\"\"")),
            ordinaryCounterChoices + "," + CounterChoice(counterOptions.Replace("\"label\":\"Stun\"", "\"label\":\" \"")),
            ordinaryCounterChoices + "," + CounterChoice(counterOptions.Replace("\"label\":\"Stun\"", "\"label\":\"Different label\"")),
            ordinaryCounterChoices + "," + CounterChoice(""" [{"index":3,"type":"STUN","label":"Stun","available":1},{"index":7,"type":"M1M1","label":"-1/-1","available":3}] """),
            ordinaryCounterChoices + "," + CounterChoice(permanent: donor.Replace("\"key\":\"\"", "\"key\":\"action-handle\"")),
            ordinaryCounterChoices + "," + CounterChoice(permanent: donor.Replace("\"cardId\":\"donor-visible\"", "\"cardId\":\"other-occurrence\"")),
            ordinaryCounterChoices + "," + CounterChoice(permanent: donor.Replace("\"zone\":\"Battlefield\"", "\"zone\":\"Hand\"")),
            ordinaryCounterChoices + "," + CounterChoice(permanent: donor.Replace("\"controllerId\":0", "\"controllerId\":99")),
            ordinaryCounterChoices + "," + CounterChoice(permanent: anonymousDonor.Replace("Face-down or hidden card", "Unrevealed printed identity")),
            ordinaryCounterChoices + "," + CounterChoice(permanent: anonymousDonor.Replace("\"type\":\"Creature\"", "\"text\":\"Unrevealed rules\",\"type\":\"Creature\"")),
            ordinaryCounterChoices + "," + CounterChoice(permanent: anonymousDonor.Replace("\"combatId\":\"anonymous-position\"", "\"visualId\":\"hidden-face-id\",\"combatId\":\"anonymous-position\""))
        }) {
            bool failed = false;
            try { Decode(invalid, 0); } catch (IOException) { failed = true; }
            check(failed, "Counter-payment metadata rejects incompatible tasks, contradictory counts/indices and actionable or unlinked donor faces");
        }
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
        scope.Observe(State(counterTypes.Payload));
        var counterCommand = scope.Encode(new ChooseOptions(game, prompt, [7]));
        check(((ImmutableArray<int>)counterCommand["choices"]!).SequenceEqual([7]) && (string)counterCommand["expectedTask"]! == "choice",
            "Counter payment presentation still submits only the exact ordinary current option index");
        Reject(new SelectCard(game, prompt, source.Key), "A counter-type donor face does not authorize selecting a card from an ordinary options task");
        var undeclaredCounterSource = State(counterProgress.Payload) with { Players = [new(0, "You", 40, true, false,
            ImmutableDictionary<string, int>.Empty, [new("Battlefield", 1, [source with { Selectable = false }], null)])] };
        check(!GameCommandPermissions.Allows(undeclaredCounterSource, new SelectCard(game, prompt, source.Key)),
            "Counter-payment progress never grants SelectCard permission to an undeclared physical source");
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
            int? decisionOwner = null, int? priorityPlayer = null, string status = "playing", object[]? actionSources = null) => JsonSerializer.SerializeToElement(new {
            id = game, status, viewerId = 0, turn = 2, phase = "Main phase", phaseKey = "MAIN1", activePlayerId = 1,
            players = priority.Players.Add(new(1, "Other seat", 40, false, false, ImmutableDictionary<string, int>.Empty, [])),
            stack = stack ?? [], combat, prompt = rawPrompt, decisionOwnerId = decisionOwner, priorityPlayerId = priorityPlayer,
            actionSources = actionSources ?? [],
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
        var explicitCard = new Card { Name = "Live stack source", VisualId = "live-stack-occurrence", Key = "explicit-stack-key" };
        var explicitSource = new ActionSourceOccurrence("wire-prompt", "Stack", 0, explicitCard);
        object liveSpell = new { id = 18, name = explicitCard.Name, text = "Public rules", ability = false, card = explicitCard with { Key = "" } };
        var sourceState = scope.ObserveRaw(Observation(Prompt(""" "kind":"input","task":"priority","okEnabled":true"""),
            [liveSpell], actionSources: [explicitSource]));
        check(sourceState?.ActionSources is [{ DecisionId: "wire-prompt", Zone: "Stack", Card.Key: "explicit-stack-key" }]
                && scope.CanReadSource(game, "wire-prompt", explicitCard.Key)
                && (string)scope.Encode(new UseOfferedAction(game, "wire-prompt", explicitCard.Key, "offered-route"))["action"]! == "useSourceAction",
            "The provider maps separately scoped current stack sources through the ordinary source-action command boundary");
        scope.ObserveRaw(Observation(Prompt(""" "kind":"input","task":"priority","okEnabled":true"""),
            [new { id = 18, name = explicitCard.Name, text = "Old face", ability = false, card = explicitCard }]));
        check(!scope.CanReadSource(game, "wire-prompt", explicitCard.Key),
            "A stack reading face with a captured key still cannot become a source-query capability");
        foreach (var malformedSources in new object[][] {
            [explicitSource with { DecisionId = "old-input" }],
            [explicitSource with { Zone = "Library" }],
            [explicitSource, explicitSource],
            [explicitSource with { Zone = "Sideboard", PlayerId = 1 }],
            [explicitSource with { Card = explicitCard with { Selectable = true } }],
            [explicitSource with { Card = explicitCard with { FaceDown = true, Name = "Hidden identity", Text = "Private rules" } }]
        }) {
            bool rejected = false;
            try { scope.ObserveRaw(Observation(Prompt(""" "kind":"input","task":"priority","okEnabled":true"""), [liveSpell], actionSources: malformedSources)); }
            catch (IOException) { rejected = true; }
            check(rejected && scope.ObservedGameId.Length == 0, "Malformed additional source authority revokes the observed provider scope");
        }
        bool historicAbilityRejected = false;
        try { scope.ObserveRaw(Observation(Prompt(""" "kind":"input","task":"priority","okEnabled":true"""),
            [new { id = 19, name = "Ability", text = "Historical source", ability = true, card = explicitCard with { Key = "" } }],
            actionSources: [explicitSource])); } catch (IOException) { historicAbilityRejected = true; }
        check(historicAbilityRejected, "A triggered or activated stack item's historical source cannot corroborate a current spell occurrence");
        var sideboardState = scope.ObserveRaw(Observation(Prompt(""" "kind":"input","task":"priority","okEnabled":true"""),
            actionSources: [explicitSource with { Zone = "Sideboard" }]));
        check(sideboardState?.ActionSources.Single().Zone == "Sideboard" && scope.CanReadSource(game, "wire-prompt", explicitCard.Key),
            "A current sideboard source is an owned decision capability rather than a public hidden-zone reveal");
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

    private static async Task ObservationCompletionOrder(Action<bool, string> check)
    {
        const string game = "ordered-observation-game";
        var scope = new ForgeCommandScope();
        var replica = new SessionReplica<GameSnapshot>(state => state.Id, state => state.Revision);
        JsonElement Wire(long revision, string prompt, string gameId = game, string? notice = null) => JsonSerializer.SerializeToElement(new {
            id = gameId, status = "playing", viewerId = 0, revision, turn = 1,
            phase = "Main phase", phaseKey = "MAIN1", activePlayerId = 0,
            decisionOwnerId = 0, priorityPlayerId = 0,
            players = new[] { new Player(0, "You", 40, true, false,
                ImmutableDictionary<string, int>.Empty, []) },
            stack = Array.Empty<object>(),
            notices = notice == null ? Array.Empty<string>() : new[] { notice },
            prompt = new { id = prompt, kind = "input", task = "priority", okEnabled = true }
        });
        var transitions = new ForgeCommandScope();
        transitions.ObserveRaw(Wire(36, "refreshed-prompt"));
        transitions.ObserveRaw(Wire(36, "refreshed-prompt", notice: "Current source facts refreshed"));
        check(transitions.ObservedState is { Revision: 36, Notices: ["Current source facts refreshed"] }
                && (string)transitions.Encode(new PassPriority(game, "refreshed-prompt"))["promptId"]! == "refreshed-prompt",
            "A same-revision observation may refresh current facts without losing its exact command scope");
        transitions.ObserveRaw(Wire(1, "replacement-prompt", gameId: "replacement-game"));
        check(transitions.ObservedState is { Id: "replacement-game", Revision: 1 }
                && (string)transitions.Encode(new PassPriority("replacement-game", "replacement-prompt"))["sessionId"]! == "replacement-game",
            "A new match starts its own revision sequence instead of inheriting the preceding match's high-water mark");
        bool previousGameRejected = false, foreignGameRejected = false;
        try { transitions.Encode(new PassPriority(game, "refreshed-prompt")); } catch (EngineCommandRejectedException) { previousGameRejected = true; }
        try { transitions.Encode(new PassPriority("foreign-game", "replacement-prompt")); } catch (EngineCommandRejectedException) { foreignGameRejected = true; }
        check(previousGameRejected && foreignGameRejected,
            "Changing matches still rejects commands from the preceding or an unrelated game");
        // ForgeEngine.ObserveAsync awaits a RunContinuationsAsynchronously RPC
        // completion, then calls this same ObserveRaw. The write semaphore only
        // serializes request writes; two read continuations can finish in either
        // order. Gates force the order without sleeps, threads or a Java process.
        var earlier = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var later = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<GameSnapshot?> Read(Task<JsonElement> response) => scope.ObserveRaw(await response);
        var earlierRead = Read(earlier.Task);
        var laterRead = Read(later.Task);
        later.SetResult(Wire(36, "current-prompt"));
        var current = await laterRead ?? throw new InvalidOperationException("The controlled current observation disappeared.");
        check(replica.Accept(current, replica.Epoch) && replica.Current?.Revision == 36,
            "A controlled newer observation becomes the displayed replica before the delayed older read completes");
        var currentCommand = new PassPriority(game, "current-prompt");
        check((string)scope.Encode(currentCommand)["promptId"]! == currentCommand.DecisionId,
            "The exact current displayed command is valid before completing the older read");

        earlier.SetResult(Wire(35, "retired-prompt"));
        var delayed = await earlierRead ?? throw new InvalidOperationException("The controlled delayed observation disappeared.");
        check(!replica.Accept(delayed, replica.Epoch) && ReferenceEquals(replica.Current, current),
            "The actual replica rejects the delayed lower revision and keeps its current displayed decision");
        bool currentRejected = false, retiredRejected = false;
        try { scope.Encode(currentCommand); } catch (EngineCommandRejectedException) { currentRejected = true; }
        try { scope.Encode(new PassPriority(game, "retired-prompt")); } catch (EngineCommandRejectedException) { retiredRejected = true; }
        check(!currentRejected && retiredRejected,
            "Completing an older observation cannot reject the current displayed command or restore its retired prompt"
                + $" (displayed={replica.Current!.Revision}/{replica.Current.Decision!.Id}, provider={scope.ObservedState?.Revision}/{scope.ObservedState?.Decision?.Id}, currentRejected={currentRejected}, retiredRejected={retiredRejected})");
    }

    private static void LobbyPublicDeckIdentity(Action<bool, string> check)
    {
        using var wire = JsonDocument.Parse("""
            {"mode":"hosting","status":"Connected","canStart":false,"matchActive":false,"addresses":[],"tableId":"public-table","revision":12,
             "slots":[{"index":0,"name":"You","type":"LOCAL","deck":"Stale legacy name","ready":false,"local":true,
               "deckPreview":{"selectionId":"public-assignment","name":"Current saved name","problem":null,"commanders":[
                 {"id":"printing-only","name":"Toothy, Imaginary Friend","artName":"Toothy, Imaginary Friend","artFace":"front","edition":"BBD",
                  "artSet":"BBD","collectorNumber":"12","artIndex":2,"foil":false,"manaCost":"3 U","type":"Legendary Creature — Illusion",
                  "oracleText":"Partner with Pir, Imaginative Rascal","power":"1","toughness":"1",
                  "key":"untrusted-action","visualId":"untrusted-occurrence","selectable":true}]}}]}
            """);
        string original = wire.RootElement.GetRawText();
        var lobby = ForgeEngine.MapLobby(wire.RootElement);
        var seat = lobby.Seats.Single();
        check(lobby.TableId == "public-table" && lobby.Revision == 12 && seat.Deck == "Current saved name"
                && seat.DeckPreview is { SelectionId: "public-assignment", Name: "Current saved name", Problem: null },
            "A public seat assignment supplies one name truth and retains its authoritative table and revision");
        check(seat.DeckPreview!.Commanders is [{ Name: "Toothy, Imaginary Friend", Edition: "BBD", ArtSet: "BBD", ArtIndex: 2,
                    Power: 1, Toughness: 1, Key: "", VisualId: "", Selectable: false }]
                && wire.RootElement.GetRawText() == original,
            "Catalog commander faces keep printing art and readable numeric characteristics without acquiring game identities or mutating the wire");
        using var legacyWire = JsonDocument.Parse("""
            {"mode":"joined","status":"Connected","canStart":false,"matchActive":false,"addresses":[],
             "slots":[{"index":1,"name":"Friend","type":"REMOTE","deck":"Legacy public name","ready":true,"local":false}]}
            """);
        var legacy = ForgeEngine.MapLobby(legacyWire.RootElement);
        check(legacy.Revision == 0 && legacy.Seats is [{ Deck: "Legacy public name", DeckPreview: null }],
            "An older provider's public deck name remains readable without inventing commander art or an assignment receipt");
    }

    private static void CurrentChoiceDisclosures(Action<bool, string> check)
    {
        const string eligible = """ "kind":"choice","task":"choice","min":1,"max":1,"choices":[{"index":7,"label":"Eligible nonland","card":{"name":"Eligible nonland","type":"Creature"}}] """;
        const string extra = """ "disclosedCards":[{"label":"Readable land","card":{"name":"Readable land","type":"Land","text":"Publicly disclosed rules"}}] """;
        var wire = Prompt(eligible + "," + extra);
        string original = wire.GetRawText();
        var decision = ForgeDecisionDecoder.Decode(wire);
        check(decision.Payload is OptionsPayload { Selection: { Choices: [{ Index: 7 }], Disclosures: [{ Index: null, CardId: "",
                Card: { Name: "Readable land", Key: "", VisualId: "", CombatId: "", Selectable: false, Highlighted: false } }] } }
                && wire.GetRawText() == original,
            "A mixed-hand choice retains its additional readable land in the current prompt without altering eligible nonland indices or the wire");
        var state = new GameSnapshot("disclosed-choice", "playing", null, null, 0, 1, "Main", "MAIN1", 0,
            [new(0, "Actor", 40, true, false, ImmutableDictionary<string, int>.Empty, [])], [], null, decision);
        check(GameCommandPermissions.Allows(state, new ChooseOptions(state.Id, decision.Id, [7]))
                && !GameCommandPermissions.Allows(state, new ChooseOptions(state.Id, decision.Id, [8]))
                && !GameCommandPermissions.Allows(state, new SelectCard(state.Id, decision.Id, "Readable land")),
            "A current extra face grants reading only and cannot become an additional option or card-selection command");
        _ = Decode(""" "kind":"reveal","task":"reveal","choices":[{"label":"Earlier land","card":{"name":"Earlier land","type":"Land"}}] """);
        check(Decode(eligible).Payload is OptionsPayload { Selection.Disclosures.IsEmpty: true },
            "A later choice does not inherit the current extra faces or permissions from an earlier reveal");
        check(Decode(eligible + "," + extra.Replace("}]", "},{\"label\":\"Readable land\",\"card\":{\"name\":\"Readable land\",\"type\":\"Land\"}}]")).Payload
                is OptionsPayload { Selection.Disclosures.Length: 2 },
            "Distinct disclosed occurrences with the same printed face retain their anonymous multiplicity");
        foreach (string invalid in new[] {
            """ "kind":"input","task":"priority" """ + "," + extra,
            """ "kind":"reveal","task":"reveal","choices":[] """ + "," + extra,
            eligible + ",\"purpose\":\"abilities\"," + extra,
            eligible + ",\"ordered\":true," + extra,
            eligible + ",\"counterPaymentChoice\":{}," + extra,
            eligible + ",\"libraryCards\":[]," + extra,
            eligible + ",\"disclosedCards\":{}",
            eligible + "," + extra.Replace("\"label\":\"Readable land\"", "\"index\":8,\"label\":\"Readable land\""),
            eligible + "," + extra.Replace("\"label\":\"Readable land\"", "\"index\":null,\"label\":\"Readable land\""),
            eligible + "," + extra.Replace("\"label\":\"Readable land\"", "\"cardId\":\"hidden-physical-id\",\"label\":\"Readable land\""),
            eligible + "," + extra.Replace("\"type\":\"Land\"", "\"type\":\"Land\",\"key\":\"action-key\""),
            eligible + "," + extra.Replace("\"type\":\"Land\"", "\"type\":\"Land\",\"visualId\":\"physical-id\""),
            eligible + "," + extra.Replace("\"type\":\"Land\"", "\"type\":\"Land\",\"combatId\":\"physical-id\""),
            eligible + "," + extra.Replace("\"type\":\"Land\"", "\"type\":\"Land\",\"selectable\":true"),
            eligible + "," + extra.Replace("\"type\":\"Land\"", "\"type\":\"Land\",\"highlighted\":true"),
            eligible + "," + extra.Replace("\"type\":\"Land\"", "\"type\":\"Land\",\"attachedTo\":{\"kind\":\"card\",\"cardId\":\"hidden-host\"}")
        }) {
            bool rejected = false;
            try { Decode(invalid); } catch (IOException) { rejected = true; }
            check(rejected, "Malformed, actionable or unrelated current-choice disclosures fail closed at the provider decoder");
        }
    }
}
