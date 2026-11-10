using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mana.AI;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

// The production process/decoder/host path over authored wire values. Java
// integration checks and the natural campaign separately establish actual rules.
internal static class PriorityResponsesProtocolProbe
{
    internal const string Marker = "--priority-responses-protocol-probe";

    internal static int Serve(string[] arguments)
    {
        int index = Array.IndexOf(arguments, Marker);
        string mode = arguments[index + 1], directory = Path.GetDirectoryName(arguments[index + 2])!;
        Directory.CreateDirectory(directory);
        var capabilities = ForgeProtocol.RequiredCapabilities.Add("automated-seats").Add("priority-continuation-v1")
            .Add("automated-priority-continuation-v1").Add("automated-priority-metadata-v1");
        if (mode != "unsupported") capabilities = capabilities.Add("automated-priority-responses-v1");
        Console.WriteLine(JsonSerializer.Serialize(new { @event = "ready", protocolVersion = ForgeProtocol.Version, capabilities }));
        Console.Out.Flush();
        var state = JsonNode.Parse("""
            {"id":"responses-wire","status":"playing","viewerId":29,"turn":1,"phaseKey":"UPKEEP","activePlayerId":29,
             "revision":1,"boardRevision":1,"decisionOwnerId":29,"priorityPlayerId":29,"stackInterruptionVersion":0,
             "players":[{"id":29,"name":"Bot","life":40,"mana":{},"zones":[]}],"stack":[],
             "priorityContinuation":{"sequence":0,"grantId":"","mode":null,"status":"Idle","reasonCode":"","automaticPasses":0,"phaseStops":[]},
             "priorityObservation":{"boardRevision":1,"publicActivitySequence":0,"acknowledgedBoardRevision":0,
                "acknowledgedPublicActivitySequence":0,"registered":false,"overflowed":false},
             "prompt":{"id":"priority","kind":"input","task":"priority","okEnabled":true,"canAutoPass":false,
                "responseAvailability":"Unknown"}}
            """)!;
        if (mode == "active") state["priorityContinuation"] = JsonNode.Parse("""
            {"sequence":1,"grantId":"existing","mode":"NoResponse","status":"Held","reasonCode":"response-unknown","automaticPasses":0,"phaseStops":[]}
            """);
        JsonObject Metadata() => new() {
            ["schemaVersion"] = 1, ["gameId"] = state["id"]!.DeepClone(), ["seatId"] = 29,
            ["revision"] = state["revision"]!.DeepClone(), ["boardRevision"] = 1,
            ["stackInterruptionVersion"] = 0, ["turn"] = 1, ["status"] = state["status"]!.DeepClone(), ["playerActive"] = true,
            ["decisionOwnerId"] = 29, ["priorityPlayerId"] = 29, ["decisionId"] = state["prompt"]?["id"]?.DeepClone(),
            ["priorityDecisionId"] = state["prompt"]?["id"]?.DeepClone(),
            ["continuation"] = state["priorityContinuation"]!.DeepClone(), ["observation"] = state["priorityObservation"]!.DeepClone()
        };
        void Reply(long id, object? value) { Console.WriteLine(JsonSerializer.Serialize(new { id, result = value })); Console.Out.Flush(); }
        long? held = null; JsonNode? heldReceipt = null;
        while (Console.ReadLine() is { } line) {
            using var parsed = JsonDocument.Parse(line);
            var request = parsed.RootElement; long id = request.GetProperty("id").GetInt64();
            string method = request.GetProperty("method").GetString()!; var args = request.GetProperty("params");
            File.AppendAllText(Path.Combine(directory, "requests.jsonl"), line + "\n");
            if (method == "automatedState") {
                Reply(id, new[] { new { seatId = 29, state, domain = new { version = 1, coverage = "Complete", sources = Array.Empty<object>(),
                    actions = Array.Empty<object>() }, semantics = new { } } }); continue;
            }
            if (method == "matchSetup") { Reply(id, new { deckId = "deck", revision = 1, maxPlayers = 4, setup = new { problem = "" } }); continue; }
            if (method == "matchStart") {
                state["id"] = "replacement-responses-wire"; state["revision"] = 1; Reply(id, state);
                if (held is { } old) { Reply(old, heldReceipt); held = null; } continue;
            }
            if (method != "automatedPriorityControl") { Reply(id, new { accepted = true }); continue; }
            string action = args.GetProperty("action").GetString()!;
            state["revision"] = state["revision"]!.GetValue<long>() + 1;
            if (action == "preparePriorityResponses") {
                if (mode is "retired" or "lost") {
                    Console.WriteLine(JsonSerializer.Serialize(new { id, error = "authored preparation failure",
                        errorCode = mode == "retired" ? "automated-priority-control-retired" : "authored-unknown" })); Console.Out.Flush(); continue;
                }
                string availability = mode is "Absent" or "Available" ? mode : "Unknown";
                state["prompt"]!["responseAvailability"] = availability;
                state["prompt"]!["canAutoPass"] = availability == "Absent";
                var receipt = new JsonObject { ["schemaVersion"] = 1, ["status"] = "prepared", ["gameId"] = state["id"]!.DeepClone(),
                    ["seatId"] = 29, ["decisionId"] = state["prompt"]!["id"]!.DeepClone(), ["boardRevision"] = 1,
                    ["publicActivitySequence"] = 0, ["responseAvailability"] = mode == "misleading" ? "Absent" : availability };
                if (mode == "late-world") { held = id; heldReceipt = receipt; File.WriteAllText(Path.Combine(directory, "held-prepare.txt"), "entered"); continue; }
                Reply(id, receipt); continue;
            }
            if (action == "acknowledgePriorityObservation") {
                state["priorityObservation"]!["registered"] = true;
                state["priorityObservation"]!["acknowledgedBoardRevision"] = 1;
            } else if (action == "grantPriorityContinuation") {
                state["priorityContinuation"] = new JsonObject { ["sequence"] = args.GetProperty("sequence").GetInt64(),
                    ["grantId"] = args.GetProperty("grantId").GetString(), ["mode"] = "NoResponse", ["status"] = "Active",
                    ["reasonCode"] = "", ["automaticPasses"] = 3, ["phaseStops"] = new JsonArray() };
            } else if (action == "revokePriorityContinuation") state["priorityContinuation"]!["status"] = "Revoked";
            Reply(id, Metadata());
        }
        return 0;
    }

