using System.Collections.Immutable;
using Mana.Client;
using Mana.Contracts;

namespace Mana.Table.Presentation;

internal enum HistorySurface { Closed, History, Inspector }
internal enum HistoryReadingSource { Live, SavedList, Archive }

/// <summary>One immutable reader composition. It contains retained facts, never
/// current cards, game commands, a provider, or writable application state.</summary>
internal sealed record HistoryPanelView(long Owner, long Revision, string Scope,
    HistorySurface Surface, HistoryReadingSource Source, MatchHistoryReadModel Model,
    MatchHistoryAction? Selected, string SelectionId, int DetailsPage, int ArchivePage,
    long SeenSequence, ImmutableArray<HistoryArchiveSummary> SavedMatches,
    HistoryArchiveDocument? Document, bool Loading, bool CanExport, bool PrivateReport,
    string Message, MatchHistoryStatus LiveStatus, string LiveError, HistoryArchiveHealth? StorageHealth);

/// <summary>Owner-thread history navigation and archive operations. Cancellation
/// retires view authority; a separate actual task owns storage until it settles.</summary>
internal sealed class HistoryPresentationSession : IDisposable
{
    private static long ownerSequence;
    private static readonly TimeSpan RequestLimit = TimeSpan.FromSeconds(10);
    private readonly long owner = Interlocked.Increment(ref ownerSequence);
    private readonly IMatchHistoryArchive? archive;
    private readonly Action<Action> post;
    private readonly TimeProvider time;
    private readonly Func<string, byte[], Task> writeReport;
    private (string Game, int Viewer)? audience;
    private bool disposed;
    private long revision, interactionEpoch;
    private HistorySurface surface;
    private HistoryReadingSource source;
    private MatchHistoryReadModel live = MatchHistoryReadModel.Empty();
    private MatchHistoryStatus liveStatus;
    private string liveError = "", selection = "", message = "";
    private int detailsPage, archivePage;
    private long seenSequence;
    private bool loading, privateReport;
    private ImmutableArray<HistoryArchiveSummary> saved = [];
    private HistoryArchiveDocument? document;
    private MatchHistoryReadModel? archived;
    private HistoryPanelView? captured;
    private MatchHistoryReadModel? detailModel;
    private HistoryArchiveSummary? detailSummary;
    private string detailKey = "", detailText = "";
    private Request? current, pending;
    private Pipeline? actual;

    private enum RequestKind { List, Archive, Export }
    private sealed class Request(RequestKind kind, long started, string game, int viewer,
        string? archiveId = null, string? destination = null, HistoryExportScope exportScope = HistoryExportScope.Public,
        long? focus = null)
    {
        internal RequestKind Kind { get; } = kind;
        internal long Started { get; } = started;
        internal string Game { get; } = game;
        internal int Viewer { get; } = viewer;
        internal string? ArchiveId { get; } = archiveId;
        internal string? Destination { get; } = destination;
        internal HistoryExportScope ExportScope { get; } = exportScope;
        internal long? Focus { get; } = focus;
        private int retired;
        internal bool Retired => Volatile.Read(ref retired) != 0;
        internal void Retire() => Interlocked.Exchange(ref retired, 1);
    }
    private sealed record StorageResult(ImmutableArray<HistoryArchiveSummary> Matches = default,
        HistoryArchiveDocument? Document = null, MatchHistoryReadModel? Model = null, bool Failed = false);
    private sealed class Pipeline(Request request)
    {
        internal Request Request { get; } = request;
        internal readonly TaskCompletionSource Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal StorageResult? Result;
        internal int Writing;
    }

    internal HistoryPresentationSession(IMatchHistoryArchive? archive, Action<Action> post,
        TimeProvider? time = null, Func<string, byte[], Task>? writeReport = null)
    {
        this.archive = archive; this.post = post ?? throw new ArgumentNullException(nameof(post));
        this.time = time ?? TimeProvider.System;
        this.writeReport = writeReport ?? ((path, bytes) => File.WriteAllBytesAsync(path, bytes));
    }

