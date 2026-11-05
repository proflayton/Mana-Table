using System.Text.Json;
using System.Text.Json.Nodes;
using Mana.Client;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

internal static partial class OwnedDecisionObservationProtocolProbe
{
    // Authored IPC envelopes exercise the real process codec and ordinary
    // ClientSession/source lane. They do not simulate a Magic source mutation.
    private static int ServeSourceReads(string mode, string directory)
    {
        var capabilities = ForgeProtocol.RequiredCapabilities.Add("source-actions").Add("match-history");
        Console.WriteLine(JsonSerializer.Serialize(new { @event = "ready", protocolVersion = ForgeProtocol.Version, capabilities }));
        Console.Out.Flush();
        bool methods = mode.StartsWith("source-read-methods-", StringComparison.Ordinal);
        bool readback = mode == "source-read-readback-rejected";
        string code = readback || mode.EndsWith("failed", StringComparison.Ordinal) ? "source-read-failed"
            : mode.EndsWith("changed", StringComparison.Ordinal) ? "source-read-changed" : "source-read-superseded";
        int states = 0, sources = 0, actions = 0;
        long revision = 10;
        string key = "source-key", decision = InitialDecision;
        void Reply(long id, object? result) {
            Console.WriteLine(JsonSerializer.Serialize(new { id, result })); Console.Out.Flush();
        }
        void Reject(long id) {
            if (mode == "source-read-legacy") Console.WriteLine(JsonSerializer.Serialize(new {
                id, error = "The source actions changed. Read the current card again." }));
            else Console.WriteLine(JsonSerializer.Serialize(new { id,
                error = mode == "source-read-failed" ? "PRIVATE-PROVIDER-SOURCE-FAILURE" : "Controlled source query rejection", errorCode = code }));
            Console.Out.Flush();
        }
        string? line;
        while ((line = Console.ReadLine()) != null) {
            using var request = JsonDocument.Parse(line);
            long id = request.RootElement.GetProperty("id").GetInt64();
            string method = request.RootElement.GetProperty("method").GetString()!;
            var args = request.RootElement.GetProperty("params");
            File.AppendAllText(Path.Combine(directory, "probe-methods.txt"), method + "\n");
            if (method == "matchState") {
                states++;
                if ((methods || readback) && states == 2) { Reject(id); continue; }
                if (states > 1 && !methods) { revision = 11; key = "fresh-source-key"; }
                if (readback && states >= 3) decision = "readback-fresh-priority";
                var state = State(InitialGame, InitialViewer, decision, revision);
                var viewer = state["players"]!.AsArray().Single(player => player!["id"]!.GetValue<int>() == InitialViewer)!;
                viewer["zones"]!.AsArray().Add(new JsonObject {
                    ["name"] = "Battlefield", ["count"] = 1, ["cards"] = new JsonArray(new JsonObject {
                        ["key"] = key, ["visualId"] = "source-occurrence", ["name"] = "Controlled artifact",
                        ["type"] = "Artifact", ["selectable"] = true }) });
                Reply(id, state); continue;
            }
            if (method == "matchSourceActions") {
                sources++;
                if (sources <= 2) { Reject(id); continue; }
                if (args.GetProperty("key").GetString() != key || args.GetProperty("expectedRevision").GetInt64() != revision)
                    throw new InvalidOperationException("The probe received a stale source request.");
                Reply(id, new { gameId = InitialGame, decisionId = decision, sourceCardKey = key,
                    sourceCardId = "source-occurrence", revision,
                    actions = new[] { new { id = "fresh-action", kind = "activate", text = "Use the current artifact" } } });
                continue;
            }
            if (method == "matchHistory") { Reject(id); continue; }
            if (method == "matchAction") {
                actions++;
                if (methods && actions == 1) { Reject(id); continue; }
                if (args.GetProperty("sessionId").GetString() != InitialGame
                    || args.GetProperty("promptId").GetString() != decision)
                    throw new InvalidOperationException("The probe received a foreign command.");
                Reply(id, new { accepted = true }); continue;
            }
            Reply(id, new { acknowledged = true });
        }
        return 0;
    }

