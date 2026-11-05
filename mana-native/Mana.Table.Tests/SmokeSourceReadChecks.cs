using Mana.Contracts;

namespace Mana.Table;

internal static class SmokeSourceReadChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var port = new Port { QueryError = new EngineSourceReadException(EngineSourceReadFailure.Superseded) };
        check(!await NativeSmoke.TryActivateManaAsync(port, port, "game", "old-decision", "old-source")
                && port.Reads.SequenceEqual([("game", "old-decision", "old-source")]) && port.Commands.Count == 0,
            "The actual smoke activation retires a superseded query without dispatch or an implicit query retry");
        port.QueryError = null;
        check(await NativeSmoke.TryActivateManaAsync(port, port, "game", "fresh-decision", "fresh-source")
                && port.Reads.Count == 2 && port.Commands.Single() == new UseOfferedAction("game", "fresh-decision", "fresh-source", "offer-fresh-source"),
            "Only a caller's fresh observation/source can dispatch its newly queried smoke offer once");

        port = new() { ExecuteError = new EngineCommandRejectedException("That command does not answer the currently observed decision. Observe the table again.") };
        check(!await NativeSmoke.TryActivateManaAsync(port, port, "game", "old-decision", "old-source")
                && port.Reads.Count == 1 && port.Commands.Count == 1,
            "The smoke preserves its separate definite dispatch rejection without treating it as a query result or replaying");
        port.ExecuteError = null;
        check(await NativeSmoke.TryActivateManaAsync(port, port, "game", "fresh-decision", "fresh-source")
                && port.Reads.Count == 2 && port.Commands.Count == 2
                && port.Commands[1] == new UseOfferedAction("game", "fresh-decision", "fresh-source", "offer-fresh-source"),
            "After definite dispatch rejection only the next fresh smoke offer is dispatched");

        foreach (var error in new Exception[] {
            new EngineSourceReadException(EngineSourceReadFailure.Changed),
            new EngineSourceReadException(EngineSourceReadFailure.Failed),
            new IOException("Unknown provider failure"),
            new EngineCommandRejectedException("That command does not answer the currently observed decision. Observe the table again.") }) {
            port = new() { QueryError = error };
            var failure = await Failure(() => NativeSmoke.TryActivateManaAsync(port, port, "game", "decision", "source"));
            check(ReferenceEquals(failure, error) && port.Reads.Count == 1 && port.Commands.Count == 0,
                "A non-superseded smoke query failure propagates unchanged without dispatch/retry: " + Label(error));
        }

        foreach (var error in new Exception[] {
            new EngineSourceReadException(EngineSourceReadFailure.Superseded),
            new EngineSourceReadException(EngineSourceReadFailure.Changed),
            new EngineSourceReadException(EngineSourceReadFailure.Failed),
            new IOException("Unknown execution outcome"),
            new EngineCommandRejectedException("The table changed. Read the current card again.") }) {
            port = new() { ExecuteError = error };
            var failure = await Failure(() => NativeSmoke.TryActivateManaAsync(port, port, "game", "decision", "source"));
            check(ReferenceEquals(failure, error) && port.Reads.Count == 1 && port.Commands.Count == 1,
                "The smoke never launders an execution failure into source supersession/replay: " + Label(error));
        }

        foreach (string legacy in new[] {
            "The table changed. Read the current card again.",
            "The source actions changed. Read the current card again.",
            "The source action context changed. Select the current card again." }) {
            int reads = 0;
            var offers = await SmokeSourceReads.TryReadAsync(() => {
                reads++; return Task.FromException<SourceActionSet>(new EngineCommandRejectedException(legacy));
            }, "The table changed. Read the current card again.",
                "The source actions changed. Read the current card again.",
                "The source action context changed. Select the current card again.");
            check(offers == null && reads == 1,
                "The conformance driver's exact legacy query rejection stays a one-read retirement: " + legacy);
        }
    }

    private static string Label(Exception error) => error is EngineSourceReadException source ? source.Reason.ToString() : error.GetType().Name;
    private static async Task<Exception?> Failure(Func<Task<bool>> action)
    {
        try { _ = await action(); return null; } catch (Exception error) { return error; }
    }
    private sealed class Port : ISourceActions, IMatchConnection
    {
        internal Exception? QueryError, ExecuteError;
        internal List<(string Game, string Decision, string Source)> Reads { get; } = [];
        internal List<GameCommand> Commands { get; } = [];
        public Task<SourceActionSet> SourceActionsAsync(string gameId, string decisionId, string sourceCardKey)
        {
            Reads.Add((gameId, decisionId, sourceCardKey));
            return QueryError is { } error ? Task.FromException<SourceActionSet>(error)
                : Task.FromResult(new SourceActionSet(gameId, decisionId, sourceCardKey, "physical-source",
                    [new("offer-" + sourceCardKey, "activate", "Add mana")]));
        }
        public Task ExecuteAsync(GameCommand command)
        {
            Commands.Add(command);
            return ExecuteError is { } error ? Task.FromException(error) : Task.CompletedTask;
        }
        public Task<GameSnapshot?> ObserveAsync() => throw new InvalidOperationException("The query helper must not acquire an observation owner.");
        public Task ConcedeAsync(string gameId) => throw new NotSupportedException();
    }
}