    internal HistorySurface Surface => surface;
    // Storage retirement only. A private completion may still be queued or
    // executing; future generation unload also needs its callback-lane fence.
    internal bool HasOutstandingIo => actual is { } work && !work.Finished.Task.IsCompleted;
    internal bool WritingReport => actual is { } work && !work.Finished.Task.IsCompleted && Volatile.Read(ref work.Writing) != 0;
    internal bool Loading => loading;
    internal string Scope => $"history:{owner}:{interactionEpoch}:{surface}:{source}:{document?.Summary.ArchiveId ?? audience?.Game}:{audience?.Viewer}:{selection}:{detailsPage}:{archivePage}:{privateReport}";
    internal MatchHistoryReadModel Model => archived ?? live;
    internal MatchHistoryAction? Selected => selection.Length == 0 ? Model.Actions.LastOrDefault()
        : Model.Actions.FirstOrDefault(action => action.Id == selection);
    internal bool CanExport => !disposed && archive != null && surface == HistorySurface.History && !loading && !HasOutstandingIo;

    private void Changed(bool retireInteraction = true)
    {
        revision++;
        if (retireInteraction) interactionEpoch++;
        captured = null;
    }
    private bool Expired(Request request) => time.GetElapsedTime(request.Started) >= RequestLimit;
    private bool Usable(Request request) => !request.Retired && !Expired(request);
    private void RetireCurrent()
    {
        current?.Retire(); pending?.Retire(); current = pending = null; loading = false;
    }
    private void ClearDetails() { detailModel = null; detailSummary = null; detailKey = detailText = ""; }

    internal void ObserveLive(string? game, int viewer, MatchHistoryReadModel model, MatchHistoryStatus status, string error)
    {
        if (disposed) return;
        var next = game == null ? ((string Game, int Viewer)?)null : (game, viewer);
        if (audience != next) {
            RetireCurrent(); surface = HistorySurface.Closed; source = HistoryReadingSource.Live;
            document = null; archived = null; saved = []; selection = ""; privateReport = false;
            detailsPage = archivePage = 0; message = ""; ClearDetails(); audience = next;
            Changed();
        }
        // The live port can still be establishing its new audience. It cannot
        // lend retained facts from a preceding viewer to the new reader.
        if (next == null || model.GameId != game || model.ViewerId != viewer)
            model = live.GameId == (game ?? "") && live.ViewerId == viewer ? live : MatchHistoryReadModel.Empty(game ?? "", viewer);
        if (ReferenceEquals(live, model) && liveStatus == status && liveError == error) return;
        live = model; liveStatus = status; liveError = error;
        if (surface != HistorySurface.Closed && source == HistoryReadingSource.Live && selection.Length == 0)
            selection = live.Actions.LastOrDefault()?.Id ?? "";
        // A fresh same-audience model invalidates measured composition, but
        // does not revoke an unchanged reader gesture while the table advances.
        Changed(retireInteraction: false);
    }

    internal void Open(bool savedMatches)
    {
        if (disposed) return;
        RetireCurrent(); surface = HistorySurface.History;
        source = savedMatches || audience == null ? HistoryReadingSource.SavedList : HistoryReadingSource.Live;
        document = null; archived = null; selection = live.Actions.LastOrDefault()?.Id ?? "";
        seenSequence = live.Cursor; detailsPage = 0; privateReport = false; message = ""; ClearDetails(); Changed();
        if (source == HistoryReadingSource.SavedList) LoadSaved();
    }
    internal void Close()
    {
        if (disposed) return;
        RetireCurrent(); surface = HistorySurface.Closed; document = null; archived = null; saved = []; ClearDetails(); Changed();
    }
    internal void ShowSaved()
    {
        if (disposed || surface == HistorySurface.Closed) return;
        source = HistoryReadingSource.SavedList; archivePage = 0; Changed(); LoadSaved();
    }
    internal void ShowLive()
    {
        if (disposed || audience == null || surface == HistorySurface.Closed) return;
        RetireCurrent(); source = HistoryReadingSource.Live; document = null; archived = null;
        selection = live.Actions.LastOrDefault()?.Id ?? ""; seenSequence = live.Cursor;
        detailsPage = 0; message = ""; ClearDetails(); Changed();
    }
    internal void Move(int delta)
    {
        if (disposed || surface != HistorySurface.History) return;
        int index = -1;
        for (int i = 0; i < Model.Actions.Length; i++) if (Model.Actions[i].Id == Selected?.Id) { index = i; break; }
        int next = (int)Math.Clamp((long)index + delta, 0, Math.Max(0, Model.Actions.Length - 1));
        selection = Model.Actions.ElementAtOrDefault(next)?.Id ?? ""; detailsPage = 0; Changed();
    }
    internal void Newest()
    {
        if (disposed || surface != HistorySurface.History) return;
        selection = Model.Actions.LastOrDefault()?.Id ?? ""; detailsPage = 0; seenSequence = Model.Cursor; Changed();
    }
    internal void MoveDetails(int delta)
    {
        if (disposed || surface != HistorySurface.History) return;
        detailsPage = (int)Math.Clamp((long)detailsPage + delta, 0, int.MaxValue); Changed();
    }
    internal void MoveArchives(int delta)
    {
        if (disposed || surface != HistorySurface.History) return;
        archivePage = (int)Math.Clamp((long)archivePage + delta, 0, int.MaxValue); Changed();
    }
    internal void TogglePrivateReport()
    {
        if (disposed || surface != HistorySurface.History) return;
        privateReport = !privateReport; Changed();
    }
    internal void InspectSource()
    {
        if (disposed || surface != HistorySurface.History || Selected?.SourceCardId is not { Length: > 0 }) return;
        surface = HistorySurface.Inspector; Changed();
    }
    internal void ReturnFromSource()
    {
        if (disposed || surface != HistorySurface.Inspector) return;
        surface = HistorySurface.History; Changed();
    }
    internal void ReportClosureFailure()
    {
        if (disposed) return;
        message = "History closure could not be saved. The match can continue."; Changed();
    }

