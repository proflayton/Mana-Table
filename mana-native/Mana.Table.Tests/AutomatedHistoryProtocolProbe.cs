using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

// Actual provider process/codec/lifetime checks. Authored records are not Magic
// rules or payment qualification; the real-engine journey supplies that evidence.
internal static class AutomatedHistoryProtocolProbe
{
    internal const string Marker = "--automated-history-protocol-probe";

    internal static int Serve(string[] arguments)
    {
        int index = Array.IndexOf(arguments, Marker);
        string mode = arguments[index + 1], profile = Path.GetDirectoryName(arguments[index + 2])!;
        Directory.CreateDirectory(profile);
        var capabilities = ForgeProtocol.RequiredCapabilities.Add("automated-seats").Add("simulation-match-setup");
        if (mode != "legacy") capabilities = capabilities.Add("automated-match-history");
        bool announcements = mode.StartsWith("history-announcements-", StringComparison.Ordinal);
        if (announcements) capabilities = capabilities.Add("match-history");
        if (mode is "history-announcements-modern" or "history-announcements-downgrade") capabilities = capabilities.Add("history-announcements");
        Console.WriteLine(JsonSerializer.Serialize(new { @event = "ready", protocolVersion = ForgeProtocol.Version, capabilities }));
        Console.Out.Flush();
        string game = "configured-game";
        int starts = 0, reads = 0;
        bool closed = false;
        (long Id, string Game)? held = null;
        void Reply(long id, object result)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { id, result })); Console.Out.Flush();
        }
        string? line;
        while ((line = Console.ReadLine()) != null) {
            using var request = JsonDocument.Parse(line);
            long id = request.RootElement.GetProperty("id").GetInt64();
            string method = request.RootElement.GetProperty("method").GetString()!;
            var args = request.RootElement.GetProperty("params");
            File.AppendAllText(Path.Combine(profile, "probe-methods.txt"), method + "\n");
            if (mode == "retained" && (method == "automatedSimulationStop"
                    || method == "automatedSimulationStartConfigured" && starts > 0 || method == "matchStart")) {
                Console.WriteLine(JsonSerializer.Serialize(new { id, error = "The replacement request was rejected." }));
                Console.Out.Flush(); continue;
            }
            if (announcements && (method is "matchHistory" or "automatedMatchHistory")) {
                bool requested = args.TryGetProperty("historySchemaVersion", out var schema);
                File.AppendAllText(Path.Combine(profile, "probe-history-requests.jsonl"), JsonSerializer.Serialize(new {
                    method, hasSchemaVersion = requested, historySchemaVersion = requested ? schema.GetInt32() : (int?)null
                }) + "\n");
                int seat = method == "matchHistory" ? 0 : args.GetProperty("seatId").GetInt32();
                Reply(id, mode is "history-announcements-modern" or "history-announcements-unnegotiated"
                    ? AnnouncementBatch(game, seat) : Batch(game, seat));
                continue;
            }
            if (method == "automatedMatchHistory") {
                reads++;
                if (mode.StartsWith("late-", StringComparison.Ordinal) && reads == 1) {
                    held = (id, args.GetProperty("gameId").GetString()!); continue;
                }
                string marker = Path.Combine(profile, "first-process-closed.txt");
                if (mode == "process-change" && !File.Exists(marker)) { File.WriteAllText(marker, "closed"); return 0; }
                if (mode == "malformed") Reply(id, new { rawSnapshot = "must-not-be-history" });
                else Reply(id, Batch(mode == "foreign-game" ? "another-game" : game, mode == "foreign-seat" ? 47 : 29));
                continue;
            }
            object result;
            switch (method) {
                case "automatedSimulationStartConfigured":
                    starts++; closed = false; game = starts > 1 ? "replacement-game" : "configured-game";
                    var receipt = ConfiguredSimulationWireChecks.Receipt();
                    receipt["setup"] = JsonNode.Parse(args.GetRawText()); receipt["state"]!["id"] = game;
                    result = receipt; break;
                case "automatedState": result = closed ? new JsonArray() : Owned(game); break;
                case "automatedSimulationState":
                    var state = ConfiguredSimulationWireChecks.State(); state["id"] = game;
                    result = state; break;
                case "matchSetup":
                    result = new { deckId = args.GetProperty("deckId").GetString(), revision = 1, maxPlayers = 4,
                        setup = new { problem = "" } }; break;
                case "matchStart": game = "normal-game"; closed = false; result = new { acknowledged = true }; break;
                case "multiplayerClose":
                case "automatedSimulationStop": closed = true; result = new { stopped = true }; break;
                default: result = new { acknowledged = true }; break;
            }
            Reply(id, result);
            if (held is { } previous && (method == "automatedAction"
                    || mode == "late-close" && method == "multiplayerClose"
                    || mode == "late-normal" && method == "matchStart")) {
                Reply(previous.Id, Batch(previous.Game, 29)); held = null;
            }
        }
        return 0;
    }

    private static object Batch(string game, int seat) => new {
        schemaVersion = 1, gameId = game, viewerId = seat, firstSequence = 1, lastSequence = 1,
        nextSequence = 1, hasMore = false, records = new[] { new {
            sequence = 1, turn = 1, phaseKey = "MAIN1", exposure = "seatPrivate", executionId = "owned-payment",
            payment = new { kind = "opened", payerId = seat, origin = "manual",
                billBefore = new[] { new { symbol = "{C}", count = 1 } } }
        } }
    };

    private static object AnnouncementBatch(string game, int seat) => new {
        schemaVersion = 2, gameId = game, viewerId = seat, firstSequence = 1, lastSequence = 1,
        nextSequence = 1, hasMore = false, records = new[] { new {
            sequence = 1, turn = 1, phaseKey = "MAIN1", exposure = "public", executionId = "historical-announcement",
            announcement = new { kind = "announced", stackId = "historical-stack", targetKnowledge = "Known",
                targets = new[] { new { kind = "card", groupIndex = 0, targetIndex = 0, cardId = "historical-target",
                    name = "Departed target", zone = "Battlefield", ownerId = 1, controllerId = 2, allocatedAmount = 3 } } }
        } }
    };

    private static JsonNode Owned(string game)
    {
        var state = ConfiguredSimulationWireChecks.State(); state["viewerId"] = 29; state["id"] = game;
        state["prompt"] = JsonNode.Parse("""{"id":"history-priority","kind":"input","task":"priority","ok":"Pass","okEnabled":true}""");
        return new JsonArray(new JsonObject { ["seatId"] = 29, ["state"] = state,
            ["domain"] = JsonNode.Parse("""{"version":0,"coverage":"NotApplicable","sources":[],"actions":[]}"""),
            ["semantics"] = new JsonObject() });
    }

    private static string[] Methods(string profile)
    {
        string file = Path.Combine(profile, "probe-methods.txt");
        if (!File.Exists(file)) return [];
        using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(input);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private static async Task WaitForMethod(string profile, string method)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (!Methods(profile).Contains(method) && elapsed.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(5);
        if (!Methods(profile).Contains(method)) throw new TimeoutException("The history probe did not receive " + method);
    }

    internal static async Task RunAsync(string profile, Action<bool, string> check)
    {
        string executable = Path.Combine(AppContext.BaseDirectory, "Mana.Table.Tests.exe");
        foreach (string mode in new[] { "legacy", "ready", "foreign-seat", "foreign-game", "malformed",
                "retained", "late-simulation", "late-close", "late-normal", "process-change" }) {
            string directory = Path.Combine(profile, "automated-history-" + mode); Directory.CreateDirectory(directory);
            await using var engine = new ForgeEngine(new(executable, Marker, mode, directory));
            await engine.InitializeAsync();
            await engine.StartConfiguredSimulationAsync(ConfiguredSimulationWireChecks.Setup());
            bool refused = false;
            try { await engine.AutomatedHistoryAsync("configured-game", 29, 0); }
            catch (Exception error) when (error is EngineCommandRejectedException or NotSupportedException) { refused = true; }
            check(refused && !Methods(directory).Contains("automatedMatchHistory"),
                "History requires a negotiated port and a previously accepted owned audience before RPC: " + mode);
            var observation = (await engine.ObserveAutomatedSeatsAsync()).Single();
            if (mode == "legacy") {
                check(!engine.SupportsAutomatedHistory, "An older engine explicitly lacks the optional automated history port");
                continue;
            }
            check(engine.SupportsAutomatedHistory, "The actual ready handshake advertises automated history: " + mode);
            if (mode.StartsWith("late-", StringComparison.Ordinal)) {
                var old = engine.AutomatedHistoryAsync(observation.State.Id, observation.SeatId, 0);
                await WaitForMethod(directory, "automatedMatchHistory");
                if (mode == "late-simulation") {
                    await engine.StartConfiguredSimulationAsync(ConfiguredSimulationWireChecks.Setup());
                    observation = (await engine.ObserveAutomatedSeatsAsync()).Single();
                    await engine.ExecuteAutomatedAsync(observation.SeatId,
                        new PassPriority(observation.State.Id, observation.State.Decision!.Id));
                } else if (mode == "late-close") await engine.LeaveAsync();
                else await engine.StartSoloAsync("new-deck", ["opponent"]);
                bool stale = false;
                try { await old; } catch (IOException) { stale = true; }
                check(stale, "A delayed historical response cannot cross an accepted world replacement: " + mode);
                if (mode == "late-close") {
                    int before = Methods(directory).Count(method => method == "automatedMatchHistory");
                    bool closed = false;
                    try { await engine.AutomatedHistoryAsync(observation.State.Id, observation.SeatId, 0); }
                    catch (EngineCommandRejectedException) { closed = true; }
                    check(closed && before == Methods(directory).Count(method => method == "automatedMatchHistory"),
                        "A successful normal leave retires the former roster before another history RPC");
                    continue;
                }
                if (mode == "late-normal") {
                    bool unobserved = false;
                    try { await engine.AutomatedHistoryAsync("configured-game", 29, 0); }
                    catch (EngineCommandRejectedException) { unobserved = true; }
                    check(unobserved, "A successful normal start cannot reuse the previous automated roster");
                    observation = (await engine.ObserveAutomatedSeatsAsync()).Single();
                }
            } else if (mode == "retained") {
                bool stop = false, configured = false, normal = false;
                try { await engine.StopSimulationAsync("obsolete-game"); } catch (EngineCommandRejectedException) { stop = true; }
                try { await engine.StartConfiguredSimulationAsync(ConfiguredSimulationWireChecks.Setup()); } catch (EngineCommandRejectedException) { configured = true; }
                try { await engine.StartSoloAsync("unavailable", ["opponent"]); } catch (EngineCommandRejectedException) { normal = true; }
                await engine.ConcedeAsync(observation.State.Id);
                check(stop && configured && normal, "Rejected world replacements and a continuing host concession preserve bot ownership");
            } else if (mode == "process-change") {
                var field = typeof(ForgeEngine).GetField("current", BindingFlags.Instance | BindingFlags.NonPublic)!;
                object previousOwner = field.GetValue(engine)!;
                bool failed = false;
                try { await engine.AutomatedHistoryAsync(observation.State.Id, observation.SeatId, 0); }
                catch (IOException) { failed = true; }
                check(failed, "A closed provider cannot complete its former audience history");
                await engine.InitializeAsync();
                await engine.StartConfiguredSimulationAsync(ConfiguredSimulationWireChecks.Setup());
                observation = (await engine.ObserveAutomatedSeatsAsync()).Single();
                typeof(ForgeEngine).GetMethod("CommitAutomatedWorldReplacement", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(engine, [previousOwner, "multiplayerClose"]);
            }
            HistoryBatch? batch = null;
            bool rejected = false;
            try { batch = await engine.AutomatedHistoryAsync(observation.State.Id, observation.SeatId, 0); }
            catch (IOException) { rejected = true; }
            check(mode is "foreign-seat" or "foreign-game" or "malformed" ? rejected && batch == null
                    : !rejected && batch is { ViewerId: 29, Records.Length: 1 } && batch.GameId == observation.State.Id,
                "Automated history preserves exact process, owned audience and typed bounded ledger facts: " + mode);
            await engine.ExecuteAutomatedAsync(observation.SeatId,
                new PassPriority(observation.State.Id, observation.State.Decision!.Id));
            check(Methods(directory).Contains("automatedAction"),
                "Reading, malformed history or a late former callback cannot revoke the current gameplay scope: " + mode);
        }
        await AnnouncementNegotiation(profile, check);
    }

    private static async Task AnnouncementNegotiation(string profile, Action<bool, string> check)
    {
        string executable = Path.Combine(AppContext.BaseDirectory, "Mana.Table.Tests.exe");
        foreach (string mode in new[] { "history-announcements-modern", "history-announcements-legacy",
                "history-announcements-downgrade", "history-announcements-unnegotiated" }) {
            string directory = Path.Combine(profile, mode); Directory.CreateDirectory(directory);
            await using var engine = new ForgeEngine(new(executable, Marker, mode, directory));
            await engine.InitializeAsync();
            await engine.StartConfiguredSimulationAsync(ConfiguredSimulationWireChecks.Setup());
            var observation = (await engine.ObserveAutomatedSeatsAsync()).Single();
            HistoryBatch? ordinary = null, automated = null;
            bool ordinaryRejected = false, automatedRejected = false;
            try { ordinary = await engine.HistoryAsync(observation.State.Id, 0); } catch (IOException) { ordinaryRejected = true; }
            try { automated = await engine.AutomatedHistoryAsync(observation.State.Id, observation.SeatId, 0); } catch (IOException) { automatedRejected = true; }
            bool reject = mode == "history-announcements-unnegotiated";
            check(reject ? ordinaryRejected && automatedRejected && ordinary == null && automated == null
                    : !ordinaryRejected && !automatedRejected && ordinary is { ViewerId: 0, Records.Length: 1 }
                        && automated is { ViewerId: 29, Records.Length: 1 },
                "Both actual history ports enforce the negotiated announcement response version: " + mode);
            if (!reject) check(mode == "history-announcements-modern"
                    ? ordinary!.SchemaVersion == 2 && automated!.SchemaVersion == 2
                        && automated.Records[0].Announcement?.Targets.Single() is { CardId: "historical-target", OwnerId: 1, ControllerId: 2, AllocatedAmount: 3 }
                    : ordinary!.SchemaVersion == 1 && automated!.SchemaVersion == 1
                        && ordinary.Records[0].Announcement == null && automated.Records[0].Announcement == null,
                "Modern announcements and explicit legacy or downgrade responses preserve distinct historical knowledge: " + mode);
            var requestLines = File.ReadAllLines(Path.Combine(directory, "probe-history-requests.jsonl"));
            bool opted = mode is "history-announcements-modern" or "history-announcements-downgrade";
            var requests = requestLines.Select(line => JsonNode.Parse(line)!).ToArray();
            check(requests.Length == 2 && requests.Select(row => row["method"]!.GetValue<string>()).ToHashSet().SetEquals(["matchHistory", "automatedMatchHistory"])
                && requests.All(row => row["hasSchemaVersion"]!.GetValue<bool>() == opted
                    && (opted ? row["historySchemaVersion"]!.GetValue<int>() == 2 : row["historySchemaVersion"] == null))
                && Methods(directory).All(method => method is not ("matchAction" or "automatedAction" or "matchSourceActions" or "automatedSourceActions")),
                "The real ready handshake adds historySchemaVersion2 only when negotiated and each readonly port sends exactly once: " + mode);
            await engine.ExecuteAutomatedAsync(observation.SeatId,
                new PassPriority(observation.State.Id, observation.State.Decision!.Id));
            check(Methods(directory).Count(method => method == "automatedAction") == 1,
                "Accepted or rejected historical announcements neither grant new commands nor retire the original gameplay scope: " + mode);
        }
    }
}
