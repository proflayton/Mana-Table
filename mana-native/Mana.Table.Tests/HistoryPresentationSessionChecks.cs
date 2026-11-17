using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Mana.Client;
using Mana.Contracts;
using Mana.Scenarios;
using Mana.Table.Interaction;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;

namespace Mana.Table;

internal static class HistoryPresentationSessionChecks
{
    internal static void Run(string profile, Action<bool, string> check)
        => RunAsync(profile, check).GetAwaiter().GetResult();

    private static async Task RunAsync(string profile, Action<bool, string> check)
    {
        Navigation(check);
        await BoundedReads(check);
        await StaleCompletions(check);
        await AudienceAndDisposal(check);
        await ExportLifetime(check);
        await RetiredExport(check);
        await ProductionFailure(profile, check);
    }
    private static MatchHistoryReadModel Model(string game = "history-owner", int viewer = 0, string suffix = "")
    {
        var records = Enumerable.Range(1, 2).Select(index => new HistoryRecord(index, 1, "MAIN1", "public") {
            ExecutionId = "play-" + index, SourceCardId = "source-" + index, SourceName = "Recorded " + index + suffix,
            Execution = new(new ExecutionOccurrence("play-" + index, "play-" + index, ExecutionKind.Spell, ExecutionStep.Stack) {
                SourceCardId = "source-" + index, SourceName = "Recorded " + index + suffix, ActorPlayerId = viewer })
        }).ToImmutableArray();
        if (suffix.Length > 0) records = records.Add(new(3, 1, "MAIN1", "public") {
            ExecutionId = "play-1", Activity = new(1, 1, "MAIN1", "effect", viewer,
                "Retained result" + suffix, "source-1", "Recorded 1" + suffix) { ExecutionId = "play-1" }
        });
        return MatchHistoryReadModel.Build(game, viewer, records);
    }
    private static HistoryArchiveSummary Summary(string id = "archive", int viewer = 0) => new(id, "history-owner", viewer,
        "test", "source", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, HistoryArchiveState.Closed,
        true, 2, 2, 2, false, 64, "hash");
    private static HistoryArchiveDocument Document(string id = "archive", int viewer = 0, string suffix = "")
    {
        var model = Model(viewer: viewer, suffix: suffix);
        return new(Summary(id, viewer) with { RecordCount = model.Records.Length, LastSequence = model.Cursor, ObservedThrough = model.Cursor },
            [new(1, model.GameId, viewer, 1, model.Cursor, model.Cursor, false, model.Records)], []);
    }
    private static void Observe(HistoryPresentationSession reader, int viewer = 0, string suffix = "")
        => reader.ObserveLive("history-owner", viewer, Model(viewer: viewer, suffix: suffix), MatchHistoryStatus.Ready, "");

    private static void Navigation(Action<bool, string> check)
    {
        var queue = new Posted();
        using var reader = new HistoryPresentationSession(null, queue.Post);
        Observe(reader); reader.Open(false);
        var first = reader.CaptureView();
        check(first.Selected?.Id == "play-2" && first.SeenSequence == 2 && !first.PrivateReport,
            "History opens on the admitted newest action with the existing seen cursor and public report default");
        reader.Move(-1);
        check(reader.Selected?.Id == "play-1", "History previous navigation retains the exact recorded occurrence");
        string details = reader.Details(reader.Selected!);
        var beforeRefresh = reader.CaptureView();
        reader.ObserveLive("history-owner", 0, Model(suffix: " refreshed"), MatchHistoryStatus.Ready, "");
        check(reader.Details(reader.Selected!) != details && reader.Selected!.SourceName.EndsWith("refreshed"),
            "A replaced immutable model with the same action ID cannot reuse the old detail text");
        check(reader.Scope == beforeRefresh.Scope && reader.CaptureView().Revision != beforeRefresh.Revision
            && !reader.ReconcilePages(beforeRefresh, 2, 4, out _),
            "A same-audience history refresh preserves unchanged gesture authority while retiring its measured composition basis");
        reader.MoveDetails(int.MaxValue);
        var basis = reader.CaptureView();
        check(reader.ReconcilePages(basis, 2, 4, out var final) && final is { DetailsPage: 1 }
            && final.Scope == reader.Scope && final.Scope != basis.Scope,
            "Measured page clamping returns one final immutable view and its new scope for text and enabled hits");
        check(!reader.ReconcilePages(basis, 2, 4, out _), "A retired measurement basis cannot finalize another history frame");
        var stable = reader.CaptureView(); reader.Tick();
        check(ReferenceEquals(stable, reader.CaptureView()), "An idle history tick preserves its immutable view and hit generation");
        reader.InspectSource(); reader.ReturnFromSource();
        check(reader.Selected?.Id == "play-1" && reader.CaptureView().DetailsPage == 1,
            "Entering and returning from source reading preserves the selected action and detail page");
        reader.Newest();
        check(reader.Selected?.Id == "play-2" && reader.CaptureView().DetailsPage == 0,
            "Newest explicitly returns to the current final action and resets details");
    }

