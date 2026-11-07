using Mana.Client;

namespace Mana.Table;

// A passive scheduler wait. Only the existing campaign owner pumps clients,
// drains evidence and polls; waking never adopts a frame or dispatches work.
internal static class CampaignOwnerWait
{
    internal static TimeSpan Bound(TimeSpan remaining, params TimeSpan?[] nextPolls)
    {
        foreach (var due in nextPolls)
            if (due is { } delay && delay < remaining) remaining = delay;
        return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
    }

    internal static async Task WaitAsync(TimeSpan maximumDelay,
        Func<CancellationToken, Task> evidence, ClientSession first, ClientSession? second = null,
        CancellationToken cancellation = default, TimeProvider? clock = null)
    {
        cancellation.ThrowIfCancellationRequested();
        if (maximumDelay <= TimeSpan.Zero) return;
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        try {
            Task deadline = Task.Delay(maximumDelay, clock ?? TimeProvider.System, waiting.Token);
            Task firstWork = first.WaitForQueuedWorkAsync(waiting.Token);
            Task evidenceWork = evidence(waiting.Token);
            Task completed = second == null
                ? await Task.WhenAny(firstWork, evidenceWork, deadline)
                : await Task.WhenAny(firstWork, second.WaitForQueuedWorkAsync(waiting.Token), evidenceWork, deadline);
            await completed;
            cancellation.ThrowIfCancellationRequested();
        } finally {
            // Cancel every losing subscription/timer so an idle owner retains
            // no per-iteration waiters on a long-lived queue signal.
            waiting.Cancel();
        }
    }
}