    internal HistoryPanelView CaptureView()
    {
        HistoryArchiveHealth? health = null;
        try { health = archive?.Health; } catch { /* Storage health cannot interrupt composition. */ }
        if (captured != null && captured.StorageHealth == health) return captured;
        return captured = new(owner, revision, Scope, surface, source, Model, Selected, selection,
            detailsPage, archivePage, seenSequence, saved, document, loading, CanExport, privateReport,
            message, liveStatus, liveError, health);
    }
    internal bool ReconcilePages(HistoryPanelView basis, int detailPages, int archiveCapacity, out HistoryPanelView? view)
    {
        view = null;
        if (disposed || basis.Owner != owner || basis.Revision != revision || archiveCapacity < 1 || detailPages < 1) return false;
        int detail = Math.Clamp(detailsPage, 0, detailPages - 1);
        int list = Math.Clamp(archivePage, 0, Math.Max(0, (saved.Length - 1) / archiveCapacity));
        if (detail != detailsPage || list != archivePage) { detailsPage = detail; archivePage = list; Changed(); }
        view = CaptureView(); return true;
    }
    internal string Details(MatchHistoryAction action)
    {
        var model = Model; var summary = document?.Summary;
        if (ReferenceEquals(detailModel, model) && detailKey == action.Id && detailSummary == summary) return detailText;
        detailModel = model; detailKey = action.Id; detailSummary = summary;
        return detailText = HistoryDetailsFormatter.Format(model, action, summary);
    }

    internal void LoadSaved()
    {
        if (disposed || archive == null || surface == HistorySurface.Closed) return;
        Begin(new(RequestKind.List, time.GetTimestamp(), audience?.Game ?? "", audience?.Viewer ?? -1));
    }
    internal void LoadArchive(string id)
    {
        if (disposed || archive == null || surface == HistorySurface.Closed || !saved.Any(item => item.ArchiveId == id)) return;
        Begin(new(RequestKind.Archive, time.GetTimestamp(), audience?.Game ?? "", audience?.Viewer ?? -1, id));
    }
    internal bool Export(string destination, string expectedScope)
    {
        if (!CanExport || expectedScope != Scope || string.IsNullOrWhiteSpace(destination)) return false;
        var exportScope = privateReport ? HistoryExportScope.IncludeOwnPrivate : HistoryExportScope.Public;
        var selected = Selected;
        long? focus = exportScope == HistoryExportScope.Public
            ? selected?.Records.FirstOrDefault(record => record.Exposure == "public")?.Sequence ?? selected?.FirstSequence
            : selected?.FirstSequence;
        Begin(new(RequestKind.Export, time.GetTimestamp(), Model.GameId, Model.ViewerId,
            document?.Summary.ArchiveId, destination, exportScope, focus));
        return true;
    }
    private void Begin(Request request)
    {
        RetireCurrent(); current = pending = request; loading = true; message = ""; Changed(); Tick();
    }

