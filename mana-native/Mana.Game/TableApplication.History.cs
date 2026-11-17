using Mana.Client;
using Mana.Contracts;
using Mana.Table.Interaction;
using Mana.Table.Presentation;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private readonly HistoryPresentationSession historyReader;
    private bool HistoryOpen => overlay == "history" && historyReader.Surface == HistorySurface.History;
    private bool historyInspector => historyReader.Surface == HistorySurface.Inspector;
    private bool historySaved => historyReader.CaptureView().Source == HistoryReadingSource.SavedList;
    private bool historyPrivateReport => historyReader.CaptureView().PrivateReport;
    private HistoryArchiveDocument? historyDocument => historyReader.CaptureView().Document;
    private string HistoryScope => historyReader.Scope;
    private MatchHistoryReadModel HistoryModel => historyReader.Model;
    private MatchHistoryAction? SelectedHistory => historyReader.Selected;
    private string HistoryDetails(MatchHistoryAction action) => historyReader.Details(action);

    // Recording and archive closure belong to the admitted live history, not
    // the reader's open/close lifetime or its saved-document operations.
    private string historyObservedGame = "";
    private int historyObservedViewer = -1;
    private long historyObservedRevision = -1;

    private void ObserveHistoryReader()
        => historyReader.ObserveLive(match?.Id, match?.ViewerId ?? -1, session.History.Model,
            session.History.Status, session.History.Error);

    private void OpenHistory(bool saved = false)
    {
        ObserveHistoryReader();
        CollapseCardReading(); textFocus = false; noticeUntil = 0;
        historyReader.Open(saved); overlay = "history";
    }
    private void CloseHistory()
    {
        historyReader.Close();
        overlay = ""; inspected = null; inspectedChoice = null; textFocus = false;
    }
    private void HandleHistoryIntent(HistoryIntent intent)
    {
        switch (intent) {
            case HistoryIntent.Open open: OpenHistory(open.Saved); break;
            case HistoryIntent.Close: CloseHistory(); break;
            case HistoryIntent.SavedMatches: historyReader.ShowSaved(); break;
            case HistoryIntent.LiveMatch when match != null: historyReader.ShowLive(); break;
            case HistoryIntent.OpenArchive open: historyReader.LoadArchive(open.Id); break;
            case HistoryIntent.Move move: historyReader.Move(move.Delta); break;
            case HistoryIntent.Newest: historyReader.Newest(); break;
            case HistoryIntent.Details details: historyReader.MoveDetails(details.Delta); break;
            case HistoryIntent.Archives page: historyReader.MoveArchives(page.Delta); break;
            case HistoryIntent.TogglePrivateReport: historyReader.TogglePrivateReport(); break;
            case HistoryIntent.SaveReport: SaveHistoryReport(); break;
            case HistoryIntent.ReadSource: ReadHistorySource(); break;
            case HistoryIntent.BackFromCard: ReturnToHistory(); break;
            case HistoryIntent.Retry:
                if (historySaved) historyReader.LoadSaved(); else session.History.Retry(); break;
        }
    }

    private void ReconcileHistory(GameSnapshot? before, GameSnapshot? state)
    {
        bool changed = before != null && (state?.Id != before.Id || state?.ViewerId != before.ViewerId);
        if (before != null && changed) CloseHistoryArchive(before, !HistoryObserved(before));
        if (changed && (HistoryOpen || historyInspector)) CloseHistory();
        historyReader.ObserveLive(state?.Id, state?.ViewerId ?? -1, session.History.Model,
            session.History.Status, session.History.Error);
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
        catch { historyReader.ReportClosureFailure(); }
    }

    private Card? CurrentHistoryCard(HistoryPanelView? view = null)
    {
        var model = view?.Model ?? HistoryModel;
        var selected = view?.Selected ?? SelectedHistory;
        if (match == null || match.Id != model.GameId || match.ViewerId != model.ViewerId
            || selected?.SourceCardId is not { Length: > 0 } id) return null;
        return VisibleCards().FirstOrDefault(card => !card.FaceDown && card.VisualId == id);
    }
    private void ReadHistorySource()
    {
        if (CurrentHistoryCard() is not { } card) return;
        CollapseCardReading();
        // A retained source is reading material, never a new action catalog.
        historyReader.InspectSource();
        inspected = card; inspectedChoice = null; inspectBack = false; inspectPage = 0; overlay = "inspect";
    }
    private void ReturnToHistory()
    {
        historyReader.ReturnFromSource(); inspected = null; inspectedChoice = null; overlay = "history";
    }
    private void SaveHistoryReport()
    {
        if (!historyReader.CanExport) return;
        var scope = HistoryScope;
        using var dialog = new SaveFileDialog {
            Title = historyPrivateReport ? "Save report including my private choices" : "Save public match report",
            Filter = "Match report|*.json", DefaultExt = "json", AddExtension = true, OverwritePrompt = true,
            FileName = "ManaTable-match-report.json"
        };
        if (dialog.ShowDialog() == DialogResult.OK) historyReader.Export(dialog.FileName, scope);
    }
}
