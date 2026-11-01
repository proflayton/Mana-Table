using System.Collections.Immutable;
using System.Text;
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
    private MatchHistoryReadModel? historyDetailModel;
    private string historyDetailKey = "", historyDetailText = "";

    private void DrawHistory()
    {
        hits.Clear(); hovered = null; ClearCardComposition();
        var bounds = new Rectangle(345, 70, 1215, 800);
        var close = new HistoryIntent.Close();
        canvas.Fill(new(0, 0, 1600, DesignHeight), Color.Black * .35f);
        hits.Add(new("history-backdrop", ScopeFor(close), new(0, 0, 1600, DesignHeight), close, ShowPointer: false));
        canvas.Panel(bounds, new(15, 25, 32), LobbyTrim, 9);
        hits.Add(new("history-body", ScopeFor(close), bounds, new UiIntent.Block(InteractionScopeKind.History), ShowPointer: false));
        canvas.Text("Match history", 377, 93, Ink, 1.35f, 790, 1, true);
        MenuAction("history-close", match == null ? "Back" : "Back to table", new(1310, 85, 222, 48), close);
        MenuAction("history-live", "This match", new(377, 144, 200, 42), new HistoryIntent.LiveMatch(), match != null, !historySaved && historyDocument == null);
        MenuAction("history-saved", "Saved matches", new(594, 144, 222, 42), new HistoryIntent.SavedMatches(), services.HistoryArchive != null, historySaved || historyDocument != null);

        if (historySaved) DrawSavedMatches();
        else DrawHistoryPlay();
        string status = historyMessage;
        if (status.Length == 0 && services.HistoryArchive?.Health is { Available: false }) status = "History storage is unavailable. The match can continue.";
        if (status.Length == 0 && services.HistoryArchive?.Health is { DroppedBatches: > 0 }) status = "Some records could not be saved. Missing coverage is marked in the archive.";
        if (status.Length > 0) canvas.Text(status, 382, 820, Gold, .66f, 1135, 2);
    }

    private void DrawSavedMatches()
    {
        if (historyLoading) { canvas.Text("Reading saved matches", 390, 254, Muted, 1, 1000, 2); return; }
        if (savedHistory.Length == 0) {
            canvas.Text("Your recorded matches will be kept here.", 390, 254, Ink, 1.1f, 1020, 2, true);
            canvas.Text("The recorder starts with this version. Older games cannot be reconstructed from the short diagnostic log.", 390, 322, Muted, .9f, 1020, 4);
            return;
        }
        const int capacity = 4;
        historyArchivePage = Math.Clamp(historyArchivePage, 0, (savedHistory.Length - 1) / capacity);
        int y = 215;
        foreach (var item in savedHistory.Skip(historyArchivePage * capacity).Take(capacity)) {
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
        MenuAction("history-matches-older", "Older matches", new(390, 754, 220, 44), new HistoryIntent.Archives(1), (historyArchivePage + 1) * capacity < savedHistory.Length);
        MenuAction("history-matches-newer", "Newer matches", new(620, 754, 220, 44), new HistoryIntent.Archives(-1), historyArchivePage > 0);
    }

    private void DrawHistoryPlay()
    {
        var model = HistoryModel;
        if (historySelection.Length == 0) historySelection = model.Actions.LastOrDefault()?.Id ?? "";
        if (SelectedHistory is not { } action) {
            bool expired = historySelection.Length > 0 && model.Actions.Length > 0;
            string empty = expired ? "This play has left the live history window" : historyLoading || session.History.Status == MatchHistoryStatus.Loading ? "Reading the table's history" : "No recorded plays yet";
            canvas.Text(empty, 391, 245, Ink, 1.15f, 1080, 2, true);
            string help = expired ? "Open Saved matches to read retained records, or deliberately return to the newest play."
                : session.History.Status == MatchHistoryStatus.Unsupported
                ? "This engine does not provide recorded history. Saved matches from a supporting version remain available."
                : session.History.Status == MatchHistoryStatus.Failed ? session.History.Error : "Casts, abilities, payments and their results will appear here.";
            canvas.Text(help, 391, 322, Muted, .9f, 1050, 4);
            if (session.History.Status == MatchHistoryStatus.Failed)
                Button("history-retry", "Try reading again", 390, 460, 275, new HistoryIntent.Retry());
            if (expired) Button("history-newest", "Newest play", 390, 460, 275, new HistoryIntent.Newest());
            return;
        }
        int index = Array.FindIndex(model.Actions.ToArray(), item => item.Id == action.Id);
        string actor = match?.Id == model.GameId && match.ViewerId == model.ViewerId
            ? match.Players.FirstOrDefault(player => player.Id == action.ActorPlayerId)?.Name ?? ""
            : action.ActorPlayerId is { } seat ? "Seat " + (seat + 1) : "";
        string phase = action.PhaseKey.Replace('_', ' ').Replace('-', ' ');
        int nameHeight = canvas.TextSize(action.SourceName, .97f, 1110, 80, true).Y;
        canvas.Text(action.SourceName, 385, 204, Ink, .97f, 1110, 80, true);
        int phaseY = 216 + nameHeight, statusY = phaseY + 36, detailY = statusY + 48;
        canvas.Text($"TURN {action.Turn} · {phase}", 700, phaseY, Gold, .76f, 794, 2, true);
        int cardHeight = Math.Clamp(660 - (phaseY + 36), 140, 333);
        var pose = new Pose(new(521, phaseY + 36 + cardHeight / 2f), new(cardHeight / 1.4f, cardHeight));
        if (CurrentHistoryCard() is { } face) {
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
        var pages = canvas.TextPages(HistoryDetails(action), .9f, 790, Math.Max(80, 690 - detailY), 200);
        historyDetailsPage = Math.Clamp(historyDetailsPage, 0, Math.Max(0, pages.Length - 1));
        canvas.Text(pages[historyDetailsPage], 700, detailY, Ink, .9f, 790, 200);
        if (pages.Length > 1) {
            MenuAction("history-details-back", "‹ Earlier detail", new(700, 702, 240, 42), new HistoryIntent.Details(-1), historyDetailsPage > 0);
            MenuAction("history-details-more", "More detail ›", new(1185, 702, 290, 42), new HistoryIntent.Details(1), historyDetailsPage + 1 < pages.Length);
        }
        if (action.HistoryIncomplete || !model.CoverageComplete)
            canvas.Text("Some earlier context is missing. No cause or result has been inferred.", 701, 177, Gold, .62f, 790, 2);
        MenuAction("history-previous", "‹ Previous play", new(382, 757, 235, 44), new HistoryIntent.Move(-1), index > 0);
        MenuAction("history-next", "Next play ›", new(632, 757, 220, 44), new HistoryIntent.Move(1), index + 1 < model.Actions.Length);
        int newActions = model.Actions.Count(item => item.FirstSequence > historySeenSequence);
        MenuAction("history-newest", newActions > 0 ? $"{newActions} new plays ›" : "Newest play", new(1240, 143, 280, 42), new HistoryIntent.Newest(), index + 1 < model.Actions.Length);
        MenuAction("history-report-scope", historyPrivateReport ? "Report includes my private choices" : "Report uses public facts", new(888, 753, 400, 48), new HistoryIntent.TogglePrivateReport(), services.HistoryArchive != null, historyPrivateReport, .7f);
        Button("history-save", "Save report", 1305, 756, 210, new HistoryIntent.SaveReport(), !historyLoading && services.HistoryArchive != null, height: 43);
    }

    private string HistoryDetails(MatchHistoryAction action)
    {
        var model = HistoryModel;
        if (ReferenceEquals(historyDetailModel, model) && historyDetailKey == action.Id) return historyDetailText;
        historyDetailModel = model; historyDetailKey = action.Id;
        var actions = model.Actions.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>(); pending.Enqueue(action.Id);
        var collected = new List<HistoryRecord>();
        while (pending.TryDequeue(out var id) && visited.Count < MatchHistoryReadModel.MaximumRetainedRecords) {
            if (!visited.Add(id) || !actions.TryGetValue(id, out var member)) continue;
            collected.AddRange(member.Records);
            foreach (string child in member.Children) pending.Enqueue(child);
        }
        var records = collected.DistinctBy(item => item.Sequence).OrderBy(item => item.Sequence).ToArray();
        var text = new StringBuilder();
        var payments = records.Where(item => item.Payment != null).ToArray();
        var opened = payments.FirstOrDefault(item => item.Payment!.Kind == "opened")?.Payment;
        if (opened != null) text.AppendLine("COST\n" + HistoryBill(opened.BillBefore) + "\n");
        foreach (var record in payments.Where(item => item.Payment!.Kind == "produced")) {
            var payment = record.Payment!;
            text.AppendLine((record.SourceName ?? "Mana source") + " produced " + HistoryMana(payment.Produced) + ".");
        }
        foreach (var group in payments.Where(item => item.Payment!.Kind == "spent").GroupBy(item => item.ExecutionId ?? "event:" + item.Sequence)) {
            var known = group.Where(item => item.Payment!.Spent != null).ToArray();
            if (known.Length > 0) {
                var spent = known.SelectMany(item => item.Payment!.Spent!).GroupBy(item => item.Key)
                    .ToDictionary(item => item.Key, item => item.Sum(value => (long)value.Value));
                text.AppendLine("Spent: " + string.Join(" + ", spent.Select(item => item.Value + " " + HistoryColor(item.Key))) + ".");
                var last = group.Last().Payment!;
                if (last.BillAfter is { IsEmpty: false }) text.AppendLine("Still to pay: " + HistoryBill(last.BillAfter));
            }
        }
        if (payments.LastOrDefault(item => item.Payment!.PoolAfter != null)?.Payment is { } remainder)
            text.AppendLine("Mana remaining: " + HistoryMana(remainder.PoolAfter) + ".\n");
        foreach (var record in payments.Where(item => item.Payment!.Kind is "completed" or "cancelled" or "restored")) {
            string label = record.Payment!.Kind switch { "completed" => "Payment completed.", "cancelled" => "Payment cancelled; already committed costs are shown above.", _ => "Mana was restored. Its earlier commitment remains recorded." };
            text.AppendLine(label);
        }
        foreach (var record in payments.Where(item => item.Payment!.Kind == "produced")) {
            var payment = record.Payment!;
            text.AppendLine("\nSOURCE DETAILS · " + (record.SourceName ?? "Mana source"));
            if (payment.SourceCounters is { Count: > 0 } counters)
                text.AppendLine("Counters at production: " + string.Join(", ", counters.Select(item => item.Value + " " + item.Key.ToLowerInvariant())));
            if (payment.ProducerMode is { Length: > 0 } mode) text.AppendLine(mode);
        }
        string section = "";
        foreach (var record in records.Where(item => item.Activity != null)) {
            var activity = record.Activity!;
            string nextSection = activity.ExecutionStep switch {
                ExecutionStep.Cost => "COMMITTED COSTS", ExecutionStep.Resolution or ExecutionStep.Complete => "RESULTS", _ => "TABLE EVENTS"
            };
            if (section != nextSection) { section = nextSection; text.AppendLine("\n" + section); }
            if (activity.Change is { } change) text.AppendLine($"{activity.CardName ?? "Card"}: {change.Property} {change.Before} → {change.After}.");
            else {
                if (activity.Message.Length > 0) text.AppendLine(activity.Message);
                if (activity.Detail.Length > 0 && activity.Detail != activity.Message) text.AppendLine(activity.Detail);
            }
        }
        if (payments.Length == 0 && !records.Any(item => item.Activity != null))
            text.AppendLine("The engine has recorded this action's stage. No committed payment or result was reported in this retained segment.");
        var origins = payments.Select(item => item.Payment!.Origin).Where(origin => origin is "manual" or "auto")
            .Distinct(StringComparer.Ordinal).Select(origin => origin == "auto" ? "Auto" : "manual input").ToArray();
        if (origins.Length > 0) text.AppendLine("\nRecorded payment gestures: " + string.Join(" and ", origins) + ".");
        if (historyDocument != null) text.AppendLine("\nRecorded match · " + historyDocument.Summary.StartedAt.ToLocalTime().ToString("MMM d, yyyy HH:mm"));
        return historyDetailText = text.ToString().Trim();
    }

    private static string HistoryColor(string symbol) => symbol switch {
        "W" => "white", "U" => "blue", "B" => "black", "R" => "red", "G" => "green", "C" => "colorless", _ => symbol
    };
    private static string HistoryMana(ImmutableDictionary<string, int>? mana) => mana == null ? "not reported"
        : mana.Any(item => item.Value > 0) ? string.Join(" + ", mana.Where(item => item.Value > 0).Select(item => item.Value + " " + HistoryColor(item.Key))) : "none";
    private static string HistoryBill(ImmutableArray<ManaBillPart>? bill) => bill is not { } parts ? "Not disclosed in this record"
        : parts.Length == 0 ? "nothing" : string.Join(" + ", parts.Select(part => part.Symbol is { Length: > 2 } symbol
            && int.TryParse(symbol.AsSpan(1, symbol.Length - 2), out int generic) ? (long)generic * part.Count + " generic"
            : part.Count + " " + HistoryColor(part.Symbol.Trim('{', '}'))));

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
