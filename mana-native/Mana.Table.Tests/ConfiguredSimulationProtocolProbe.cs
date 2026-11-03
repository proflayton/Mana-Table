using System.Text.Json;
using System.Text.Json.Nodes;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

// Static publications exercise the actual process codec/lifecycle, not rules or
// AI choices. Authoritative configured games have a separate real-engine journey.
internal static class ConfiguredSimulationProtocolProbe
{
    internal const string Marker = "--configured-simulation-protocol-probe";

    internal static int Serve(string[] arguments)
    {
        int marker = Array.IndexOf(arguments, Marker);
        string mode = arguments[marker + 1], profile = Path.GetDirectoryName(arguments[marker + 2])!;
        Directory.CreateDirectory(profile);
        var capabilities = ForgeProtocol.RequiredCapabilities.Add("automated-seats");
        if (mode != "legacy") capabilities = capabilities.Add("simulation-match-setup");
        Console.WriteLine(JsonSerializer.Serialize(new { @event = "ready", protocolVersion = ForgeProtocol.Version, capabilities }));
        Console.Out.Flush();
        int starts = 0;
        bool rejectedLifecycle = false;
        long? heldObservation = null;
        string? line;
        while ((line = Console.ReadLine()) != null) {
            using var request = JsonDocument.Parse(line);
            string method = request.RootElement.GetProperty("method").GetString()!;
            long requestId = request.RootElement.GetProperty("id").GetInt64();
            File.AppendAllText(Path.Combine(profile, "probe-methods.txt"), method + "\n");
            if (method == "automatedSimulationStartConfigured") starts++;
            if ((mode is "late-observation" or "late-superseded") && method == "automatedState" && starts == 1) {
                heldObservation = requestId;
                continue;
            }
            if ((mode is "rejected-stop" or "retained-scope") && method == "automatedSimulationStop"
                || (mode is "rejected-start" or "retained-scope") && method == "automatedSimulationStartConfigured" && starts > 1) {
                rejectedLifecycle = true;
                Console.WriteLine(JsonSerializer.Serialize(new { id = request.RootElement.GetProperty("id").GetInt64(), error = "The prior scope or saved deck reference is unavailable." }));
                Console.Out.Flush();
                continue;
            }
            if (mode == "closed-owner" && method == "automatedSimulationState") return 0;
            object result;
            if (method == "automatedSimulationStartConfigured") {
                var receipt = ConfiguredSimulationWireChecks.Receipt(starting: mode is not ("retained-scope" or "late-observation" or "late-superseded" or "full-starting"));
                receipt["setup"] = JsonNode.Parse(request.RootElement.GetProperty("params").GetRawText());
                if (mode == "full-starting") receipt["state"]!["status"] = "starting";
                if ((mode is "late-observation" or "late-superseded") && starts > 1) receipt["state"]!["id"] = "replacement-game";
                if (mode == "changed-plan") receipt["setup"]!["decks"]![0] = "replacement";
                result = receipt;
            } else if (method is "automatedSimulationState" or "automatedSimulationStart") {
                var state = ConfiguredSimulationWireChecks.State(starting: mode == "superseded"
                    || (mode is "rejected-stop" or "rejected-start") && !rejectedLifecycle);
                if (mode == "legacy") foreach (var player in state["players"]!.AsArray()) player!["teamId"] = player["id"]!.DeepClone();
                if (mode == "foreign-game") state["id"] = "replacement-game";
                if (mode == "changed-team") state["players"]![0]!["teamId"] = 17;
                result = state;
            } else if (method == "automatedState") {
                result = Owned((mode is "late-observation" or "late-superseded") && starts > 1 ? "replacement-game" : "configured-game");
            } else result = new { acknowledged = true };
            Console.WriteLine(JsonSerializer.Serialize(new { id = request.RootElement.GetProperty("id").GetInt64(), result }));
            if (method == "automatedAction" && heldObservation is { } held) {
                if (mode == "late-superseded") Console.WriteLine(JsonSerializer.Serialize(new {
                    id = held, error = "The former observation was superseded.", errorCode = "observation-superseded" }));
                else Console.WriteLine(JsonSerializer.Serialize(new { id = held, result = Owned("configured-game") }));
                heldObservation = null;
            }
            Console.Out.Flush();
        }
        return 0;
    }

    private static JsonNode Owned(string game)
    {
        var owned = ConfiguredSimulationWireChecks.State(); owned["viewerId"] = 29; owned["id"] = game;
        owned["prompt"] = JsonNode.Parse("""{"id":"retained-priority","kind":"input","task":"priority","ok":"Pass","okEnabled":true}""");
        return new JsonArray(new JsonObject { ["seatId"] = 29, ["state"] = owned,
            ["domain"] = JsonNode.Parse("""{"version":0,"coverage":"NotApplicable","sources":[],"actions":[]}"""),
            ["semantics"] = new JsonObject() });
    }

