using System.Text.Json;
using System.Text.Json.Nodes;
using Mana.Client;
using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

internal static partial class OwnedDecisionObservationProtocolProbe
{
    // Two actual pending pipe requests deliberately cross a command admission.
    // Neither replies nor gameplay are retried by the probe or the client.
    private static int ServePublicationAdmission(string mode, string directory)
    {
        const string game = "publication-admission-game", initial = "publication-initial";
        bool owned = mode.Contains("-owned-", StringComparison.Ordinal);
        bool postCommand = mode == "publication-admission-postcommand-newer";
        string outcome = mode[(mode.LastIndexOf('-') + 1)..];
        Console.WriteLine(JsonSerializer.Serialize(new { @event = "ready", protocolVersion = ForgeProtocol.Version,
            capabilities = ForgeProtocol.RequiredCapabilities.Add("automated-seats").Add("owned-decision-observation") }));
        Console.Out.Flush();
        int states = 0, ownedReads = 0, controls = 0, actions = 0;
        long? oldRead = null, readback = null;
        void Reply(long id, object? result) { Console.WriteLine(JsonSerializer.Serialize(new { id, result })); Console.Out.Flush(); }
        string? line;
        while ((line = Console.ReadLine()) != null) {
            using var request = JsonDocument.Parse(line);
            long id = request.RootElement.GetProperty("id").GetInt64();
            string method = request.RootElement.GetProperty("method").GetString()!;
            var args = request.RootElement.GetProperty("params");
            File.AppendAllText(Path.Combine(directory, "probe-methods.txt"), method + "\n");
            if (method == "matchState") {
                states++;
                if (states == 1) { Reply(id, State(game, InitialViewer, initial, 10)); continue; }
                if (!owned && !postCommand && states == 2) { oldRead = id; File.WriteAllText(Path.Combine(directory, "old-read-held.txt"), "held"); continue; }
                readback = id; File.WriteAllText(Path.Combine(directory, "readback-held.txt"), "held"); continue;
            }
            if (method == "matchDecisionObservation") {
                ownedReads++;
                if (postCommand) { Reply(id, Envelope(game, InitialViewer, "publication-current-newer", 12)); continue; }
                if (!owned || ownedReads != 1) throw new InvalidOperationException("Unexpected rich observation in publication probe");
                oldRead = id; File.WriteAllText(Path.Combine(directory, "old-read-held.txt"), "held"); continue;
            }
            if (method == "matchAction") {
                actions++;
                if (actions != 1 || args.GetProperty("sessionId").GetString() != game
                    || args.GetProperty("promptId").GetString() != initial)
                    throw new InvalidOperationException("The publication probe received an extra or foreign gameplay command");
                Reply(id, new { accepted = true }); continue;
            }
            if (method == "automatedEnable") {
                controls++; Reply(id, new { acknowledged = true });
                if (postCommand && controls == 1 && readback is { } post) {
                    Reply(post, State(game, InitialViewer, "publication-after-command", 11)); readback = null; continue;
                }
                if (controls == 1 && oldRead is { } old) {
                    if (outcome == "superseded") {
                        Console.WriteLine(JsonSerializer.Serialize(new { id = old, error = "Controlled retired discovery", errorCode = "observation-superseded" })); Console.Out.Flush();
                    } else {
                        JsonNode payload = owned ? Envelope(game, InitialViewer, "publication-retired", 12)
                            : State(game, InitialViewer, "publication-retired", 12);
                        if (outcome == "malformed") {
                            if (owned) payload["state"]!["players"] = "invalid-roster";
                            else payload["players"] = "invalid-roster";
                        }
                        Reply(old, outcome == "null" ? null : payload);
                    }
                    File.WriteAllText(Path.Combine(directory, "old-read-released.txt"), "released"); oldRead = null;
                } else if (controls == 2 && readback is { } current) {
                    Reply(current, State(game, InitialViewer, "publication-after-command", 11));
                    File.WriteAllText(Path.Combine(directory, "readback-released.txt"), "released"); readback = null;
                } else throw new InvalidOperationException("The publication probe received an out-of-order release control");
                continue;
            }
            Reply(id, new { acknowledged = true });
        }
        return 0;
    }

