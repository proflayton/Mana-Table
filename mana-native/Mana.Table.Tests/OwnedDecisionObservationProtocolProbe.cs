using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

// Actual child pipes, provider codec and command scope; authored envelopes are
// not Magic rules, TCP audience authorization or human gameplay evidence.
internal static class OwnedDecisionObservationProtocolProbe
{
    internal const string Marker = "--owned-decision-observation-protocol-probe";
    private const string InitialGame = "owned-observation-game", InitialDecision = "owned-priority";
    private const int InitialViewer = 29;

    internal static int Serve(string[] arguments)
    {
        int index = Array.IndexOf(arguments, Marker);
        string mode = arguments[index + 1], directory = Path.GetDirectoryName(arguments[index + 2])!;
        Directory.CreateDirectory(directory);
        var capabilities = ForgeProtocol.RequiredCapabilities.Add("automated-seats");
        if (mode != "legacy") capabilities = capabilities.Add("owned-decision-observation");
        if (mode == "private-faces") capabilities = capabilities.Add("private-action-faces");
        Console.WriteLine(JsonSerializer.Serialize(new { @event = "ready", protocolVersion = ForgeProtocol.Version, capabilities }));
        Console.Out.Flush();
        string game = InitialGame, decision = InitialDecision;
        int viewer = InitialViewer, reads = 0, states = 0, actions = 0;
        long revision = 10;
        (long Id, JsonNode Payload, bool Ordinary)? held = null;
        void Reply(long id, object? result) {
            Console.WriteLine(JsonSerializer.Serialize(new { id, result })); Console.Out.Flush();
        }
        void Error(long id, string code) {
            Console.WriteLine(JsonSerializer.Serialize(new { id, error = "Controlled probe rejection", errorCode = code })); Console.Out.Flush();
        }
        string? line;
        while ((line = Console.ReadLine()) != null) {
            using var request = JsonDocument.Parse(line);
            long id = request.RootElement.GetProperty("id").GetInt64();
            string method = request.RootElement.GetProperty("method").GetString()!;
            var args = request.RootElement.GetProperty("params");
            File.AppendAllText(Path.Combine(directory, "probe-methods.txt"), method + "\n");
            if (method is "matchState" or "matchDecisionObservation") {
                bool optedIn = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("privateActionFaces", out var opt)
                    && opt.ValueKind == JsonValueKind.True;
                if (optedIn != (mode == "private-faces")) { Error(id, "incorrect-private-face-negotiation"); continue; }
            }
            if (method == "matchDecisionObservation") {
                reads++;
                if (mode.StartsWith("ordinary-newer-", StringComparison.Ordinal)) { revision = 11; decision = "newer-priority"; }
                if (mode == "ordinary-aba-success") viewer = reads == 1 ? 47 : InitialViewer;
                var envelope = Envelope(game, viewer, decision, revision);
                if (reads == 2 && mode.StartsWith("late-", StringComparison.Ordinal)) { held = (id, envelope, false); continue; }
                if (reads == 2 && mode == "null") {
                    Reply(id, null); game = "fresh-owned-game"; decision = "fresh-priority"; revision = 1; continue;
                }
                if (reads == 2 && mode == "malformed") { envelope["state"]!["viewerId"] = -1; Reply(id, envelope); continue; }
                if (reads == 2 && mode == "superseded") { Error(id, "observation-superseded"); continue; }
                if (reads == 2 && mode == "unsupported") { Error(id, "unsupported-capability"); continue; }
                Reply(id, envelope); continue;
            }
            if (method == "matchState") {
                states++;
                if (states == 2 && mode.StartsWith("ordinary-", StringComparison.Ordinal)) {
                    held = (id, State(game, viewer, decision, revision), true); continue;
                }
                if (mode.StartsWith("late-aba-", StringComparison.Ordinal)) viewer = states == 1 ? 47 : InitialViewer;
                if (mode.StartsWith("late-newer-", StringComparison.Ordinal) || mode == "late-cancel-success") {
                    revision = 11; decision = "newer-priority";
                }
                if (mode == "late-null-success" && states == 1) {
                    Reply(id, null); game = "fresh-owned-game"; decision = "fresh-priority"; revision = 1; continue;
                }
                Reply(id, State(game, viewer, decision, revision)); continue;
            }
            if (method == "matchAction") {
                actions++;
                if (mode == "action-code" && actions == 1) { Error(id, "observation-superseded"); continue; }
                if (args.GetProperty("sessionId").GetString() != game || args.GetProperty("promptId").GetString() != decision)
                    Error(id, "foreign-command");
                else Reply(id, new { accepted = true });
                continue;
            }
            if (method == "matchSetup") {
                Reply(id, new { deckId = "new-deck", revision = 1, maxPlayers = 4, setup = new { problem = "" } }); continue;
            }
            if (method == "matchStart") { game = "replacement-owned-game"; decision = "replacement-priority"; revision = 1; }
            Reply(id, new { acknowledged = true });
            if (method == "automatedEnable" && held is { } old) {
                if (mode.EndsWith("superseded", StringComparison.Ordinal)) Error(old.Id, "observation-superseded");
                else {
                    if (mode.EndsWith("malformed", StringComparison.Ordinal)) {
                        if (old.Ordinary) old.Payload["players"] = "invalid-roster";
                        else old.Payload["state"]!["viewerId"] = -1;
                    }
                    Reply(old.Id, mode.EndsWith("null", StringComparison.Ordinal) ? null : old.Payload);
                }
                held = null;
                File.WriteAllText(Path.Combine(directory, "held-response-sent.txt"), "sent\n");
            }
        }
        return 0;
    }