    private static string[] Methods(string profile)
    {
        string file = Path.Combine(profile, "probe-methods.txt");
        if (!File.Exists(file)) return [];
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    internal static async Task RunAsync(string profile, Action<bool, string> check)
    {
        string executable = Path.Combine(AppContext.BaseDirectory, "Mana.Table.Tests.exe");
        foreach (string mode in new[] { "legacy", "ready", "full-starting", "changed-plan", "foreign-game", "changed-team", "closed-owner", "superseded", "rejected-stop", "rejected-start", "retained-scope", "late-observation", "late-superseded" }) {
            string path = Path.Combine(profile, "configured-protocol-" + mode); Directory.CreateDirectory(path);
            bool rejected = false; string? rejectionDetail = null; ConfiguredSimulationStart? accepted = null;
            await using (var engine = new ForgeEngine(new(executable, Marker, mode, path))) {
                await engine.InitializeAsync();
                try {
                    var start = engine.StartConfiguredSimulationAsync(ConfiguredSimulationWireChecks.Setup());
                    if (mode is "superseded" or "rejected-stop" or "rejected-start") {
                        var deadline = System.Diagnostics.Stopwatch.StartNew();
                        while (!Methods(path).Contains("automatedSimulationStartConfigured")
                            && deadline.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(5);
                        if (mode == "superseded") await engine.StopSimulationAsync("configured-game");
                        else {
                            bool lifecycleRejected = false;
                            try {
                                if (mode == "rejected-stop") await engine.StopSimulationAsync("obsolete-game");
                                else await engine.StartConfiguredSimulationAsync(ConfiguredSimulationWireChecks.Setup() with { Decks = ["unavailable-saved-token", "b", "c", "d"] });
                            } catch (EngineCommandRejectedException) { lifecycleRejected = true; }
                            check(lifecycleRejected, "The provider explicitly rejected the stale lifecycle request: " + mode);
                        }
                    }
                    accepted = await start;
                    if (mode == "retained-scope") {
                        var prior = (await engine.ObserveAutomatedSeatsAsync()).Single();
                        bool stopRejected = false, replacementRejected = false;
                        try { await engine.StopSimulationAsync("obsolete-game"); } catch (EngineCommandRejectedException) { stopRejected = true; }
                        try { await engine.StartConfiguredSimulationAsync(ConfiguredSimulationWireChecks.Setup() with { Decks = ["unavailable-saved-token", "b", "c", "d"] }); }
                        catch (EngineCommandRejectedException) { replacementRejected = true; }
                        await engine.ExecuteAutomatedAsync(prior.SeatId, new PassPriority(prior.State.Id, prior.State.Decision!.Id));
                        check(stopRejected && replacementRejected,
                            "Rejected scoped Stop and configured replacement preserve the prior accepted automated action scope");
                    }
                    if (mode is "late-observation" or "late-superseded") {
                        var old = engine.ObserveAutomatedSeatsAsync();
                        var deadline = System.Diagnostics.Stopwatch.StartNew();
                        while (!Methods(path).Contains("automatedState")
                                && deadline.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(5);
                        accepted = await engine.StartConfiguredSimulationAsync(ConfiguredSimulationWireChecks.Setup());
                        var current = (await engine.ObserveAutomatedSeatsAsync()).Single();
                        await engine.ExecuteAutomatedAsync(current.SeatId, new PassPriority(current.State.Id, current.State.Decision!.Id));
                        bool oldRejected = false;
                        try { await old; } catch (Exception error) when (error is IOException or EngineObservationSupersededException) { oldRejected = true; }
                        await engine.ExecuteAutomatedAsync(current.SeatId, new PassPriority(current.State.Id, current.State.Decision!.Id));
                        check(oldRejected, "A delayed former observation cannot replace or erase the new game's accepted action scope: " + mode);
                    }
                } catch (Exception error) when (error is NotSupportedException or IOException) { rejected = true; rejectionDetail = error.ToString(); }
                if (mode == "legacy") {
                    var legacy = await engine.StartSimulationAsync(["a", "b", "c", "d"]);
                    check(legacy.Id == "configured-game" && !engine.SupportsConfiguredSimulation,
                        "An older provider keeps its legacy FFA start without claiming configured support");
                }
            }
            var methods = Methods(path);
            File.WriteAllText(Path.Combine(path, "probe-client-result.json"), JsonSerializer.Serialize(new {
                mode, rejected, rejectionDetail, acceptedGame = accepted?.State.Id, methods }));
            check(mode is "ready" or "full-starting" or "rejected-stop" or "rejected-start" or "retained-scope" or "late-observation" or "late-superseded" ? !rejected && accepted is { State.Players.Length: 4 }
                    && (mode is "retained-scope" or "late-observation" or "late-superseded" || methods.Count(method => method == "automatedSimulationState") >= 1)
                : rejected && (mode != "legacy" || !methods.Contains("automatedSimulationStartConfigured")),
                "Actual configured adapter startup preserves capability/plan/game/team/process scope and waits for real roster: " + mode);
            check(mode == "retained-scope" ? methods.Count(method => method == "automatedState") == 1
                    && methods.Count(method => method == "automatedAction") == 1
                : mode is "late-observation" or "late-superseded" ? methods.Count(method => method == "automatedState") == 2
                    && methods.Count(method => method == "automatedAction") == 2
                : !methods.Any(method => method is "automatedState" or "automatedAction"),
                "No configured startup issues policy observations or answers before its acceptance boundary: " + mode);
            if (mode is "changed-plan" or "foreign-game" or "changed-team")
                check(File.Exists(Path.Combine(path, "diagnostics", "private-rejected-configured-simulation.json")),
                    "Rejected configured startup retains private raw failure evidence: " + mode);
        }
    }
}