    /// <summary>Owner lane only. Actual completion is independent of whether an
    /// old view callback is pumped; logical expiry cannot release its task slot.</summary>
    internal void Tick()
    {
        if (disposed) return;
        if (actual is { } done && done.Finished.Task.IsCompleted) Finish(done);
        if (current is { } request && !Usable(request)) {
            request.Retire(); current = null; if (ReferenceEquals(pending, request)) pending = null;
            loading = false; message = "History storage is taking too long. The match can continue."; Changed();
        }
        if (actual == null && pending is { } next && ReferenceEquals(current, next) && Usable(next)) {
            pending = null;
            var pipeline = new Pipeline(next); actual = pipeline;
            _ = Task.Run(() => RunStorageAsync(pipeline));
        }
    }
    private async Task RunStorageAsync(Pipeline pipeline)
    {
        var request = pipeline.Request;
        StorageResult result;
        try {
            if (!Usable(request)) throw new OperationCanceledException();
            if (request.Kind == RequestKind.List) result = new(Matches: await archive!.ListAsync(CancellationToken.None).ConfigureAwait(false));
            else if (request.Kind == RequestKind.Archive) {
                var read = await archive!.ReadAsync(request.ArchiveId!, CancellationToken.None).ConfigureAwait(false) ?? throw new IOException();
                result = new(Document: read, Model: MatchHistoryReadModel.Read(read.Batches));
            } else {
                string? id = request.ArchiveId;
                if (id == null) id = (await archive!.ListAsync(CancellationToken.None).ConfigureAwait(false))
                    .FirstOrDefault(item => item.GameId == request.Game && item.ViewerId == request.Viewer)?.ArchiveId;
                if (id == null) throw new IOException();
                if (!Usable(request)) throw new OperationCanceledException();
                using var prepared = new MemoryStream();
                await archive!.ExportAsync(id, prepared, request.ExportScope, CancellationToken.None, request.Focus).ConfigureAwait(false);
                if (!Usable(request)) throw new OperationCanceledException();
                Volatile.Write(ref pipeline.Writing, 1);
                await writeReport(request.Destination!, prepared.ToArray()).ConfigureAwait(false);
                result = new();
            }
        } catch { result = new(Failed: true); }
        pipeline.Result = result;
        pipeline.Finished.TrySetResult();
        // A closed ClientSession may discard this callback. Its existence is
        // not the I/O retirement signal and never owns a replacement queue.
        try { post(() => Finish(pipeline)); } catch { /* Retirement stays observable without a presentation callback. */ }
    }
    private void Finish(Pipeline pipeline)
    {
        if (disposed || !ReferenceEquals(actual, pipeline) || !pipeline.Finished.Task.IsCompleted) return;
        actual = null;
        Changed(); // Actual retirement can make an explicit fresh export available.
        var request = pipeline.Request;
        if (ReferenceEquals(current, request) && Usable(request) && surface != HistorySurface.Closed) {
            current = null; loading = false;
            var result = pipeline.Result!;
            if (result.Failed) message = request.Kind switch {
                RequestKind.List => "Saved history could not be read. The match can continue.",
                RequestKind.Archive => "This match could not be read. Its files were left unchanged.",
                _ => "The report could not be saved. Your match can continue."
            };
            else if (request.Kind == RequestKind.List) saved = result.Matches.IsDefault ? [] : result.Matches;
            else if (request.Kind == RequestKind.Archive) {
                document = result.Document; archived = result.Model; source = HistoryReadingSource.Archive;
                selection = archived!.Actions.LastOrDefault()?.Id ?? ""; seenSequence = archived.Cursor; detailsPage = 0; ClearDetails();
                if (document!.Issues.Length > 0 || document.Summary.HasGaps)
                    message = "This archive has missing or incomplete records. Only retained facts are shown.";
            } else message = "Report saved to " + request.Destination;
            Changed();
        }
        Tick();
    }
    public void Dispose()
    {
        if (disposed) return;
        RetireCurrent(); disposed = true; surface = HistorySurface.Closed;
        live = MatchHistoryReadModel.Empty(); document = null; archived = null; saved = []; selection = "";
        ClearDetails(); captured = null;
        // The one already dispatched pipeline remains observed until it ends.
        // Neither disposal nor a later callback dispatches another request.
    }
}
