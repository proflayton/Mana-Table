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
        using var app = new TableApplication(engine, directory, initialState: engine.State) { Automated = true };
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
    }
}