    private static async Task BoundedReads(Action<bool, string> check)
    {
        var port = new ArchivePort(); var queue = new Posted(); var clock = new Clock();
        using var reader = new HistoryPresentationSession(port, queue.Post, clock);
        Observe(reader); reader.Open(true);
        var old = await port.Next("list");
        for (int i = 0; i < 80; i++) { reader.Close(); reader.Open(true); reader.LoadSaved(); }
        check(port.Calls == 1 && reader.HasOutstandingIo && reader.Loading && !old.Token.CanBeCanceled,
            "Repeated close/open/retry retains one actual uncancelled archive operation instead of accumulating canceled reads");
        clock.Advance(TimeSpan.FromSeconds(11)); reader.Tick();
        check(reader.HasOutstandingIo && !reader.Loading && reader.CaptureView().Message.Contains("taking too long"),
            "Logical history timeout removes loading authority without claiming the outstanding storage task ended");
        old.Done.TrySetResult(ImmutableArray.Create(Summary("obsolete")));
        await Until(() => !reader.HasOutstandingIo);
        reader.Tick(); queue.Pump();
        check(port.Calls == 1 && reader.CaptureView().SavedMatches.IsEmpty,
            "An expired latest pending read never dispatches when the old actual operation finally settles");
        reader.LoadSaved(); var fresh = await port.Next("list");
        fresh.Done.TrySetResult(ImmutableArray.Create(Summary("fresh")));
        await Settle(reader, queue);
        check(reader.CaptureView().SavedMatches.Single().ArchiveId == "fresh",
            "A deliberately fresh read remains usable after the timed-out storage owner retires");
    }

    private static async Task StaleCompletions(Action<bool, string> check)
    {
        foreach (bool fail in new[] { false, true }) {
            var port = new ArchivePort(); var queue = new Posted();
            using var reader = new HistoryPresentationSession(port, queue.Post);
            Observe(reader); reader.Open(true); var old = await port.Next("list");
            reader.Close();
            if (fail) old.Done.TrySetException(new IOException("old read failed"));
            else old.Done.TrySetResult(ImmutableArray.Create(Summary("old")));
            await Until(() => !reader.HasOutstandingIo); // Hold its posted view completion.
            reader.Open(true); var fresh = await port.Next("list");
            fresh.Done.TrySetResult(ImmutableArray.Create(Summary("current")));
            await Until(() => !reader.HasOutstandingIo); reader.Tick();
            var current = reader.CaptureView(); queue.Pump();
            check(reader.CaptureView().SavedMatches.Single().ArchiveId == "current" && reader.CaptureView().Message.Length == 0
                && reader.Scope == current.Scope,
                "A held obsolete " + (fail ? "failure" : "success") + " cannot replace a successfully reopened history reader");
        }
    }

