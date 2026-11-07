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
        Directory.CreateDirectory(directory);
        var capabilities = ForgeProtocol.RequiredCapabilities.Add("automated-seats").Add("priority-continuation-v1");
        if (mode != "human-only") capabilities = capabilities.Add("automated-priority-continuation-v1");
        Console.WriteLine(JsonSerializer.Serialize(new { @event = "ready", protocolVersion = ForgeProtocol.Version, capabilities }));
        Console.Out.Flush();
        var state = JsonNode.Parse("""
            {"id":"automated-priority-wire","status":"playing","viewerId":29,"turn":1,"phaseKey":"MAIN1","activePlayerId":29,
             "revision":1,"boardRevision":1,"decisionOwnerId":29,"priorityPlayerId":29,
             "players":[{"id":29,"name":"Bot","life":40,"mana":{},"zones":[]}],"stack":[],
             "priorityContinuation":{"sequence":0,"grantId":"","mode":null,"status":"Idle","reasonCode":"","automaticPasses":0,"phaseStops":[]},
             "priorityObservation":{"boardRevision":1,"publicActivitySequence":0,"acknowledgedBoardRevision":0,
                "acknowledgedPublicActivitySequence":0,"registered":false,"overflowed":false},
             "prompt":{"id":"priority","kind":"input","task":"priority","okEnabled":true,"canAutoPass":true,
                "responseAvailability":"Absent"}}
            """)!;
        void Reply(long id, object? value) { Console.WriteLine(JsonSerializer.Serialize(new { id, result = value })); Console.Out.Flush(); }
        int enables = 0; bool empty = false; long? held = null; JsonNode? heldState = null;
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
            if (method != "automatedAction") { Reply(id, new { accepted = true }); continue; }
            string action = args.GetProperty("action").GetString()!;
            if (args.GetProperty("sessionId").GetString() != state["id"]!.GetValue<string>()
                || args.GetProperty("seatId").GetInt32() != 29) {
                Console.WriteLine(JsonSerializer.Serialize(new { id, error = "retired authored control" })); Console.Out.Flush(); continue;
            }
            state["revision"] = state["revision"]!.GetValue<long>() + 1;
            if (action == "acknowledgePriorityObservation") {
                state["priorityObservation"]!["registered"] = true;
                state["priorityObservation"]!["acknowledgedBoardRevision"] = 1;
            } else if (action == "grantPriorityContinuation") {
                if (mode == "retired") {
                    Console.WriteLine(JsonSerializer.Serialize(new { id, error = "authored pre-admission retirement",
                        errorCode = "automated-priority-control-retired" })); Console.Out.Flush(); continue;
                }
                state["priorityContinuation"]!["sequence"] = args.GetProperty("sequence").GetInt64();
                state["priorityContinuation"]!["grantId"] = args.GetProperty("grantId").GetString();
                state["priorityContinuation"]!["mode"] = "NoResponse";
                state["priorityContinuation"]!["status"] = "Active";
                if (mode == "superseded-receipt") {
                    state["priorityContinuation"]!["sequence"] = args.GetProperty("sequence").GetInt64() + 1;
                    state["priorityContinuation"]!["status"] = "Revoked";
                }
                if (mode == "post-admission-error") {
                    Console.WriteLine(JsonSerializer.Serialize(new { id, error = "authored failure after consent mutation" }));
                    Console.Out.Flush(); continue;
                }
                if (mode == "malformed-receipt") { Reply(id, new { accepted = true }); continue; }
                if (mode == "late-world") {
                    held = id; heldState = state.DeepClone();
                    File.WriteAllText(Path.Combine(directory, "held-grant.txt"), "ready"); continue;
                }
            } else if (action == "revokePriorityContinuation") {
                state["priorityContinuation"]!["sequence"] = args.GetProperty("throughSequence").GetInt64();
                state["priorityContinuation"]!["status"] = "Revoked";
            }
            Reply(id, state);
        }
        return 0;
    }

    internal static async Task RunAsync(string profile, Action<bool, string> check)
    {
        foreach (string mode in new[] { "human-only", "bot", "late-world", "retired", "superseded-receipt", "post-admission-error", "malformed-receipt" }) {
            string directory = Path.Combine(profile, "automated-priority-protocol-" + mode); Directory.CreateDirectory(directory);
            await using var engine = new ForgeEngine(new(Path.Combine(AppContext.BaseDirectory, "Mana.Table.Tests.exe"), Marker, mode, directory));
            check(!engine.SupportsAutomatedPriorityContinuation, "Bot continuation has no authority before ready negotiation: " + mode);
            await engine.InitializeAsync();
            check(engine.SupportsPriorityContinuation && engine.SupportsAutomatedPriorityContinuation == (mode != "human-only"),
                "Human continuation support cannot imply the distinct bot completed-scan/credit contract: " + mode);
            await engine.EnableAutomatedSeatsAsync();
            var row = (await engine.ObserveAutomatedSeatsAsync()).Single(); long epoch = engine.AutomatedPriorityEpoch;
            var ack = new AutomatedPriorityControl.Acknowledge(row.State.Id, 1, 0);
            if (mode == "human-only") {
                check(await engine.ExecuteAutomatedPriorityControlAsync(epoch, 29, ack) == AutomatedPriorityControlResult.Retired
                    && Count(directory, "automatedAction") == 0, "Missing bot capability rejects metadata before any RPC despite human support");
                continue;
            }
            check(await engine.ExecuteAutomatedPriorityControlAsync(epoch, 29, ack) == AutomatedPriorityControlResult.Accepted,
                "The actual provider maps a closed owned ACK through the host-only route");
            row = (await engine.ObserveAutomatedSeatsAsync()).Single();
            var grant = new AutomatedPriorityControl.NoResponse(row.State.Id, row.State.Decision!.Id, 1, "owned-grant", []);
            bool rejected = false;
            try { await engine.ExecuteAutomatedAsync(29, new GrantPriorityContinuation(row.State.Id, row.State.Decision.Id,
                1, "policy-grant", PriorityContinuationMode.NoResponse, [])); } catch (EngineCommandRejectedException) { rejected = true; }
            check(rejected && Count(directory, "automatedAction") == 1,
                "A policy gameplay port cannot acquire consent authority or add an input receipt");
            var positive = engine.ExecuteAutomatedPriorityControlAsync(epoch, 29, grant);
            if (mode == "superseded-receipt") {
                check(await positive == AutomatedPriorityControlResult.Retired,
                    "A later negative sequence cannot certify acceptance of the original affirmative grant or keep awaiting its retired ID");
            } else if (mode == "retired") {
                check(await positive == AutomatedPriorityControlResult.Retired,
                    "Only the exact backend pre-admission retirement code gives a known no-effect metadata result");
            } else if (mode is "post-admission-error" or "malformed-receipt") {
                Exception? failure = null;
                try { await positive; } catch (Exception error) { failure = error; }
                check(failure is IOException && failure is not ForgeAutomatedPriorityRetiredException,
                    "A generic or malformed post-admission receipt remains uncertain rather than granting policy fallback: " + mode);
                check(await engine.ExecuteAutomatedPriorityControlAsync(epoch, 29,
                    new AutomatedPriorityControl.Withdraw(row.State.Id, 1)) == AutomatedPriorityControlResult.Accepted,
                    "Uncertain affirmative consent retains its separate negative fence: " + mode);
            } else if (mode == "late-world") {
                var elapsed = Stopwatch.StartNew();
                while (!File.Exists(Path.Combine(directory, "held-grant.txt")) && elapsed.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(5);
                if (!File.Exists(Path.Combine(directory, "held-grant.txt"))) throw new TimeoutException("The authored grant did not reach the child.");
                await engine.StartSoloAsync("authored-deck", ["opponent"]);
                check(await positive == AutomatedPriorityControlResult.Retired && engine.AutomatedPriorityEpoch != epoch,
                    "An admitted old-world grant reply cannot become current authority after an actual provider world replacement");
                int before = Count(directory, "automatedAction");
                check(await engine.ExecuteAutomatedPriorityControlAsync(epoch, 29, grant) == AutomatedPriorityControlResult.Retired
                    && Count(directory, "automatedAction") == before, "A retired world epoch cannot send another affirmative control to its replacement");
            } else {
                check(await positive == AutomatedPriorityControlResult.Accepted, "The actual provider accepts only its current NoResponse grant");
                await engine.EnableAutomatedSeatsAsync();
                check((await engine.ObserveAutomatedSeatsAsync()).IsEmpty, "The authored discovery scope really retires before negative consent");
                check(await engine.ExecuteAutomatedPriorityControlAsync(epoch, 29, new AutomatedPriorityControl.Withdraw(row.State.Id, 1))
                    == AutomatedPriorityControlResult.Accepted, "A previously dispatched grant retains its negative fence after current discovery scopes disappear");
            }
        }
    }

    private static int Count(string directory, string method) => File.ReadAllLines(Path.Combine(directory, "requests.jsonl"))
        .Count(line => JsonDocument.Parse(line).RootElement.GetProperty("method").GetString() == method);
}
