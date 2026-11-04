using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Mana.Client;
using Mana.Contracts;

internal static class SourceReadDiagnosticRegression
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        await CurrentFailure(check);
        await CurrentReasons(check);
        foreach (string transition in new[] { "viewer-aba", "reset", "null" })
        foreach (string completion in new[] { "success", "unavailable", "superseded", "changed", "failed" })
            await RetiredRead(check, transition, completion);
        await UnownedContext(check);
        await SourceSignalDoesNotOwnCommands(check);
        await ThrowingSink(check);
        await JournalPrivacy(check);
    }
    private static async Task CurrentFailure(Action<bool, string> check)
    {
        using var driver = new Driver();
        var state = State();
        driver.Client.Accept(state);
        driver.Events.Clear();
        driver.Client.SourceActions.Observe(Context(state));
        var request = await driver.Request(1);
        request.Result.SetException(new IOException("PRIVATE-PROVIDER-MESSAGE source-key action-key PRIVATE-FACE"));
        await driver.Until(() => driver.Client.SourceActions.View.Status == SourceActionStatus.Failed);
        check(driver.Sources.Requests.Count == 1 && driver.Connection.Commands == 0
                && !driver.Client.UncertainCommand && !driver.Client.NeedsRecovery,
            "A failed current source read neither retries nor becomes an uncertain gameplay command");
        check(driver.Events.Any(item => item.Origin.ToString() == "SourceRead"),
            "A current source-query failure retains typed operational evidence through the ordinary ClientSession diagnostic pipeline");
    }

    private static async Task CurrentReasons(Action<bool, string> check)
    {
        foreach (var item in new (string Label, Exception? Error, SourceActionStatus Status, ClientSourceReadReason Reason)[] {
            ("unsupported", new NotSupportedException("PRIVATE-UNSUPPORTED"), SourceActionStatus.Unsupported, ClientSourceReadReason.Unsupported),
            ("rejected", new EngineCommandRejectedException("PRIVATE-REJECTION"), SourceActionStatus.Failed, ClientSourceReadReason.Rejected),
            ("timeout", new TimeoutException("PRIVATE-TIMEOUT"), SourceActionStatus.Failed, ClientSourceReadReason.Timeout),
            ("cancelled", new OperationCanceledException("PRIVATE-CANCEL"), SourceActionStatus.Failed, ClientSourceReadReason.Cancelled),
            ("unavailable", new IOException("PRIVATE-TRANSPORT"), SourceActionStatus.Failed, ClientSourceReadReason.Unavailable),
            ("unexpected", new InvalidOperationException("PRIVATE-UNEXPECTED"), SourceActionStatus.Failed, ClientSourceReadReason.Unexpected),
            ("superseded", new EngineSourceReadException(EngineSourceReadFailure.Superseded), SourceActionStatus.Stale, ClientSourceReadReason.Superseded),
            ("changed", new EngineSourceReadException(EngineSourceReadFailure.Changed), SourceActionStatus.Failed, ClientSourceReadReason.Changed),
            ("failed", new EngineSourceReadException(EngineSourceReadFailure.Failed), SourceActionStatus.Failed, ClientSourceReadReason.Unexpected),
            ("wrong-method-signal", new EngineObservationSupersededException(), SourceActionStatus.Failed, ClientSourceReadReason.Unexpected),
            ("empty", null, SourceActionStatus.Failed, ClientSourceReadReason.EmptyResult),
            ("invalid", null, SourceActionStatus.Failed, ClientSourceReadReason.InvalidResult),
            ("foreign", null, SourceActionStatus.Stale, ClientSourceReadReason.ContextMismatch)
        }) {
            using var driver = new Driver(); var state = State(); var context = Context(state);
            driver.Client.Accept(state); driver.Events.Clear(); driver.Client.SourceActions.Observe(context);
            var request = await driver.Request(1);
            if (item.Error != null) request.Result.SetException(item.Error);
            else request.Result.SetResult(item.Label switch {
                "empty" => null!, "invalid" => Catalog(context) with { Actions = default },
                _ => Catalog(context with { GameId = "FOREIGN-PRIVATE-GAME" })
            });
            await driver.Until(() => driver.Client.SourceActions.View.Status != SourceActionStatus.Loading);
            var events = driver.Events.Where(value => value.Origin == ClientDiagnosticOrigin.SourceRead).ToArray();
            check(events.Length == 2 && events[0].Stage == ClientDiagnosticStage.SourceReadStarted
                    && events.Select(value => value.AttemptId).Distinct().Count() == 1
                    && events[1].SourceRead is { Current: true, Generation: 1, CurrentGeneration: 1, Epoch: 0,
                        CurrentEpoch: 0, ViewerId: 0, Revision: 7 } read && read.Reason == item.Reason,
                "Source read " + item.Label + " records one current numeric request lifetime and its closed reason");
            check(driver.Client.SourceActions.View.Status == item.Status && driver.Client.SourceActions.View.Actions.Count == 0
                    && driver.Client.SourceActions.TryCreateCommand(context, "action") == null
                    && events.All(value => value.Command == ClientDiagnosticCommand.Unknown
                        && value.Facts.DecisionSourceName == null && value.Facts.RemainingMana.IsEmpty)
                    && !JsonSerializer.Serialize(events).Contains("PRIVATE", StringComparison.Ordinal)
                    && !driver.Client.NeedsRecovery && !driver.Client.UncertainCommand,
                "Source read " + item.Label + " cannot grant actions, leak a face or acquire command recovery semantics");
            driver.Client.SourceActions.Observe(context); driver.Client.Pump();
            check(driver.Sources.Requests.Count == 1 && driver.Connection.Commands == 0,
                "Source read " + item.Label + " stays terminal for its unchanged context without implicit retry or dispatch");
        }
    }

    private static async Task RetiredRead(Action<bool, string> check, string transition, string completion)
    {
        using var driver = new Driver(); var original = State(); var firstContext = Context(original);
        driver.Client.Accept(original); driver.Events.Clear(); driver.Client.SourceActions.Observe(firstContext);
        var old = await driver.Request(1);
        var started = driver.Events.Single(item => item.Origin == ClientDiagnosticOrigin.SourceRead);
        GameSnapshot current;
        if (transition == "viewer-aba") {
            driver.Client.Accept(original with { ViewerId = 1 });
            driver.Client.Accept(original); current = original;
        } else {
            if (transition == "reset") driver.Client.Reset(); else driver.Client.Accept(null);
            current = original with { Id = "REPLACEMENT-PRIVATE-GAME" };
            driver.Client.Accept(current);
        }
        var context = Context(current); driver.Client.SourceActions.Observe(context);
        if (completion == "success") old.Result.SetResult(Catalog(firstContext, "OLD-PRIVATE-ACTION"));
        else old.Result.SetException(completion switch {
            "superseded" => new EngineSourceReadException(EngineSourceReadFailure.Superseded),
            "changed" => new EngineSourceReadException(EngineSourceReadFailure.Changed),
            "failed" => new EngineSourceReadException(EngineSourceReadFailure.Failed),
            _ => new IOException("OLD-PRIVATE-PROVIDER-ERROR")
        });
        var fresh = await driver.Request(2);
        var retired = driver.Events.Single(item => item.Stage == ClientDiagnosticStage.SourceReadDiscarded);
        check(retired.AttemptId == started.AttemptId && retired.SourceRead is { Current: false, ViewerId: 0, Revision: 7 } stamp
                && stamp.Generation == started.SourceRead!.Generation && stamp.Epoch == started.SourceRead.Epoch
                && stamp.CurrentGeneration > stamp.Generation && stamp.CurrentEpoch > stamp.Epoch
                && !retired.Facts.HasSnapshot && retired.Facts.ViewerId == null && retired.Facts.Revision == null
                && retired.Facts.DecisionSourceName == null && retired.Facts.RemainingMana.IsEmpty
                && !JsonSerializer.Serialize(driver.Events.Where(item => item.Origin == ClientDiagnosticOrigin.SourceRead))
                    .Contains("PRIVATE", StringComparison.Ordinal),
            "Source " + transition + "/" + completion + " preserves retired numeric attribution without borrowing replacement facts");
        check(driver.Client.SourceActions.View.Status == SourceActionStatus.Loading && driver.Client.SourceActions.View.Actions.Count == 0
                && ReferenceEquals(driver.Client.State, current) && driver.Connection.Commands == 0,
            "Source " + transition + "/" + completion + " cannot revive old data, faults or action authority");
        fresh.Result.SetResult(Catalog(context));
        await driver.Until(() => driver.Client.SourceActions.View.Status == SourceActionStatus.Ready);
        var accepted = driver.Events.Single(item => item.Stage == ClientDiagnosticStage.SourceReadAccepted);
        check(accepted.AttemptId != retired.AttemptId && accepted.SourceRead is { Current: true } currentRead
                && currentRead.Generation == driver.Client.SourceActions.View.Generation
                && currentRead.Epoch == retired.SourceRead!.CurrentEpoch
                && driver.Client.SourceActions.TryCreateCommand(context, "action") != null
                && driver.Client.SourceActions.TryCreateCommand(firstContext, "OLD-PRIVATE-ACTION") == null
                && driver.Sources.Requests.Count == 2 && !driver.Client.NeedsRecovery && !driver.Client.UncertainCommand,
            "Source " + transition + "/" + completion + " admits exactly one fresh read after the actual composed lifecycle");
    }

    private static async Task UnownedContext(Action<bool, string> check)
    {
        using var driver = new Driver(); var state = State(); driver.Client.Accept(state); driver.Events.Clear();
        var unowned = Context(state) with { SourceCardId = "INVENTED-PRIVATE-INCARNATION" };
        driver.Client.SourceActions.Observe(unowned);
        (await driver.Request(1)).Result.SetResult(Catalog(unowned));
        await driver.Until(() => driver.Client.SourceActions.View.Status == SourceActionStatus.Ready);
        check(driver.Events.Where(item => item.Origin == ClientDiagnosticOrigin.SourceRead).All(item =>
                    item.SourceRead is { Current: false, ViewerId: null, Revision: null } && !item.Facts.HasSnapshot)
                && !driver.Events.Any(item => item.Stage == ClientDiagnosticStage.SourceReadAccepted)
                && !driver.Client.Submit(driver.Client.SourceActions.TryCreateCommand(unowned, "action")!)
                && driver.Connection.Commands == 0,
            "Source diagnostics use the central exact occurrence context, rather than a weaker game/viewer/revision predicate");
    }

    private static async Task ThrowingSink(Action<bool, string> check)
    {
        using var driver = new Driver(throwingSink: true); var state = State(); var context = Context(state);
        driver.Client.Accept(state); driver.Events.Clear(); driver.Client.SourceActions.Observe(context);
        (await driver.Request(1)).Result.SetException(new IOException("PRIVATE-FIRST-FAILURE"));
        await driver.Until(() => driver.Client.SourceActions.View.Status == SourceActionStatus.Failed);
        var next = state with { Revision = 8 }; driver.Client.Accept(next); context = Context(next);
        driver.Client.SourceActions.Observe(context);
        (await driver.Request(2)).Result.SetResult(Catalog(context));
        await driver.Until(() => driver.Client.SourceActions.View.Status == SourceActionStatus.Ready);
        check(driver.Events.Count(item => item.Origin == ClientDiagnosticOrigin.SourceRead) == 4
                && driver.Client.SourceActions.TryCreateCommand(context, "action") != null && driver.Connection.Commands == 0
                && !driver.Client.NeedsRecovery && !driver.Client.UncertainCommand,
            "A throwing diagnostic listener cannot block another listener, current read admission or the next coalesced lifetime");
    }

    private static async Task SourceSignalDoesNotOwnCommands(Action<bool, string> check)
    {
        foreach (var reason in Enum.GetValues<EngineSourceReadFailure>()) {
            using var driver = new Driver(); var state = State(); driver.Client.Accept(state); driver.Events.Clear();
            driver.Connection.ExecuteError = new EngineSourceReadException(reason);
            check(driver.Client.Submit(new PassPriority(state.Id, state.Decision!.Id)),
                "A current explicit command enters its ordinary lane before an unexpected source-read signal: " + reason);
            await driver.Until(() => !driver.Client.Busy);
            check(driver.Connection.Commands == 1 && driver.Client.UncertainCommand && driver.Client.NeedsRecovery
                    && driver.Client.LastFailureWasCommand
                    && driver.Events.Any(item => item.Stage == ClientDiagnosticStage.OutcomeUnknown)
                    && !driver.Events.Any(item => item.Origin == ClientDiagnosticOrigin.SourceRead)
                    && driver.Sources.Requests.IsEmpty,
                "A source-read exception thrown by command execution stays uncertain and cannot authorize safe-read recovery or replay: " + reason);
        }
    }

    private static async Task JournalPrivacy(Action<bool, string> check)
    {
        string temporary = Path.GetFullPath(Path.GetTempPath());
        string root = Path.GetFullPath(Path.Combine(temporary, "mana-source-read-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        bool passed = false;
        try {
            using var driver = new Driver();
            await using var journal = new ClientDiagnosticJournal(root, new("0.2.0", false, new string('A', 40)));
            driver.Client.DiagnosticRecorded += journal.Record;
            var state = State(); driver.Client.Accept(state); driver.Client.SourceActions.Observe(Context(state));
            (await driver.Request(1)).Result.SetException(new IOException("PRIVATE-PROVIDER-MESSAGE"));
            await driver.Until(() => driver.Client.SourceActions.View.Status == SourceActionStatus.Failed);
            var failure = driver.Events.Single(item => item.Stage == ClientDiagnosticStage.SourceReadFailed);
            journal.Record(failure with { Command = ClientDiagnosticCommand.EnterText, SubmittedNumber = 123,
                Facts = failure.Facts with { DecisionSourceName = "PRIVATE-INJECTED-FACE", RemainingMana = [new("{U}", 1)] } });
            foreach (var invalid in new[] {
                failure with { SourceRead = null },
                failure with { Origin = ClientDiagnosticOrigin.Direct },
                failure with { SourceRead = failure.SourceRead! with { Reason = (ClientSourceReadReason)999 } },
                failure with { SourceRead = failure.SourceRead! with { Generation = -1 } },
                failure with { SourceRead = failure.SourceRead! with { CurrentEpoch = failure.SourceRead.Epoch + 1 } },
                failure with { Stage = ClientDiagnosticStage.DispatchStarted }
            }) journal.Record(invalid);
            check(await journal.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5)),
                "Source-read evidence reaches the existing bounded journal writer through its real durability barrier");
            await journal.DisposeAsync();
            await journal.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            string path = Path.Combine(root, "diagnostics", "client-decisions.jsonl");
            string text = File.ReadAllText(path);
            var entries = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonDocument.Parse(line).RootElement.Clone())
                .Where(line => line.GetProperty("type").GetString() == "event")
                .Select(line => line.GetProperty("diagnostic")).Where(line => line.GetProperty("origin").GetString() == "SourceRead").ToArray();
            check(entries.Length == 3 && !text.Contains("PRIVATE", StringComparison.Ordinal)
                    && entries.All(item => item.GetProperty("sourceRead").GetProperty("viewerId").GetInt32() == 0
                        && item.GetProperty("sourceRead").GetProperty("revision").GetInt64() == 7
                        && item.GetProperty("command").GetString() == "Unknown"
                        && item.GetProperty("submittedNumber").ValueKind == JsonValueKind.Null
                        && item.GetProperty("facts").GetProperty("decisionSourceName").ValueKind == JsonValueKind.Null
                        && item.GetProperty("facts").GetProperty("remainingMana").GetArrayLength() == 0)
                    && entries.Last().GetProperty("sourceRead").GetProperty("reason").GetString() == "Unavailable",
                "Source journal records retain useful numeric/closed evidence while rejecting malformed stamps and stripping faces, handles and exception text");
            passed = true;
        } finally {
            if (passed && root.StartsWith(temporary.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                Directory.Delete(root, true);
        }
    }

    private static SourceActionSet Catalog(SourceActionContext context, string action = "action")
        => new(context.GameId, context.DecisionId, context.SourceCardKey, context.SourceCardId,
            [new(action, "activate", "PRIVATE-ACTION-TEXT")]) { Revision = context.Revision };

    private static GameSnapshot State() => new("PRIVATE-GAME", "playing", null, null, 0, 1, "Main", "MAIN1", 0,
        [new(0, "Viewer", 40, true, false, ImmutableDictionary<string, int>.Empty,
            [new("Battlefield", 1, [new Card { Key = "PRIVATE-SOURCE-KEY", VisualId = "PRIVATE-SOURCE-ID", Name = "PRIVATE-FACE", Text = "PRIVATE-ORACLE" }], null)]),
         new(1, "Other", 40, false, false, ImmutableDictionary<string, int>.Empty, [])], [], null,
        new("PRIVATE-DECISION", new(), new PriorityPayload(new("Pass", true)))) {
            Revision = 7, BoardRevision = 5, DecisionOwnerId = 0, PriorityPlayerId = 0 };

    private static SourceActionContext Context(GameSnapshot state) => new(state.Id, state.Decision!.Id, state.Revision,
        state.ViewerId, "PRIVATE-SOURCE-KEY", "PRIVATE-SOURCE-ID");
    private sealed record Query(string Game, string Decision, string Key)
    {
        internal TaskCompletionSource<SourceActionSet> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class Sources : ISourceActions
    {
        internal ConcurrentQueue<Query> Requests { get; } = new();
        public Task<SourceActionSet> SourceActionsAsync(string gameId, string decisionId, string sourceCardKey)
        {
            var request = new Query(gameId, decisionId, sourceCardKey); Requests.Enqueue(request); return request.Result.Task;
        }
    }
    private sealed class Connection : IMatchConnection
    {
        internal int Commands;
        internal Exception? ExecuteError;
        public Task<GameSnapshot?> ObserveAsync() => throw new InvalidOperationException("Source reads must not poll their match implicitly.");
        public Task ExecuteAsync(GameCommand command) {
            Commands++; throw ExecuteError ?? new InvalidOperationException("Source reads grant no implicit commands.");
        }
        public Task ConcedeAsync(string gameId) => throw new InvalidOperationException("A source read cannot concede.");
    }
    private sealed class Driver : IDisposable
    {
        internal readonly Sources Sources = new();
        internal readonly Connection Connection = new();
        internal readonly ClientSession Client;
        internal readonly ConcurrentQueue<ClientDiagnostic> Events = new();
        internal Driver(bool throwingSink = false)
        {
            Client = new(Connection, sourceActions: Sources);
            if (throwingSink) Client.DiagnosticRecorded += _ => throw new InvalidOperationException("PRIVATE-SINK-FAULT");
            Client.DiagnosticRecorded += Events.Enqueue;
        }
        internal async Task<Query> Request(int count)
        {
            await Until(() => Sources.Requests.Count >= count); return Sources.Requests.ToArray()[count - 1];
        }
        internal async Task Until(Func<bool> ready)
        {
            var clock = Stopwatch.StartNew();
            do {
                Client.Pump(); if (ready()) return;
                if (clock.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("The source diagnostic owner did not reach its held gate.");
                await Task.Yield();
            } while (true);
        }
        public void Dispose() => Client.Close();
    }
}
