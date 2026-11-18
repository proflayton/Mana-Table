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
}
