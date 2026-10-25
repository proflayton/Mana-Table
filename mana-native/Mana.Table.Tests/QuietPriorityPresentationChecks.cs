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
        PresentationFrame frame = null!;
        Tick();
        GameTime Time() => new(TimeSpan.FromSeconds(seconds), TimeSpan.FromMilliseconds(1));
        PresentationFrame Tick(Point? next = null, ButtonState left = ButtonState.Released)
        {
            point = next ?? point; seconds += .001;
            app.Update(Time(), new(point.X, point.Y, 0, left, ButtonState.Released, ButtonState.Released,
                ButtonState.Released, ButtonState.Released), new(), new(1600, 900));
            return frame = app.ReadPresentation();
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
        check(view.DecisionDock?.Surface == DecisionDockSurface.Portrait && DecisionDockInteractionChecks.NoTransportLabels(frame)
                && frame.Hits.Single(hit => hit.Id == "quiet-priority-stop").Bounds == original.Bounds,
            "Arming during a delayed observation keeps one portrait surface and the same cancelable input target");
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
        var thirdStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thirdRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fourthStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fourthRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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
                else if (replies.Count == 3) { thirdStarted.SetResult(); await thirdRelease.Task; }
                else if (replies.Count == 4) { fourthStarted.SetResult(); await fourthRelease.Task; }
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
        PresentationFrame frame = null!;
        Tick();
        void Tick(Point? next = null, ButtonState left = ButtonState.Released)
        {
            point = next ?? point; seconds += 1.0 / 60;
            app.Update(Time(), new(point.X, point.Y, 0, left, ButtonState.Released, ButtonState.Released,
                ButtonState.Released, ButtonState.Released), new(), new(1600, 900));
            frame = app.ReadPresentation();
        }
        void Until(Func<bool> ready, string description)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!ready() && view.Error.Length == 0 && DateTime.UtcNow < deadline) { Tick(); Thread.Yield(); }
            check(ready() && view.Error.Length == 0, description + (view.Error.Length > 0 ? ": " + view.Error : ""));
        }
        bool HasText(string value) => frame.Drawing.Commands.Any(command => command switch {
            DrawCommand.Text text => text.Value == value, DrawCommand.CenterText text => text.Value == value, _ => false
        });
        void Click(string id) {
            var hit = frame.Hits.Single(candidate => candidate.Id == id);
            Tick(hit.Bounds.Center, ButtonState.Pressed); Tick(hit.Bounds.Center);
        }
        try {
            var arm = frame.Hits.Single(hit => hit.Id == "quiet-priority-arm").Bounds.Center;
            Tick(arm, ButtonState.Pressed); Tick(arm);
            Until(() => firstStarted.Task.IsCompleted, "Clicking Pass for now submits a real quiet pass despite an available instant");
            var takeControl = frame.Hits.Single(hit => hit.Id == "quiet-priority-stop");
            var autoBounds = frame.Hits.Single(hit => hit.Id == "auto").Bounds;
            var holdBounds = frame.Hits.Single(hit => hit.Id == "hold").Bounds;
            void CheckQuietSurface(string description) => check(view.DecisionDock?.Surface == DecisionDockSurface.Portrait
                    && HasText("Pass for now") && HasText("Take control") && DecisionDockInteractionChecks.NoTransportLabels(frame)
                    && frame.Hits.Single(hit => hit.Id == "quiet-priority-stop").Bounds == takeControl.Bounds
                    && frame.Hits.Single(hit => hit.Id == "auto").Bounds == autoBounds
                    && frame.Hits.Single(hit => hit.Id == "hold").Bounds == holdBounds, description);
            for (int index = 0; index < 4; index++) {
                Tick(); CheckQuietSurface("A delayed quiet reply retains the same Pass for now surface and cancel control");
            }
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
            CheckQuietSurface("An opponent priority handoff preserves the local Pass for now surface while reporting the actual responder");

            var freshPoll = new TaskCompletionSource<GameSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.ObserveOverride = () => freshPoll.Task;
            var pollingLobby = new Lobby("local", "Controlled quiet observation", null, false, null, true, "", null, [], []);
            view.PresentLobby(pollingLobby, false); Tick(); view.PresentLobby(pollingLobby, true);
            check(view.Polling, "The armed quiet continuation holds an actual fresh observation after the opponent handoff");
            CheckQuietSurface("The armed quiet surface stays in place while the fresh handoff observation is pending");
            engine.Present(engine.State); freshPoll.SetResult(engine.State);
            Until(() => !view.Polling, "The armed quiet observation reconciles its current authoritative snapshot");
            CheckQuietSurface("A fresh revision with the same quiet permission preserves heading and control geometry");
            engine.ObserveOverride = null;
            view.PresentLobby(pollingLobby, false);

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
            CheckQuietSurface("Returning to another local quiet window never swaps the stable intent for a Passing announcement");

            engine.Present(engine.State with { Stack = [new("Opponent spell", "An opponent cast a spell.", false, null) { Id = "new-public-spell" }],
                Decision = engine.State.Decision! with { QuietYieldToken = "" },
                Activity = [.. routineDraw, new(3, initial.Turn, "MAIN2", "cast", 1, "An opponent cast a spell.", null, null)] });
            string responsePrompt = engine.State.Decision!.Id;
            secondRelease.SetResult();
            Until(() => !view.Busy && view.Match?.Stack.FirstOrDefault()?.Id == "new-public-spell",
                "A new stack object interrupts quiet passing and is reconciled through the application");
            for (int index = 0; index < 12; index++) Tick();
            check(replies.Count == 2 && view.Match?.Decision?.Id == responsePrompt && engine.State.Decision?.Id == responsePrompt
                && view.DecisionDock?.Surface == DecisionDockSurface.Portrait
                && frame.Hits.Any(hit => hit.Id == "confirm") && !frame.Hits.Any(hit => hit.Id == "quiet-priority-stop"),
                "The new local response remains unanswered and manually available after quiet passing stops");

            engine.Present(initial); view.Accept(engine.State); Tick();
            Click("quiet-priority-arm");
            Until(() => thirdStarted.Task.IsCompleted, "A newly armed quiet intent reaches its next real local pass");
            var staleCancel = frame.Hits.Single(hit => hit.Id == "quiet-priority-stop");
            var source = engine.State.Viewer!.Zone("Hand").Cards.Single();
            engine.Present(engine.State with { PriorityPlayerId = null, DecisionOwnerId = initial.ViewerId, ActionClass = "selection",
                Decision = new() { Task = DecisionTask.TargetSelection, SourceCardId = source.VisualId, SourceCard = source, SourceControllerId = initial.ViewerId,
                    Min = 1, Max = 1, Ok = "Confirm target", OkEnabled = false,
                    Message = "Choose one legal creature for the required effect.", QuietYieldToken = "continuation-token" } });
            string targetPrompt = engine.State.Decision!.Id;
            thirdRelease.SetResult();
            Until(() => !view.Busy && view.Match?.Decision?.Id == targetPrompt, "The quiet worker publishes the required target immediately instead of passing it");
            check(view.DecisionDock is { Status: DecisionDockStatus.Target, Surface: DecisionDockSurface.Workspace, DecisionId: var shownTarget,
                        SourceCardId: var shownSource, SourceCard: { Name: "Lightning Bolt" } }
                    && shownTarget == targetPrompt && shownSource == source.VisualId
                    && !frame.Hits.Any(hit => hit.Id is "quiet-priority-stop" or "quiet-priority-arm" or "confirm")
                    && !view.Activate(staleCancel) && replies.Count == 3 && DecisionDockInteractionChecks.NoTransportLabels(frame),
                "A mandatory target replaces quiet attention with its exact source and decision, revokes the old cancel hit, and stays unanswered");

            engine.Present(initial); view.Accept(engine.State); Tick();
            Click("quiet-priority-arm");
            Until(() => fourthStarted.Task.IsCompleted, "The Take control journey holds one final scoped quiet pass in flight");
            Click("quiet-priority-stop");
            check(view.Busy && !frame.Hits.Any(hit => hit.Id is "quiet-priority-stop" or "confirm"),
                "Take control cancels the intent during the delayed reply without exposing the already submitted priority");
            engine.Present(initial); string controlledPrompt = engine.State.Decision!.Id;
            fourthRelease.SetResult();
            Until(() => !view.Busy && view.Match?.Decision?.Id == controlledPrompt, "Take control reconciles the command already sent before restoring input");
            for (int index = 0; index < 8; index++) Tick();
            check(replies.Count == 4 && view.DecisionDock?.Surface == DecisionDockSurface.Portrait
                    && frame.Hits.Any(hit => hit.Id == "quiet-priority-arm") && !frame.Hits.Any(hit => hit.Id == "quiet-priority-stop")
                    && DecisionDockInteractionChecks.NoTransportLabels(frame),
                "Take control leaves the next eligible quiet window manual and never silently rearms or resubmits it");
        } finally {
            firstRelease.TrySetResult(); secondRelease.TrySetResult(); thirdRelease.TrySetResult(); fourthRelease.TrySetResult();
        }
    }
}
