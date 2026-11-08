using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Mana.Client;
using Mana.Contracts;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

internal static class SmokeSourceReadChecks
{
    internal static async Task RunAsync(string profile, Action<bool, string> check)
    {
        await CheckExternalReaderAsync(profile, check);
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

    // Authored source outcomes exercise the production reader and the caller's
    // ordinary observation owner. They do not infer the uncaptured CI failure
    // reason or simulate a Forge payment/permission decision.
    private static async Task CheckExternalReaderAsync(string profile, Action<bool, string> check)
    {
        foreach (string mode in new[] { "same-revision", "newer-revision", "adapter-rejected", "typed-superseded" }) {
            string directory = Path.Combine(profile, "external-reader-" + mode);
            Directory.CreateDirectory(directory);
            new PlayPreferences { Automatic = false, ReducedMotion = true, Sounds = false, TextScale = 1.3f }.Save(directory);
            var forest = new Card { Key = "current-forest-key", VisualId = "current-forest", CombatId = "current-forest",
                Name = "Forest", Type = "Basic Land — Forest", Selectable = true };
            var players = Enumerable.Range(0, 4).Select(id => new Player(id, "Player " + id, 40, id == 0, false,
                ImmutableDictionary<string, int>.Empty, [
                    new("Battlefield", id == 0 ? 1 : 0, id == 0 ? [forest] : [], null),
                    new("Hand", 0, [], null), new("Command", 0, [], null), new("Library", 0, [], null),
                    new("Graveyard", 0, [], null), new("Exile", 0, [], null)
                ])).ToImmutableArray();
            var state = new GameSnapshot("external-source-reader", "playing", null, null, 0, 1, "Main phase", "MAIN1", 0,
                players, [], null, new("current-payment", new(Message: "Pay {G/W}."),
                    new PaymentPayload(new("Auto", true), new("Cancel", true)) { UnpaidManaParts = [new("{G/W}", 1)], PayerId = 0 })) {
                Revision = 69, BoardRevision = 17, DecisionOwnerId = 0, PriorityPlayerId = 0
            };
            var reader = new ReaderPort(state, forest);
            var diagnostics = new ConcurrentQueue<ClientDiagnostic>();
            var setup = new InteractionScenarioEngine();
            using var run = new PresentationRun(new TableServices(setup, setup, reader, () => Task.CompletedTask) {
                SourceActions = reader, Diagnostics = diagnostic => {
                    if (diagnostic.Origin != ClientDiagnosticOrigin.SourceRead) return;
                    diagnostics.Enqueue(diagnostic);
                    while (diagnostics.Count > 64) diagnostics.TryDequeue(out _);
                }
            }, directory, state, new(1600, 900), ObservationDeliveryMode.External);
            var view = run.View;
            Point pointer = new(20, 60);
            PresentationFrame? evidenceFrame = null;
            bool same = mode == "same-revision";
            bool? readyBeforeRefresh = null;
            string? caseError = null;
            PresentationFrame Tick(ButtonState left = ButtonState.Released)
            {
                var current = run.Tick(TimeSpan.FromSeconds(1d / 60), new(pointer.X, pointer.Y, 0, left,
                    ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released));
                evidenceFrame = current; return current;
            }
            try {
                var frame = Tick(); view.SetTableStudy(true); frame = Tick();
                var source = frame.Cards.Single(card => card.Id == forest.VisualId && card.Zone == "Battlefield");
                var hit = frame.Hits.Last(item => item.Id == source.Hit.Id && item.Scope == source.Hit.Scope);
                bool Exposed(Point point) => view.HitAt(point) is { } current && current.Id == hit.Id && current.Scope == hit.Scope;
                pointer = hit.Bounds.Center;
                if (!Exposed(pointer)) {
                    bool found = false;
                    for (int y = hit.Bounds.Top + 2; y < hit.Bounds.Bottom - 2 && !found; y += 3)
                        for (int x = hit.Bounds.Left + 2; x < hit.Bounds.Right - 2; x += 3)
                            if (Exposed(new(x, y))) { pointer = new(x, y); found = true; break; }
                    check(found, "The authored Forest payment reader has a physical exposed pointer target: " + mode);
                }
                frame = Tick(ButtonState.Pressed); frame = Tick(); frame = Tick(); frame = Tick();
                for (int i = 0; i < 1000 && reader.SourceReads == 0; i++) { frame = Tick(); await Task.Delay(1); }
                check(reader.SourceReads == 1 && frame.Cards.Any(card => card.Id == forest.VisualId && card.Focused),
                    "The production external Forest reader starts one held source query without answering payment: " + mode);
                Exception? queryError = mode == "adapter-rejected" ? new EngineCommandRejectedException("Controlled source rejection")
                    : mode == "typed-superseded" ? new EngineSourceReadException(EngineSourceReadFailure.Superseded) : null;
                reader.ReleaseFirst(same ? 69 : 70, queryError);
                var expected = same ? ClientDiagnosticStage.SourceReadAccepted : mode == "adapter-rejected"
                    ? ClientDiagnosticStage.SourceReadFailed : ClientDiagnosticStage.SourceReadStale;
                for (int i = 0; i < 1000 && !diagnostics.Any(row => row.Stage == expected); i++) { frame = Tick(); await Task.Delay(1); }
                check(diagnostics.Any(row => row.Stage == expected), "The controlled reader records its actual terminal source-read status: " + mode);
                for (int i = 0; i < 64; i++) frame = Tick();
                readyBeforeRefresh = frame.Hits.Any(item => item.Id == "read-card-action");
                check(view.Match?.Revision == 69 && reader.OrdinaryReads == 0 && reader.SourceReads == 1 && reader.Commands == 0
                        && readyBeforeRefresh == same,
                    "Sixty-four Tick-only pumps retain the old external scope without autopoll, query retry, authority or commands: " + mode);
                if (!same) {
                    var reason = mode == "newer-revision" ? ClientSourceReadReason.ContextMismatch
                        : mode == "adapter-rejected" ? ClientSourceReadReason.Rejected : ClientSourceReadReason.Superseded;
                    check(diagnostics.Any(row => row.Stage == expected && row.SourceRead?.Reason == reason),
                        "A stale or rejected source catalog retains its method-specific reason without replacing the displayed publication: " + mode);
                    var fresh = await reader.ObserveAsync() ?? throw new InvalidOperationException("Missing controlled ordinary publication.");
                    run.QueueObservation(fresh); frame = Tick();
                    for (int i = 0; i < 1000 && !frame.Hits.Any(item => item.Id == "read-card-action"); i++) { frame = Tick(); await Task.Delay(1); }
                    check(view.Match?.Revision == 70 && frame.Hits.Any(item => item.Id == "read-card-action")
                            && reader.OrdinaryReads == 1 && reader.SourceReads == 2 && reader.Commands == 0,
                        "Exactly one owned ordinary publication restores the freshly scoped reader without a command or direct source retry: " + mode);
                }
            } catch (Exception error) { caseError = error.ToString(); throw; }
            finally {
                try {
                    File.WriteAllText(Path.Combine(directory, "source-reader-evidence.json"), JsonSerializer.Serialize(new {
                        mode, passed = caseError == null, error = caseError,
                        initialRevision = 69, replyRevision = same ? 69 : 70, readyBeforeRefresh,
                        finalRevision = view.Match?.Revision, finalReady = evidenceFrame?.Hits.Any(item => item.Id == "read-card-action") == true,
                        reader.OrdinaryReads, reader.SourceReads, reader.Commands,
                        sourceDiagnostics = diagnostics.Select(diagnostic => new { stage = diagnostic.Stage.ToString(),
                            reason = diagnostic.SourceRead?.Reason.ToString(), diagnostic }),
                        originalCiSourceReadReasonKnown = false
                    }, new JsonSerializerOptions { WriteIndented = true }));
                } catch when (caseError != null) {
                    // Preserve the original assertion/read failure if capture also fails.
                }
            }
        }
    }

    private sealed class ReaderPort : IMatchConnection, ISourceActions
    {
        private readonly GameSnapshot initial;
        private readonly Card forest;
        private readonly TaskCompletionSource<SourceActionSet> first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private GameSnapshot current;
        private int ordinaryReads, sourceReads, commands;
        internal ReaderPort(GameSnapshot initial, Card forest) { this.initial = initial; this.forest = forest; current = initial; }
        internal int OrdinaryReads => Volatile.Read(ref ordinaryReads);
        internal int SourceReads => Volatile.Read(ref sourceReads);
        internal int Commands => Volatile.Read(ref commands);
        public bool SupportsSourceActions => true;
        private SourceActionSet Offers(long revision) => new(initial.Id, initial.Decision!.Id, forest.Key, forest.VisualId,
            [new("current-forest-mana", "activate", "{T}: Add {G}.")]) { Revision = revision };
        internal void ReleaseFirst(long revision, Exception? error)
        {
            current = initial with { Revision = revision };
            if (error != null) first.SetException(error); else first.SetResult(Offers(revision));
        }
        public Task<SourceActionSet> SourceActionsAsync(string game, string decision, string key)
        {
            if (game != initial.Id || decision != initial.Decision!.Id || key != forest.Key)
                throw new InvalidOperationException("Foreign controlled source scope.");
            return Interlocked.Increment(ref sourceReads) == 1 ? first.Task : Task.FromResult(Offers(current.Revision));
        }
        public Task<GameSnapshot?> ObserveAsync() { Interlocked.Increment(ref ordinaryReads); return Task.FromResult<GameSnapshot?>(current); }
        public Task ExecuteAsync(GameCommand command)
        {
            Interlocked.Increment(ref commands);
            throw new InvalidOperationException("The read-only controlled reader must not submit a command.");
        }
        public Task ConcedeAsync(string gameId) => throw new NotSupportedException();
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