    private static async Task AudienceAndDisposal(Action<bool, string> check)
    {
        var port = new ArchivePort(); var queue = new Posted();
        using var reader = new HistoryPresentationSession(port, queue.Post);
        Observe(reader); reader.Open(true);
        (await port.Next("list")).Done.TrySetResult(ImmutableArray.Create(Summary()));
        await Settle(reader, queue);
        reader.LoadArchive("archive"); var old = await port.Next("archive");
        Observe(reader, 1); Observe(reader, 0, " current"); reader.Open(false);
        old.Done.TrySetResult(Document(suffix: " concealed old read"));
        await Settle(reader, queue);
        check(reader.CaptureView() is { Source: HistoryReadingSource.Live, Document: null, PrivateReport: false }
            && reader.Selected!.SourceName.EndsWith("current"),
            "Game/viewer ABA rejects a late saved document even after the original audience values return");
        reader.Open(true); var held = await port.Next("list");
        reader.Dispose(); held.Done.TrySetException(new IOException("retired failure"));
        await Until(() => !reader.HasOutstandingIo); queue.Pump(); reader.Tick();
        check(reader.Surface == HistorySurface.Closed && reader.Model.Actions.IsEmpty && port.Closures.Count == 0 && port.Records == 0,
            "Disposed history readers observe late failure without retaining facts, dispatching more work or acquiring archive recording/closure");
    }

    private static async Task ExportLifetime(Action<bool, string> check)
    {
        var port = new ArchivePort(); var queue = new Posted(); var clock = new Clock();
        var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int writes = 0;
        using var reader = new HistoryPresentationSession(port, queue.Post, clock, async (_, bytes) => {
            Interlocked.Increment(ref writes); writing.TrySetResult(); await finishWrite.Task;
        });
        Observe(reader); reader.Open(false); reader.TogglePrivateReport();
        check(reader.Export("owned-report.json", reader.Scope), "An explicit current history export captures its confirmed report request");
        (await port.Next("list")).Done.TrySetResult(ImmutableArray.Create(Summary()));
        var export = await port.Next("export"); reader.TogglePrivateReport();
        check(export.Scope == HistoryExportScope.IncludeOwnPrivate && !export.Token.CanBeCanceled,
            "The confirmed export keeps its original own-private scope despite later report toggles");
        export.Done.TrySetResult(Encoding.UTF8.GetBytes("prepared report")); await writing.Task.WaitAsync(TimeSpan.FromSeconds(3));
        reader.Close(); clock.Advance(TimeSpan.FromSeconds(11)); reader.Tick();
        check(reader.Surface == HistorySurface.Closed && !reader.Loading && reader.HasOutstandingIo && reader.WritingReport,
            "Closing an expired report view retains actual destination-write accounting for future passive-mount deferral");
        reader.Open(false);
        check(!reader.Export("duplicate-report.json", reader.Scope) && writes == 1,
            "A second export cannot queue or replay while the original confirmed destination write is outstanding");
        finishWrite.TrySetResult(); await Until(() => !reader.HasOutstandingIo);
        check(!reader.WritingReport, "Actual export retirement is observable without pumping the old presentation callback");
        queue.Pump(); reader.Tick();
        check(reader.CaptureView().Message.Length == 0 && reader.CanExport && writes == 1,
            "A retired export cannot reopen its old result message and a fresh explicit save remains available");
    }

    private static async Task RetiredExport(Action<bool, string> check)
    {
        var port = new ArchivePort(); var queue = new Posted(); int writes = 0;
        using var reader = new HistoryPresentationSession(port, queue.Post, writeReport: (_, _) => { writes++; return Task.CompletedTask; });
        Observe(reader); reader.Open(false); reader.Export("never-written.json", reader.Scope);
        (await port.Next("list")).Done.TrySetResult(ImmutableArray.Create(Summary()));
        var exporting = await port.Next("export"); reader.Close();
        exporting.Done.TrySetResult(Encoding.UTF8.GetBytes("prepared after close"));
        await Until(() => !reader.HasOutstandingIo); queue.Pump(); reader.Tick();
        check(writes == 0 && reader.Surface == HistorySurface.Closed,
            "An export retired during archive preparation never begins a destination write afterward");
    }

