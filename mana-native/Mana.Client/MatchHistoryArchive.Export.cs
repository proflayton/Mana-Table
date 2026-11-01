using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.Client;

public sealed partial class MatchHistoryArchive
{
    // Visibility is certified when the provider captures each fact. A later
    // public result stays readable even when preparation belonged to its owner.
    // Export additionally closes references and never includes private bills in
    // a public producer receipt, regardless of a malformed mixed-scope caller.
    internal static ImmutableArray<HistoryRecord> PublicRecords(ImmutableArray<HistoryRecord> records)
    {
        var visible = records.Where(record => record.Exposure == "public").ToImmutableArray();
        // A reference does not certify its own disclosure. Only an explicitly
        // public occurrence admits an execution identity into this export.
        var executions = visible.Select(record => record.Execution?.Occurrence.Id)
            .Where(id => !string.IsNullOrEmpty(id)).ToHashSet(StringComparer.Ordinal);
        string? PublicId(string? id) => id != null && executions.Contains(id) ? id : null;
        return visible.Select(record => {
            bool concealedParent = record.ParentId != null && PublicId(record.ParentId) == null;
            var execution = record.Execution;
            if (execution != null) {
                var occurrence = execution.Occurrence;
                execution = execution with { Occurrence = occurrence with {
                    ParentId = PublicId(occurrence.ParentId),
                    BudgetRootId = PublicId(occurrence.BudgetRootId) ?? occurrence.Id
                } };
            }
            var payment = record.Payment;
            if (payment != null) payment = payment with {
                ProducerExecutionId = PublicId(payment.ProducerExecutionId),
                RestoredExecutionId = PublicId(payment.RestoredExecutionId),
                BillBefore = concealedParent || payment.Kind == "produced" ? null : payment.BillBefore,
                BillAfter = concealedParent || payment.Kind == "produced" ? null : payment.BillAfter
            };
            var activity = record.Activity;
            if (activity != null) activity = activity with {
                ExecutionId = PublicId(activity.ExecutionId) ?? "",
                Detail = "",
                Cause = activity.Cause is { } cause ? cause with { Id = PublicId(cause.Id) ?? "" } : null
            };
            return record with { ParentId = PublicId(record.ParentId), ExecutionId = PublicId(record.ExecutionId),
                Activity = activity, Execution = execution, Payment = payment };
        }).ToImmutableArray();
    }
}
