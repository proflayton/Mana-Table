using System.Collections.Immutable;
using Mana.Client;
using Mana.Contracts;

internal static class QueuedWorkRegression
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var port = new Port();
        var client = new ClientSession(port, observationDelivery: ObservationDeliveryMode.External);
        var order = new List<string>();
        var waiting = client.WaitForQueuedWorkAsync();
        check(!waiting.IsCompleted && client.State == null && port.Calls == 0,
            "Waiting for client work creates no poll, command, acceptance or pump");
        await Task.Run(() => client.Post(() => order.Add("first")));
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        check(client.WaitForQueuedWorkAsync().IsCompletedSuccessfully && order.Count == 0,
            "Posted-before-wait and waited-before-post work both wake without executing a callback");
        client.Pump();
        check(order.SequenceEqual(["first"]) && !client.WaitForQueuedWorkAsync().IsCompleted,
            "The serial pump alone executes queued work and its consumed wake does not cause an idle spin");

        using var cancellation = new CancellationTokenSource();
        var canceled = client.WaitForQueuedWorkAsync(cancellation.Token);
        var survivor = client.WaitForQueuedWorkAsync();
        cancellation.Cancel();
        check(await IsCanceled(canceled) && !survivor.IsCompleted,
            "Canceling one queued-work waiter does not cancel the queue signal or another waiter");
        using (var alreadyCanceled = new CancellationTokenSource()) {
            alreadyCanceled.Cancel();
            check(await IsCanceled(client.WaitForQueuedWorkAsync(alreadyCanceled.Token)),
                "A pre-canceled queued-work wait honors its caller's cancellation");
        }
        for (int index = 0; index < 1024; index++) {
            using var detached = new CancellationTokenSource();
            var task = client.WaitForQueuedWorkAsync(detached.Token);
            detached.Cancel();
            if (!await IsCanceled(task)) throw new InvalidOperationException("A canceled queue waiter remained attached.");
        }
        client.Post(() => order.Add("second"));
        await survivor.WaitAsync(TimeSpan.FromSeconds(5));
        client.Pump();
        check(order.SequenceEqual(["first", "second"]),
            "Repeated detached waiters leave the shared queue signal usable for its surviving owner");

        var state = State();
        using var ingress = client.OpenObservationIngress(state.Id, state.ViewerId);
        var incoming = client.WaitForQueuedWorkAsync();
        bool posted = await Task.Run(() => ingress.TryPost(state));
        await incoming.WaitAsync(TimeSpan.FromSeconds(5));
        client.Post(() => order.Add("after-observation"));
        client.Changed += (_, current) => {
            order.Add("state:" + current!.Revision);
            client.Post(() => order.Add("follow-up"));
        };
        check(posted && client.State == null && port.Calls == 0,
            "External observation ingress wakes the ordinary queue without publishing private state or accessing the provider");
        client.Pump();
        check(ReferenceEquals(client.State, state) && order.TakeLast(2).SequenceEqual(["state:1", "after-observation"])
                && client.WaitForQueuedWorkAsync().IsCompletedSuccessfully,
            "Wake notifications preserve external FIFO and leave callbacks appended during the frozen prefix for the next pump");
        client.Pump();
        check(order.Last() == "follow-up" && !client.WaitForQueuedWorkAsync().IsCompleted,
            "The next owner pump consumes the follow-up before waiting again");

        int delivered = 0;
        for (int index = 0; index < 64; index++) {
            var wake = client.WaitForQueuedWorkAsync();
            await Task.Run(() => client.Post(() => delivered++));
            await wake.WaitAsync(TimeSpan.FromSeconds(5));
            client.Pump();
        }
        check(delivered == 64 && port.Calls == 0,
            "Repeated cross-thread completion handoffs have no lost wake and preserve the single command owner");
        var closing = client.WaitForQueuedWorkAsync();
        client.Close();
        await closing.WaitAsync(TimeSpan.FromSeconds(5));
        check(client.WaitForQueuedWorkAsync().IsCompletedSuccessfully && port.Calls == 0,
            "Closing a session releases current and future passive waiters without requiring another pump or provider read");
    }

    private static async Task<bool> IsCanceled(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)); return false; }
        catch (OperationCanceledException) { return true; }
    }

    private static GameSnapshot State() => new("queued-work", "playing", null, null, 0, 1, "Main", "MAIN1", 0,
        [new(0, "You", 40, true, false, ImmutableDictionary<string, int>.Empty, []),
         new(1, "Other", 40, false, false, ImmutableDictionary<string, int>.Empty, [])], [], null,
        new("priority", new(), new PriorityPayload(new("Pass", true)))) {
            Revision = 1, BoardRevision = 1, DecisionOwnerId = 0, PriorityPlayerId = 0, ActionClass = "priority" };

    private sealed class Port : IMatchConnection
    {
        internal int Calls;
        public Task<GameSnapshot?> ObserveAsync() { Calls++; throw new InvalidOperationException("A passive queue wait cannot poll."); }
        public Task ExecuteAsync(GameCommand command) { Calls++; throw new InvalidOperationException("A passive queue wait cannot dispatch."); }
        public Task ConcedeAsync(string gameId) { Calls++; throw new InvalidOperationException("A passive queue wait cannot concede."); }
    }
}
