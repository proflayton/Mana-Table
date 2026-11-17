using System.Collections.Immutable;
using Mana.Table.Presentation;
using Mana.Client;
using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Mana.Table.Interaction;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private sealed record HistoryPaintPlan(HistoryPanelView View, string[] Pages, int PhaseY, int StatusY, int DetailY, int CardHeight);

    private HistoryPaintPlan? PrepareHistoryPaint()
    {
        var basis = historyReader.CaptureView();
        int phaseY = 0, statusY = 0, detailY = 0, cardHeight = 0;
        string[] pages = [""];
        if (basis.Source != HistoryReadingSource.SavedList && basis.Selected is { } selected) {
            int nameHeight = canvas.TextSize(selected.SourceName, .97f, 1110, 80, true).Y;
            phaseY = 216 + nameHeight; statusY = phaseY + 36; detailY = statusY + 48;
            cardHeight = Math.Clamp(660 - (phaseY + 36), 140, 333);
            pages = canvas.TextPages(historyReader.Details(selected), .9f, 790, Math.Max(80, 690 - detailY), 200);
        }
        return historyReader.ReconcilePages(basis, Math.Max(1, pages.Length), 4, out var view)
            ? new(view!, pages, phaseY, statusY, detailY, cardHeight) : null;
    }

    private void DrawHistory()
    {
        // Measure without emitting paint/hits, then clamp pages and capture one
        // final immutable view. Every history control uses that final scope.
        var plan = PrepareHistoryPaint();
        if (plan == null) return;
        var view = plan.View;
        hits.Clear(); hovered = null; ClearCardComposition();
        var bounds = new Rectangle(345, 70, 1215, 800);
        var close = new HistoryIntent.Close();
        canvas.Fill(new(0, 0, 1600, DesignHeight), Color.Black * .35f);
        hits.Add(new("history-backdrop", ScopeFor(close), new(0, 0, 1600, DesignHeight), close, ShowPointer: false));
        canvas.Panel(bounds, new(15, 25, 32), LobbyTrim, 9);
        hits.Add(new("history-body", ScopeFor(close), bounds, new UiIntent.Block(InteractionScopeKind.History), ShowPointer: false));
        canvas.Text("Match history", 377, 93, Ink, 1.35f, 790, 1, true);
        MenuAction("history-close", match == null ? "Back" : "Back to table", new(1310, 85, 222, 48), close);
        MenuAction("history-live", "This match", new(377, 144, 200, 42), new HistoryIntent.LiveMatch(), match != null, view.Source != HistoryReadingSource.SavedList && view.Document == null);
        MenuAction("history-saved", "Saved matches", new(594, 144, 222, 42), new HistoryIntent.SavedMatches(), services.HistoryArchive != null, view.Source == HistoryReadingSource.SavedList || view.Document != null);

        if (view.Source == HistoryReadingSource.SavedList) DrawSavedMatches(view);
        else DrawHistoryPlay(plan);
        string status = view.Message;
        if (status.Length == 0 && view.StorageHealth is { Available: false }) status = "History storage is unavailable. The match can continue.";
        if (status.Length == 0 && view.StorageHealth is { DroppedBatches: > 0 }) status = "Some records could not be saved. Missing coverage is marked in the archive.";
        if (status.Length > 0) canvas.Text(status, 382, 820, Gold, .66f, 1135, 2);
    }

    private void DrawSavedMatches(HistoryPanelView view)
    {
        if (view.Loading) { canvas.Text("Reading saved matches", 390, 254, Muted, 1, 1000, 2); return; }
        if (view.SavedMatches.Length == 0) {
            canvas.Text("Your recorded matches will be kept here.", 390, 254, Ink, 1.1f, 1020, 2, true);
            canvas.Text("The recorder starts with this version. Older games cannot be reconstructed from the short diagnostic log.", 390, 322, Muted, .9f, 1020, 4);
            return;
        }
        const int capacity = 4;
        int y = 215;
        foreach (var item in view.SavedMatches.Skip(view.ArchivePage * capacity).Take(capacity)) {
            var action = new HistoryIntent.OpenArchive(item.ArchiveId);
            string id = "history-match:" + item.ArchiveId;
            var area = new Rectangle(382, y, 1138, 123);
            if (IsControlHovered(id)) canvas.Rounded(area, new(30, 48, 56), 5);
            if (resources.CardBack is { } back) {
                canvas.Sprite(back, new(422, y + 61), new(60, 84), -.08f);
                canvas.Sprite(back, new(435, y + 61), new(60, 84), .03f);
            }
            canvas.Text(item.StartedAt.ToLocalTime().ToString("MMM d, yyyy · HH:mm"), 490, y + 12, Ink, 1.03f, 760, 2, true);
            string state = item.State == HistoryArchiveState.Recording ? "In progress" : item.Finished ? "Finished" : item.State == HistoryArchiveState.Interrupted ? "Closed without a final record" : "Left table";
            canvas.Text($"Seat {item.ViewerId + 1} · {state} · {item.RecordCount} records", 490, y + 52, Muted, .78f, 900, 2);
            canvas.Text((item.HasGaps ? "Partial history · " : "") + "Version " + (item.Version ?? "unknown") + "   ›", 490, y + 84, item.HasGaps ? Gold : Blue, .72f, 900, 2);
            hits.Add(new(id, ScopeFor(action), area, action));
            y += 130;
        }
        MenuAction("history-matches-older", "Older matches", new(390, 754, 220, 44), new HistoryIntent.Archives(1), (view.ArchivePage + 1) * capacity < view.SavedMatches.Length);
        MenuAction("history-matches-newer", "Newer matches", new(620, 754, 220, 44), new HistoryIntent.Archives(-1), view.ArchivePage > 0);
    }

    private void DrawHistoryPlay(HistoryPaintPlan plan)
    {
        var view = plan.View;
        var model = view.Model;
        if (view.Selected is not { } action) {
            bool expired = view.SelectionId.Length > 0 && model.Actions.Length > 0;
            string empty = expired ? "This play has left the live history window" : view.Loading || view.LiveStatus == MatchHistoryStatus.Loading ? "Reading the table's history" : "No recorded plays yet";
            canvas.Text(empty, 391, 245, Ink, 1.15f, 1080, 2, true);
            string help = expired ? "Open Saved matches to read retained records, or deliberately return to the newest play."
                : view.LiveStatus == MatchHistoryStatus.Unsupported
                ? "This engine does not provide recorded history. Saved matches from a supporting version remain available."
                : view.LiveStatus == MatchHistoryStatus.Failed ? view.LiveError : "Casts, abilities, payments and their results will appear here.";
            canvas.Text(help, 391, 322, Muted, .9f, 1050, 4);
            if (view.LiveStatus == MatchHistoryStatus.Failed)
                Button("history-retry", "Try reading again", 390, 460, 275, new HistoryIntent.Retry());
            if (expired) Button("history-newest", "Newest play", 390, 460, 275, new HistoryIntent.Newest());
            return;
        }
        int index = Array.FindIndex(model.Actions.ToArray(), item => item.Id == action.Id);
        string actor = match?.Id == model.GameId && match.ViewerId == model.ViewerId
            ? match.Players.FirstOrDefault(player => player.Id == action.ActorPlayerId)?.Name ?? ""
            : action.ActorPlayerId is { } seat ? "Seat " + (seat + 1) : "";
        string phase = action.PhaseKey.Replace('_', ' ').Replace('-', ' ');
        canvas.Text(action.SourceName, 385, 204, Ink, .97f, 1110, 80, true);
        int phaseY = plan.PhaseY, statusY = plan.StatusY, detailY = plan.DetailY;
        canvas.Text($"TURN {action.Turn} · {phase}", 700, phaseY, Gold, .76f, 794, 2, true);
        int cardHeight = plan.CardHeight;
        var pose = new Pose(new(521, phaseY + 36 + cardHeight / 2f), new(cardHeight / 1.4f, cardHeight));
        if (CurrentHistoryCard(view) is { } face) {
            PaintCard(face, pose, false, false);
            MenuAction("history-read-source", "Read current card", new(386, 668, 280, 44), new HistoryIntent.ReadSource(), size: .8f);
        } else {
            if (resources.CardBack is { } back) canvas.Sprite(back, pose.Center, pose.Size);
            else canvas.CardFrame(pose.Center, pose.Size, 0, Gold * .4f, 2);
            canvas.Text("Historical source", 405, (int)pose.Center.Y - 20, Ink, .9f, 230, 2, true);
            canvas.Text("Its current face is not disclosed.", 398, 668, Muted, .74f, 260, 3);
        }
        if (actor.Length > 0) canvas.Text(actor, 390, 718, Muted, .75f, 280, 2);
        string outcome = action.Outcome?.ToString() ?? action.Step switch {
            ExecutionStep.Cost => "Paying costs", ExecutionStep.Choice => "Choosing", ExecutionStep.Stack => "On the stack",
            ExecutionStep.Resolution => "Resolving", ExecutionStep.Preparation => "Preparing", _ => "Recorded result"
        };
        canvas.Text(outcome, 700, statusY, action.Outcome == ExecutionOutcome.Failed ? Red : Blue, 1.05f, 794, 2, true);
        var pages = plan.Pages;
        canvas.Text(pages[view.DetailsPage], 700, detailY, Ink, .9f, 790, 200);
        if (pages.Length > 1) {
            MenuAction("history-details-back", "‹ Earlier detail", new(700, 702, 240, 42), new HistoryIntent.Details(-1), view.DetailsPage > 0);
            MenuAction("history-details-more", "More detail ›", new(1185, 702, 290, 42), new HistoryIntent.Details(1), view.DetailsPage + 1 < pages.Length);
        }
        if (action.HistoryIncomplete || !model.CoverageComplete)
            canvas.Text("Some earlier context is missing. No cause or result has been inferred.", 701, 177, Gold, .62f, 790, 2);
        MenuAction("history-previous", "‹ Previous play", new(382, 757, 235, 44), new HistoryIntent.Move(-1), index > 0);
        MenuAction("history-next", "Next play ›", new(632, 757, 220, 44), new HistoryIntent.Move(1), index + 1 < model.Actions.Length);
        int newActions = model.Actions.Count(item => item.FirstSequence > view.SeenSequence);
        MenuAction("history-newest", newActions > 0 ? $"{newActions} new plays ›" : "Newest play", new(1240, 143, 280, 42), new HistoryIntent.Newest(), index + 1 < model.Actions.Length);
        MenuAction("history-report-scope", view.PrivateReport ? "Report includes my private choices" : "Report uses public facts", new(888, 753, 400, 48), new HistoryIntent.TogglePrivateReport(), services.HistoryArchive != null, view.PrivateReport, .7f);
        Button("history-save", "Save report", 1305, 756, 210, new HistoryIntent.SaveReport(), view.CanExport, height: 43);
    }

    private void IsolateHistoryInput()
    {
        if (!HistoryOpen && !historyInspector) return;
        var allowed = hits.Where(hit => hit.Intent is HistoryIntent || historyInspector && hit.Intent is MatchIntent.FlipInspector
            or MatchIntent.SetPage { PageKind: MatchPage.Inspect } || hit.Intent is UiIntent.Block { Kind: InteractionScopeKind.History }).ToArray();
        hits.Clear();
        hits.Add(new("history-input-surface", HistoryScope, new(0, 0, 1600, DesignHeight), new UiIntent.Block(InteractionScopeKind.History), ShowPointer: false));
        foreach (var hit in allowed) hits.Add(hit);
    }
}
