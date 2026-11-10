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
            .Add("automated-priority-continuation-v1").Add("automated-priority-metadata-v1").Add("automated-match-history");
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
        void Reject(long id) { Console.WriteLine(JsonSerializer.Serialize(new { id, error = "Authored replacement refusal." })); Console.Out.Flush(); }
        long? held = null; JsonNode? heldReceipt = null;
        (long Id, HistoryBatch Batch)? heldHistory = null;
        int observations = 0, histories = 0;
        while (Console.ReadLine() is { } line) {
            using var parsed = JsonDocument.Parse(line);
            var request = parsed.RootElement; long id = request.GetProperty("id").GetInt64();
            string method = request.GetProperty("method").GetString()!; var args = request.GetProperty("params");
            File.AppendAllText(Path.Combine(directory, "requests.jsonl"), line + "\n");
            if (method == "automatedState") {
                observations++;
                if (observations == 2 && mode == "history-hold-empty") { Reply(id, Array.Empty<object>()); continue; }
                if (observations == 2 && mode == "history-hold-malformed") { Reply(id, new[] { new { seatId = 29, broken = true } }); continue; }
                int seat = observations == 2 && mode == "history-hold-removal" ? 47 : 29;
                var audience = state.DeepClone();
                audience["viewerId"] = seat; audience["decisionOwnerId"] = seat; audience["priorityPlayerId"] = seat;
                if (seat != 29) {
                    var player = audience["players"]![0]!.DeepClone(); player["id"] = seat;
                    audience["players"]!.AsArray().Add(player);
                }
                Reply(id, new[] { new { seatId = seat, state = audience, domain = new { version = 1, coverage = "Complete", sources = Array.Empty<object>(),
                    actions = Array.Empty<object>() }, semantics = new { } } }); continue;
            }
            if (method == "automatedMatchHistory") {
                histories++;
                string processMarker = Path.Combine(directory, "history-process-ended.txt");
                if (mode == "history-process" && !File.Exists(processMarker)) { File.WriteAllText(processMarker, "entered"); return 0; }
                var batch = History(mode == "history-bad-game" ? "foreign-history-game" : state["id"]!.GetValue<string>(),
                    mode == "history-bad-viewer" ? 47 : 29);
                if (mode.StartsWith("history-hold-", StringComparison.Ordinal) && histories == 1) {
                    heldHistory = (id, batch); File.WriteAllText(Path.Combine(directory, "held-history.txt"), "entered"); continue;
                }
                Reply(id, batch);
                if (heldHistory is { } previous) { Reply(previous.Id, previous.Batch); heldHistory = null; }
                continue;
            }
            if (method == "matchSetup") { Reply(id, new { deckId = "deck", revision = 1, maxPlayers = 4, setup = new { problem = "" } }); continue; }
            if (method == "matchStart") {
                if (mode == "history-retained") { Reject(id); continue; }
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
            check(await engine.ExecuteAutomatedPriorityControlAsync(epoch, 29, new AutomatedPriorityControl.Acknowledge(row.State.Id, 1, 0))
                    is AutomatedPriorityControlResult.Accepted,
                "The production adapter retains an actual ACK cursor before response preparation: " + mode);
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
            var history = await engine.AutomatedHistoryAsync(row.State.Id, 29, 0);
            check(history.GameId == row.State.Id && history.ViewerId == 29 && history.Records.Single().Payment?.PayerId == 29
                    && MethodCount(directory, "automatedState") == 1,
                "Preparing controller facts retains the owned private history audience before any fresh rich observation: " + mode);
            check(await Rejected(() => engine.ExecuteAutomatedAsync(29, new PassPriority(row.State.Id, row.State.Decision.Id)))
                    && await engine.ExecuteAutomatedPriorityControlAsync(epoch, 29,
                        new AutomatedPriorityControl.NoResponse(row.State.Id, row.State.Decision.Id, 1, "history-cannot-grant", []))
                        is AutomatedPriorityControlResult.Retired,
                "Successful historical reading cannot mint a new gameplay scope or restore the retired positive-control cursor: " + mode);
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
        await HistoryAudienceLifetimes(profile, check);
    }

    private static HistoryBatch History(string game, int seat) => new(HistoryFacts.LegacySchemaVersion, game, seat, 1, 1, 1, false,
        [new HistoryRecord(1, 1, "UPKEEP", "seatPrivate") { ExecutionId = "owned-payment",
            Payment = new("opened", seat, "manual") { BillBefore = [new("{C}", 1)] } }]);

    private static async Task HistoryAudienceLifetimes(string profile, Action<bool, string> check)
    {
        foreach (string mode in new[] { "history-hold-refresh", "history-hold-prepare", "history-hold-empty", "history-hold-malformed",
                "history-hold-removal", "history-hold-world", "history-process", "history-retained",
                "history-bad-game", "history-bad-viewer", "history-bad-cursor" }) {
            string directory = Path.Combine(profile, mode); Directory.CreateDirectory(directory);
            await using var engine = new ForgeEngine(new(Path.Combine(AppContext.BaseDirectory, "Mana.Table.Tests.exe"), Marker, mode, directory));
            await engine.InitializeAsync(); await engine.EnableAutomatedSeatsAsync();
            check(await Rejected(() => engine.AutomatedHistoryAsync("responses-wire", 29, 0))
                    && MethodCount(directory, "automatedMatchHistory") == 0,
                "An unobserved automated audience cannot acquire historical reading permission: " + mode);
            var row = (await engine.ObserveAutomatedSeatsAsync()).Single();
            check(await Rejected(() => engine.AutomatedHistoryAsync(row.State.Id, 47, 0))
                    && await Rejected(() => engine.AutomatedHistoryAsync("foreign-game", 29, 0))
                    && MethodCount(directory, "automatedMatchHistory") == 0,
                "Observed bot history cannot substitute another game or unowned viewer before RPC: " + mode);
            if (mode.StartsWith("history-bad-", StringComparison.Ordinal)) {
                check(await Rejected(() => engine.AutomatedHistoryAsync(row.State.Id, 29, mode == "history-bad-cursor" ? 2 : 0)),
                    "The actual history decoder rejects a mismatched game, viewer or source cursor: " + mode);
                await engine.ExecuteAutomatedAsync(29, new PassPriority(row.State.Id, row.State.Decision!.Id));
                check(MethodCount(directory, "automatedAction") == 1,
                    "Malformed historical reading cannot revoke an independent current gameplay scope: " + mode);
                continue;
            }
            if (mode == "history-retained") {
                check(await Rejected(() => engine.StartSoloAsync("deck", ["opponent"])), "The authored replacement really fails admission");
                await engine.ConcedeAsync(row.State.Id);
                var batch = await engine.AutomatedHistoryAsync(row.State.Id, 29, 0);
                check(batch.GameId == row.State.Id && batch.ViewerId == 29,
                    "A rejected replacement and concession admission preserve the still-owned historical audience");
                continue;
            }
            var held = engine.AutomatedHistoryAsync(row.State.Id, 29, 0);
            if (mode == "history-process") {
                check(await Rejected(() => held), "A process ending during a history request cannot certify a private batch");
                await engine.InitializeAsync(); await engine.EnableAutomatedSeatsAsync();
                int before = MethodCount(directory, "automatedMatchHistory");
                check(await Rejected(() => engine.AutomatedHistoryAsync(row.State.Id, 29, 0))
                        && MethodCount(directory, "automatedMatchHistory") == before,
                    "A replacement process cannot borrow the former process's audience before its own accepted observation");
                var replacement = (await engine.ObserveAutomatedSeatsAsync()).Single();
                check((await engine.AutomatedHistoryAsync(replacement.State.Id, 29, 0)).ViewerId == 29,
                    "A replacement process admits its own newly observed private audience");
                continue;
            }
            await WaitForFile(Path.Combine(directory, "held-history.txt"));
            bool survives = mode is "history-hold-refresh" or "history-hold-prepare";
            if (mode == "history-hold-prepare") {
                long epoch = engine.AutomatedPriorityEpoch;
                await engine.ExecuteAutomatedPriorityControlAsync(epoch, 29, new AutomatedPriorityControl.Acknowledge(row.State.Id, 1, 0));
                check(await engine.ExecuteAutomatedPriorityControlAsync(epoch, 29,
                        new AutomatedPriorityControl.PrepareResponses(row.State.Id, row.State.Decision!.Id)) is AutomatedPriorityControlResult.Prepared,
                    "The held historical read overlaps actual response preparation which retires its old gameplay scope");
                check(await Rejected(() => engine.ExecuteAutomatedAsync(29, new PassPriority(row.State.Id, row.State.Decision!.Id)))
                        && await engine.ExecuteAutomatedPriorityControlAsync(epoch, 29,
                            new AutomatedPriorityControl.NoResponse(row.State.Id, row.State.Decision.Id, 1, "old-history-grant", []))
                            is AutomatedPriorityControlResult.Retired,
                    "A historical audience lease cannot revive the retired gameplay scope or preceding ACK grant cursor");
            } else if (mode == "history-hold-world") {
                await engine.StartSoloAsync("deck", ["opponent"]);
                check(await Rejected(() => engine.AutomatedHistoryAsync(row.State.Id, 29, 0)),
                    "A successful world replacement immediately retires its former private audience");
                row = (await engine.ObserveAutomatedSeatsAsync()).Single();
            } else {
                if (mode == "history-hold-malformed")
                    check(await Rejected(async () => { await engine.ObserveAutomatedSeatsAsync(); }),
                        "The intervening malformed observation is rejected before any new audience admission");
                else {
                    var intervening = await engine.ObserveAutomatedSeatsAsync();
                    check(mode == "history-hold-refresh" ? intervening.Single().SeatId == 29
                            : mode == "history-hold-empty" ? intervening.IsEmpty : intervening.Single().SeatId == 47,
                        "The actual adapter accepts the intended refreshed, empty or changed audience roster: " + mode);
                }
                if (!survives) {
                    int before = MethodCount(directory, "automatedMatchHistory");
                    check(await Rejected(() => engine.AutomatedHistoryAsync(row.State.Id, 29, 0))
                            && MethodCount(directory, "automatedMatchHistory") == before,
                        "A removed or rejected audience cannot read history while its prior reply is pending: " + mode);
                    var reappeared = (await engine.ObserveAutomatedSeatsAsync()).Single();
                    check(reappeared.State.Id == row.State.Id && reappeared.SeatId == row.SeatId,
                        "The same game/viewer pair actually reappears before the old historical reply is released: " + mode);
                }
            }
            // A second independently admitted read releases the delayed former
            // reply only after the intervening operation has fully returned.
            var current = await engine.AutomatedHistoryAsync(row.State.Id, 29, 0);
            check(current.GameId == row.State.Id && current.ViewerId == 29,
                "The current accepted audience retains its own historical reading permission: " + mode);
            if (survives) check((await held).ViewerId == 29,
                "The same audience's in-flight history survives ordinary refresh and action-only retirement: " + mode);
            else check(await Rejected(() => held),
                "A retired audience lease cannot be revived by reappearing game/seat strings or a replacement world: " + mode);
            if (mode == "history-hold-prepare") check(await Rejected(() => engine.ExecuteAutomatedAsync(29,
                    new PassPriority(row.State.Id, row.State.Decision!.Id))) && MethodCount(directory, "automatedState") == 1,
                "A reply accepted after preparation retains only its historical audience, with no fresh-read or gameplay authority");
        }
    }

    private static async Task<bool> Rejected(Func<Task> action)
    {
        try { await action(); return false; }
        catch (Exception error) when (error is EngineCommandRejectedException or IOException) { return true; }
    }

    private static async Task WaitForFile(string path)
    {
        var timer = Stopwatch.StartNew();
        while (!File.Exists(path) && timer.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(1);
        if (!File.Exists(path)) throw new TimeoutException("The authored history request never entered the child process.");
    }

    private sealed class PassPolicy : IAiPolicy
    {
        public AiPolicyDescriptor Descriptor { get; } = new("responses-probe", AiTasks.All, AiPriorityAutomation.NoResponse);
        public Task<AiProposal> EvaluateAsync(AiDecisionRequest request, CancellationToken cancellation)
            => Task.FromResult<AiProposal>(new AiProposal.Answer(new PassPriority(request.Frame.State.Id, request.Frame.State.Decision!.Id), "ordinary"));
    }

    private static string[] Requests(string directory)
    {
        string path = Path.Combine(directory, "requests.jsonl");
        if (!File.Exists(path)) return [];
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(file);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }
    private static int MethodCount(string directory, string method) => Requests(directory)
        .Count(line => { using var json = JsonDocument.Parse(line); return json.RootElement.GetProperty("method").GetString() == method; });
    private static int Count(string directory, string action) => Requests(directory)
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