    private static JsonNode State(string game, int viewer, string decision, long revision)
    {
        var state = ConfiguredSimulationWireChecks.State();
        state["id"] = game; state["viewerId"] = viewer; state["revision"] = revision; state["boardRevision"] = revision;
        state["turn"] = 1; state["phaseKey"] = "MAIN1"; state["activePlayerId"] = viewer; state["priorityPlayerId"] = viewer;
        state["prompt"] = new JsonObject { ["id"] = decision, ["kind"] = "input", ["task"] = "priority",
            ["ok"] = "Pass", ["okEnabled"] = true, ["canAutoPass"] = true };
        return state;
    }
    private static JsonNode Envelope(string game = InitialGame, int viewer = InitialViewer, string decision = InitialDecision, long revision = 10) => new JsonObject {
        ["state"] = State(game, viewer, decision, revision),
        ["domain"] = JsonNode.Parse("""{"version":7,"coverage":"Complete","sources":[],"actions":[]}"""),
        ["semantics"] = new JsonObject()
    };

    internal static async Task RunAsync(string profile, Action<bool, string> check)
    {
        CheckWire(check);
        await CheckOrdinaryReadsAsync(profile, check);
        string executable = Path.Combine(AppContext.BaseDirectory, "Mana.Table.Tests.exe");
        foreach (string mode in new[] { "legacy", "ready", "private-faces", "null", "malformed", "superseded", "unsupported", "action-code",
                "late-aba-success", "late-aba-malformed", "late-aba-superseded", "late-newer-success", "late-newer-malformed",
                "late-newer-superseded", "late-newer-null", "late-null-success", "late-world-success", "late-cancel-success" }) {
            string directory = Path.Combine(profile, "owned-observation-" + mode); Directory.CreateDirectory(directory);
            await using var engine = new ForgeEngine(new(executable, Marker, mode, directory));
            check(!engine.SupportsOwnedDecisionObservations, "Owned decision support is unavailable before the actual ready handshake: " + mode);
            await engine.InitializeAsync();
            if (mode == "legacy") {
                var error = await Failure(async () => { _ = await engine.ObserveDecisionAsync(); });
                check(error is NotSupportedException && !engine.SupportsOwnedDecisionObservations
                        && Count(directory, "matchDecisionObservation") == 0,
                    "An absent optional owned-observation capability is explicit unsupported before any RPC");
                continue;
            }
            check(engine.SupportsOwnedDecisionObservations, "The provider negotiates owned decision support from this process: " + mode);
            var first = await engine.ObserveDecisionAsync() ?? throw new InvalidOperationException("Missing initial owned observation");
            check(first.State is { Id: InitialGame, ViewerId: InitialViewer, Revision: 10, Decision.Id: InitialDecision }
                    && first.Domain.Coverage == ActionDomainCoverage.Complete,
                "The actual child envelope decodes one coherent owned decision and domain: " + mode);
            if (mode == "private-faces") {
                var ordinary = await engine.ObserveAsync();
                check(ordinary?.Decision?.Id == first.State.Decision?.Id && Count(directory, "matchState") == 1,
                    "Both own and ordinary reads explicitly opt into private action faces only after this process advertises support");
                await ExecuteCurrent(engine, first.State);
                check(Count(directory, "matchAction") == 1, "Private face discovery still requires one explicit current action command");
            } else if (mode == "ready") {
                using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
                var error = await Failure(async () => { _ = await engine.ObserveDecisionAsync(cancelled.Token); });
                check(error is OperationCanceledException && Count(directory, "matchDecisionObservation") == 1,
                    "Pre-cancelled own reads create no RPC or command");
                await ExecuteCurrent(engine, first.State);
            } else if (mode == "action-code") {
                var error = await Failure(() => ExecuteCurrent(engine, first.State));
                check(error is EngineCommandRejectedException && error is not EngineObservationSupersededException
                        && Count(directory, "matchAction") == 1,
                    "An identical superseded error code on matchAction is a command rejection and never a retryable read");
                var fresh = await engine.ObserveDecisionAsync() ?? throw new InvalidOperationException();
                await ExecuteCurrent(engine, fresh.State);
                check(Count(directory, "matchAction") == 2, "Only a newly explicit command after reobservation adds another action receipt");
            } else if (mode.StartsWith("late-", StringComparison.Ordinal)) {
                using var cancellation = new CancellationTokenSource();
                var old = engine.ObserveDecisionAsync(cancellation.Token);
                await WaitFor(directory, () => Count(directory, "matchDecisionObservation") == 2, "held own read");
                if (mode == "late-cancel-success") {
                    cancellation.Cancel();
                    check(await Failure(async () => { _ = await old; }) is OperationCanceledException,
                        "Cancellation stops a dispatched own read's wait without damaging the process or inventing command cancellation");
                }
                GameSnapshot fresh;
                if (mode == "late-world-success") {
                    await engine.StartSoloAsync("new-deck", ["opponent"]);
                    fresh = await engine.ObserveAsync() ?? throw new InvalidOperationException();
                } else if (mode.StartsWith("late-aba-", StringComparison.Ordinal)) {
                    var middle = await engine.ObserveAsync();
                    fresh = await engine.ObserveAsync() ?? throw new InvalidOperationException();
                    check(middle?.ViewerId == 47 && fresh.ViewerId == first.State.ViewerId && fresh.Id == first.State.Id
                            && fresh.Revision == first.State.Revision && fresh.Decision?.Id == first.State.Decision?.Id,
                        "The held read spans an actual audience A-to-B-to-identical-A round trip: " + mode);
                } else if (mode == "late-null-success") {
                    check(await engine.ObserveAsync() == null, "An actual null ordinary observation retires the former own audience");
                    fresh = await engine.ObserveAsync() ?? throw new InvalidOperationException();
                } else fresh = await engine.ObserveAsync() ?? throw new InvalidOperationException();
                await engine.EnableAutomatedSeatsAsync();
                await WaitFor(directory, () => File.Exists(Path.Combine(directory, "held-response-sent.txt")), "late own response");
                if (mode != "late-cancel-success") {
                    var error = await Failure(async () => { _ = await old; });
                    check(error is EngineObservationSupersededException or IOException,
                        "A late old own read cannot return authority across its captured audience/world/revision: " + mode);
                }
                check(Count(directory, "matchAction") == 0, "Read retirement, late payloads and cancellation never replay or create commands: " + mode);
                await ExecuteCurrent(engine, fresh);
                check(Count(directory, "matchAction") == 1,
                    "Late former success/malformed/superseded replies leave the fresh ordinary scope executable exactly once: " + mode);
                var current = await engine.ObserveDecisionAsync() ?? throw new InvalidOperationException();
                check(current.State.Id == fresh.Id && current.State.ViewerId == fresh.ViewerId
                        && current.State.Revision == fresh.Revision && current.State.Decision?.Id == fresh.Decision?.Id,
                    "A fresh own read remains coherent after the retired callback and explicit current command: " + mode);
            } else {
                OwnedDecisionObservation? value = null;
                var error = await Failure(async () => { value = await engine.ObserveDecisionAsync(); });
                check(mode switch {
                    "null" => error == null && value == null,
                    "malformed" => error is IOException,
                    "superseded" => error is EngineObservationSupersededException,
                    "unsupported" => error is NotSupportedException,
                    _ => false
                }, "Own absence, malformed values and typed read failures remain distinct: " + mode);
                var commandError = await Failure(() => ExecuteCurrent(engine, first.State));
                check(mode == "unsupported" ? commandError == null && Count(directory, "matchAction") == 1
                        : commandError is EngineCommandRejectedException && Count(directory, "matchAction") == 0,
                    "Absent/malformed/superseded authority is withdrawn, while unavailable discovery preserves an already observed manual scope: " + mode);
                var fresh = await engine.ObserveDecisionAsync() ?? throw new InvalidOperationException();
                await ExecuteCurrent(engine, fresh.State);
                check(Count(directory, "matchAction") == (mode == "unsupported" ? 2 : 1),
                    "A fresh exact owned read admits only the subsequently explicit command: " + mode);
            }
        }
    }

