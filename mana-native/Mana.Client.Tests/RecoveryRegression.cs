using Mana.Client;
using Mana.Contracts;

internal static class RecoveryRegression
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var state = new GameSnapshot("recovery", "playing", null, null, 0, 1, "Main", "MAIN1", 0, [], [], null,
            new() { Id = "before", Kind = "input", Intent = DecisionIntent.Priority, CanAutoPass = true, OkEnabled = true }) { Revision = 1 };
        var connection = new FailingConnection(state);
        var client = new ClientSession(connection); client.Accept(state);
        var synchronized = client.LastSynchronizedAt;
        connection.FailReads = true;
        client.Poll(false, _ => { }); await Drain(client);
        check(client.NeedsRecovery && !client.UncertainCommand && client.LastSynchronizedAt == synchronized && client.State == state,
            "Failed observation retains inspectable state and its synchronization timestamp, marked stale");
        var reply = new PassPriority(state.Id, "before");
        check(!client.Submit(reply) && !client.Advance(_ => true, (_, _) => false) && connection.Replies == 0,
            "Stale decisions cannot be submitted manually or automatically");
        connection.FailReads = false;
        client.Poll(false, _ => { }); await Drain(client);
        check(!client.NeedsRecovery && client.LastConnectionError.Length == 0 && client.LastSynchronizedAt >= synchronized,
            "A fresh authoritative observation restores play and clears the connection warning");
        connection.FailReply = true;
        check(client.Submit(reply), "A recovered connection accepts the current scoped action"); await Drain(client);
        check(client.NeedsRecovery && client.UncertainCommand && connection.Replies == 1,
            "A failed command acknowledgement is explicitly uncertain and is never automatically resubmitted");
        connection.State = state with { Revision = 2, Decision = state.Decision! with { Id = "after" } };
        connection.FailReply = false;
        client.Poll(false, _ => { }); await Drain(client);
        check(!client.NeedsRecovery && !client.Submit(reply) && client.State?.Decision?.Id == "after" && connection.Replies == 1,
            "Recovery reconciles the next decision without replaying an uncertain old command");
        client.Reset();
        check(client.State == null && !client.NeedsRecovery, "Leaving retires the stale table and recovery state together");

        var rejectedConnection = new FailingConnection(state) { RejectReply = true };
        var rejected = new ClientSession(rejectedConnection); rejected.Accept(state);
        Exception? reported = null;
        rejected.Failed += error => reported = error;
        check(rejected.Submit(reply), "A definite rejection begins as one ordinary scoped command"); await Drain(rejected);
        check(reported is EngineCommandRejectedException && reported.Message == "That choice has changed. Use the current prompt."
                && rejected.LastFailureWasCommand && !rejected.NeedsRecovery && !rejected.UncertainCommand && rejected.LastConnectionError.Length == 0,
            "An explicit engine rejection retains the action error without pretending the connection or acknowledgement was lost");
        rejectedConnection.State = state with { Revision = 2, Decision = state.Decision! with { Id = "after-rejection" } };
        rejected.Poll(false, _ => { }); await Drain(rejected);
        check(rejected.State?.Decision?.Id == "after-rejection" && rejected.LastFailureWasCommand && reported is EngineCommandRejectedException
                && rejectedConnection.Replies == 1 && !rejected.Submit(reply),
            "Fresh observation reconciles the rejected decision without replaying it or erasing the command-failure acknowledgement");
        rejectedConnection.FailReads = true;
        rejected.Poll(false, _ => { }); await Drain(rejected);
        check(rejected.NeedsRecovery && !rejected.LastFailureWasCommand && !rejected.UncertainCommand && reported is IOException,
            "A later observation failure is classified as a connection failure rather than a rejected command");
        rejected.Reset();
        check(!rejected.LastFailureWasCommand && !rejected.NeedsRecovery, "Leaving clears rejection classification with the retired table");

        var clock = new RecoveryClock(); var stalled = new StalledConnection();
        var waiting = new ClientSession(stalled, clock: clock); waiting.Accept(state);
        waiting.Poll(false, _ => { });
        await stalled.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Now += TimeSpan.FromSeconds(6);
        check(waiting.NeedsRecovery && !waiting.Submit(reply) && !waiting.Advance(_ => true, (_, _) => false),
            "A stalled observation marks the board stale and blocks play before the connection timeout");
        stalled.Snapshot.SetResult(state with { Revision = 2 }); await Drain(waiting);
        check(!waiting.NeedsRecovery && waiting.LastSynchronizedAt == clock.Now,
            "A late healthy observation restores freshness without dropping the retained table");

        var ignored = new PriorityAbility("fetch", "card", "Arid Mesa", "Search", true);
        var queuedConnection = new FailingConnection(state with { IgnoredPriorityAbilities = [ignored] }) { FailReply = true };
        var queued = new ClientSession(queuedConnection); queued.Accept(queuedConnection.State);
        queued.Submit(reply);
        check(queued.SubmitSetting(new RestorePriorityAbility(state.Id, AbilityId: ignored.Id)),
            "Undo can queue behind an in-flight command before its acknowledgement fails");
        await Drain(queued);
        check(queued.NeedsRecovery && queuedConnection.Replies == 1, "Queued Undo waits for recovery rather than being lost or sent on stale state");
        queuedConnection.FailReply = false;
        queued.Poll(false, _ => { }); await Drain(queued);
        check(queuedConnection.Replies == 2 && queued.State!.IgnoredPriorityAbilities.Length == 0,
            "A queued Undo executes exactly once after fresh reconciliation");
    }
    private static async Task Drain(ClientSession client)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while ((client.Busy || client.Polling) && DateTime.UtcNow < deadline) { client.Pump(); await Task.Delay(5); }
        client.Pump();
        if (client.Busy || client.Polling) throw new TimeoutException("Recovery test did not finish");
    }
    private sealed class FailingConnection(GameSnapshot state) : IMatchConnection
    {
        internal GameSnapshot State = state;
        internal bool FailReads, FailReply, RejectReply;
        internal int Replies;
        public Task<GameSnapshot?> ObserveAsync() => FailReads ? Task.FromException<GameSnapshot?>(new IOException("Connection interrupted")) : Task.FromResult<GameSnapshot?>(State);
        public Task ExecuteAsync(GameCommand reply) {
            Replies++;
            if (FailReply) return Task.FromException(new IOException("Acknowledgement lost"));
            if (RejectReply) return Task.FromException(new EngineCommandRejectedException("That choice has changed. Use the current prompt."));
            if (reply is RestorePriorityAbility) State = State with { Revision = State.Revision + 1, IgnoredPriorityAbilities = [] };
            return Task.CompletedTask;
        }
        public Task ConcedeAsync(string gameId) => Task.CompletedTask;
    }
    private sealed class RecoveryClock : TimeProvider
    {
        internal DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class StalledConnection : IMatchConnection
    {
        internal TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<GameSnapshot?> Snapshot = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<GameSnapshot?> ObserveAsync() { Started.TrySetResult(); return Snapshot.Task; }
        public Task ExecuteAsync(GameCommand reply) => throw new InvalidOperationException("Stale state must not submit a command");
        public Task ConcedeAsync(string gameId) => Task.CompletedTask;
    }
}