    private static async Task CheckSourceReadsAsync(string profile, Action<bool, string> check)
    {
        string executable = Path.Combine(AppContext.BaseDirectory, "Mana.Table.Tests.exe");
        foreach (string outcome in new[] { "superseded", "changed", "failed", "legacy" }) {
            string directory = Path.Combine(profile, "source-read-" + outcome); Directory.CreateDirectory(directory);
            await using var engine = new ForgeEngine(new(executable, Marker, "source-read-" + outcome, directory));
            await engine.InitializeAsync();
            var client = new ClientSession(engine, sourceActions: engine);
            try {
                client.Poll(false, _ => { });
                await WaitFor(directory, () => { client.Pump(); return !client.Polling && client.State != null; }, "initial source observation");
                var state = client.State!;
                var context = new SourceActionContext(state.Id, state.Decision!.Id, state.Revision, state.ViewerId,
                    "source-key", "source-occurrence");
                var error = await Failure(async () => { _ = await engine.SourceActionsAsync(context.GameId, context.DecisionId, context.SourceCardKey); });
                check(outcome switch {
                    "superseded" => error is EngineSourceReadException { Reason: EngineSourceReadFailure.Superseded },
                    "changed" => error is EngineSourceReadException { Reason: EngineSourceReadFailure.Changed },
                    "failed" => error is EngineSourceReadException { Reason: EngineSourceReadFailure.Failed }
                        && !error.ToString().Contains("PRIVATE-PROVIDER", StringComparison.Ordinal),
                    _ => error is EngineCommandRejectedException
                }, "Actual source-query IPC preserves the method-scoped closed outcome or legacy untyped rejection: " + outcome);
                client.SourceActions.Observe(context);
                await WaitFor(directory, () => { client.Pump(); return client.SourceActions.View.Status != SourceActionStatus.Loading; }, "source read rejection");
                var expected = outcome == "superseded" ? SourceActionStatus.Stale : SourceActionStatus.Failed;
                check(client.SourceActions.View.Status == expected && client.SourceActions.View.Actions.Count == 0
                        && client.SourceActions.TryCreateCommand(context, "fresh-action") == null
                        && client.SourceActions.Supported && engine.SupportsSourceActions
                        && !client.NeedsRecovery && !client.UncertainCommand && ReferenceEquals(client.State, state),
                    "An ordinary source read exposes stale versus failed without actions, recovery or replacing state: " + outcome);
                client.SourceActions.Observe(context); client.Pump();
                check(Count(directory, "matchSourceActions") == 2 && Count(directory, "matchAction") == 0,
                    "The unchanged source context is not retried and a read rejection emits no command: " + outcome);
                client.Poll(false, _ => { });
                await WaitFor(directory, () => { client.Pump(); return !client.Polling && client.State?.Revision == 11; }, "fresh ordinary source publication");
                var fresh = client.State!;
                var current = context with { Revision = fresh.Revision, SourceCardKey = "fresh-source-key" };
                client.SourceActions.Observe(current);
                await WaitFor(directory, () => { client.Pump(); return client.SourceActions.View.Status == SourceActionStatus.Ready; }, "fresh source catalog");
                var command = client.SourceActions.TryCreateCommand(current, "fresh-action");
                check(command != null && client.SourceActions.TryCreateCommand(context, "fresh-action") == null
                        && client.Submit(command),
                    "Only the newly observed source offer admits one explicit normal ClientSession command: " + outcome);
                await WaitFor(directory, () => { client.Pump(); return !client.Busy; }, "fresh source command receipt");
                check(Count(directory, "matchSourceActions") == 3 && Count(directory, "matchAction") == 1
                        && client.LastConnectionError.Length == 0 && !client.NeedsRecovery && !client.UncertainCommand,
                    "Fresh read and explicit source command settle once without hidden read or command replay: " + outcome);
            } finally { client.Close(); }
        }
        foreach (string outcome in new[] { "superseded", "changed", "failed" }) {
            string directory = Path.Combine(profile, "source-read-methods-" + outcome); Directory.CreateDirectory(directory);
            await using var engine = new ForgeEngine(new(executable, Marker, "source-read-methods-" + outcome, directory));
            await engine.InitializeAsync();
            var first = await engine.ObserveAsync() ?? throw new InvalidOperationException("Missing method-safety observation");
            var commandError = await Failure(() => ExecuteCurrent(engine, first));
            var ordinaryError = await Failure(async () => { _ = await engine.ObserveAsync(); });
            var historyError = await Failure(async () => { _ = await engine.HistoryAsync(first.Id, 0); });
            check(new[] { commandError, ordinaryError, historyError }.All(error => error is EngineCommandRejectedException)
                    && Count(directory, "matchAction") == 1 && Count(directory, "matchState") == 2
                    && Count(directory, "matchHistory") == 1 && Count(directory, "matchSourceActions") == 0,
                "The same source error code on command, ordinary observation and history stays a rejection, without safe-read laundering or retries: " + outcome);
            var fresh = await engine.ObserveAsync() ?? throw new InvalidOperationException("Missing fresh method-safety observation");
            await ExecuteCurrent(engine, fresh);
            check(Count(directory, "matchAction") == 2 && Count(directory, "matchState") == 3,
                "A method-scoped rejection adds another command only after fresh observation and an explicit caller request: " + outcome);
        }
        await CheckRejectedReadbackAsync(profile, executable, check);
    }

