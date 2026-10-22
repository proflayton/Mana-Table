using System.Collections.Concurrent;
using Mana.Contracts;
using Mana.Magic;
using Mana.Scenarios;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

internal static class DecisionDockInteractionChecks
{
    internal static void Run(string profile, Action<bool, string> check)
    {
        var initial = DecisionFlowScenario.Create()[1].Snapshot;
        string directory = Path.Combine(profile, "decision-dock-phase-stop");
        Directory.CreateDirectory(directory);
        new PlayPreferences { Automatic = true, ReducedMotion = true, Sounds = false, Stops = ["MAIN1", "COMBAT_BEGIN"] }.Save(directory);
        var engine = new InteractionScenarioEngine(); engine.Present(initial);
        var replies = new ConcurrentQueue<DecisionReply>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.ReplyOverride = async reply => { replies.Enqueue(reply); started.TrySetResult(); await release.Task; };
        using var app = new TableApplication(engine, directory, initialState: engine.State) { Automated = true };
        app.Initialize(); var view = app.TestPort;
        view.PresentLobby(new("local", "Controlled decision policy", null, false, null, true, "", null, [], []), true);
        view.SetAutomatic(true);
        double seconds = 0;
        PresentationFrame frame = app.ComposePresentation(Time());
        GameTime Time() => new(TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(1.0 / 60));
        void Tick(Point? pointer = null, ButtonState left = ButtonState.Released)
        {
            seconds += 1.0 / 60; var point = pointer ?? new Point(20, 60);
            app.Update(Time(), new(point.X, point.Y, 0, left, default, default, default, default), new(), new(1600, 900));
            frame = app.ComposePresentation(Time());
        }
        void Until(Func<bool> ready, string message)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!ready() && view.Error.Length == 0 && DateTime.UtcNow < deadline) { Tick(); Thread.Yield(); }
            check(ready() && view.Error.Length == 0, message);
        }
        try {
            Tick();
            check(view.DecisionDock is { Status: DecisionDockStatus.Priority, PauseReason: DecisionDockPauseReason.PhaseStop, Caption: "Main 1 stop" }
                    && frame.Hits.Any(hit => hit.Id == "resume-phase-stop") && replies.Count == 0,
                "The production dock explains the saved Main 1 stop after the last land, with no automatic gameplay reply");
            var resume = frame.Hits.Single(hit => hit.Id == "resume-phase-stop");
            string game = view.Match!.Id, decision = view.Match.Decision!.Id;
            Tick(resume.Bounds.Center, ButtonState.Pressed); Tick(resume.Bounds.Center);
            Until(() => started.Task.IsCompleted, "Removing the current stop allows a real engine-authorized Auto reply through the application");
            var saved = PlayPreferences.Load(directory);
            check(!saved.Stops.Contains("MAIN1") && saved.Stops.Contains("COMBAT_BEGIN")
                    && replies.Single() is { Action: ReplyAction.AutoPass } pass && pass.GameId == game && pass.DecisionId == decision
                    && view.DecisionDock is { Status: DecisionDockStatus.AutoPassing, PauseReason: DecisionDockPauseReason.None },
                "The explicit stop-removal gesture changes only that preference; Auto uses the original authorized decision rather than confirming a phase or choosing an attack");
            engine.Present(engine.State with { Decision = null, DecisionOwnerId = 1, PriorityPlayerId = null, ActionClass = "selection", Status = "resolving" });
            release.TrySetResult();
            Until(() => !view.Busy && view.Match?.ActionClass == "selection", "The application reconciles the next required public choice");
            check(replies.Count == 1 && view.DecisionDock is { Status: DecisionDockStatus.Waiting, ActorId: 1, ActorRole: DecisionDockActorRole.Choosing }
                    && !frame.Hits.Any(hit => hit.Id == "confirm"),
                "The required choice returns control to its named owner and is never consumed or shown as a disabled local confirmation");
        } finally { release.TrySetResult(); }

        CheckPhaseChangeDuringPress(profile, initial, check);
        CheckInspection(profile, initial, check);
    }

    private static void CheckPhaseChangeDuringPress(string profile, GameSnapshot initial, Action<bool, string> check)
    {
        string directory = Path.Combine(profile, "decision-dock-stop-press-refresh"); Directory.CreateDirectory(directory);
        new PlayPreferences { Automatic = true, ReducedMotion = true, Sounds = false, Stops = ["MAIN1", "MAIN2", "COMBAT_BEGIN"] }.Save(directory);
        var engine = new InteractionScenarioEngine(); engine.Present(initial);
        engine.ReplyOverride = _ => throw new InvalidOperationException("Both authored phases retain their saved stop");
        using var app = new TableApplication(engine, directory, initialState: engine.State) { Automated = true };
        app.Initialize(); var view = app.TestPort;
        view.PresentLobby(new("local", "Controlled stop refresh", null, false, null, true, "", null, [], []), true);
        double seconds = 0;
        PresentationFrame Tick(Point point, ButtonState left = ButtonState.Released)
        {
            seconds += 1.0 / 60;
            var time = new GameTime(TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(1.0 / 60));
            app.Update(time, new(point.X, point.Y, 0, left, default, default, default, default), new(), new(1600, 900));
            return app.ComposePresentation(time);
        }
        var first = Tick(new(20, 60)).Hits.Single(hit => hit.Id == "resume-phase-stop");
        Tick(first.Bounds.Center, ButtonState.Pressed);
        engine.Present(engine.State with { PhaseKey = "MAIN2", Phase = "Main phase, postcombat" });
        view.Accept(engine.State);
        var refreshed = Tick(first.Bounds.Center, ButtonState.Pressed).Hits.Single(hit => hit.Id == first.Id);
        check(view.DecisionDock is { PauseReason: DecisionDockPauseReason.PhaseStop, Caption: "Main 2 stop" }
                && view.Pressed?.Scope == first.Scope && refreshed.Bounds.Contains(first.Bounds.Center),
            "A same-game phase refresh keeps the original Main 1 press held over the newly displayed Main 2 stop");
        Tick(first.Bounds.Center);
        var saved = PlayPreferences.Load(directory);
        check(first.Scope != refreshed.Scope && app.AcceptedHit != first.Id && saved.Stops.SetEquals(["MAIN1", "MAIN2", "COMBAT_BEGIN"])
                && engine.Replies.Count == 0 && view.Error.Length == 0,
            "Releasing a Main 1 stop press after a Main 2 refresh cannot remove either saved stop or submit a gameplay action");
    }

    private static void CheckInspection(string profile, GameSnapshot initial, Action<bool, string> check)
    {
        string directory = Path.Combine(profile, "decision-dock-inspection"); Directory.CreateDirectory(directory);
        new PlayPreferences { Automatic = false, ReducedMotion = true, Sounds = false }.Save(directory);
        var engine = new InteractionScenarioEngine(); engine.Present(initial);
        engine.ReplyOverride = _ => throw new InvalidOperationException("Inspection must suspend automatic game commands");
        using var app = new TableApplication(engine, directory, initialState: engine.State) { Automated = true };
        app.Initialize(); var view = app.TestPort;
        view.PresentLobby(new("local", "Controlled inspection", null, false, null, true, "", null, [], []), true);
        double seconds = 0;
        PresentationFrame Tick(Point point, ButtonState left = ButtonState.Released)
        {
            seconds += 1.0 / 60;
            var time = new GameTime(TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(1.0 / 60));
            app.Update(time, new(point.X, point.Y, 0, left, default, default, default, default), new(), new(1600, 900));
            return app.ComposePresentation(time);
        }
        var frame = Tick(new(20, 60));
        var land = frame.Cards.Single(card => card.Id == DecisionFlowScenario.LandId);
        var point = land.Hit.Bounds.Center;
        Tick(point, ButtonState.Pressed); Tick(point);
        view.SetAutomatic(true); Tick(new(20, 60));
        check(view.Automatic && view.DecisionDock is { Status: DecisionDockStatus.Priority, PauseReason: DecisionDockPauseReason.Inspection, Caption: "Paused while reading" }
                && engine.Replies.Count == 0 && view.Error.Length == 0,
            "Reading an actual card pauses both the production Auto gate and its visible status instead of advertising a pass that is not happening");
    }
}