    private static async Task ProductionFailure(string profile, Action<bool, string> check)
    {
        var path = Path.Combine(profile, "history-owner-production"); Directory.CreateDirectory(path);
        new PlayPreferences { Automatic = false, ReducedMotion = true, Sounds = false }.Save(path);
        var state = PresentationSmoke.Example(); var engine = new InteractionScenarioEngine(); engine.Present(state);
        state = engine.State!; // Present issues the controlled port's actual fresh decision identity.
        var archive = new ArchivePort();
        using var run = new PresentationRun(TestTableServices.For(engine) with { HistoryArchive = archive }, path, state, new(1000, 740));
        run.Tick(TimeSpan.Zero, default);
        var records = Model(state.Id, state.ViewerId).Records;
        check(run.View.AcceptHistory(new(1, state.Id, state.ViewerId, 1, 2, 2, false, records)),
            "The production application admits the retained facts used by the history extraction journey");
        var frame = run.Tick(TimeSpan.FromSeconds(.016), default);
        check(run.View.Activate(frame.Hits.Single(hit => hit.Id == "history")), "The production History gesture opens the extracted reader");
        frame = run.Tick(TimeSpan.FromSeconds(.016), default);
        check(frame.Hits.Where(hit => hit.Intent is HistoryIntent).All(hit => run.View.CurrentHit(hit))
            && frame.Hits.Where(hit => hit.Intent is HistoryIntent).Select(hit => hit.Scope).Distinct().Count() == 1,
            "Published history text and enabled controls share one final current history scope");
        var heldPrevious = frame.Hits.Single(hit => hit.Id == "history-previous");
        var appended = new HistoryRecord(3, 1, "MAIN1", "public") {
            ExecutionId = "play-3", SourceName = "A later recorded play",
            Execution = new(new ExecutionOccurrence("play-3", "play-3", ExecutionKind.Spell, ExecutionStep.Stack) {
                SourceName = "A later recorded play", ActorPlayerId = state.ViewerId })
        };
        check(run.View.AcceptHistory(new(1, state.Id, state.ViewerId, 3, 3, 3, false, [appended])),
            "A later admitted history record arrives while an actual previous-play control is held");
        frame = run.Tick(TimeSpan.Zero, default);
        check(run.View.CurrentHit(heldPrevious) && run.View.Activate(heldPrevious) && run.View.HistorySelectionId == "play-1",
            "An unchanged held History control remains usable after background model refresh through production input");
        check(!run.View.CurrentHit(heldPrevious) && !run.View.Activate(heldPrevious),
            "The same held control retires after its explicit selected-action navigation changes interaction identity");
        frame = run.Tick(TimeSpan.Zero, default);
        run.View.Activate(frame.Hits.Single(hit => hit.Id == "history-saved"));
        (await archive.Next("list")).Done.TrySetException(new IOException("controlled storage failure"));
        await Until(() => {
            frame = run.Tick(TimeSpan.Zero, default);
            return frame.Observe().Text.Any(text => text.Value.Contains("Saved history could not be read"));
        });
        run.View.Activate(frame.Hits.Single(hit => hit.Id == "history-close")); run.Tick(TimeSpan.Zero, default);
        check(run.View.SubmitCurrent(new AutoPassPriority(state.Id, state.Decision!.Id)),
            "A saved-history I/O failure leaves an ordinary current game command available through the production session");
        await Until(() => { run.Tick(TimeSpan.Zero, default); return engine.Replies.Count == 1 && !run.View.Busy; });
        check(engine.Replies.Single() is AutoPassPriority && archive.Records == 2,
            "Only the deliberate game command reaches the rules port and history recording remains subscribed once");
        frame = run.Tick(TimeSpan.Zero, default);
        run.View.Activate(frame.Hits.Single(hit => hit.Id == "history")); frame = run.Tick(TimeSpan.Zero, default);
        var oldAudienceHit = frame.Hits.Single(hit => hit.Id == "history-close");
        var current = run.View.Match!;
        var foreign = current with { ViewerId = 1, Revision = current.Revision + 1,
            Players = current.Players.Select(player => player with { Zones = player.Zones.Select(zone => zone.Name == "Hand"
                ? zone with { Cards = [] } : zone).ToImmutableArray() }).ToImmutableArray() };
        run.View.Accept(foreign); run.Tick(TimeSpan.Zero, default);
        run.View.Accept(foreign with { ViewerId = current.ViewerId, Revision = foreign.Revision + 1 }); run.Tick(TimeSpan.Zero, default);
        check(run.View.Match?.ViewerId == current.ViewerId && !run.View.CurrentHit(oldAudienceHit)
            && !run.View.Activate(oldAudienceHit) && run.View.Overlay != "history",
            "A real game/viewer ABA retires the previous History control even when the original audience values return");
    }

