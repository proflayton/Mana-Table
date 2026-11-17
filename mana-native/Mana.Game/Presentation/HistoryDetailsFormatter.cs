using System.Collections.Immutable;
using System.Text;
using Mana.Client;
using Mana.Contracts;

namespace Mana.Table.Presentation;

// Pure reading over retained facts; it never resolves a current card or action.
internal static class HistoryDetailsFormatter
{
    internal static string Format(MatchHistoryReadModel model, MatchHistoryAction action, HistoryArchiveSummary? summary = null)
    {
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
        var announcements = records.Where(item => item.Announcement != null).OrderBy(item => item.Sequence).ToArray();
        if (announcements.Length == 0 && action.Kind is ExecutionKind.Spell or ExecutionKind.Activation or ExecutionKind.Trigger)
            text.AppendLine("\nANNOUNCED TARGETS\nNot reported in this retained history.");
        foreach (var record in announcements) {
            var announcement = record.Announcement!;
            text.AppendLine(announcement.Kind == "retargeted" ? "\nTARGETS CHANGED" : "\nANNOUNCED TARGETS");
            if (announcement.CauseExecutionId is { } cause && model.Actions.FirstOrDefault(item => item.Id == cause) is { } redirect)
                text.AppendLine("Changed by " + redirect.SourceName + ".");
            if (announcement.AnnouncedX is { } x) text.AppendLine("Announced X: " + x + ".");
            if (announcement.Targets.IsEmpty && announcement.TargetKnowledge == "Known") text.AppendLine("No targets.");
            foreach (var target in announcement.Targets) {
                var details = new List<string>();
                if (target.Type != null) details.Add(target.Type);
                if (target.Zone != null) details.Add(target.Zone + " at announcement");
                if (target.ControllerId is { } controller) details.Add("controlled by player " + controller + " at announcement");
                if (target.AllocatedAmount is { } amount) details.Add("assigned " + amount);
                string name = target.Name ?? (target.Kind == "player" ? "Player " + target.PlayerId
                    : target.Kind == "stack" ? "Recorded spell or ability" : "Undisclosed card");
                text.AppendLine("Group " + (target.GroupIndex + 1) + ", target " + (target.TargetIndex + 1) + ": " + name
                    + (details.Count == 0 ? "." : " — " + string.Join(" · ", details) + "."));
            }
            if (announcement.TargetKnowledge == "Unknown") text.AppendLine("Some target information was not disclosed at announcement.");
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
        if (payments.Length == 0 && announcements.Length == 0 && !records.Any(item => item.Activity != null))
            text.AppendLine("The engine has recorded this action's stage. No committed payment or result was reported in this retained segment.");
        var origins = payments.Select(item => item.Payment!.Origin).Where(origin => origin is "manual" or "auto")
            .Distinct(StringComparer.Ordinal).Select(origin => origin == "auto" ? "Auto" : "manual input").ToArray();
        if (origins.Length > 0) text.AppendLine("\nRecorded payment gestures: " + string.Join(" and ", origins) + ".");
        if (summary != null) text.AppendLine("\nRecorded match · " + summary.StartedAt.ToLocalTime().ToString("MMM d, yyyy HH:mm"));
        return text.ToString().Trim();
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

}
