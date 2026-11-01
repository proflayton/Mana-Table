using System.Collections.Immutable;
using Mana.Client;
using Mana.Contracts;
using Mana.Table.Interaction;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private bool HistoryOpen => overlay == "history";
    private bool historySaved, historyLoading, historyInspector, historyPrivateReport;
    private int historyGeneration, historyDetailsPage, historyArchivePage;
    private string historySelection = "", historyMessage = "";
    private long historySeenSequence;
    private string historyObservedGame = "";
    private int historyObservedViewer = -1;
    private long historyObservedRevision = -1;
    private ImmutableArray<HistoryArchiveSummary> savedHistory = [];
    private HistoryArchiveDocument? historyDocument;
    private MatchHistoryReadModel? archivedHistory;
    private CancellationTokenSource? historyReadCancellation;
    private string HistoryScope => "history:" + historyGeneration + ":" + (HistoryOpen || historyInspector)
        + ":" + (historyDocument?.Summary.ArchiveId ?? (match?.Id + ":" + match?.ViewerId))
        + ":" + historySaved + ":" + historyInspector + ":" + historyPrivateReport
        + ":" + SelectedHistory?.Id + ":" + historyDetailsPage + ":" + historyArchivePage;

    private MatchHistoryReadModel HistoryModel => archivedHistory ?? session.History.Model;
    private MatchHistoryAction? SelectedHistory => historySelection.Length == 0 ? HistoryModel.Actions.LastOrDefault()
        : HistoryModel.Actions.FirstOrDefault(action => action.Id == historySelection);

    private void OpenHistory(bool saved = false)
    {
        historyReadCancellation?.Cancel(); historyLoading = false;
        CollapseCardReading(); textFocus = false; noticeUntil = 0;
        overlay = "history"; historyInspector = false; historySaved = saved || match == null;
        historyDocument = null; archivedHistory = null; historySelection = session.History.Model.Actions.LastOrDefault()?.Id ?? "";
        historySeenSequence = session.History.Model.Cursor; historyDetailsPage = 0;
        historyMessage = ""; historyPrivateReport = false; historyGeneration++;
        if (historySaved) LoadSavedHistory();
    }

    private void CloseHistory()
    {
        historyReadCancellation?.Cancel();
        overlay = ""; historyInspector = false; inspected = null; inspectedChoice = null;
        ClearHistoryDetails();
        historyDocument = null; archivedHistory = null; savedHistory = [];
        historyGeneration++; historyLoading = false; textFocus = false;
    }

    private void HandleHistoryIntent(HistoryIntent intent)
    {
        switch (intent) {
            case HistoryIntent.Open open: OpenHistory(open.Saved); break;
            case HistoryIntent.Close: CloseHistory(); break;
            case HistoryIntent.SavedMatches: historySaved = true; historyArchivePage = 0; LoadSavedHistory(); break;
            case HistoryIntent.LiveMatch when match != null:
                historyReadCancellation?.Cancel(); historyLoading = false;
                historySaved = false; historyDocument = null; archivedHistory = null;
                historySelection = session.History.Model.Actions.LastOrDefault()?.Id ?? ""; historyDetailsPage = 0; historyMessage = ""; historyGeneration++;
                historySeenSequence = session.History.Model.Cursor; break;
            case HistoryIntent.OpenArchive open: LoadHistoryArchive(open.Id); break;
            case HistoryIntent.Move move:
                int index = Array.FindIndex(HistoryModel.Actions.ToArray(), action => action.Id == SelectedHistory?.Id);
                int next = Math.Clamp(index + move.Delta, 0, Math.Max(0, HistoryModel.Actions.Length - 1));
                historySelection = HistoryModel.Actions.ElementAtOrDefault(next)?.Id ?? ""; historyDetailsPage = 0; break;
            case HistoryIntent.Newest:
                historySelection = HistoryModel.Actions.LastOrDefault()?.Id ?? ""; historyDetailsPage = 0;
                historySeenSequence = HistoryModel.Cursor; break;
            case HistoryIntent.Details details: historyDetailsPage = Math.Max(0, historyDetailsPage + details.Delta); break;
            case HistoryIntent.Archives page: historyArchivePage = Math.Max(0, historyArchivePage + page.Delta); break;
            case HistoryIntent.TogglePrivateReport: historyPrivateReport = !historyPrivateReport; break;
            case HistoryIntent.SaveReport: SaveHistoryReport(); break;
            case HistoryIntent.ReadSource: ReadHistorySource(); break;
            case HistoryIntent.BackFromCard: ReturnToHistory(); break;
            case HistoryIntent.Retry:
                if (historySaved) LoadSavedHistory(); else session.History.Retry(); break;
        }
    }

    private void ReconcileHistory(GameSnapshot? before, GameSnapshot? state)
    {
        bool changed = before != null && (state?.Id != before.Id || state?.ViewerId != before.ViewerId);
        if (before != null && changed)
            CloseHistoryArchive(before, !HistoryObserved(before));
        if (changed) {
            if (HistoryOpen || historyInspector) CloseHistory();
            ClearHistoryDetails();
            historyDocument = null; archivedHistory = null; savedHistory = [];
            historySelection = ""; historyPrivateReport = false;
        }
        if (historyInspector && CurrentHistoryCard() == null) ReturnToHistory();
    }

    private bool HistoryObserved(GameSnapshot state) => historyObservedGame == state.Id
        && historyObservedViewer == state.ViewerId && historyObservedRevision >= state.Revision;

    private void HistoryUpdated()
    {
        var history = session.History;
        if (history.ObservedRevision >= 0) {
            historyObservedGame = history.Model.GameId;
            historyObservedViewer = history.Model.ViewerId;
            historyObservedRevision = history.ObservedRevision;
        }
        if (match is { Status: "finished" } state && history.IsCaughtUp && HistoryObserved(state))
            CloseHistoryArchive(state, false);
    }

    private void CloseHistoryArchive(GameSnapshot state, bool incomplete)
    {
        try { services.HistoryArchive?.MarkClosed(state.Id, state.ViewerId, state.Status == "finished", incomplete); }
        catch { historyMessage = "History closure could not be saved. The match can continue."; }
    }

    private Card? CurrentHistoryCard()
    {
        if (match == null || match.Id != HistoryModel.GameId || match.ViewerId != HistoryModel.ViewerId
            || SelectedHistory?.SourceCardId is not { Length: > 0 } id) return null;
        return VisibleCards().FirstOrDefault(card => !card.FaceDown && card.VisualId == id);
    }

    private void ReadHistorySource()
    {
        if (CurrentHistoryCard() is not { } card) return;
        CollapseCardReading();
        // Never use InspectPendingEffect/CardClick or a source action catalog.
        inspected = card; inspectedChoice = null; inspectBack = false; inspectPage = 0;
        historyInspector = true; overlay = "inspect";
    }

    private void ReturnToHistory()
    {
        historyInspector = false; inspected = null; inspectedChoice = null; overlay = "history";
    }

    private void LoadSavedHistory()
    {
        if (services.HistoryArchive is not { } archive) return;
        int generation = ++historyGeneration; historyLoading = true; historyMessage = "";
        var cancellation = BeginHistoryRead();
        _ = Task.Run(async () => {
            try {
                var matches = await archive.ListAsync(cancellation);
                updates.Enqueue(() => {
                    if (generation != historyGeneration || closing) return;
                    savedHistory = matches; historyLoading = false;
                });
            } catch {
                updates.Enqueue(() => {
                    if (generation != historyGeneration || closing) return;
                    historyLoading = false; historyMessage = "Saved history could not be read. The match can continue.";
                });
            }
        });
    }

    private void LoadHistoryArchive(string id)
    {
        if (services.HistoryArchive is not { } archive || !savedHistory.Any(item => item.ArchiveId == id)) return;
        int generation = ++historyGeneration; historyLoading = true; historyMessage = "";
        var cancellation = BeginHistoryRead();
        _ = Task.Run(async () => {
            try {
                var document = await archive.ReadAsync(id, cancellation);
                if (document == null) throw new IOException();
                var model = MatchHistoryReadModel.Read(document.Batches);
                updates.Enqueue(() => {
                    if (generation != historyGeneration || closing) return;
                    historyLoading = false; historyDocument = document; archivedHistory = model;
                    historySaved = false; historySelection = model.Actions.LastOrDefault()?.Id ?? "";
                    historySeenSequence = model.Cursor; historyDetailsPage = 0;
                    if (document.Issues.Length > 0 || document.Summary.HasGaps)
                        historyMessage = "This archive has missing or incomplete records. Only retained facts are shown.";
                });
            } catch {
                updates.Enqueue(() => {
                    if (generation != historyGeneration || closing) return;
                    historyLoading = false; historyMessage = "This match could not be read. Its files were left unchanged.";
                });
            }
        });
    }

    private void SaveHistoryReport()
    {
        if (historyLoading || services.HistoryArchive is not { } archive) return;
        using var dialog = new SaveFileDialog {
            Title = historyPrivateReport ? "Save report including my private choices" : "Save public match report",
            Filter = "Match report|*.json", DefaultExt = "json", AddExtension = true, OverwritePrompt = true,
            FileName = "ManaTable-match-report.json"
        };
        if (dialog.ShowDialog() != DialogResult.OK) return;
        string destination = dialog.FileName;
        string? archiveId = historyDocument?.Summary.ArchiveId;
        string gameId = HistoryModel.GameId; int viewer = HistoryModel.ViewerId;
        var scope = historyPrivateReport ? HistoryExportScope.IncludeOwnPrivate : HistoryExportScope.Public;
        var selected = SelectedHistory;
        long? focus = scope == HistoryExportScope.Public
            ? selected?.Records.FirstOrDefault(record => record.Exposure == "public")?.Sequence ?? selected?.FirstSequence
            : selected?.FirstSequence;
        int generation = historyGeneration; historyLoading = true;
        var cancellation = BeginHistoryRead();
        _ = Task.Run(async () => {
            try {
                archiveId ??= (await archive.ListAsync(cancellation)).FirstOrDefault(item => item.GameId == gameId && item.ViewerId == viewer)?.ArchiveId;
                if (archiveId == null) throw new IOException();
                // Validate and prepare before touching the chosen destination.
                using var prepared = new MemoryStream();
                await archive.ExportAsync(archiveId, prepared, scope, cancellation, focus);
                cancellation.ThrowIfCancellationRequested();
                await File.WriteAllBytesAsync(destination, prepared.ToArray());
                updates.Enqueue(() => {
                    if (generation != historyGeneration || closing) return;
                    historyLoading = false; historyMessage = "Report saved to " + destination;
                });
            } catch {
                updates.Enqueue(() => {
                    if (generation != historyGeneration || closing) return;
                    historyLoading = false; historyMessage = "The report could not be saved. Your match can continue.";
                });
            }
        });
    }

    private CancellationToken BeginHistoryRead()
    {
        historyReadCancellation?.Cancel(); historyReadCancellation?.Dispose();
        historyReadCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return historyReadCancellation.Token;
    }

    private void ClearHistoryDetails()
    {
        historyDetailModel = null; historyDetailKey = historyDetailText = "";
    }
}