    private static async Task Settle(HistoryPresentationSession reader, Posted queue)
    {
        await Until(() => !reader.HasOutstandingIo); reader.Tick(); queue.Pump();
    }
    private static async Task Until(Func<bool> ready)
    {
        var elapsed = Stopwatch.StartNew();
        while (!ready()) {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(3)) throw new TimeoutException("History owner controlled completion did not settle.");
            await Task.Delay(1);
        }
    }
    private sealed class Clock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        internal void Advance(TimeSpan elapsed) => Interlocked.Add(ref ticks, elapsed.Ticks);
    }
    private sealed class Posted
    {
        private readonly ConcurrentQueue<Action> queue = new();
        internal void Post(Action action) => queue.Enqueue(action);
        internal void Pump() { while (queue.TryDequeue(out var action)) action(); }
    }
    private sealed record ArchiveCall(string Kind, CancellationToken Token, HistoryExportScope? Scope = null)
    {
        internal readonly TaskCompletionSource<object?> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class ArchivePort : IMatchHistoryArchive
    {
        private readonly Channel<ArchiveCall> calls = Channel.CreateUnbounded<ArchiveCall>();
        private int count;
        internal int Calls => Volatile.Read(ref count);
        internal int Records { get; private set; }
        internal Dictionary<(string, int), (bool Finished, bool Incomplete)> Closures { get; } = [];
        public HistoryArchiveHealth Health => new(true, 0, null);
        public void Record(HistoryBatch batch) => Records++;
        public void MarkClosed(string gameId, int viewerId, bool finished, bool incomplete = false)
        {
            var old = Closures.GetValueOrDefault((gameId, viewerId));
            Closures[(gameId, viewerId)] = (old.Finished || finished, old.Incomplete || incomplete);
        }
        private ArchiveCall Start(string kind, CancellationToken token, HistoryExportScope? scope = null)
        {
            var call = new ArchiveCall(kind, token, scope); Interlocked.Increment(ref count); calls.Writer.TryWrite(call); return call;
        }
        public async Task<ImmutableArray<HistoryArchiveSummary>> ListAsync(CancellationToken cancellation = default)
            => (ImmutableArray<HistoryArchiveSummary>)(await Start("list", cancellation).Done.Task)!;
        public async Task<HistoryArchiveDocument?> ReadAsync(string id, CancellationToken cancellation = default)
            => (HistoryArchiveDocument?)await Start("archive", cancellation).Done.Task;
        public async Task ExportAsync(string id, Stream destination, HistoryExportScope scope = HistoryExportScope.Public,
            CancellationToken cancellation = default, long? focusSequence = null)
        {
            var bytes = (byte[])(await Start("export", cancellation, scope).Done.Task)!;
            await destination.WriteAsync(bytes);
        }
        internal async Task<ArchiveCall> Next(string kind)
        {
            var result = await calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            if (result.Kind != kind) throw new InvalidOperationException("Expected " + kind + ", got " + result.Kind);
            return result;
        }
    }
}
