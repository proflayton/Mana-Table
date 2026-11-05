using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

// Actual child pipes and codec with authored envelopes. This deliberately
// does not claim Java TCP timing, rules simulation or human UX acceptance.
internal static class TablePresenceWireChecks
{
    internal const string Marker = "--table-presence-wire-probe";
    private const string Game = "presence-wire-game";
    internal static int Serve(string[] arguments)
    {
        int index = Array.IndexOf(arguments, Marker);
        string mode = arguments[index + 1], directory = Path.GetDirectoryName(arguments[index + 2])!;
        Directory.CreateDirectory(directory);
        var capabilities = ForgeProtocol.RequiredCapabilities;
        if (mode != "legacy") capabilities = capabilities.Add("table-presence");
        Console.WriteLine(JsonSerializer.Serialize(new { @event = "ready", protocolVersion = ForgeProtocol.Version, capabilities }));
        Console.Out.Flush();
        int reads = 0, presenceReads = 0;
        long? held = null;
        void Reply(long id, object? result) { Console.WriteLine(JsonSerializer.Serialize(new { id, result })); Console.Out.Flush(); }
        string? line;
        while ((line = Console.ReadLine()) != null) {
            using var document = JsonDocument.Parse(line);
            var request = document.RootElement; long id = request.GetProperty("id").GetInt64();
            string method = request.GetProperty("method").GetString()!;
            File.AppendAllText(Path.Combine(directory, "methods.txt"), method + "\n");
            if (method == "matchState") {
                Reply(id, State(mode == "held-audience" && ++reads == 2 ? 47 : 29));
                if (mode == "held-audience" && reads == 3 && held is { } old) { Reply(old, Frame()); held = null; }
            } else if (method == "tablePresence") {
                File.WriteAllText(Path.Combine(directory, "presence-request.json"), request.GetProperty("params").GetRawText());
                if (++presenceReads == 1 && mode.StartsWith("held-", StringComparison.Ordinal)) {
                    held = id; File.WriteAllText(Path.Combine(directory, "held.txt"), "held"); continue;
                }
                if (mode == "relay-unsupported") Reply(id, null);
                else if (mode == "retired-host" && presenceReads == 1) {
                    Console.WriteLine(JsonSerializer.Serialize(new { id, error = "This table inspection has changed.", errorCode = "table-presence-superseded" })); Console.Out.Flush();
                }
                else if (mode == "failure") {
                    Console.WriteLine(JsonSerializer.Serialize(new { id, error = "Controlled presence failure" })); Console.Out.Flush();
                } else Reply(id, Frame());
            } else {
                Reply(id, new { accepted = true });
                if (method == "matchAction" && held is { } old) { Reply(old, Frame()); held = null; }
            }
        }
        return 0;
    }

    private static JsonNode State(int viewer)
    {
        var state = ConfiguredSimulationWireChecks.State();
        state["id"] = Game; state["viewerId"] = viewer; state["revision"] = 10; state["boardRevision"] = 10;
        state["turn"] = 1; state["phaseKey"] = "MAIN1"; state["activePlayerId"] = viewer;
        state["priorityPlayerId"] = viewer; state["decisionOwnerId"] = viewer; state["actionClass"] = "priority";
        state["publicInspection"] = new JsonObject { ["id"] = "public-context", ["boardRevision"] = 10, ["playerId"] = viewer };
        state["prompt"] = new JsonObject { ["id"] = "priority", ["kind"] = "input", ["task"] = "priority", ["okEnabled"] = true };
        return state;
    }
    private static JsonNode Frame() => JsonNode.Parse("""
        {"gameId":"presence-wire-game","boardRevision":10,"revision":1,"cues":[
        {"playerId":29,"contextId":"public-context","subject":{"kind":"card","id":"public-card"},"remainingMilliseconds":1500}]}
        """)!;

