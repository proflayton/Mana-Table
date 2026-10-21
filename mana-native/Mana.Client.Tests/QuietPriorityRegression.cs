using Mana.Client;
using Mana.Contracts;

internal static class QuietPriorityRegression
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var initial = State("first", "armed-token");
        try {
            new DecisionReply(initial.Id, initial.Decision!.Id, ReplyAction.PassUntilPublicAction).Validate();
            throw new InvalidOperationException("An empty quiet-yield token was accepted.");
        } catch (ArgumentException) { check(true, "Explicit quiet passing requires a nonempty engine token and current decision scope"); }
        new DecisionReply(initial.Id, initial.Decision!.Id, ReplyAction.PassUntilPublicAction, YieldToken: "armed-token").Validate();
        var ordinary = new SequenceConnection(initial);
        await new DecisionAdvance(ordinary).RunAsync(initial, _ => true, (_, _) => false);
        check(ordinary.Replies.Count == 0 && !initial.Decision!.CanAutoPass && DecisionQuietAdvance.CanPass(initial, "armed-token"),
            "A quiet token never broadens ordinary Auto when an instant remains available");

        var refreshed = initial with { Revision = 2, PhaseKey = "COMBAT_BEGIN", Decision = initial.Decision! with { Id = "second" } };
        var cast = refreshed with { Revision = 3, Stack = [new("Opponent's spell", "", false, null) { Id = "fresh-opponent-spell" }],
            Decision = refreshed.Decision! with { Id = "response", QuietYieldToken = "", CanAutoPass = true } };
        var windows = new SequenceConnection(refreshed, cast);
        var observed = await new DecisionQuietAdvance(windows).RunAsync(initial, "armed-token", _ => true, (_, _) => false);
        check(observed == cast && windows.Replies.Count == 2
            && windows.Replies.All(reply => reply.Action == ReplyAction.PassUntilPublicAction && reply.YieldToken == "armed-token")
            && windows.Replies.Select(reply => reply.DecisionId).SequenceEqual(["first", "second"]),
            "Quiet phase and prompt refreshes reuse the explicitly armed token, then stop before the new opponent-stack response");

        var busy = initial with { Revision = 2, Status = "resolving", Decision = null, DecisionOwnerId = null,
            PriorityPlayerId = null, ActionClass = "resolving" };
        var laterPriority = refreshed with { Revision = 3 };
        var laterCast = cast with { Revision = 4 };
        var otherResponder = busy with { DecisionOwnerId = 1, PriorityPlayerId = 1, ActionClass = "priority" };
        var transitions = new SequenceConnection(busy, otherResponder, laterPriority, laterCast);
        observed = await new DecisionQuietAdvance(transitions).RunAsync(initial, "armed-token", _ => true, (_, _) => false);
        var otherChoice = busy with { DecisionOwnerId = 1, ActionClass = "choice" };
        var publicAction = busy with { Activity = [new(1, 7, "MAIN1", "ability", 1, "Opponent activated an ability", null, null)] };
        var actionConnection = new SequenceConnection(publicAction, laterPriority);
        var stoppedOnAction = await new DecisionQuietAdvance(actionConnection).RunAsync(initial, "armed-token", _ => true, (_, _) => false);
        check(observed == laterCast && transitions.Replies.Count == 2
            && !DecisionQuietAdvance.CanPass(busy, "armed-token")
            && !DecisionQuietAdvance.CanWait(otherChoice, initial.Id, initial.Turn, initial.ActivePlayerId, "armed-token")
            && stoppedOnAction == publicAction && actionConnection.Replies.Count == 1,
            "The no-owner markBusy resolving transition only waits, then resumes the same-token prompt; private required choices and intervening public actions stop it");

        var replacement = refreshed with { Decision = refreshed.Decision! with { QuietYieldToken = "replacement-token" } };
        var revoked = new SequenceConnection(replacement);
        observed = await new DecisionQuietAdvance(revoked).RunAsync(initial, "armed-token", _ => true, (_, _) => false);
        check(observed == replacement && revoked.Replies.Count == 1,
            "A newly issued token ends the original quiet intent rather than silently rearming it");

        var required = refreshed with { ActionClass = "choice", Decision = new() { Id = "required", Kind = "choice", Min = 1, Max = 1,
            QuietYieldToken = "armed-token", Choices = [new(0, "Required choice", null)] } };
        var choiceConnection = new SequenceConnection(required);
        observed = await new DecisionQuietAdvance(choiceConnection).RunAsync(initial, "armed-token", _ => true, (_, _) => false);
        var stopped = new SequenceConnection(initial);
        await new DecisionQuietAdvance(stopped).RunAsync(initial, "armed-token", state => state.PhaseKey != "MAIN1", (_, _) => false);
        check(observed == required && choiceConnection.Replies.Count == 1 && stopped.Replies.Count == 0,
            "A required choice or configured phase stop cannot be consumed by quiet passing");

        using var cancellation = new CancellationTokenSource();
        var held = new HeldConnection(cast);
        var draining = new DecisionQuietAdvance(held).RunAsync(initial, "armed-token", _ => true, (_, _) => false, cancellation.Token);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel(); held.Release.SetResult();
        observed = await draining.WaitAsync(TimeSpan.FromSeconds(5));
        check(observed == cast && held.Replies == 1 && held.Observations == 1,
            "Take control reconciles an already submitted pass, then stops without submitting a response to the new spell");

        var serialConnection = new HeldConnection(cast); var session = new ClientSession(serialConnection); session.Accept(initial);
        bool started = session.AdvanceQuietly("armed-token", _ => true, (_, _) => false);
        await serialConnection.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        bool duplicate = session.AdvanceQuietly("armed-token", _ => true, (_, _) => false);
        bool interleaved = session.Submit(new(initial.Id, initial.Decision!.Id, ReplyAction.Confirm));
        session.CancelAdvance(); serialConnection.Release.SetResult();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (session.Busy && DateTime.UtcNow < deadline) { session.Pump(); await Task.Delay(2); }
        check(started && !duplicate && !interleaved && !session.Busy && session.State == cast && serialConnection.Replies == 1,
            "The shared client command lane serializes quiet batches with ordinary game commands and publishes the reconciled outcome");
        session.Close();
    }

    private static GameSnapshot State(string decision, string token) => new("quiet-game", "playing", null, null, 0, 7, "Main phase", "MAIN1", 1,
        [new(0, "You", 40, true, false, [], []), new(1, "Opponent", 40, false, false, [], [])], [], null,
        new() { Id = decision, Kind = "input", Intent = DecisionIntent.Priority, OkEnabled = true, CanAutoPass = false, QuietYieldToken = token })
        { Revision = 1, PriorityPlayerId = 0, DecisionOwnerId = 0, ActionClass = "priority" };

    private sealed class SequenceConnection(params GameSnapshot[] snapshots) : IMatchConnection
    {
        private int index;
        internal List<DecisionReply> Replies { get; } = [];
        public Task<GameSnapshot?> ObserveAsync() => Task.FromResult<GameSnapshot?>(snapshots[Math.Min(index++, snapshots.Length - 1)]);
        public Task ReplyAsync(DecisionReply reply) { reply.Validate(); Replies.Add(reply); return Task.CompletedTask; }
        public Task ConcedeAsync(string gameId) => Task.CompletedTask;
    }
    private sealed class HeldConnection(GameSnapshot after) : IMatchConnection
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Replies, Observations;
        public async Task ReplyAsync(DecisionReply reply) { reply.Validate(); Replies++; Started.SetResult(); await Release.Task; }
        public Task<GameSnapshot?> ObserveAsync() { Observations++; return Task.FromResult<GameSnapshot?>(after); }
        public Task ConcedeAsync(string gameId) => Task.CompletedTask;
    }
}
