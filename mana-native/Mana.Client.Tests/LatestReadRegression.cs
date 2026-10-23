using System.Collections.Concurrent;
using Mana.Client;

internal static class LatestReadRegression
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var completions = new ConcurrentQueue<Action>();
        var signal = new SemaphoreSlim(0);
        var read = new LatestRead<string, string>(action => { completions.Enqueue(action); signal.Release(); });
        var first = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = 0;
        read.Request("first", () => { Interlocked.Increment(ref requests); started.SetResult(); return first.Task; });
        read.Start(); await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        read.Request("skipped", () => throw new Exception("An intermediate request must never start"));
        read.Request("latest", () => { Interlocked.Increment(ref requests); return Task.FromResult("latest page"); });
        read.Start();
        check(requests == 1 && read.View is { Context: "latest", Status: ReadStatus.Pending, Value: null },
            "Search changes coalesce while the previous provider call is outstanding");
        first.SetException(new IOException("old query failed"));
        await Drain();
        check(read.View is { Context: "latest", Status: ReadStatus.Pending, Error: "" },
            "An old search failure cannot replace a newer query or its loading state");
        read.Start(); await Drain();
        check(requests == 2 && read.View is { Context: "latest", Status: ReadStatus.Ready, Value: "latest page" },
            "Only the latest pending search starts after the old request settles");

        var revoked = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        read.Request("revoked printing", () => { started.SetResult(); return revoked.Task; });
        read.Start(); await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        read.Clear(); revoked.SetResult("wrong card's printing"); await Drain();
        check(read.View is { Context: null, Status: ReadStatus.Empty, Value: null },
            "Closing a printing chooser revokes both its request context and its late result");

        read.Request("retry", () => Task.FromException<string>(new IOException("offline")));
        read.Start(); await Drain();
        long failedGeneration = read.View.Generation;
        read.Start();
        check(read.View is { Status: ReadStatus.Failed, Error: "offline" } && read.View.Generation == failedGeneration,
            "A failed read remains visible without an implicit retry loop");

        var uncooperative = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        read.Request("closing", () => { started.SetResult(); return uncooperative.Task; });
        read.Start(); await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        read.Close(); await Drain();
        uncooperative.SetResult("after close");
        read.Request("reopen", () => Task.FromResult("must not reopen")); read.Start();
        check(read.View is { Status: ReadStatus.Closed, Value: null, Context: null },
            "Closing cancels the client wait and permanently rejects later reads without waiting for the provider");

        async Task Drain()
        {
            await signal.WaitAsync(TimeSpan.FromSeconds(5));
            while (completions.TryDequeue(out var completion)) completion();
        }
    }
}
