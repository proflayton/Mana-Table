using System.Collections.Concurrent;
using Mana.Contracts;
using Mana.Magic;
using Mana.Scenarios;
using Mana.Renderer;
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
        var replies = new ConcurrentQueue<GameCommand>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.ReplyOverride = async reply => { replies.Enqueue(reply); started.TrySetResult(); await release.Task; };
        using var app = new TableApplication(TestTableServices.For(engine), directory, initialState: engine.State) { Automated = true };
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
                    && replies.Single() is AutoPassPriority pass && pass.GameId == game && pass.DecisionId == decision
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
        CheckPortraitControls(profile, initial, check);
    }

    private static void CheckPortraitControls(string profile, GameSnapshot initial, Action<bool, string> check)
    {
        string directory = Path.Combine(profile, "decision-dock-portrait-controls"); Directory.CreateDirectory(directory);
        new PlayPreferences { Automatic = false, ReducedMotion = true, Sounds = false, Stops = ["UPKEEP"] }.Save(directory);
        var engine = new InteractionScenarioEngine(); engine.Present(initial);
        engine.ReplyOverride = _ => throw new InvalidOperationException("Timing controls must not answer the required library search");
        using var app = new TableApplication(TestTableServices.For(engine), directory, initialState: engine.State) { Automated = true };
        app.Initialize(); var view = app.TestPort;
        view.PresentLobby(new("local", "Controlled portrait timing", null, false, null, true, "", null, [], []), true);
        double seconds = 0;
        PresentationFrame frame = null!;
        void Tick(Point? pointer = null, ButtonState left = ButtonState.Released)
        {
            seconds += 1.0 / 60;
            var time = new GameTime(TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(1.0 / 60));
            var point = pointer ?? new Point(20, 60);
            app.Update(time, new(point.X, point.Y, 0, left, default, default, default, default), new(), new(1600, 900));
            frame = app.ComposePresentation(time);
        }
        void Click(string id)
        {
            var hit = frame.Hits.Single(candidate => candidate.Id == id);
            check(view.HitAt(hit.Bounds.Center)?.Id == id && view.CurrentHit(hit),
                "The current portrait journey uses the exposed production hit for " + id);
            Tick(hit.Bounds.Center, ButtonState.Pressed); Tick(hit.Bounds.Center);
            check(app.AcceptedHit == id, "The application accepts the actual pointer release for " + id);
        }
        bool NearOwnPortrait(string id)
        {
            var hero = frame.Scene!.Seat(view.Match!.ViewerId).Geometry.Hero;
            var center = frame.Hits.Single(hit => hit.Id == id).Bounds.Center.ToVector2();
            return Vector2.Distance(center, hero) < 400;
        }

        Tick();
        var own = view.TurnProgress!;
        check(own.IsOwnTurn && own.Markers.Single(marker => marker.Current).Key == "MAIN1"
                && own.Markers.Single(marker => marker.Key == "UPKEEP") is { Stop: true, Current: false }
                && frame.Hits.Where(hit => hit.Id.StartsWith("phase-stop:", StringComparison.Ordinal)).Select(hit => hit.Id)
                    .Order().SequenceEqual(TurnGuide.Stops.Select(stop => "phase-stop:" + stop.Key).Order())
                && TurnGuide.Stops.All(stop => NearOwnPortrait("phase-stop:" + stop.Key)),
            "One local portrait marker set distinguishes the actual own main phase from a recurring saved upkeep stop");
        var end = frame.Hits.Single(hit => hit.Id == "phase-stop:END_OF_TURN");
        Tick(end.Bounds.Center);
        check(frame.Drawing.Commands.OfType<DrawCommand.Text>().Any(text => text.Value == "Stop on my end step"),
            "Hovering the actual end marker explains its own-turn scope with Stop on my end step");
        string decision = view.Match!.Decision!.Id;
        Click(end.Id);
        check(PlayPreferences.Load(directory).Stops.SetEquals(["UPKEEP", "END_OF_TURN"])
                && view.Match?.Decision?.Id == decision && engine.Replies.Count == 0,
            "An end-marker click adds only the recurring own end-step preference without answering the current priority");

        int opponent = initial.Players.First(player => player.Id != initial.ViewerId).Id;
        engine.Present(engine.State with { ActivePlayerId = opponent, PhaseKey = "END_OF_TURN", Phase = "End step" });
        view.Accept(engine.State); Tick();
        check(view.TurnProgress is { IsOwnTurn: false, PhaseLabel: "End step" } other
                && other.ActivePlayerId == opponent && other.Markers.All(marker => !marker.Current)
                && other.Markers.Single(marker => marker.Key == "END_OF_TURN").Stop
                && NearOwnPortrait(end.Id),
            "The active opponent's end step never lights the local current-phase markers or moves their saved own-turn control");
        Tick(frame.Hits.Single(hit => hit.Id == end.Id).Bounds.Center);
        check(frame.Drawing.Commands.OfType<DrawCommand.Text>().Any(text => text.Value == "Remove my end-step stop"),
            "A saved end marker describes removing the same own-turn stop even during an opponent's phase");
        Click(end.Id);
        check(PlayPreferences.Load(directory).Stops.SetEquals(["UPKEEP"]) && engine.Replies.Count == 0,
            "Removing the end marker preserves the unrelated upkeep stop and sends no game command");

        var privateCard = new Card { Name = "Disclosed search option", Type = "Creature", Text = "A scoped private library choice." };
        engine.Present(engine.State with { ActionClass = "selection", DecisionOwnerId = initial.ViewerId, PriorityPlayerId = null,
            Decision = new() { Kind = "choice", Intent = DecisionIntent.Selection, Min = 1, Max = 1,
                Message = "Search your library for a creature.", LibraryCards = [new(7, privateCard.Name, privateCard)],
                Choices = [new(7, privateCard.Name, privateCard)] } });
        view.Accept(engine.State); Tick();
        var search = view.Match!.Decision!;
        Click("close-overlay"); // The actual View battlefield gesture keeps the choice pending.
        check(NearOwnPortrait("auto") && NearOwnPortrait("hold"),
            "Auto and Hold remain attached to the local portrait while a required search is pending");
        Click("auto");
        check(view.Automatic && !view.Held, "The portrait Auto icon changes only the local automation mode");
        Click("hold");
        check(view.Automatic && view.Held, "The portrait Hold icon pauses that mode for the current turn");
        Click("hold"); Click("auto");
        check(!view.Automatic && !view.Held && view.Match?.Decision is { } currentSearch && currentSearch.Id == search.Id
                && currentSearch.LibraryCards.SequenceEqual(search.LibraryCards) && view.Selections.Count == 0
                && engine.Replies.Count == 0 && view.Error.Length == 0,
            "Actual Auto/Hold clicks and reversals never confirm, choose or replace the unanswered required search");
    }

    private static void CheckPhaseChangeDuringPress(string profile, GameSnapshot initial, Action<bool, string> check)
    {
        string directory = Path.Combine(profile, "decision-dock-stop-press-refresh"); Directory.CreateDirectory(directory);
        new PlayPreferences { Automatic = true, ReducedMotion = true, Sounds = false, Stops = ["MAIN1", "MAIN2", "COMBAT_BEGIN"] }.Save(directory);
        var engine = new InteractionScenarioEngine(); engine.Present(initial);
        engine.ReplyOverride = _ => throw new InvalidOperationException("Both authored phases retain their saved stop");
        using var app = new TableApplication(TestTableServices.For(engine), directory, initialState: engine.State) { Automated = true };
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
        using var app = new TableApplication(TestTableServices.For(engine), directory, initialState: engine.State) { Automated = true };
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
