namespace Mana.Client;

public sealed partial class ClientSession
{
    private readonly object queuedWorkGate = new();
    private TaskCompletionSource queuedWork = NewQueuedWorkSignal();

    private static TaskCompletionSource NewQueuedWorkSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Waits for posted work or closure without pumping the session or
    /// granting any command or observation authority. The owner must still Pump.</summary>
    public Task WaitForQueuedWorkAsync(CancellationToken cancellation = default)
    {
        if (cancellation.IsCancellationRequested) return Task.FromCanceled(cancellation);
        lock (queuedWorkGate) {
            if (Closed || !completions.IsEmpty) return Task.CompletedTask;
            // All waiters share the same signal; cancellation only detaches the
            // individual waiter. The next empty wait retires a consumed pulse.
            if (queuedWork.Task.IsCompleted) queuedWork = NewQueuedWorkSignal();
            return queuedWork.Task.WaitAsync(cancellation);
        }
    }

    private void WakeQueuedWorkWaiters()
    {
        lock (queuedWorkGate) queuedWork.TrySetResult();
    }
}
