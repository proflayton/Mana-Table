using Mana.Contracts;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    // Retire reading intent when its current workload disappears, before any
    // pacing consumer can mistake a no-longer-painted view for active reading.
    private void ReconcileStackWorkloadInspection()
    {
        if (expandedStackWorkload.Length == 0) return;
        if (match is not { Status: "playing", Stack.IsEmpty: false }
            || match.Decision is { Payload: not PriorityPayload }
            || !RepeatedStackWorkload
            || expandedStackWorkload != AllWorkloadsKey && !CurrentStackWorkloads.Any(group => group.Key == expandedStackWorkload))
            expandedStackWorkload = "";
    }

    // Current furniture is measured after StudyAttention, before composition.
    // An existing inspection has already paused this update's Auto consumers.
    // Open the fallback before retiring that intent so no update can unpause
    // between two read-only views. Required/stale reconciliation wins first.
    private void PrepareStackWorkloadOverview()
    {
        stackWorkloadOverviewLayout = null;
        if (match is not { Status: "playing", Stack.IsEmpty: false }
            || match.Decision is { Payload: not PriorityPayload } || !RepeatedStackWorkload) return;
        stackWorkloadOverviewLayout = MeasureStackWorkloadOverview();
        if (expandedStackWorkload == AllWorkloadsKey && stackWorkloadOverviewLayout == null) {
            OpenStack();
            expandedStackWorkload = "";
        }
    }
}