    private static async Task CheckOrdinaryReadsAsync(string profile, Action<bool, string> check)
    {
        string executable = Path.Combine(AppContext.BaseDirectory, "Mana.Table.Tests.exe");
        foreach (string mode in new[] { "ordinary-newer-malformed", "ordinary-newer-success", "ordinary-newer-null", "ordinary-aba-success", "ordinary-world-success" }) {
            string directory = Path.Combine(profile, "owned-observation-" + mode); Directory.CreateDirectory(directory);
            await using var engine = new ForgeEngine(new(executable, Marker, mode, directory));
            await engine.InitializeAsync();
            var first = await engine.ObserveAsync() ?? throw new InvalidOperationException();
            var old = engine.ObserveAsync();
            await WaitFor(directory, () => Count(directory, "matchState") == 2, "held ordinary observation");
            if (mode == "ordinary-world-success") await engine.StartSoloAsync("new-deck", ["opponent"]);
            if (mode == "ordinary-aba-success") {
                var middle = await engine.ObserveDecisionAsync() ?? throw new InvalidOperationException();
                check(middle.State.ViewerId == 47, "A real own-port observation replaces the held ordinary read's audience");
            }
            var fresh = await engine.ObserveDecisionAsync() ?? throw new InvalidOperationException();
            if (mode == "ordinary-aba-success") check(fresh.State.Id == first.Id && fresh.State.ViewerId == first.ViewerId
                    && fresh.State.Revision == first.Revision && fresh.State.Decision?.Id == first.Decision?.Id,
                "Ordinary read retirement uses the audience generation even when all returning A values are identical");
            await engine.EnableAutomatedSeatsAsync();
            await WaitFor(directory, () => File.Exists(Path.Combine(directory, "held-response-sent.txt")), "late ordinary response");
            var error = await Failure(async () => { _ = await old; });
            check(error is IOException or EngineObservationSupersededException,
                "A delayed ordinary success/absence/malformed response cannot replace a newer own-port or world observation: " + mode);
            check(Count(directory, "matchAction") == 0, "Retiring an ordinary observation sends no gameplay command: " + mode);
            await ExecuteCurrent(engine, fresh.State);
            check(Count(directory, "matchAction") == 1,
                "An old ordinary read cannot clear or repopulate the fresh own-port command scope: " + mode);
            var retained = await engine.ObserveDecisionAsync() ?? throw new InvalidOperationException();
            check(retained.State.Id == fresh.State.Id && retained.State.ViewerId == fresh.State.ViewerId
                    && retained.State.Revision == fresh.State.Revision && retained.State.Decision?.Id == fresh.State.Decision?.Id,
                "A fresh owned observation keeps the actual replacement identity after the late ordinary callback: " + mode);
        }
    }