    internal static async Task CheckPublicationAdmissionAsync(string profile, Action<bool, string> check)
    {
        string executable = Path.Combine(Path.GetDirectoryName(typeof(OwnedDecisionObservationProtocolProbe).Assembly.Location)!, "Mana.Table.Tests.exe");
        foreach (bool owned in new[] { false, true })
        foreach (string outcome in owned ? new[] { "newer", "null", "malformed", "superseded" } : new[] { "newer", "null", "malformed" }) {
            string mode = "publication-admission-" + (owned ? "owned-" : "ordinary-") + outcome;
            string directory = Path.Combine(profile, mode); Directory.CreateDirectory(directory);
            await using var engine = new ForgeEngine(new(executable, Marker, mode, directory));
            await engine.InitializeAsync();
            var connection = new PublicationConnection(engine, !owned);
            var client = new ClientSession(connection); var failures = new List<ClientOperationFailure>(); client.Failed += failures.Add;
            try {
                client.Poll(false, _ => { });
                await PublicationPumpUntil(client, () => !client.Polling && client.State != null);
                var initial = client.State!;
                check(initial.Revision == 10 && initial.Decision?.Id == "publication-initial", "Publication admission probe binds its initial provider authority: " + mode);
                Task<OwnedDecisionObservation?>? oldOwned = owned ? engine.ObserveDecisionAsync() : null;
                if (!owned) client.Poll(false, _ => { });
                await WaitFor(directory, () => File.Exists(Path.Combine(directory, "old-read-held.txt")), "pre-command provider read");
                check(client.Submit(new PassPriority(initial.Id, initial.Decision!.Id)), "One explicit command is admitted while the old provider read remains held: " + mode);
                await WaitFor(directory, () => File.Exists(Path.Combine(directory, "readback-held.txt")), "post-command readback");
                await engine.EnableAutomatedSeatsAsync();
                Exception? retired;
                if (oldOwned != null) retired = await Failure(async () => { _ = await oldOwned.WaitAsync(TimeSpan.FromSeconds(5)); });
                else { await connection.OldCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5)); retired = connection.OldFailure; }
                client.Pump();
                check(ReferenceEquals(client.State, initial) && Count(directory, "matchAction") == 1,
                    "The old read cannot publish through the retired client epoch or issue another command: " + mode);
                await engine.EnableAutomatedSeatsAsync();
                await PublicationPumpUntil(client, () => !client.Busy && !client.Polling);
                check(failures.Count == 0 && !client.NeedsRecovery && !client.UncertainCommand
                        && client.State is { Revision: 11, Decision.Id: "publication-after-command" }
                        && Count(directory, "matchAction") == 1,
                    "A pre-command provider read cannot poison the admitted command's one normal readback: " + mode);
                check(retired is EngineObservationSupersededException { Retirement: {
                            Reason: ObservationRetirementReason.CommandAdmitted, ReadAudience: 1, CurrentAudience: 1,
                            ReadAdmission: 0, CurrentAdmission: 1, CachedRevision: 10 } }
                        && Count(directory, "matchState") == (owned ? 2 : 3)
                        && Count(directory, "matchDecisionObservation") == (owned ? 1 : 0),
                    "Retired ordinary/rich/null/malformed reads produce typed supersession without hidden retries or authority clearing: " + mode);
            } finally { client.Close(); }
        }
        CheckPublicationAdmissionScope(check);
        await CheckPostCommandReadbackAsync(profile, executable, check);
    }

    private static void CheckPublicationAdmissionScope(Action<bool, string> check)
    {
        var initial = ForgeSnapshotDecoder.Decode(JsonSerializer.SerializeToElement(State("scope-game", InitialViewer, "scope-decision", 10)))!;
        var scope = new ForgeCommandScope(); scope.Observe(initial);
        var read = scope.CaptureRead();
        GameCommand[] rejected = [new PassPriority("", ""), new PassPriority(initial.Id, "foreign-decision"), new UndoLastAction(initial.Id, initial.Decision!.Id)];
        foreach (var command in rejected) {
            Exception? error = null;
            try { scope.Encode(command); } catch (Exception caught) { error = caught; }
            scope.EnsureCurrentRead(read);
            check(error is ArgumentException or EngineCommandRejectedException && scope.CaptureRead() == read,
                "Malformed, foreign and unsupported legacy commands leave current read ownership intact: " + command.GetType().Name);
        }
        scope.AdmitRead(read, initial with { Revision = 11 });
        check(scope.ObservedState?.Revision == 11, "A read retained across rejected commands can still publish normally");
        var admitted = scope.CaptureRead(); scope.Encode(new PassPriority(initial.Id, initial.Decision!.Id));
        Exception? retired = null;
        try { scope.AdmitRead(admitted, initial with { Revision = 12 }); } catch (Exception error) { retired = error; }
        check(retired is EngineObservationSupersededException { Retirement: { Reason: ObservationRetirementReason.CommandAdmitted,
                ReplyRevision: 12, CachedRevision: 11, NullReply: false } } && scope.ObservedState?.Revision == 11,
            "Admission diagnostics retain the closed rejection branch and revisions without admitting an older lease");
        check(typeof(EngineObservationSupersededException).GetConstructor(Type.EmptyTypes) != null
                && new EngineObservationSupersededException().Retirement == null
                && retired!.Message == new EngineObservationSupersededException().Message
                && !retired.ToString().Contains(initial.Id, StringComparison.Ordinal)
                && !retired.ToString().Contains(initial.Decision.Id, StringComparison.Ordinal),
            "Retirement diagnostics preserve the actual parameterless constructor and generic message without private identities");
    }

    private static async Task CheckPostCommandReadbackAsync(string profile, string executable, Action<bool, string> check)
    {
        const string mode = "publication-admission-postcommand-newer";
        string directory = Path.Combine(profile, mode); Directory.CreateDirectory(directory);
        await using var engine = new ForgeEngine(new(executable, Marker, mode, directory));
        await engine.InitializeAsync();
        var client = new ClientSession(engine); var failures = new List<ClientOperationFailure>(); client.Failed += failures.Add;
        try {
            client.Poll(false, _ => { });
            await PublicationPumpUntil(client, () => !client.Polling && client.State != null);
            var initial = client.State!; var command = new PassPriority(initial.Id, initial.Decision!.Id);
            check(client.Submit(command), "A genuine post-command readback test admits exactly one explicit command");
            await WaitFor(directory, () => File.Exists(Path.Combine(directory, "readback-held.txt")), "current command readback");
            var newer = await engine.ObserveDecisionAsync();
            check(newer?.State.Revision == 12, "A rich observation begun after command admission may publish newer current authority");
            await engine.EnableAutomatedSeatsAsync();
            await PublicationPumpUntil(client, () => !client.Busy);
            check(failures is [{ Phase: ClientFailurePhase.ReadbackUnknown, Cause: EngineObservationSupersededException {
                        Retirement: { Reason: ObservationRetirementReason.RevisionBehind, ReadAdmission: 1, CurrentAdmission: 1,
                            ReplyRevision: 11, CachedRevision: 12, NullReply: false } } }]
                    && client.UncertainCommand && client.NeedsRecovery && ReferenceEquals(client.State, initial),
                "A genuinely superseded post-command readback remains typed ReadbackUnknown without substituting the rich cache");
            check(!client.Submit(command) && Count(directory, "matchAction") == 1 && Count(directory, "matchState") == 2
                    && Count(directory, "matchDecisionObservation") == 1,
                "Uncertain readback never retries a command or quietly starts another discovery");
        } finally { client.Close(); }
    }

    private static async Task PublicationPumpUntil(ClientSession client, Func<bool> done)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true) {
            client.Pump(); if (done()) return;
            await client.WaitForQueuedWorkAsync(guard.Token);
        }
    }
    private sealed class PublicationConnection(ForgeEngine engine, bool trackOld) : IMatchConnection
    {
        private int observations;
        internal readonly TaskCompletionSource OldCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Exception? OldFailure;
        public Task ExecuteAsync(GameCommand command) => engine.ExecuteAsync(command);
        public Task ConcedeAsync(string gameId) => throw new InvalidOperationException("Publication ownership does not concede");
        public async Task<GameSnapshot?> ObserveAsync()
        {
            int ordinal = Interlocked.Increment(ref observations);
            try { return await engine.ObserveAsync(); }
            catch (Exception error) { if (trackOld && ordinal == 2) OldFailure = error; throw; }
            finally { if (trackOld && ordinal == 2) OldCompleted.TrySetResult(); }
        }
    }
}
