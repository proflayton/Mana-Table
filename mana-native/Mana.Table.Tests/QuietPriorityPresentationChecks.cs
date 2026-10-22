using System.Collections.Immutable;
using System.Collections.Concurrent;
using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Mana.Table.Interaction;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal static class QuietPriorityPresentationChecks
{
    internal static void Run(string profile, Action<bool, string> check)
    {
        string directory = Path.Combine(profile, "quiet-priority-polling");
        Directory.CreateDirectory(directory);
        new PlayPreferences { Automatic = false, ReducedMotion = true, Sounds = false }.Save(directory);
        var engine = new InteractionScenarioEngine();
        var example = PriorityAbilitySmoke.Example();
        engine.Present(example with { Id = "quiet-priority-polling", PriorityPlayerId = 0, DecisionOwnerId = 0,
            ActionClass = "priority", Decision = example.Decision! with { QuietYieldToken = "original-token" } });
        using var app = new TableApplication(TestTableServices.For(engine), directory, initialState: engine.State) { Automated = true };
        app.Initialize();
        var view = app.TestPort;
        double seconds = 0;
        var point = new Point(20, 60);
        PresentationFrame frame = app.ComposePresentation(Time());
        GameTime Time() => new(TimeSpan.FromSeconds(seconds), TimeSpan.FromMilliseconds(1));
        PresentationFrame Tick(Point? next = null, ButtonState left = ButtonState.Released)
        {
            point = next ?? point; seconds += .001;
            app.Update(Time(), new(point.X, point.Y, 0, left, ButtonState.Released, ButtonState.Released,
                ButtonState.Released, ButtonState.Released), new(), new(1600, 900));
            return frame = app.ComposePresentation(Time());
        }
        TableHit Hit(string id) => frame.Hits.Single(hit => hit.Id == id);
        DrawCommand.Panel Surface(PresentationFrame value, Rectangle bounds) => value.Drawing.Commands
            .OfType<DrawCommand.Panel>().Single(panel => panel.Bounds == bounds);
        var pausedLobby = new Lobby("local", "Controlled observation", null, false, null, true, "", null, [], []);
        TaskCompletionSource<GameSnapshot?> StartPoll()
        {
            var gate = new TaskCompletionSource<GameSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.ObserveOverride = () => gate.Task;
            view.PresentLobby(pausedLobby, false); Tick(); view.PresentLobby(pausedLobby, true);
            check(view.Polling, "The quiet-priority regression holds a real ClientSession observation in flight");
            return gate;
        }
        void CompletePoll(TaskCompletionSource<GameSnapshot?> gate, GameSnapshot state, bool mouseHeld = false)
        {
            gate.SetResult(state);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (view.Polling && DateTime.UtcNow < deadline) {
                Tick(left: mouseHeld ? ButtonState.Pressed : ButtonState.Released); Thread.Yield();
            }
            check(!view.Polling && view.Error.Length == 0, "The held observation completes without a command or connection error");
        }
        void Click(string id)
        {
            var center = Hit(id).Bounds.Center;
            Tick(center, ButtonState.Pressed); Tick(center);
        }

        var original = Hit("quiet-priority-arm");
        var idle = Surface(frame, original.Bounds);
        var firstPoll = StartPoll();
        check(Hit(original.Id).Bounds == original.Bounds && Surface(frame, original.Bounds) == idle,
            "Pass for now retains its enabled appearance and exact input bounds during an unchanged observation");
        Tick(original.Bounds.Center);
        var hover = Surface(frame, original.Bounds);
        Tick(original.Bounds.Center);
        check(Surface(frame, original.Bounds) == hover && hover.FillColor != idle.FillColor,
            "Pass for now keeps its hover response throughout an in-flight observation");
        Tick(original.Bounds.Center, ButtonState.Pressed);
        var pressed = Surface(frame, original.Bounds);
        check(pressed.FillColor != hover.FillColor && Hit(original.Id).Bounds == original.Bounds,
            "Pass for now keeps its pressed response and input target while polling");
        Tick(original.Bounds.Center);
        check(frame.Hits.Any(hit => hit.Id == "quiet-priority-stop") && engine.Replies.Count == 0 && view.Polling,
            "Clicking Pass for now during polling records local intent without interleaving an engine command");
        Click("quiet-priority-stop");
        check(frame.Hits.Any(hit => hit.Id == original.Id) && engine.Replies.Count == 0,
            "Take control can cancel the local quiet intent before the observation returns");
        CompletePoll(firstPoll, engine.State);

        var revokedPoll = StartPoll();
        Click(original.Id);
        engine.Present(engine.State with { Decision = engine.State.Decision! with { QuietYieldToken = "replacement-token" } });
        CompletePoll(revokedPoll, engine.State);
        check(!frame.Hits.Any(hit => hit.Id == "quiet-priority-stop")
            && frame.Hits.Any(hit => hit.Id == original.Id) && engine.Replies.Count == 0,
            "A completed poll with a replacement token cancels the original intent before any stale pass is sent");

        var stalePressPoll = StartPoll();
        Tick(Hit(original.Id).Bounds.Center, ButtonState.Pressed);
        engine.Present(engine.State with { Decision = engine.State.Decision! with { QuietYieldToken = "" } });
        CompletePoll(stalePressPoll, engine.State, mouseHeld: true);
        Tick();
        check(!frame.Hits.Any(hit => hit.Id is "quiet-priority-arm" or "quiet-priority-stop") && engine.Replies.Count == 0,
            "Revoking eligibility between press and release removes the control and cannot arm or submit a pass");
        RunContinuation(profile, check);
    }

    private static void RunContinuation(string profile, Action<bool, string> check)
    {
        string directory = Path.Combine(profile, "quiet-priority-continuation");
        Directory.CreateDirectory(directory);
        new PlayPreferences { Automatic = false, ReducedMotion = true, Sounds = false }.Save(directory);
        var engine = new InteractionScenarioEngine();
        var example = PriorityAbilitySmoke.Example();
        var instant = new Card { VisualId = "quiet-instant", CombatId = "quiet-instant", Name = "Lightning Bolt", Type = "Instant",
            ManaCost = "{R}", Text = "Lightning Bolt deals 3 damage to any target.", Selectable = true };
        engine.Present(example with { Id = "quiet-priority-continuation", PriorityPlayerId = 0, DecisionOwnerId = 0, ActionClass = "priority",
            Phase = "Upkeep", PhaseKey = "UPKEEP",
            Players = example.Players.Select(player => player.Id != example.ViewerId ? player : player with {
                Zones = player.Zones.Select(zone => zone.Name == "Hand" ? zone with { Count = 1, Cards = [instant] } : zone).ToImmutableArray()
            }).ToImmutableArray(), Decision = example.Decision! with { QuietYieldToken = "continuation-token", PriorityAbilities = [] } });
        var initial = engine.State;
        var replies = new ConcurrentQueue<GameCommand>();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int replying = 0, observationsAtReply = 0;
        engine.ReplyOverride = async reply => {
            reply.Validate();
            if (reply is not PassForNow pass || pass.YieldToken != "continuation-token")
                throw new InvalidOperationException("Quiet intent submitted a different action or permission: " + reply);
            if (Interlocked.Increment(ref replying) != 1) throw new InvalidOperationException("Quiet commands overlapped");
            try {
                replies.Enqueue(reply); observationsAtReply = engine.Observations;
                if (replies.Count == 1) { firstStarted.SetResult(); await firstRelease.Task; }
                else if (replies.Count == 2) { secondStarted.SetResult(); await secondRelease.Task; }
                else throw new InvalidOperationException("Quiet intent submitted an unexpected extra pass");
            } finally { Interlocked.Decrement(ref replying); }
        };
        using var app = new TableApplication(TestTableServices.For(engine), directory, initialState: initial) { Automated = true };
        app.Initialize();
        var view = app.TestPort;
        view.PresentLobby(new("local", "Controlled continuation", null, false, null, true, "", null, [], []), true);
        double seconds = 0;
        var point = new Point(20, 60);
        GameTime Time() => new(TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(1.0 / 60));
        PresentationFrame frame = app.ComposePresentation(Time());
        void Tick(Point? next = null, ButtonState left = ButtonState.Released)
        {
            point = next ?? point; seconds += 1.0 / 60;
            app.Update(Time(), new(point.X, point.Y, 0, left, ButtonState.Released, ButtonState.Released,
                ButtonState.Released, ButtonState.Released), new(), new(1600, 900));
            frame = app.ComposePresentation(Time());
        }
        void Until(Func<bool> ready, string description)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!ready() && view.Error.Length == 0 && DateTime.UtcNow < deadline) { Tick(); Thread.Yield(); }
            check(ready() && view.Error.Length == 0, description + (view.Error.Length > 0 ? ": " + view.Error : ""));
        }
        try {
            var arm = frame.Hits.Single(hit => hit.Id == "quiet-priority-arm").Bounds.Center;
            Tick(arm, ButtonState.Pressed); Tick(arm);
            Until(() => firstStarted.Task.IsCompleted, "Clicking Pass for now submits a real quiet pass despite an available instant");
            for (int index = 0; index < 4; index++) Tick();
            check(view.Busy && !view.Polling && replies.Count == 1 && engine.Observations == observationsAtReply
                && replies.Single() is PassForNow { DecisionId: var submittedDecision } && submittedDecision == initial.Decision!.Id
                && frame.Hits.Any(hit => hit.Id == "quiet-priority-stop"),
                "The first quiet reply owns the serialized connection until it finishes, without duplicate passes or interleaved polls");

            ActivityEntry[] routineDraw = [
                new(1, initial.Turn, "DRAW", "moved", 1, "An opponent's card moved from library to hand.", null, null) { InterruptsQuietYield = false },
                new(2, initial.Turn, "DRAW", "draw", 1, "An opponent drew for the turn.", null, null) { InterruptsQuietYield = false }
            ];
            engine.Present(initial with { Status = "resolving", Decision = null, PriorityPlayerId = 2, DecisionOwnerId = 2, ActionClass = "priority",
                Phase = "Draw", PhaseKey = "DRAW", Activity = routineDraw.ToImmutableArray() });
            firstRelease.SetResult();
            Until(() => !view.Busy && view.Match is { Decision: null, DecisionOwnerId: 2 },
                "The actual application reconciles the first pass into another seat's priority");
            check(replies.Count == 1 && frame.Hits.Any(hit => hit.Id == "quiet-priority-stop")
                && view.Match!.Activity.Select(entry => entry.Kind).SequenceEqual(["moved", "draw"]),
                "Other-seat priority and engine-marked routine draw history retain quiet intent without submitting that seat's decision");

            engine.Present(engine.State with { Status = "playing", Decision = null, PriorityPlayerId = null, DecisionOwnerId = 1, ActionClass = "attack",
                Phase = "Declare attackers", PhaseKey = "COMBAT_DECLARE_ATTACKERS", Combat = new([], [], null) });
            Until(() => view.Match is { Decision: null, DecisionOwnerId: 1, ActionClass: "attack" },
                "The application observes the active opponent selecting attackers");
            check(replies.Count == 1 && frame.Hits.Any(hit => hit.Id == "quiet-priority-stop"),
                "An opponent's attacker selection retains quiet intent without answering the opponent's required decision");

            engine.Present(initial with { Phase = "Main phase", PhaseKey = "MAIN2", Activity = routineDraw.ToImmutableArray() });
            string nextPrompt = engine.State.Decision!.Id;
            Until(() => secondStarted.Task.IsCompleted, "The next empty local priority passes automatically without another click");
            var sent = replies.OfType<PassForNow>().ToArray();
            check(sent.Length == 2 && sent[1].DecisionId == nextPrompt && sent[0].DecisionId != sent[1].DecisionId
                && sent.All(reply => reply.GameId == initial.Id && reply is PassForNow { YieldToken: "continuation-token" }) && view.Busy && !view.Polling,
                "Both scoped local windows use the same explicitly armed engine token despite retained routine draw history");

            engine.Present(engine.State with { Stack = [new("Opponent spell", "An opponent cast a spell.", false, null) { Id = "new-public-spell" }],
                Decision = engine.State.Decision! with { QuietYieldToken = "" },
                Activity = [.. routineDraw, new(3, initial.Turn, "MAIN2", "cast", 1, "An opponent cast a spell.", null, null)] });
            string responsePrompt = engine.State.Decision!.Id;
            secondRelease.SetResult();
            Until(() => !view.Busy && view.Match?.Stack.FirstOrDefault()?.Id == "new-public-spell",
                "A new stack object interrupts quiet passing and is reconciled through the application");
            for (int index = 0; index < 12; index++) Tick();
            check(replies.Count == 2 && view.Match?.Decision?.Id == responsePrompt && engine.State.Decision?.Id == responsePrompt
                && frame.Hits.Any(hit => hit.Id == "confirm") && !frame.Hits.Any(hit => hit.Id == "quiet-priority-stop"),
                "The new local response remains unanswered and manually available after quiet passing stops");
        } finally { firstRelease.TrySetResult(); secondRelease.TrySetResult(); }
    }
}