    private static async Task CheckRejectedReadbackAsync(string profile, string executable, Action<bool, string> check)
    {
        string directory = Path.Combine(profile, "source-read-readback-rejected"); Directory.CreateDirectory(directory);
        await using var engine = new ForgeEngine(new(executable, Marker, "source-read-readback-rejected", directory));
        await engine.InitializeAsync();
        // No diagnostic listener: command-phase safety cannot depend on optional evidence.
        var client = new ClientSession(engine);
        var failures = new List<Exception>(); client.Failed += failures.Add;
        try {
            client.Poll(false, _ => { });
            await WaitFor(directory, () => { client.Pump(); return !client.Polling && client.State != null; }, "initial readback owner");
            var first = client.State!;
            var command = new PassPriority(first.Id, first.Decision!.Id);
            check(client.Submit(command), "The actual child admits a normal command before its separate readback is rejected");
            await WaitFor(directory, () => { client.Pump(); return !client.Busy; }, "acknowledged command with rejected readback");
            check(failures is [EngineCommandRejectedException] && client.UncertainCommand && client.NeedsRecovery
                    && client.LastFailureWasCommand && ReferenceEquals(client.State, first)
                    && Count(directory, "matchAction") == 1 && Count(directory, "matchState") == 2,
                "A successful command ACK followed by rejected ordinary readback stays uncertain and preserves the anchor without diagnostics");
            check(!client.Submit(command) && Count(directory, "matchAction") == 1 && Count(directory, "matchSourceActions") == 0,
                "Rejected post-command observation cannot authorize resubmission or acquire source-read retry authority");
            client.Poll(false, _ => { });
            await WaitFor(directory, () => { client.Pump(); return !client.Polling && client.State?.Decision?.Id == "readback-fresh-priority"; },
                "fresh ordinary reconciliation after rejected readback");
            var fresh = client.State!;
            check(!client.UncertainCommand && !client.NeedsRecovery && fresh.Revision == 11
                    && Count(directory, "matchAction") == 1 && !client.Submit(command),
                "Only an ordinary fresh observation reconciles uncertainty; the old decision command remains retired");
            check(client.Submit(new PassPriority(fresh.Id, fresh.Decision!.Id)),
                "The reconciled actual child admits one newly explicit command for the fresh decision");
            await WaitFor(directory, () => { client.Pump(); return !client.Busy; }, "fresh command receipt after reconciliation");
            check(Count(directory, "matchAction") == 2 && Count(directory, "matchState") == 4
                    && failures.Count == 1 && !client.UncertainCommand && !client.NeedsRecovery && client.LastConnectionError.Length == 0,
                "Readback recovery completes with two explicit commands, one failure and no automatic replay");
        } finally { client.Close(); }
    }
}
