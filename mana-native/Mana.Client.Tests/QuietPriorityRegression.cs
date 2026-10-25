using System.Collections.Immutable;
using System.Text.Json;
using Mana.Client;
using Mana.Contracts;

internal static class QuietPriorityRegression
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var initial = State("first", "armed-token");
        var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var legacyDraw = JsonSerializer.Deserialize<ActivityEntry>("""
            {"id":1,"turn":7,"phaseKey":"DRAW","kind":"draw","playerId":1,"message":"Opponent drew a card.","cardId":null,"cardName":null}
            """, json)!;
        var routineDraw = JsonSerializer.Deserialize<ActivityEntry>("""
            {"id":2,"turn":7,"phaseKey":"DRAW","kind":"draw","playerId":1,"message":"Opponent drew for the turn.","cardId":null,"cardName":null,"interruptsQuietYield":false}
            """, json)!;
        check(legacyDraw.InterruptsQuietYield && !routineDraw.InterruptsQuietYield,
            "Old activity JSON remains interrupting by default; an explicit engine false flag permits a routine draw");
        var effectDraw = new ActivityEntry(3, 7, "MAIN1", "draw", 1, "Opponent drew from an effect.", null, null);
        var unknown = new ActivityEntry(4, 7, "MAIN1", "future-public-event", 1, "A newly supported event occurred.", null, null);
        check(new[] { legacyDraw, effectDraw, unknown }.All(entry => QuietPriorityPolicy.HasInterruption(initial with { Activity = [entry] }, 0)),
            "Unmarked legacy draws, effect draws and unfamiliar public events all interrupt quiet passing");
        try {
            new PassForNow(initial.Id, initial.Decision!.Id, "").Validate();
            throw new InvalidOperationException("An empty quiet-yield token was accepted.");
        } catch (ArgumentException) { check(true, "Explicit quiet passing requires a nonempty engine token and current decision scope"); }
        new PassForNow(initial.Id, initial.Decision!.Id, YieldToken: "armed-token").Validate();
        var ordinary = new SequenceConnection(initial);
        await new DecisionAdvance(ordinary).RunAsync(initial, _ => true, (_, _) => false);
        check(ordinary.Replies.Count == 0 && !((PriorityPayload)initial.Decision!.Payload).CanAutoPass && QuietPriorityPolicy.CanPass(initial, "armed-token"),
            "A quiet token never broadens ordinary Auto when an instant remains available");
        var both = initial with { Decision = initial.Decision! with { Payload = ((PriorityPayload)initial.Decision!.Payload) with { CanAutoPass = true } } };
        var sharedConnection = new SequenceConnection(both);
        var shared = new DecisionAdvance(sharedConnection);
        for (int iteration = 0; iteration < 4; iteration++) {
            using var stoppedAfterObserve = new CancellationTokenSource();
            sharedConnection.OnObserve = stoppedAfterObserve.Cancel;
            if (iteration % 2 == 0) await shared.RunAsync(both, _ => true, (_, _) => false, stoppedAfterObserve.Token);
            else await shared.RunQuietlyAsync(both, "armed-token", _ => true, (_, _) => false, stoppedAfterObserve.Token);
        }
        check(sharedConnection.Replies is [AutoPassPriority, PassForNow] && sharedConnection.Observations == 4,
            "One executor retains independent Auto and quiet submitted scopes across batches; observing the same prompt never resubmits either intent");
        foreach (bool quiet in new[] { false, true }) {
            var advancing = new SequenceConnection(Enumerable.Range(1, 40).Select(index => both with {
                Revision = index + 1, Decision = both.Decision! with { Id = "bounded-" + index }
            }).ToArray());
            var runner = new DecisionAdvance(advancing);
            if (quiet) await runner.RunQuietlyAsync(both, "armed-token", _ => true, (_, _) => false);
            else await runner.RunAsync(both, _ => true, (_, _) => false);
            // A busy runner may hit the elapsed-time budget before the pass cap.
            check(advancing.Replies.Count is > 0 and <= 32 && advancing.Observations == advancing.Replies.Count,
                $"{(quiet ? "Quiet" : "Auto")} yields within 32 authorized passes and reconciles every submitted pass");
        }

        var refreshed = initial with { Revision = 2, PhaseKey = "COMBAT_BEGIN", Decision = initial.Decision! with { Id = "second" } };
        var cast = refreshed with { Revision = 3, Stack = [new("Opponent's spell", "", false, null) { Id = "fresh-opponent-spell" }],
            Decision = refreshed.Decision! with { Id = "response", Payload = ((PriorityPayload)refreshed.Decision!.Payload) with { QuietYieldToken = "", CanAutoPass = true } } };
        var windows = new SequenceConnection(refreshed, cast);
        var observed = await new DecisionAdvance(windows).RunQuietlyAsync(initial, "armed-token", _ => true, (_, _) => false);
        check(observed == cast && windows.Replies.Count == 2
            && windows.Replies.All(reply => reply is PassForNow { YieldToken: "armed-token" })
            && windows.Replies.OfType<PassForNow>().Select(reply => reply.DecisionId).SequenceEqual(["first", "second"]),
            "Quiet phase and prompt refreshes reuse the explicitly armed token, then stop before the new opponent-stack response");

        var busy = initial with { Revision = 2, Status = "resolving", Decision = null, DecisionOwnerId = null,
            PriorityPlayerId = null, ActionClass = "resolving" };
        var laterPriority = refreshed with { Revision = 3 };
        var laterCast = cast with { Revision = 4 };
        var otherResponder = busy with { DecisionOwnerId = 1, PriorityPlayerId = 1, ActionClass = "priority" };
        var otherAttack = busy with { DecisionOwnerId = 1, ActionClass = "attack" };
        var transitions = new SequenceConnection(busy, otherResponder, otherAttack, initial, laterPriority, laterCast);
        observed = await new DecisionAdvance(transitions).RunQuietlyAsync(initial, "armed-token", _ => true, (_, _) => false);
        var otherChoice = busy with { DecisionOwnerId = 1, ActionClass = "choice" };
        var publicAction = busy with { Activity = [new(1, 7, "MAIN1", "ability", 1, "Opponent activated an ability", null, null)] };
        var actionConnection = new SequenceConnection(publicAction, laterPriority);
        var stoppedOnAction = await new DecisionAdvance(actionConnection).RunQuietlyAsync(initial, "armed-token", _ => true, (_, _) => false);
        check(observed == laterCast && transitions.Replies.Count == 2
            && !QuietPriorityPolicy.CanPass(busy, "armed-token")
            && !QuietPriorityPolicy.CanPass(otherAttack, "armed-token")
            && QuietPriorityPolicy.CanWait(otherAttack, initial.Id, initial.Turn, initial.ActivePlayerId, "armed-token")
            && !QuietPriorityPolicy.CanWait(otherAttack with { DecisionOwnerId = 0 }, initial.Id, initial.Turn, initial.ActivePlayerId, "armed-token")
            && !QuietPriorityPolicy.CanWait(otherChoice, initial.Id, initial.Turn, initial.ActivePlayerId, "armed-token")
            && stoppedOnAction == publicAction && actionConnection.Replies.Count == 1,
            "Busy, other-seat priority and empty attacker selection only wait, then resume the same-token prompt; local attacks, private required choices and public actions stop it");

        var routineMove = new ActivityEntry(1, 7, "DRAW", "moved", 1, "Opponent moved a card from library to hand.", null, null) { InterruptsQuietYield = false };
        var drawHandoff = otherResponder with { PhaseKey = "DRAW", Activity = [routineMove, routineDraw] };
        var afterDraw = laterPriority with { Revision = 4, Activity = drawHandoff.Activity };
        var afterDrawCast = laterCast with { Revision = 5, Activity = drawHandoff.Activity };
        var drawConnection = new SequenceConnection(drawHandoff, afterDraw, afterDrawCast);
        observed = await new DecisionAdvance(drawConnection).RunQuietlyAsync(initial, "armed-token", _ => true, (_, _) => false);
        check(observed == afterDrawCast && drawConnection.Replies.Count == 2
            && drawConnection.Replies.OfType<PassForNow>().Select(reply => reply.DecisionId).SequenceEqual(["first", "second"])
            && drawConnection.Replies.All(reply => reply is PassForNow { YieldToken: "armed-token" }),
            "Engine-marked routine moved and draw history survives an opponent handoff and the next same-token local pass");
        var effectDrawState = busy with { Activity = [effectDraw] };
        var effectDrawConnection = new SequenceConnection(effectDrawState, laterPriority);
        observed = await new DecisionAdvance(effectDrawConnection).RunQuietlyAsync(initial, "armed-token", _ => true, (_, _) => false);
        check(observed == effectDrawState && effectDrawConnection.Replies.Count == 1,
            "An ordinary effect draw interrupts the worker before it can pass another empty local window");

        var replacement = refreshed with { Decision = refreshed.Decision! with { Payload = ((PriorityPayload)refreshed.Decision!.Payload) with { QuietYieldToken = "replacement-token" } } };
        var revoked = new SequenceConnection(replacement);
        observed = await new DecisionAdvance(revoked).RunQuietlyAsync(initial, "armed-token", _ => true, (_, _) => false);
        check(observed == replacement && revoked.Replies.Count == 1,
            "A newly issued token ends the original quiet intent rather than silently rearming it");

        var required = refreshed with { ActionClass = "choice", Decision = new Decision("required", new DecisionContext(), new OptionsPayload(new ChoiceSelection([new(0, "Required choice", null)], new SelectionRange(1, 1), []))) };
        var choiceConnection = new SequenceConnection(required);
        observed = await new DecisionAdvance(choiceConnection).RunQuietlyAsync(initial, "armed-token", _ => true, (_, _) => false);
        var stopped = new SequenceConnection(initial);
        await new DecisionAdvance(stopped).RunQuietlyAsync(initial, "armed-token", state => state.PhaseKey != "MAIN1", (_, _) => false);
        check(observed == required && choiceConnection.Replies.Count == 1 && stopped.Replies.Count == 0,
            "A required choice or configured phase stop cannot be consumed by quiet passing");

        using var cancellation = new CancellationTokenSource();
        var held = new HeldConnection(cast);
        var draining = new DecisionAdvance(held).RunQuietlyAsync(initial, "armed-token", _ => true, (_, _) => false, cancellation.Token);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel(); held.Release.SetResult();
        observed = await draining.WaitAsync(TimeSpan.FromSeconds(5));
        check(observed == cast && held.Replies == 1 && held.Observations == 1,
            "Take control reconciles an already submitted pass, then stops without submitting a response to the new spell");

        var serialConnection = new HeldConnection(cast); var session = new ClientSession(serialConnection); session.Accept(initial);
        bool started = session.AdvanceQuietly("armed-token", _ => true, (_, _) => false);
        await serialConnection.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        check(session.IsAdvancing, "The shared session marks a running quiet batch as advancement before its pass finishes");
        bool duplicate = session.AdvanceQuietly("armed-token", _ => true, (_, _) => false);
        bool interleaved = session.Submit(new PassPriority(initial.Id, initial.Decision!.Id));
        session.CancelAdvance(); serialConnection.Release.SetResult();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (session.Busy && DateTime.UtcNow < deadline) { session.Pump(); await Task.Delay(2); }
        check(started && !duplicate && !interleaved && !session.Busy && !session.IsAdvancing && session.State == cast && serialConnection.Replies == 1,
            "The shared client command lane serializes quiet batches with ordinary game commands and publishes the reconciled outcome");
        session.Close();

        var retiredConnection = new HeldConnection(cast); var retired = new ClientSession(retiredConnection); retired.Accept(initial);
        retired.AdvanceQuietly("armed-token", _ => true, (_, _) => false);
        await retiredConnection.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        retired.Reset(); retiredConnection.Release.SetResult();
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (retired.Busy && DateTime.UtcNow < deadline) { retired.Pump(); await Task.Delay(2); }
        check(!retired.Busy && !retired.IsAdvancing && retired.State == null && retiredConnection.Observations == 1,
            "Reset retires a quiet batch's publication while still reconciling its in-flight pass and releasing the shared lifecycle");
        retired.Close();
    }

    private static GameSnapshot State(string decision, string token) => new("quiet-game", "playing", null, null, 0, 7, "Main phase", "MAIN1", 1,
        [new(0, "You", 40, true, false, ImmutableDictionary<string, int>.Empty, []), new(1, "Opponent", 40, false, false, ImmutableDictionary<string, int>.Empty, [])], [], null,
        new Decision(decision, new DecisionContext(), new PriorityPayload(new DecisionReply("Confirm", true), null, CanAutoPass: false, QuietYieldToken: token, Abilities: [])))
        { Revision = 1, PriorityPlayerId = 0, DecisionOwnerId = 0, ActionClass = "priority" };

    private sealed class SequenceConnection(params GameSnapshot[] snapshots) : IMatchConnection
    {
        private int index;
        internal List<GameCommand> Replies { get; } = [];
        internal Action? OnObserve;
        internal int Observations;
        public Task<GameSnapshot?> ObserveAsync() { Observations++; OnObserve?.Invoke(); return Task.FromResult<GameSnapshot?>(snapshots[Math.Min(index++, snapshots.Length - 1)]); }
        public Task ExecuteAsync(GameCommand reply) { reply.Validate(); Replies.Add(reply); return Task.CompletedTask; }
        public Task ConcedeAsync(string gameId) => Task.CompletedTask;
    }
    private sealed class HeldConnection(GameSnapshot after) : IMatchConnection
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Replies, Observations;
        public async Task ExecuteAsync(GameCommand reply) { reply.Validate(); Replies++; Started.SetResult(); await Release.Task; }
        public Task<GameSnapshot?> ObserveAsync() { Observations++; return Task.FromResult<GameSnapshot?>(after); }
        public Task ConcedeAsync(string gameId) => Task.CompletedTask;
    }
}