    internal static async Task RunAsync(string profile, Action<bool, string> check)
    {
        CheckSnapshotContext(check);
        var valid = Frame();
        check(ForgeEngine.DecodeTablePresence(JsonSerializer.SerializeToElement(valid), Game).Cues.Single().Subject.Kind
            == TableInspectionKind.BattlefieldCard, "Public presence has one closed battlefield subject kind");
        var stack = valid.DeepClone(); stack["cues"]![0]!["subject"]!["kind"] = "stack";
        check(ForgeEngine.DecodeTablePresence(JsonSerializer.SerializeToElement(stack), Game).Cues.Single().Subject.Kind
            == TableInspectionKind.StackItem, "Actual Java stack-kind wire values decode to the closed stack subject");
        var coded = JsonSerializer.SerializeToElement(new { error = "Retired inspection", errorCode = "table-presence-superseded" });
        check(ForgeEngine.RequestFailure("tablePresence", coded) is EngineObservationSupersededException
            && ForgeEngine.RequestFailure("matchAction", coded) is EngineCommandRejectedException,
            "Only the optional presence method classifies its retirement code as superseded");
        foreach (Action<JsonNode> corrupt in new Action<JsonNode>[] {
            node => node["cues"]![0]!["subject"]!["kind"] = "hand",
            node => node["cues"]![0]!["subject"]!["kind"] = 0,
            node => ((JsonObject)node["cues"]![0]!["subject"]!).Remove("kind"),
            node => ((JsonObject)node["cues"]![0]!).Remove("playerId"),
            node => node["cues"]![0]!["subject"]!["id"] = new string('x', 129),
            node => node["cues"]![0]!["remainingMilliseconds"] = 1501,
            node => node["boardRevision"] = -1,
            node => node["gameId"] = "other-game",
            node => ((JsonArray)node["cues"]!).Add(node["cues"]![0]!.DeepClone())
        }) {
            var invalid = valid.DeepClone(); corrupt(invalid);
            bool rejected = false;
            try { ForgeEngine.DecodeTablePresence(JsonSerializer.SerializeToElement(invalid), Game); }
            catch (IOException) { rejected = true; }
            check(rejected, "Malformed or unbounded public activity cannot become an inspection cue");
        }
        foreach (string mode in new[] { "legacy", "relay-unsupported", "ready", "failure", "retired-host", "held-cancel", "held-audience" }) {
            string directory = Path.Combine(profile, "presence-wire-" + mode); Directory.CreateDirectory(directory);
            if (Directory.EnumerateFiles(directory).Any()) throw new IOException("Presence wire checks need a fresh disposable profile.");
            var executable = Path.Combine(AppContext.BaseDirectory, "Mana.Table.Tests.exe");
            await using var engine = new ForgeEngine(new(executable, Marker, mode, directory));
            await engine.InitializeAsync();
            var state = await engine.ObserveAsync() ?? throw new InvalidOperationException("Missing initial presence state");
            var update = new TablePresenceUpdate(Game, 10, 1, "public-context", new(TableInspectionKind.BattlefieldCard, "public-card"));
            if (mode == "legacy") {
                check(!engine.SupportsTablePresence && await engine.ExchangePresenceAsync(update) is TablePresenceResult.Unsupported && Count(directory, "tablePresence") == 0,
                    "An older local adapter receives no optional presence request"); continue;
            }
            if (mode == "relay-unsupported") {
                check(await engine.ExchangePresenceAsync(update) is TablePresenceResult.Unsupported && !engine.SupportsTablePresence
                    && await engine.ExchangePresenceAsync(update with { Sequence = 2 }) is TablePresenceResult.Unsupported && Count(directory, "tablePresence") == 1,
                    "An older remote host disables presence quietly despite the local ready capability");
            } else if (mode == "retired-host") {
                check(await engine.ExchangePresenceAsync(update) is TablePresenceResult.Superseded && engine.SupportsTablePresence
                    && await engine.ExchangePresenceAsync(update with { Sequence = 2 }) is TablePresenceResult.Current,
                    "A new host's retired match/epoch response stays supported and admits a fresh current exchange");
            } else if (mode == "failure") {
                bool failed = false;
                try { await engine.ExchangePresenceAsync(update); } catch (EngineCommandRejectedException) { failed = true; }
                check(failed, "A presence rejection remains an optional activity failure");
            } else if (mode.StartsWith("held-", StringComparison.Ordinal)) {
                using var cancellation = new CancellationTokenSource();
                var pending = engine.ExchangePresenceAsync(update, cancellation.Token);
                await WaitFor(directory, "held.txt");
                if (mode == "held-cancel") {
                    cancellation.Cancel();
                    await engine.ExecuteAsync(new PassPriority(Game, state.Decision!.Id));
                    bool retired = false;
                    try { await pending; } catch (OperationCanceledException) { retired = true; }
                    check(retired && Count(directory, "matchAction") == 1,
                        "Retiring presence never cancels or poisons the shared pipe's explicit rules command");
                } else {
                    var middle = await engine.ObserveAsync(); var returned = await engine.ObserveAsync();
                    check(middle?.ViewerId == 47 && returned?.ViewerId == 29 && await pending is TablePresenceResult.Superseded
                        && engine.SupportsTablePresence,
                        "Audience A-to-B-to-identical-A invalidates the old presence response");
                    check(await engine.ExchangePresenceAsync(update with { Sequence = 2 }) is TablePresenceResult.Current,
                        "A superseded response cannot disable a subsequent current exchange in the same game");
                }
            } else {
                var result = await engine.ExchangePresenceAsync(update);
                using var request = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "presence-request.json")));
                check(result is TablePresenceResult.Current { Frame.Cues.Length: 1 } frame
                    && frame.Frame.Cues.Single().Subject.Id == "public-card" && request.RootElement.GetProperty("subject").GetProperty("kind").GetString() == "card"
                    && !request.RootElement.TryGetProperty("playerId", out _) && !request.RootElement.TryGetProperty("promptId", out _),
                    "The actual provider wire carries a closed public subject without actor claims or action scope");
            }
            var fresh = await engine.ObserveAsync() ?? throw new InvalidOperationException();
            await engine.ExecuteAsync(new PassPriority(Game, fresh.Decision!.Id));
            check(Count(directory, "matchAction") >= 1, "Rules commands remain usable after the optional presence journey: " + mode);
        }
    }

    private static void CheckSnapshotContext(Action<bool, string> check)
    {
        var valid = State(29);
        var issued = ForgeSnapshotDecoder.Decode(JsonSerializer.SerializeToElement(valid))!;
        check(PublicInspectionFacts.IsCurrent(issued), "The ordinary-priority snapshot retains its valid public inspection context");
        var older = valid.DeepClone(); older["publicInspection"] = null;
        var legacy = ForgeSnapshotDecoder.Decode(JsonSerializer.SerializeToElement(older))!;
        check(PublicInspectionFacts.Violation(legacy) == PublicInspectionViolation.None && !PublicInspectionFacts.IsCurrent(legacy),
            "An older host's omitted public context remains a valid snapshot without granting shared inspection");
        var observer = valid.DeepClone(); observer["viewerId"] = 47; observer["prompt"] = null;
        check(PublicInspectionFacts.IsCurrent(ForgeSnapshotDecoder.Decode(JsonSerializer.SerializeToElement(observer))!),
            "A foreign viewing seat may retain the current public priority context without borrowing its private prompt");
        foreach (var (reason, corrupt) in new (PublicInspectionViolation, Action<JsonNode>)[] {
            (PublicInspectionViolation.InvalidContextId, node => node["publicInspection"]!["id"] = "PRIVATE-NONCE-CANARY\n"),
            (PublicInspectionViolation.InvalidContextId, node => node["publicInspection"]!["id"] = new string('x', 129)),
            (PublicInspectionViolation.BoardRevisionMismatch, node => node["publicInspection"]!["boardRevision"] = 11),
            (PublicInspectionViolation.UnknownPlayer, node => node["publicInspection"]!["playerId"] = 999),
            (PublicInspectionViolation.DecisionOwnerMismatch, node => { node["prompt"] = null; node["decisionOwnerId"] = 47; }),
            (PublicInspectionViolation.PriorityPlayerMismatch, node => { node["prompt"] = null; node["priorityPlayerId"] = 47; }),
            (PublicInspectionViolation.NonPriorityAction, node => node["actionClass"] = "payment"),
            (PublicInspectionViolation.NotPlaying, node => { node["prompt"] = null; node["status"] = "resolving"; }),
            (PublicInspectionViolation.MissingLocalPriority, node => node["prompt"] = null)
        }) {
            var invalid = valid.DeepClone(); corrupt(invalid);
            string? message = null;
            try { ForgeSnapshotDecoder.Decode(JsonSerializer.SerializeToElement(invalid)); }
            catch (IOException error) { message = error.Message; }
            check(message == "The rules provider published an invalid match snapshot (public inspection: " + reason + ").",
                "Invalid public context retains only its fixed closed violation reason: " + reason);
        }
    }
    private static int Count(string directory, string method) => File.Exists(Path.Combine(directory, "methods.txt"))
        ? File.ReadAllLines(Path.Combine(directory, "methods.txt")).Count(value => value == method) : 0;
    private static async Task WaitFor(string directory, string name)
    {
        var elapsed = Stopwatch.StartNew();
        while (!File.Exists(Path.Combine(directory, name))) {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Presence probe did not reach its held response.");
            await Task.Delay(5);
        }
    }
}
