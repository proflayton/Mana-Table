using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

// Real process/codec/dispatch boundaries with authored metadata, not Magic rules.
internal static class AutomatedPriorityProtocolProbe
{
    internal const string Marker = "--automated-priority-protocol-probe";
    internal static int Serve(string[] arguments)
    {
        int index = Array.IndexOf(arguments, Marker);
        string mode = arguments[index + 1], directory = Path.GetDirectoryName(arguments[index + 2])!;
        bool metadata = mode.StartsWith("metadata-", StringComparison.Ordinal);
        string scenario = metadata ? mode[9..] : mode;
        Directory.CreateDirectory(directory);
        var capabilities = ForgeProtocol.RequiredCapabilities.Add("automated-seats").Add("priority-continuation-v1");
        if (scenario is not ("human-only" or "cap-only")) capabilities = capabilities.Add("automated-priority-continuation-v1");
        if (metadata) capabilities = capabilities.Add("automated-priority-metadata-v1");
        Console.WriteLine(JsonSerializer.Serialize(new { @event = "ready", protocolVersion = ForgeProtocol.Version, capabilities }));
        Console.Out.Flush();
        var state = JsonNode.Parse("""
            {"id":"automated-priority-wire","status":"playing","viewerId":29,"turn":1,"phaseKey":"MAIN1","activePlayerId":29,
             "revision":1,"boardRevision":1,"decisionOwnerId":29,"priorityPlayerId":29,"stackInterruptionVersion":0,
             "players":[{"id":29,"name":"Bot","life":40,"mana":{},"zones":[]}],"stack":[],
             "priorityContinuation":{"sequence":0,"grantId":"","mode":null,"status":"Idle","reasonCode":"","automaticPasses":0,"phaseStops":[]},
             "priorityObservation":{"boardRevision":1,"publicActivitySequence":0,"acknowledgedBoardRevision":0,
                "acknowledgedPublicActivitySequence":0,"registered":false,"overflowed":false},
             "prompt":{"id":"priority","kind":"input","task":"priority","okEnabled":true,"canAutoPass":true,
                "responseAvailability":"Absent"}}
            """)!;
        if (scenario == "wrong-decision-owner") state["decisionOwnerId"] = 99;
        if (scenario == "wrong-priority-owner") state["priorityPlayerId"] = 99;
        JsonNode Metadata()
        {
            var prompt = state["prompt"];
            bool active = state["players"]![0]!["eliminated"]?.GetValue<bool>() != true;
            bool priority = state["status"]!.GetValue<string>() == "playing" && state["turn"]!.GetValue<int>() > 0 && active
                && state["decisionOwnerId"]?.GetValue<int>() == 29 && state["priorityPlayerId"]?.GetValue<int>() == 29
                && prompt?["kind"]?.GetValue<string>() == "input" && prompt["task"]?.GetValue<string>() == "priority"
                && prompt["okEnabled"]?.GetValue<bool>() == true;
            return new JsonObject {
                ["schemaVersion"] = 1, ["gameId"] = state["id"]!.DeepClone(), ["seatId"] = 29,
                ["revision"] = state["revision"]!.DeepClone(), ["boardRevision"] = state["boardRevision"]!.DeepClone(),
                ["stackInterruptionVersion"] = state["stackInterruptionVersion"]!.DeepClone(), ["turn"] = state["turn"]!.DeepClone(),
                ["status"] = state["status"]!.DeepClone(), ["playerActive"] = active,
                ["decisionOwnerId"] = state["decisionOwnerId"]?.DeepClone(), ["priorityPlayerId"] = state["priorityPlayerId"]?.DeepClone(),
                ["decisionId"] = prompt?["id"]?.DeepClone(), ["priorityDecisionId"] = priority ? prompt!["id"]!.DeepClone() : null,
                ["continuation"] = state["priorityContinuation"]!.DeepClone(), ["observation"] = state["priorityObservation"]!.DeepClone()
            };
        }
        void Reply(long id, object? value) { Console.WriteLine(JsonSerializer.Serialize(new { id, result = value })); Console.Out.Flush(); }
        void ReplyControl(long id, string action)
        {
            if (!metadata) { Reply(id, state); return; }
            var result = Metadata();
            if (scenario == "null-owner" && action == "acknowledgePriorityObservation") {
                result["decisionOwnerId"] = null; result["priorityPlayerId"] = null; result["priorityDecisionId"] = null;
            }
            if (action == "grantPriorityContinuation") {
                if (scenario == "legacy-shaped-reply") { Reply(id, state); return; }
                if (scenario == "foreign-game") result["gameId"] = "another-game";
                if (scenario == "foreign-seat") result["seatId"] = 30;
                if (scenario == "extra-payload") result["players"] = new JsonArray();
            }
            Reply(id, result);
        }
        int enables = 0, acknowledgements = 0; bool empty = false; long? held = null; JsonNode? heldState = null;
        while (Console.ReadLine() is { } line) {
            using var parsed = JsonDocument.Parse(line);
            var request = parsed.RootElement; long id = request.GetProperty("id").GetInt64();
            string method = request.GetProperty("method").GetString()!; var args = request.GetProperty("params");
            File.AppendAllText(Path.Combine(directory, "requests.jsonl"), line + "\n");
            if (method == "automatedEnable") { empty = ++enables == 2; Reply(id, new { enabled = true }); continue; }
            if (method == "automatedState") {
                Reply(id, empty ? new JsonArray() : new JsonArray(new JsonObject { ["seatId"] = 29,
                    ["state"] = state.DeepClone(), ["domain"] = JsonNode.Parse("""{"version":1,"coverage":"Complete","sources":[],"actions":[]}"""),
                    ["semantics"] = new JsonObject() })); continue;
            }
            if (method == "matchStart") {
                state["id"] = "replacement-priority-wire"; state["revision"] = 1;
                state["priorityContinuation"] = JsonNode.Parse("""{"sequence":0,"grantId":"","mode":null,"status":"Idle","reasonCode":"","automaticPasses":0,"phaseStops":[]}""");
                Reply(id, state);
                if (held is { } old) { Reply(old, heldState); held = null; }
                continue;
            }
            if (method == "matchSetup") {
                Reply(id, new { deckId = args.GetProperty("deckId").GetString(), revision = 1, maxPlayers = 4,
                    setup = new { problem = "" } }); continue;
            }
            if (method != (metadata ? "automatedPriorityControl" : "automatedAction")) { Reply(id, new { accepted = true }); continue; }
            string action = args.GetProperty("action").GetString()!;
            if (args.GetProperty("sessionId").GetString() != state["id"]!.GetValue<string>()
                || args.GetProperty("seatId").GetInt32() != 29) {
                Console.WriteLine(JsonSerializer.Serialize(new { id, error = "retired authored control" })); Console.Out.Flush(); continue;
            }
            state["revision"] = state["revision"]!.GetValue<long>() + 1;
            if (action == "acknowledgePriorityObservation") {
                state["priorityObservation"]!["registered"] = true;
                state["priorityObservation"]!["acknowledgedBoardRevision"] = 1;
                if (scenario == "late-ack") {
                    if (++acknowledgements == 1) {
                        held = id; heldState = metadata ? Metadata() : state.DeepClone();
                        File.WriteAllText(Path.Combine(directory, "held-ack.txt"), "ready"); continue;
                    }
                    state["boardRevision"] = 2;
                    state["priorityObservation"]!["boardRevision"] = 2;
                    state["prompt"]!["id"] = "replacement-priority";
                    ReplyControl(id, action);
                    if (held is { } old) { Reply(old, heldState); held = null; }
                    continue;
                }
            } else if (action == "grantPriorityContinuation") {
                if (scenario == "retired") {
                    Console.WriteLine(JsonSerializer.Serialize(new { id, error = "authored pre-admission retirement",
                        errorCode = "automated-priority-control-retired" })); Console.Out.Flush(); continue;
                }
                state["priorityContinuation"]!["sequence"] = args.GetProperty("sequence").GetInt64();
                state["priorityContinuation"]!["grantId"] = args.GetProperty("grantId").GetString();
                state["priorityContinuation"]!["mode"] = "NoResponse";
                state["priorityContinuation"]!["status"] = "Active";
                if (scenario == "superseded-receipt") {
                    state["priorityContinuation"]!["sequence"] = args.GetProperty("sequence").GetInt64() + 1;
                    state["priorityContinuation"]!["status"] = "Revoked";
                }
                if (scenario == "post-admission-error") {
                    Console.WriteLine(JsonSerializer.Serialize(new { id, error = "authored failure after consent mutation" }));
                    Console.Out.Flush(); continue;
                }
                if (scenario == "malformed-receipt") { Reply(id, new { accepted = true }); continue; }
                if (scenario == "late-world") {
                    held = id; heldState = metadata ? Metadata() : state.DeepClone();
                    File.WriteAllText(Path.Combine(directory, "held-grant.txt"), "ready"); continue;
                }
            } else if (action == "revokePriorityContinuation") {
                state["priorityContinuation"]!["sequence"] = args.GetProperty("throughSequence").GetInt64();
                state["priorityContinuation"]!["status"] = "Revoked";
            }
            ReplyControl(id, action);
        }
        return 0;
    }