    private static void CheckWire(Action<bool, string> check)
    {
        bool Reject(JsonNode node) {
            try { ForgeEngine.DecodeOwnedDecisionObservation(JsonSerializer.SerializeToElement(node)); return false; }
            catch (IOException) { return true; }
        }
        foreach (var change in new Action<JsonNode>[] {
            node => ((JsonObject)node).Remove("domain"), node => ((JsonObject)node).Remove("semantics"),
            node => node["state"] = null, node => node["state"]!["viewerId"] = -1,
            node => node["domain"]!["version"] = -1, node => node["domain"]!["sources"] = null,
            node => node["domain"]!["actions"] = null, node => node["domain"]!["coverage"] = "Invented",
            node => node["semantics"]!["options"] = null, node => node["semantics"]!["unpaidMana"] = null,
            node => node["state"]!["prompt"] = null
        }) { var value = Envelope(); change(value); check(Reject(value), "Malformed own wire cannot grant an unbound state, domain or semantics"); }
        check(ForgeEngine.DecodeOwnedDecisionObservation(JsonSerializer.SerializeToElement<object?>(null)) == null,
            "An explicit null own envelope is absence, not unsupported or an invented empty audience");
        using var coded = JsonDocument.Parse("""{"error":"Same prose","errorCode":"observation-superseded"}""");
        check(ForgeEngine.RequestFailure("matchDecisionObservation", coded.RootElement) is EngineObservationSupersededException
                && ForgeEngine.RequestFailure("matchAction", coded.RootElement) is EngineCommandRejectedException
                && ForgeEngine.RequestFailure("matchState", coded.RootElement) is EngineCommandRejectedException,
            "Retryable discovery codes are scoped to the exact read method rather than error prose or arbitrary commands");
    }
    private static Task ExecuteCurrent(ForgeEngine engine, GameSnapshot state) => engine.ExecuteAsync(new PassPriority(state.Id, state.Decision!.Id));
    private static async Task<Exception?> Failure(Func<Task> action) { try { await action(); return null; } catch (Exception error) { return error; } }
    private static int Count(string directory, string method)
    {
        string path = Path.Combine(directory, "probe-methods.txt");
        if (!File.Exists(path)) return 0;
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(file);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Count(value => value == method);
    }
    private static async Task WaitFor(string directory, Func<bool> condition, string description)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition() && elapsed.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(5);
        if (!condition()) throw new TimeoutException("The own-observation probe did not reach " + description + ": " + directory);
    }
}
