using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using Mana.Client;
using Mana.Contracts;

internal static class ReadAudienceLifecycleRegression
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        foreach (var transition in Enum.GetValues<Transition>())
        foreach (var completion in Enum.GetValues<Completion>())
        foreach (bool historyFirst in new[] { false, true })
            await HeldReads(check, transition, completion, historyFirst);
    }

    private static async Task HeldReads(Action<bool, string> check, Transition transition,
        Completion completion, bool historyFirst)
    {
        string label = $"Read lifecycle {transition}/{completion}/{(historyFirst ? "history-first" : "source-first")}";
        using var driver = new Driver(composed: transition != Transition.ReaderClear);
        var original = State();
        var originalContext = Context(original);
        check(driver.Open(original), label + ": the actual owners accept the initial audience");
        var oldSource = await driver.SourceRequest(1);
        var oldHistory = await driver.HistoryRequest(1);
        long oldGeneration = driver.Source.View.Generation;

        GameSnapshot current;
        if (transition == Transition.ViewerRoundTrip) {
            var other = original with { ViewerId = 1 };
            check(driver.Accept(other), label + ": ClientSession accepts the intermediate viewer");
            check(driver.Source.View is { Context: null, Actions.Count: 0 }
                && driver.History.Model.ViewerId == 1 && driver.History.Model.Records.IsEmpty,
                label + ": ClientSession revokes both readers without another reader observation");
            current = original;
        } else {
            driver.Clear(reset: transition == Transition.Reset);
            check(driver.Source.View is { Context: null, Actions.Count: 0 }
                && driver.History.Model.GameId == "" && driver.History.Model.Records.IsEmpty,
                label + ": leaving clears both readers before any replacement is accepted");
            // ClientSession retires a departed game. The direct-owner control can
            // reopen the identical context; session reset/null must bind a new game.
            current = transition == Transition.ReaderClear ? original : original with { Id = "replacement-game" };
        }
        check(driver.Open(current), label + ": the final audience is accepted and its source is reopened");
        var currentContext = Context(current);
        check(driver.Source.View.Generation > oldGeneration && driver.Source.View.Context == currentContext
            && driver.Source.View.Actions.Count == 0 && driver.History.Model.GameId == current.Id
            && driver.History.Model.ViewerId == current.ViewerId && driver.History.Model.Records.IsEmpty
            && driver.SourceRequests.Count == 1 && driver.HistoryRequests.Count == 1,
            label + ": both held requests coalesce intermediate scopes while revoking their data");
        if (transition is Transition.ViewerRoundTrip or Transition.ReaderClear)
            check(currentContext == originalContext, label + ": equal returning identities require lifetime authority beyond value equality");

        SourceRequest freshSource;
        HistoryRequest freshHistory;
        if (historyFirst) {
            Complete(oldHistory.Result, History(original, "OLD PRIVATE HISTORY", 99), completion);
            freshHistory = await driver.HistoryRequest(2);
            NoOldMaterial();
            Complete(oldSource.Result, Catalog(originalContext, "old-action", "OLD PRIVATE LINK"), completion);
            freshSource = await driver.SourceRequest(2);
        } else {
            Complete(oldSource.Result, Catalog(originalContext, "old-action", "OLD PRIVATE LINK"), completion);
            freshSource = await driver.SourceRequest(2);
            NoOldMaterial();
            Complete(oldHistory.Result, History(original, "OLD PRIVATE HISTORY", 99), completion);
            freshHistory = await driver.HistoryRequest(2);
        }
        NoOldMaterial();
        check(freshSource.Game == current.Id && freshSource.Decision == current.Decision!.Id
            && freshSource.Key == currentContext.SourceCardKey && freshHistory.Game == current.Id
            && freshHistory.After == 0 && freshHistory.Limit == HistoryFacts.MaximumBatchRecords,
            label + ": only the final audience is queried, with a fresh history cursor");

        freshSource.Result.SetResult(Catalog(currentContext, "fresh-action", "FRESH PRIVATE LINK"));
        freshHistory.Result.SetResult(History(current, "FRESH PRIVATE HISTORY", 1));
        await driver.Until(() => driver.Source.View.Status == SourceActionStatus.Ready && driver.History.IsCaughtUp);
        check(driver.Source.View.Actions.Single() is { Id: "fresh-action", Card.Name: "FRESH PRIVATE LINK" }
            && driver.Source.TryCreateCommand(currentContext, "fresh-action") == new UseOfferedAction(
                current.Id, current.Decision!.Id, currentContext.SourceCardKey, "fresh-action")
            && driver.Source.TryCreateCommand(currentContext, "old-action") == null,
            label + ": only the freshly returned opaque offer can create a command");
        check(driver.History.Model.Records.Single() is { Sequence: 1, SourceName: "FRESH PRIVATE HISTORY", Exposure: "seatPrivate" }
            && driver.History.Model.Cursor == 1 && driver.History.ObservedRevision == current.Revision
            && driver.Admissions == 1 && driver.HistoryUpdates == 1,
            label + ": fresh private history is admitted exactly once, with no old success or failure notification");
        long readyGeneration = driver.Source.View.Generation;
        check(driver.Open(current), label + ": an unchanged final observation remains admissible");
        driver.Pump();
        check(driver.Source.View.Generation == readyGeneration && driver.SourceRequests.Count == 2
            && driver.HistoryRequests.Count == 2 && driver.Admissions == 1 && driver.HistoryUpdates == 1
            && driver.Connection.Commands == 0 && driver.Connection.Observations == 0 && driver.Failures == 0,
            label + ": settled reads neither replay nor issue rules commands, polls or connection failures");

        void NoOldMaterial() => check(driver.Source.View.Context == currentContext
            && driver.Source.View.Actions.Count == 0 && driver.Source.View.Error == "" && driver.Source.Supported
            && driver.Source.View.Status == SourceActionStatus.Loading
            && driver.Source.TryCreateCommand(currentContext, "old-action") == null
            && driver.History.Model.GameId == current.Id && driver.History.Model.ViewerId == current.ViewerId
            && driver.History.Model.Records.IsEmpty && driver.History.Model.Cursor == 0
            && driver.History.Error == "" && driver.History.Supported
            && driver.History.Status is not (MatchHistoryStatus.Failed or MatchHistoryStatus.Unsupported)
            && driver.Admissions == 0 && driver.HistoryUpdates == 0,
            label + ": a completed obsolete read cannot publish private data, error or capability loss");
    }

    private enum Transition { ViewerRoundTrip, Reset, Null, ReaderClear }
    private enum Completion { Success, Failure, Unsupported }

    private static void Complete<T>(TaskCompletionSource<T> result, T value, Completion completion)
    {
        if (completion == Completion.Success) result.SetResult(value);
        else result.SetException(completion == Completion.Unsupported
            ? new NotSupportedException("OLD PRIVATE capability failure") : new IOException("OLD PRIVATE provider failure"));
    }

    private static GameSnapshot State() => new("original-game", "playing", null, null, 0, 57, "Main", "MAIN1", 0,
        [new(0, "You", 40, true, false, ImmutableDictionary<string, int>.Empty,
            [new("Battlefield", 1, [new() { Key = "source-key", VisualId = "source-occurrence", Name = "Source" }], null)]),
         new(1, "Other", 40, false, false, ImmutableDictionary<string, int>.Empty, [])], [], null,
        new("same-priority", new(), new PriorityPayload(new("Pass", true)))) { Revision = 4 };

    private static SourceActionContext Context(GameSnapshot state)
    {
        var source = state.Players[0].Zone("Battlefield").Cards.Single();
        return new(state.Id, state.Decision!.Id, state.Revision, state.ViewerId, source.Key, CardViews.Identity(source));
    }

    private static SourceActionSet Catalog(SourceActionContext context, string action, string name) => new(
        context.GameId, context.DecisionId, context.SourceCardKey, context.SourceCardId,
        [new(action, "cast", "Cast " + name) { Card = new() { Name = name, VisualId = name }, Zone = "Exile" }]) {
            Revision = context.Revision };

    private static HistoryBatch History(GameSnapshot state, string name, long sequence) => new(
        1, state.Id, state.ViewerId, 1, sequence, sequence, false,
        [new(sequence, state.Turn, state.PhaseKey, "seatPrivate") {
            SourceName = name, SourceCardId = name,
            Activity = new(sequence, state.Turn, state.PhaseKey, "draw", state.ViewerId, name, null, null) { Count = 1 } }]);

    private sealed class Driver : IDisposable
    {
        private readonly ConcurrentQueue<Action> posted = new();
        private readonly ClientSession? client;
        internal readonly Connection Connection = new();
        internal readonly Sources SourceRequests = new();
        internal readonly Histories HistoryRequests = new();
        internal readonly SourceActionSession Source;
        internal readonly MatchHistorySession History;
        internal int Admissions, HistoryUpdates, Failures;

        internal Driver(bool composed)
        {
            if (composed) {
                client = new(Connection, sourceActions: SourceRequests, history: HistoryRequests);
                Source = client.SourceActions; History = client.History;
                client.Failed += _ => Failures++;
            } else {
                Source = new(SourceRequests, posted.Enqueue);
                History = new(HistoryRequests, posted.Enqueue);
            }
            History.Accepted += _ => Admissions++;
            History.Updated += () => HistoryUpdates++;
        }

        internal bool Open(GameSnapshot state)
        {
            if (!Accept(state)) return false;
            Source.Observe(Context(state));
            return true;
        }

        internal bool Accept(GameSnapshot? state)
        {
            if (client != null) return client.Accept(state);
            History.Observe(state);
            return true;
        }

        internal void Clear(bool reset)
        {
            if (client != null) {
                if (reset) client.Reset();
                else if (!client.Accept(null)) throw new Exception("ClientSession rejected leaving the test match.");
            } else { Source.Clear(); History.Observe(null); }
        }

        internal void Pump()
        {
            if (client != null) client.Pump();
            else while (posted.TryDequeue(out var action)) action();
        }

        internal async Task Until(Func<bool> ready)
        {
            var elapsed = Stopwatch.StartNew();
            while (!ready()) {
                Pump();
                if (elapsed.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("A held read lifecycle did not reach its controlled completion gate.");
                await Task.Yield();
            }
        }

        internal async Task<SourceRequest> SourceRequest(int count)
        {
            await Until(() => SourceRequests.Count >= count);
            return SourceRequests.Requests.ToArray()[count - 1];
        }
        internal async Task<HistoryRequest> HistoryRequest(int count)
        {
            await Until(() => HistoryRequests.Count >= count);
            return HistoryRequests.Requests.ToArray()[count - 1];
        }
        public void Dispose()
        {
            if (client != null) client.Close();
            else { Source.Close(); History.Close(); }
        }
    }

    private sealed record SourceRequest(string Game, string Decision, string Key)
    {
        internal readonly TaskCompletionSource<SourceActionSet> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class Sources : ISourceActions
    {
        internal readonly ConcurrentQueue<SourceRequest> Requests = new();
        internal int Count => Requests.Count;
        public Task<SourceActionSet> SourceActionsAsync(string gameId, string decisionId, string sourceCardKey)
        {
            var request = new SourceRequest(gameId, decisionId, sourceCardKey);
            Requests.Enqueue(request);
            return request.Result.Task;
        }
    }
    private sealed record HistoryRequest(string Game, long After, int Limit)
    {
        internal readonly TaskCompletionSource<HistoryBatch> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class Histories : IMatchHistory
    {
        internal readonly ConcurrentQueue<HistoryRequest> Requests = new();
        internal int Count => Requests.Count;
        public Task<HistoryBatch> HistoryAsync(string gameId, long afterSequence, int limit = 128)
        {
            var request = new HistoryRequest(gameId, afterSequence, limit);
            Requests.Enqueue(request);
            return request.Result.Task;
        }
    }
    private sealed class Connection : IMatchConnection
    {
        internal int Commands, Observations;
        public Task<GameSnapshot?> ObserveAsync() { Observations++; throw new Exception("A read lifecycle must not poll rules."); }
        public Task ExecuteAsync(GameCommand command) { Commands++; throw new Exception("A read lifecycle must not execute rules."); }
        public Task ConcedeAsync(string gameId) => throw new Exception("A read lifecycle must not concede.");
    }
}
