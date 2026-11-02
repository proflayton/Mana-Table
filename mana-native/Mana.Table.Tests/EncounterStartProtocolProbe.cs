using System.Text.Json;
using System.Text.Json.Nodes;
using Mana.Forge;

namespace Mana.Table;

// Test-only process transport: published lobby facts and recorded RPC methods.
// No game state, rules answer or simulated match is supplied by this probe.
internal static class EncounterStartProtocolProbe
{
    internal const string Marker = "--encounter-start-protocol-probe";

    internal static int Serve(string[] arguments)
    {
        int marker = Array.IndexOf(arguments, Marker);
        string mode = arguments[marker + 1], profile = Path.GetDirectoryName(arguments[marker + 2])!;
        Directory.CreateDirectory(profile);
        var capabilities = ForgeProtocol.RequiredCapabilities.Add("match-setup");
        if (mode == "process-only") capabilities = capabilities.Add("durable-encounter-admission");
        Console.WriteLine(JsonSerializer.Serialize(new { @event = "ready", protocolVersion = ForgeProtocol.Version, capabilities }));
        Console.Out.Flush();
        string? line;
        while ((line = Console.ReadLine()) != null) {
            using var request = JsonDocument.Parse(line);
            string method = request.RootElement.GetProperty("method").GetString()!;
            File.AppendAllText(Path.Combine(profile, "probe-methods.txt"), method + "\n");
            var lobby = EncounterStartWireChecks.PreparingWire();
            ((JsonObject)lobby).Remove("startState"); ((JsonObject)lobby).Remove("encounterStartSupported");
            lobby["canStart"] = true; lobby["status"] = "Ready";
            if (mode == "legacy-ffa") {
                lobby["matchSetup"]!["rulesetId"] = "commander.free-for-all.v1";
                foreach (var seat in lobby["matchSetup"]!["seats"]!.AsArray()) seat!["role"] = "participant";
                foreach (var seat in lobby["slots"]!.AsArray()) seat!["role"] = "participant";
                ((JsonObject)lobby).Remove("partyProgress");
            }
            object result = method == "multiplayerState" ? lobby : new { acknowledged = true };
            Console.WriteLine(JsonSerializer.Serialize(new { id = request.RootElement.GetProperty("id").GetInt64(), result }));
            Console.Out.Flush();
        }
        return 0;
    }

    internal static async Task RunAsync(string profile, Action<bool, string> check)
    {
        string executable = Path.Combine(AppContext.BaseDirectory, "Mana.Table.Tests.exe");
        foreach (string mode in new[] { "old-coop", "process-only", "legacy-ffa" }) {
            string path = Path.Combine(profile, "admission-protocol-" + mode);
            Directory.CreateDirectory(path);
            bool rejected = false;
            await using (var engine = new ForgeEngine(new(executable, Marker, mode, path))) {
                await engine.InitializeAsync();
                try { await engine.StartAsync(); }
                catch (NotSupportedException) { rejected = true; }
            }
            var methods = File.ReadAllLines(Path.Combine(path, "probe-methods.txt"));
            check(mode == "legacy-ffa" ? !rejected && methods.Count(method => method == "multiplayerStart") == 1
                    : rejected && methods.All(method => method is not ("multiplayerStart" or "startEncounter")),
                "The actual native setup port rejects unsupported co-op admission before any start RPC while preserving legacy FFA: " + mode);
        }
    }
}