    internal static async Task RunAsync(string profile, Action<bool, string> check)
    {
        foreach (string mode in new[] { "human-only", "bot", "wrong-decision-owner", "wrong-priority-owner", "late-world", "retired", "superseded-receipt", "post-admission-error", "malformed-receipt" }) {
            string directory = Path.Combine(profile, "automated-priority-protocol-" + mode); Directory.CreateDirectory(directory);
            await using var engine = new ForgeEngine(new(Path.Combine(AppContext.BaseDirectory, "Mana.Table.Tests.exe"), Marker, mode, directory));
            check(!engine.SupportsAutomatedPriorityContinuation, "Bot continuation has no authority before ready negotiation: " + mode);
            await engine.InitializeAsync();
            check(engine.SupportsPriorityContinuation && engine.SupportsAutomatedPriorityContinuation == (mode != "human-only"),
                "Human continuation support cannot imply the distinct bot completed-scan/credit contract: " + mode);
            await engine.EnableAutomatedSeatsAsync();
            if (mode is "wrong-decision-owner" or "wrong-priority-owner") {
                bool refused = false;
                try { await engine.ObserveAutomatedSeatsAsync(); } catch (IOException) { refused = true; }
                check(refused && Count(directory, "automatedAction") == 0,
                    "The actual provider rejects a foreign priority owner before either an ACK or chained grant can acquire authority: " + mode);
                continue;
            }
            var row = (await engine.ObserveAutomatedSeatsAsync()).Single(); long epoch = engine.AutomatedPriorityEpoch;
            var ack = new AutomatedPriorityControl.Acknowledge(row.State.Id, 1, 0);
            if (mode == "human-only") {
                check(await engine.ExecuteAutomatedPriorityControlAsync(epoch, 29, ack) is AutomatedPriorityControlResult.Retired
                    && Count(directory, "automatedAction") == 0, "Missing bot capability rejects metadata before any RPC despite human support");
                continue;
            }
            check(await engine.ExecuteAutomatedPriorityControlAsync(epoch, 29, ack) is AutomatedPriorityControlResult.Accepted {
                    Receipt.Epoch: var acceptedEpoch, Receipt.SeatId: 29 } && acceptedEpoch == epoch,
                "The actual provider maps a closed owned ACK through the host-only route");
            var grant = new AutomatedPriorityControl.NoResponse(row.State.Id, row.State.Decision!.Id, 1, "owned-grant", []);
            bool rejected = false;
            try { await engine.ExecuteAutomatedAsync(29, new GrantPriorityContinuation(row.State.Id, row.State.Decision.Id,
                1, "policy-grant", PriorityContinuationMode.NoResponse, [])); } catch (EngineCommandRejectedException) { rejected = true; }
            check(rejected && Count(directory, "automatedAction") == 1,
                "A policy gameplay port cannot acquire consent authority or add an input receipt");
            var positive = engine.ExecuteAutomatedPriorityControlAsync(epoch, 29, grant);
            if (mode == "superseded-receipt") {
                check(await positive is AutomatedPriorityControlResult.Retired,
                    "A later negative sequence cannot certify acceptance of the original affirmative grant or keep awaiting its retired ID");
            } else if (mode == "retired") {
                check(await positive is AutomatedPriorityControlResult.Retired,
                    "Only the exact backend pre-admission retirement code gives a known no-effect metadata result");
            } else if (mode is "post-admission-error" or "malformed-receipt") {
                Exception? failure = null;
                try { await positive; } catch (Exception error) { failure = error; }
                check(failure is IOException && failure is not ForgeAutomatedPriorityRetiredException,
                    "A generic or malformed post-admission receipt remains uncertain rather than granting policy fallback: " + mode);
                check(await engine.ExecuteAutomatedPriorityControlAsync(epoch, 29,
                    new AutomatedPriorityControl.Withdraw(row.State.Id, 1)) is AutomatedPriorityControlResult.Accepted,
                    "Uncertain affirmative consent retains its separate negative fence: " + mode);
            } else if (mode == "late-world") {
                var elapsed = Stopwatch.StartNew();
                while (!File.Exists(Path.Combine(directory, "held-grant.txt")) && elapsed.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(5);
                if (!File.Exists(Path.Combine(directory, "held-grant.txt"))) throw new TimeoutException("The authored grant did not reach the child.");
                await engine.StartSoloAsync("authored-deck", ["opponent"]);
                check(await positive is AutomatedPriorityControlResult.Retired && engine.AutomatedPriorityEpoch != epoch,
                    "An admitted old-world grant reply cannot become current authority after an actual provider world replacement");
                int before = Count(directory, "automatedAction");
                check(await engine.ExecuteAutomatedPriorityControlAsync(epoch, 29, grant) is AutomatedPriorityControlResult.Retired
                    && Count(directory, "automatedAction") == before, "A retired world epoch cannot send another affirmative control to its replacement");
            } else {
                check(await positive is AutomatedPriorityControlResult.Accepted && Count(directory, "automatedState") == 1,
                    "An exact ACK receipt chains its current NoResponse grant with one owned read and no control-only observation round trip");
                await engine.EnableAutomatedSeatsAsync();
                check((await engine.ObserveAutomatedSeatsAsync()).IsEmpty, "The authored discovery scope really retires before negative consent");
                check(await engine.ExecuteAutomatedPriorityControlAsync(epoch, 29, new AutomatedPriorityControl.Withdraw(row.State.Id, 1))
                    is AutomatedPriorityControlResult.Accepted, "A previously dispatched grant retains its negative fence after current discovery scopes disappear");
            }
        }
        await OutOfOrderMetadata(profile, check);
        await CompactProtocol(profile, check);
        await OutOfOrderMetadata(profile, check, compact: true);
        ClosedMetadata(check);
    }

    private static async Task CompactProtocol(string profile, Action<bool, string> check)
    {
        foreach (string scenario in new[] { "bot", "cap-only", "null-owner", "retired", "superseded-receipt", "late-world",
            "post-admission-error", "malformed-receipt", "foreign-game", "foreign-seat", "extra-payload", "legacy-shaped-reply" }) {
            string mode = "metadata-" + scenario, directory = Path.Combine(profile, "automated-priority-protocol-" + mode);
            Directory.CreateDirectory(directory);
            await using var engine = new ForgeEngine(new(Path.Combine(AppContext.BaseDirectory, "Mana.Table.Tests.exe"), Marker, mode, directory));
            await engine.InitializeAsync(); await engine.EnableAutomatedSeatsAsync();
            var row = (await engine.ObserveAutomatedSeatsAsync()).Single(); long epoch = engine.AutomatedPriorityEpoch;
            var ack = new AutomatedPriorityControl.Acknowledge(row.State.Id, 1, 0);
            if (scenario == "cap-only") {
                check(!engine.SupportsAutomatedPriorityContinuation
                    && await engine.ExecuteAutomatedPriorityControlAsync(epoch, 29, ack) is AutomatedPriorityControlResult.Retired
                    && Count(directory, "automatedPriorityControl") == 0 && Count(directory, "automatedAction") == 0,
                    "The compact capability alone cannot confer the older host-only continuation authority");
                continue;
            }
            AutomatedPriorityControlResult? acknowledged = null;
            try { acknowledged = await engine.ExecuteAutomatedPriorityControlAsync(epoch, 29, ack); }
            catch (IOException error) { File.WriteAllText(Path.Combine(directory, "compact-ack-failure.txt"), error.ToString()); }
            check(acknowledged is AutomatedPriorityControlResult.Accepted { Receipt.Epoch: var actualEpoch } && actualEpoch == epoch
                && Count(directory, "automatedPriorityControl") == 1 && Count(directory, "automatedAction") == 0,
                "The negotiated compact ACK carries only closed metadata on its distinct method and the native epoch: " + scenario);
            var grant = new AutomatedPriorityControl.NoResponse(row.State.Id, row.State.Decision!.Id, 1, "compact-grant", []);
            if (scenario == "null-owner") {
                check(acknowledged is AutomatedPriorityControlResult.Accepted { Receipt.PriorityDecisionId: null, Receipt.DecisionOwnerId: null }
                    && await engine.ExecuteAutomatedPriorityControlAsync(epoch, 29, grant) is AutomatedPriorityControlResult.Retired
                    && Count(directory, "automatedPriorityControl") == 1,
                    "Explicit unknown owners remain valid metadata but cannot certify a chained priority input");
                await engine.ObserveAutomatedSeatsAsync();
            }
            var positive = engine.ExecuteAutomatedPriorityControlAsync(epoch, 29, grant);
            if (scenario is "retired" or "superseded-receipt") {
                check(await positive is AutomatedPriorityControlResult.Retired,
                    "A compact known retirement or later negative sequence cannot retain the original grant: " + scenario);
            } else if (scenario == "late-world") {
                var elapsed = Stopwatch.StartNew();
                while (!File.Exists(Path.Combine(directory, "held-grant.txt")) && elapsed.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(5);
                if (!File.Exists(Path.Combine(directory, "held-grant.txt"))) throw new TimeoutException("The compact grant did not reach the authored child.");
                await engine.StartSoloAsync("authored-deck", ["opponent"]);
                check(await positive is AutomatedPriorityControlResult.Retired && engine.AutomatedPriorityEpoch != epoch,
                    "A delayed compact old-world receipt cannot acquire the replacement world's native epoch");
            } else if (scenario is "post-admission-error" or "malformed-receipt" or "foreign-game" or "foreign-seat" or "extra-payload" or "legacy-shaped-reply") {
                Exception? failure = null; try { await positive; } catch (Exception error) { failure = error; }
                check(failure is IOException && failure is not ForgeAutomatedPriorityRetiredException,
                    "A malformed, foreign or uncertain compact receipt cannot become known retirement or legacy fallback: " + scenario);
                check(await engine.ExecuteAutomatedPriorityControlAsync(epoch, 29, new AutomatedPriorityControl.Withdraw(row.State.Id, 1))
                    is AutomatedPriorityControlResult.Accepted,
                    "A failed compact affirmative receipt preserves its separate negative fence: " + scenario);
            } else {
                check(await positive is AutomatedPriorityControlResult.Accepted
                    && Count(directory, "automatedState") == (scenario == "null-owner" ? 2 : 1),
                    "A certified compact ACK chains consent without a control-only rich observation: " + scenario);
                await engine.EnableAutomatedSeatsAsync(); await engine.ObserveAutomatedSeatsAsync();
                check(await engine.ExecuteAutomatedPriorityControlAsync(epoch, 29, new AutomatedPriorityControl.Withdraw(row.State.Id, 1))
                    is AutomatedPriorityControlResult.Accepted,
                    "Compact withdrawal survives discovery-scope retirement without borrowing policy authority: " + scenario);
            }
            check(Count(directory, "automatedAction") == 0,
                "An advertised compact route never retries a receipt through the legacy action method: " + scenario);
        }
    }

    private static async Task OutOfOrderMetadata(string profile, Action<bool, string> check, bool compact = false)
    {
        string mode = compact ? "metadata-late-ack" : "late-ack", method = compact ? "automatedPriorityControl" : "automatedAction";
        string directory = Path.Combine(profile, "automated-priority-protocol-" + mode); Directory.CreateDirectory(directory);
        await using var engine = new ForgeEngine(new(Path.Combine(AppContext.BaseDirectory, "Mana.Table.Tests.exe"), Marker, mode, directory));
        await engine.InitializeAsync(); await engine.EnableAutomatedSeatsAsync();
        var observed = (await engine.ObserveAutomatedSeatsAsync()).Single(); long epoch = engine.AutomatedPriorityEpoch;
        var ack = new AutomatedPriorityControl.Acknowledge(observed.State.Id, 1, 0);
        var first = engine.ExecuteAutomatedPriorityControlAsync(epoch, 29, ack);
        var elapsed = Stopwatch.StartNew();
        while (!File.Exists(Path.Combine(directory, "held-ack.txt")) && elapsed.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(5);
        if (!File.Exists(Path.Combine(directory, "held-ack.txt"))) throw new TimeoutException("The authored ACK did not reach the child.");
        var newer = await engine.ExecuteAutomatedPriorityControlAsync(epoch, 29, ack);
        var older = await first;
        check(newer is AutomatedPriorityControlResult.Accepted { Receipt.BoardRevision: 2 }
            && older is AutomatedPriorityControlResult.Accepted { Receipt.BoardRevision: 1 },
            "Actual out-of-order metadata replies retain their own authoritative values instead of borrowing the later receipt");
        int before = Count(directory, method);
        check(await engine.ExecuteAutomatedPriorityControlAsync(epoch, 29,
                new AutomatedPriorityControl.NoResponse(observed.State.Id, observed.State.Decision!.Id, 1, "old-priority", []))
                is AutomatedPriorityControlResult.Retired && Count(directory, method) == before,
            "A delayed older ACK cannot roll back the control cursor and revive an input already replaced by newer metadata");
        bool rejected = false;
        try { await engine.ExecuteAutomatedAsync(29, new PassPriority(observed.State.Id, "replacement-priority")); }
        catch (EngineCommandRejectedException) { rejected = true; }
        check(rejected && Count(directory, method) == before && Count(directory, "automatedState") == 1,
            "A receipt's newer decision never enters policy gameplay scopes or invents another owned rich observation");
    }

    private static void ClosedMetadata(Action<bool, string> check)
    {
        const string wire = """
            {"schemaVersion":1,"gameId":"metadata-game","seatId":29,"revision":1,"boardRevision":1,"stackInterruptionVersion":0,
             "turn":1,"status":"playing","playerActive":true,"decisionOwnerId":29,"priorityPlayerId":29,
             "decisionId":"priority","priorityDecisionId":"priority",
             "continuation":{"sequence":0,"grantId":"","mode":null,"status":"Idle","reasonCode":"","automaticPasses":0,"phaseStops":[]},
             "observation":{"boardRevision":1,"publicActivitySequence":0,"acknowledgedBoardRevision":1,
                "acknowledgedPublicActivitySequence":0,"registered":true,"overflowed":false}}
            """;
        using (var document = JsonDocument.Parse(wire))
            check(ForgeAutomatedPriorityMetadataDecoder.Decode(document.RootElement, 73) is {
                Epoch: 73, SeatId: 29, PriorityDecisionId: "priority", Observation.Registered: true },
                "A compact receipt decodes without any card, domain, snapshot or timer payload and uses only its supplied native epoch");
        bool Refused(string text) {
            using var document = JsonDocument.Parse(text);
            try { ForgeAutomatedPriorityMetadataDecoder.Decode(document.RootElement, 73); return false; }
            catch (IOException) { return true; }
        }
        foreach (string section in new[] { "", "continuation", "observation" }) {
            var original = JsonNode.Parse(wire)!;
            var fields = (JsonObject)(section == "" ? original : original[section]!);
            foreach (string field in fields.Select(item => item.Key).ToArray()) {
                var missing = JsonNode.Parse(wire)!;
                ((JsonObject)(section == "" ? missing : missing[section]!)).Remove(field);
                check(Refused(missing.ToJsonString()), "Compact metadata requires every explicit field, including nullable values: " + section + "/" + field);
            }
            foreach (string field in new[] { "epoch", "players", "domain", "timer", "unexpected" }) {
                var extra = JsonNode.Parse(wire)!;
                ((JsonObject)(section == "" ? extra : extra[section]!))[field] = 1;
                check(Refused(extra.ToJsonString()), "Compact metadata rejects foreign fields at its closed boundary: " + section + "/" + field);
            }
        }
        foreach (var (field, value) in new (string, string)[] {
            ("schemaVersion","2"), ("schemaVersion","1.0"), ("seatId","-1"), ("seatId","2147483648"),
            ("revision","-1"), ("revision","9223372036854775808"), ("revision","1e999"), ("boardRevision","null"),
            ("stackInterruptionVersion","-1"), ("turn","-1"), ("turn","0"), ("turn","1.5"),
            ("status","\"Playing\""), ("status","\"resolving\""), ("playerActive","false"), ("playerActive","1"),
            ("decisionOwnerId","null"), ("decisionOwnerId","30"), ("priorityPlayerId","null"), ("priorityPlayerId","30"),
            ("decisionId","null"), ("priorityDecisionId","\"another-input\""), ("gameId","\"\""),
            ("gameId",JsonSerializer.Serialize(new string('g',257))), ("decisionId",JsonSerializer.Serialize("invalid\ninput"))
        }) {
            var invalid = JsonNode.Parse(wire)!; invalid[field] = JsonNode.Parse(value);
            check(Refused(invalid.ToJsonString()), "Compact metadata rejects invalid primitives or an uncertified priority input: " + field + "/" + value);
        }
        foreach (var (section, field, value) in new (string, string, string)[] {
            ("continuation","mode","1"), ("continuation","mode","\"Unknown\""), ("continuation","status","\"active\""),
            ("continuation","sequence","-1"), ("continuation","automaticPasses","2147483648"),
            ("continuation","reasonCode",JsonSerializer.Serialize(new string('r',129))),
            ("continuation","reasonCode",JsonSerializer.Serialize("invalid\nreason")),
            ("continuation","phaseStops","[\"MAIN1\",\"MAIN1\"]"), ("continuation","phaseStops","[\"UNKNOWN\"]"),
            ("continuation","phaseStops","[null]"), ("continuation","status","\"Active\""),
            ("observation","boardRevision","2"), ("observation","acknowledgedBoardRevision","2"),
            ("observation","publicActivitySequence","-1"), ("observation","acknowledgedPublicActivitySequence","1"),
            ("observation","registered","\"true\"")
        }) {
            var invalid = JsonNode.Parse(wire)!; invalid[section]![field] = JsonNode.Parse(value);
            check(Refused(invalid.ToJsonString()), "Compact nested metadata retains closed enums, bounds and consistency: " + section + "/" + field);
        }
        foreach (string duplicate in new[] {
            wire.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1"),
            wire.Replace("\"sequence\":0", "\"sequence\":0,\"sequence\":0"),
            wire.Replace("\"registered\":true", "\"registered\":true,\"registered\":true"),
            wire.Replace("\"gameId\"", "\"GameId\"")
        }) check(Refused(duplicate), "Duplicate or differently cased compact fields cannot borrow permissive snapshot decoding");
        var unknownOwners = JsonNode.Parse(wire)!;
        unknownOwners["decisionOwnerId"] = null; unknownOwners["priorityPlayerId"] = null; unknownOwners["priorityDecisionId"] = null;
        using var acceptedUnknown = JsonDocument.Parse(unknownOwners.ToJsonString());
        check(ForgeAutomatedPriorityMetadataDecoder.Decode(acceptedUnknown.RootElement, 73) is { DecisionOwnerId: null, PriorityDecisionId: null },
            "Explicit unknown owners with no priority certificate remain valid metadata and force another rich observation");
        var granted = JsonNode.Parse(wire)!;
        granted["continuation"]!["sequence"] = 1; granted["continuation"]!["grantId"] = new string('g', 256);
        granted["continuation"]!["mode"] = "NoResponse"; granted["continuation"]!["status"] = "Held";
        using (var validGrant = JsonDocument.Parse(granted.ToJsonString()))
            check(ForgeAutomatedPriorityMetadataDecoder.Decode(validGrant.RootElement, 73).Continuation.GrantId.Length == 256,
                "A compact held receipt retains the existing bounded grant identity without card or policy data");
        granted["continuation"]!["grantId"] = new string('g', 257);
        check(Refused(granted.ToJsonString()), "An otherwise valid compact grant rejects an overlong grant identity");
        foreach (string status in new[] { "starting", "resolving", "finished", "error" }) {
            var inactive = JsonNode.Parse(unknownOwners.ToJsonString())!;
            inactive["status"] = status; inactive["playerActive"] = false; inactive["turn"] = 0;
            using var inactiveDocument = JsonDocument.Parse(inactive.ToJsonString());
            check(ForgeAutomatedPriorityMetadataDecoder.Decode(inactiveDocument.RootElement, 73).PriorityDecisionId == null,
                "Closed nonplaying metadata can reconcile control lifetime without certifying an input: " + status);
        }
    }

    private static int Count(string directory, string method) => File.ReadAllLines(Path.Combine(directory, "requests.jsonl"))
        .Count(line => JsonDocument.Parse(line).RootElement.GetProperty("method").GetString() == method);
}