    internal static async Task RunAsync(string profile, Action<bool, string> check)
    {
        Decoder(check);
        foreach (string mode in new[] { "Absent", "Available", "Unknown", "unsupported", "active", "retired", "lost", "late-world" }) {
            string directory = Path.Combine(profile, "priority-responses-wire-" + mode); Directory.CreateDirectory(directory);
            await using var engine = new ForgeEngine(new(Path.Combine(AppContext.BaseDirectory, "Mana.Table.Tests.exe"), Marker, mode, directory));
            await engine.InitializeAsync(); await engine.EnableAutomatedSeatsAsync();
            var row = (await engine.ObserveAutomatedSeatsAsync()).Single(); long epoch = engine.AutomatedPriorityEpoch;
            check(row.State.Decision?.Payload is PriorityPayload { ResponseAvailability: PriorityResponseAvailability.Unknown, CanAutoPass: false },
                "Actual automated process decoding retains the unprepared Unknown input: " + mode);
            var prepare = new AutomatedPriorityControl.PrepareResponses(row.State.Id, row.State.Decision!.Id);
            check(await engine.ExecuteAutomatedPriorityControlAsync(epoch + 1, 29, prepare) is AutomatedPriorityControlResult.Retired
                    && Count(directory, "preparePriorityResponses") == 0,
                "A foreign epoch cannot dispatch response preparation: " + mode);
            if (mode is "unsupported" or "active") {
                check(await engine.ExecuteAutomatedPriorityControlAsync(epoch, 29, prepare) is AutomatedPriorityControlResult.Retired
                        && Count(directory, "preparePriorityResponses") == 0,
                    "Missing capability or actual Held consent prevents preparation before dispatch: " + mode); continue;
            }
            check(await engine.ExecuteAutomatedPriorityControlAsync(epoch, 29,
                    prepare with { DecisionId = "another-input" }) is AutomatedPriorityControlResult.Retired,
                "Another input cannot borrow the current automated scope for preparation");
            var pending = engine.ExecuteAutomatedPriorityControlAsync(epoch, 29, prepare);
            if (mode == "late-world") {
                var timer = Stopwatch.StartNew();
                while (!File.Exists(Path.Combine(directory, "held-prepare.txt")) && timer.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(1);
                if (!File.Exists(Path.Combine(directory, "held-prepare.txt"))) throw new TimeoutException("Preparation never entered the authored child.");
                await engine.StartSoloAsync("deck", ["opponent"]);
                check(await pending is AutomatedPriorityControlResult.Retired && engine.AutomatedPriorityEpoch != epoch,
                    "A late prepared receipt cannot cross an actual provider world replacement"); continue;
            }
            if (mode == "retired") { check(await pending is AutomatedPriorityControlResult.Retired, "Only typed pre-admission retirement is a known no-effect preparation"); continue; }
            if (mode == "lost") {
                Exception? failure = null; try { await pending; } catch (Exception error) { failure = error; }
                check(failure is IOException, "A lost current-world preparation stays uncertain and never fabricates Prepared or Retired"); continue;
            }
            check(await pending is AutomatedPriorityControlResult.Prepared { Receipt: var receipt }
                    && receipt.Epoch == epoch && receipt.DecisionId == row.State.Decision.Id,
                "The production process maps only action-specific prepared metadata for its exact scope: " + mode);
            check(await engine.ExecuteAutomatedPriorityControlAsync(epoch, 29,
                    new AutomatedPriorityControl.NoResponse(row.State.Id, row.State.Decision.Id, 1, "stale-grant", [])) is AutomatedPriorityControlResult.Retired,
                "Preparation retires its old rich scope and ACK cursor before another positive instruction");
            bool oldActionRejected = false;
            try { await engine.ExecuteAutomatedAsync(29, new PassPriority(row.State.Id, row.State.Decision.Id)); }
            catch (EngineCommandRejectedException) { oldActionRejected = true; }
            check(oldActionRejected, "A Prepared receipt cannot authorize gameplay against the preceding rich read");
            var fresh = (await engine.ObserveAutomatedSeatsAsync()).Single();
            check(fresh.State.Decision?.Payload is PriorityPayload priority && priority.ResponseAvailability.ToString() == mode
                    && priority.CanAutoPass == (mode == "Absent") && fresh.State.PriorityContinuation?.Sequence == 0,
                "Only a fresh automated snapshot maps completed availability without manufacturing consent: " + mode);
        }
        foreach (string mode in new[] { "Absent", "Available", "Unknown", "misleading", "unsupported" }) {
            string directory = Path.Combine(profile, "priority-responses-host-" + mode); Directory.CreateDirectory(directory);
            await using var engine = new ForgeEngine(new(Path.Combine(AppContext.BaseDirectory, "Mana.Table.Tests.exe"), Marker, mode, directory));
            await engine.InitializeAsync(); await engine.EnableAutomatedSeatsAsync();
            await using var host = new AiTableHost(engine, _ => new PassPolicy(), 1);
            await host.StepAsync(); await host.StepAsync();
            if (mode != "unsupported") check(Count(directory, "preparePriorityResponses") == 1 && Count(directory, "grantPriorityContinuation") == 0,
                "The actual host ACKs then prepares Unknown without chaining metadata into a grant: " + mode);
            await host.StepAsync(); await host.StepAsync();
            check(host.LastFault == null && Count(directory, "preparePriorityResponses") == (mode == "unsupported" ? 0 : 1)
                    && Count(directory, "grantPriorityContinuation") == (mode == "Absent" ? 1 : 0)
                    && host.PriorityContinuations.Single().CertifiedAutomaticPasses == (mode == "Absent" ? 3 : 0),
                "The production host grants only fresh Absent and otherwise keeps ordinary policy ownership, even with an Absent metadata hint: " + mode);
        }
    }

    private sealed class PassPolicy : IAiPolicy
    {
        public AiPolicyDescriptor Descriptor { get; } = new("responses-probe", AiTasks.All, AiPriorityAutomation.NoResponse);
        public Task<AiProposal> EvaluateAsync(AiDecisionRequest request, CancellationToken cancellation)
            => Task.FromResult<AiProposal>(new AiProposal.Answer(new PassPriority(request.Frame.State.Id, request.Frame.State.Decision!.Id), "ordinary"));
    }

    private static int Count(string directory, string action) => File.ReadAllLines(Path.Combine(directory, "requests.jsonl"))
        .Count(line => { using var json = JsonDocument.Parse(line); var args = json.RootElement.GetProperty("params");
            return args.ValueKind == JsonValueKind.Object && args.TryGetProperty("action", out var value)
            && value.GetString() == action; });

    private static void Decoder(Action<bool, string> check)
    {
        const string valid = """{"schemaVersion":1,"status":"prepared","gameId":"g","seatId":1,"decisionId":"d","boardRevision":2,"publicActivitySequence":3,"responseAvailability":"Unknown"}""";
        using (var json = JsonDocument.Parse(valid)) check(ForgeAutomatedPriorityResponsesDecoder.Decode(json.RootElement, 1).ResponseAvailability == PriorityResponseAvailability.Unknown,
            "The real prepared decoder preserves explicit Unknown without an automatic-pass permission");
        foreach (var (field, value) in new[] { ("schemaVersion", "0"), ("schemaVersion", "\"1\""), ("status", "\"Prepared\""),
            ("gameId", "null"), ("gameId", "\"\""), ("seatId", "-1"), ("seatId", "\"1\""), ("decisionId", "\"\""),
            ("boardRevision", "-1"), ("publicActivitySequence", "-1"), ("responseAvailability", "0"), ("responseAvailability", "true"),
            ("responseAvailability", "null"), ("responseAvailability", "\"absent\""), ("responseAvailability", "\"AVAILABLE\"") }) {
            var node = JsonNode.Parse(valid)!; node[field] = JsonNode.Parse(value); bool rejected = false;
            try { using var json = JsonDocument.Parse(node.ToJsonString()); ForgeAutomatedPriorityResponsesDecoder.Decode(json.RootElement, 1); }
            catch (IOException) { rejected = true; }
            check(rejected, "The real prepared decoder rejects malformed closed metadata: " + field + "=" + value);
        }
        foreach (string invalid in new[] { valid.Replace("\"status\":\"prepared\",", ""), valid[..^1] + ",\"players\":[]}",
            valid[..^1] + ",\"seatId\":1}" }) {
            bool rejected = false;
            try { using var json = JsonDocument.Parse(invalid); ForgeAutomatedPriorityResponsesDecoder.Decode(json.RootElement, 1); }
            catch (IOException) { rejected = true; }
            check(rejected, "The real prepared decoder rejects missing, extra or duplicate metadata fields");
        }
    }
}
