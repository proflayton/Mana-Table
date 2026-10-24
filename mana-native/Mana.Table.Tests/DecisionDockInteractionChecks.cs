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
        PresentationFrame frame = null!;
        Tick();
        GameTime Time() => new(TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(1.0 / 60));
        void Tick(Point? pointer = null, ButtonState left = ButtonState.Released)
        {
            seconds += 1.0 / 60; var point = pointer ?? new Point(20, 60);
            app.Update(Time(), new(point.X, point.Y, 0, left, default, default, default, default), new(), new(1600, 900));
            frame = app.ReadPresentation();
        }
        void Until(Func<bool> ready, string message)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!ready() && view.Error.Length == 0 && DateTime.UtcNow < deadline) { Tick(); Thread.Yield(); }
            check(ready() && view.Error.Length == 0, message);
        }
        try {
            Tick();
            check(view.DecisionDock is { Status: DecisionDockStatus.Priority, PauseReason: DecisionDockPauseReason.PhaseStop, Caption: "Main 1 stop", Surface: DecisionDockSurface.Portrait }
                    && frame.Hits.Any(hit => hit.Id == "resume-phase-stop") && replies.Count == 0,
                "The production dock explains the saved Main 1 stop after the last land, with no automatic gameplay reply");
            var resume = frame.Hits.Single(hit => hit.Id == "resume-phase-stop");
            string game = view.Match!.Id, decision = view.Match.Decision!.Id;
            Tick(resume.Bounds.Center, ButtonState.Pressed); Tick(resume.Bounds.Center);
            Until(() => started.Task.IsCompleted, "Removing the current stop allows a real engine-authorized Auto reply through the application");
            var saved = PlayPreferences.Load(directory);
            check(!saved.Stops.Contains("MAIN1") && saved.Stops.Contains("COMBAT_BEGIN")
                    && replies.Single() is AutoPassPriority pass && pass.GameId == game && pass.DecisionId == decision
                    && view.DecisionDock is { Status: DecisionDockStatus.AutoPassing, PauseReason: DecisionDockPauseReason.None, Surface: DecisionDockSurface.None },
                "The explicit stop-removal gesture changes only that preference; Auto uses the original authorized decision rather than confirming a phase or choosing an attack");
            var autoBounds = frame.Hits.Single(hit => hit.Id == "auto").Bounds;
            var holdBounds = frame.Hits.Single(hit => hit.Id == "hold").Bounds;
            for (int index = 0; index < 6; index++) {
                Tick();
                check(view.Busy && view.DecisionDock?.Surface == DecisionDockSurface.None && NoTransportLabels(frame)
                    && !frame.Hits.Any(hit => hit.Id is "confirm" or "cancel")
                    && frame.Hits.Single(hit => hit.Id == "auto").Bounds == autoBounds
                    && frame.Hits.Single(hit => hit.Id == "hold").Bounds == holdBounds && replies.Count == 1,
                    "A delayed automatic reply retains quiet presentation and fixed timing controls without exposing the submitted priority again");
            }
            engine.Present(engine.State with { Decision = null, DecisionOwnerId = 1, PriorityPlayerId = 1, ActionClass = "priority" });
            release.TrySetResult();
            Until(() => !view.Busy && view.Match is { Decision: null, PriorityPlayerId: 1 }, "The automatic batch reconciles the other player's actual priority");
            check(view.DecisionDock is { Status: DecisionDockStatus.Waiting, ActorId: 1, Surface: DecisionDockSurface.None }
                    && NoTransportLabels(frame) && replies.Count == 1
                    && frame.Hits.Single(hit => hit.Id == "auto").Bounds == autoBounds
                    && frame.Hits.Single(hit => hit.Id == "hold").Bounds == holdBounds,
                "An ordinary responder handoff changes the public actor without opening another dock or moving the local timing controls");

            var observation = new TaskCompletionSource<GameSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.ObserveOverride = () => observation.Task;
            var pollingLobby = new Lobby("local", "Controlled observation", null, false, null, true, "", null, [], []);
            view.PresentLobby(pollingLobby, false); Tick(); view.PresentLobby(pollingLobby, true);
            check(view.Polling && view.DecisionDock?.Surface == DecisionDockSurface.None && NoTransportLabels(frame),
                "A fresh observation of the handoff introduces no transport attention state");
            engine.Present(engine.State);
            observation.SetResult(engine.State);
            Until(() => !view.Polling, "The fresh handoff observation finishes through the shared client session");
            check(view.DecisionDock is { ActorId: 1, Surface: DecisionDockSurface.None } && NoTransportLabels(frame) && replies.Count == 1,
                "A revision-only observation preserves the quiet handoff and cannot resubmit its consumed local priority");
            engine.ObserveOverride = null;
            engine.Present(engine.State with { Decision = null, DecisionOwnerId = 1, PriorityPlayerId = null, ActionClass = "selection", Status = "resolving" });
            view.Accept(engine.State); Tick();
            Until(() => !view.Busy && view.Match?.ActionClass == "selection", "The application reconciles the next required public choice");
            check(replies.Count == 1 && view.DecisionDock is { Status: DecisionDockStatus.Waiting, ActorId: 1, ActorRole: DecisionDockActorRole.Choosing, Surface: DecisionDockSurface.Workspace }
                    && !frame.Hits.Any(hit => hit.Id == "confirm"),
                "The required choice returns control to its named owner and is never consumed or shown as a disabled local confirmation");
        } finally { release.TrySetResult(); }

        CheckPhaseChangeDuringPress(profile, initial, check);
        CheckInspection(profile, initial, check);
        CheckPortraitControls(profile, initial, check);
        CheckInterruptedAdvance(profile, initial, check);
    }

    internal static bool NoTransportLabels(PresentationFrame frame) => !frame.Drawing.Commands.Any(command => command switch {
        DrawCommand.Text text => IsTransportLabel(text.Value), DrawCommand.CenterText text => IsTransportLabel(text.Value), _ => false
    });
    private static bool IsTransportLabel(string? value) => value is "Passing" or "Auto passing" or "Passing for now" or "Passing paused" or "Action submitted";

    private static void CheckInterruptedAdvance(string profile, GameSnapshot initial, Action<bool, string> check)
    {
        foreach (string interruption in new[] { "hold", "rejected", "lost-acknowledgement" }) {
            string directory = Path.Combine(profile, "decision-dock-advance-" + interruption); Directory.CreateDirectory(directory);
            new PlayPreferences { Automatic = true, ReducedMotion = true, Sounds = false, Stops = [] }.Save(directory);
            var engine = new InteractionScenarioEngine(); engine.Present(initial);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var recovery = new TaskCompletionSource<GameSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
            int replies = 0;
            engine.ReplyOverride = async command => {
                if (command is not AutoPassPriority || Interlocked.Increment(ref replies) != 1)
                    throw new InvalidOperationException("The interrupted Auto window must submit exactly one scoped pass");
                started.TrySetResult(); await release.Task;
                if (interruption == "rejected") throw new EngineCommandRejectedException("Controlled automatic rejection");
                if (interruption == "lost-acknowledgement") {
                    // The engine applied the pass, but its acknowledgement was
                    // lost. Recovery must observe the actual handoff, not retry.
                    engine.Present(engine.State with { Decision = null, DecisionOwnerId = 1, PriorityPlayerId = 1, ActionClass = "priority" });
                    throw new IOException("Controlled lost acknowledgement");
                }
            };
            using var app = new TableApplication(TestTableServices.For(engine), directory, initialState: engine.State) { Automated = true };
            app.Initialize(); var view = app.TestPort;
            view.PresentLobby(new("local", "Controlled interruption", null, false, null, true, "", null, [], []), true);
            view.SetAutomatic(true);
            double seconds = 0; PresentationFrame frame = null!;
            void Tick(Point? pointer = null, ButtonState left = ButtonState.Released) {
                seconds += 1.0 / 60; var point = pointer ?? new Point(20, 60);
                app.Update(new(TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(1.0 / 60)),
                    new(point.X, point.Y, 0, left, default, default, default, default), new(), new(1600, 900));
                frame = app.ReadPresentation();
            }
            void Until(Func<bool> ready, string message) {
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (!ready() && DateTime.UtcNow < deadline) { Tick(); Thread.Yield(); }
                check(ready(), message);
            }
            string Actual() => $" Actual: dock={view.DecisionDock?.Status}/{view.DecisionDock?.Surface}/{view.DecisionDock?.PauseReason};"
                + $" busy={view.Busy}; polling={view.Polling}; recovery={view.NeedsRecovery}; replies={replies}; error='{view.Error}';"
                + " hits=" + string.Join(',', frame.Hits.Select(hit => hit.Id));
            try {
                Until(() => started.Task.IsCompleted, "The " + interruption + " journey holds a real automatic pass in flight");
                check(view.Busy && view.DecisionDock?.Surface == DecisionDockSurface.None && NoTransportLabels(frame),
                    "An in-flight pass has no transient attention surface before " + interruption);
                if (interruption == "hold") {
                    var hold = frame.Hits.Single(hit => hit.Id == "hold");
                    Tick(hold.Bounds.Center, ButtonState.Pressed); Tick(hold.Bounds.Center);
                    check(view.Held && view.Busy && !frame.Hits.Any(hit => hit.Id is "confirm" or "cancel") && NoTransportLabels(frame),
                        "Hold takes effect during the delayed pass without exposing a stale priority action");
                    engine.Present(engine.State with { Decision = initial.Decision! with { CanAutoPass = true } });
                }
                if (interruption == "lost-acknowledgement") engine.ObserveOverride = () => recovery.Task;
                release.TrySetResult();
                Until(() => !view.Busy, "The interrupted pass finishes its one serialized command");
                for (int index = 0; index < 8; index++) Tick();
                check(replies == 1 && NoTransportLabels(frame), "The " + interruption + " outcome never replays the pass or announces transport as game state");
                if (interruption == "hold")
                    check(view.DecisionDock is { Status: DecisionDockStatus.Priority, Surface: DecisionDockSurface.Portrait, PauseReason: DecisionDockPauseReason.Held }
                            && frame.Hits.Any(hit => hit.Id == "confirm") && view.Match?.Decision?.Id == engine.State.Decision?.Id && view.Error.Length == 0,
                        "After reconciliation Hold presents the actual new local priority and its current manual action." + Actual());
                else if (interruption == "rejected")
                    check(view.Error == "Controlled automatic rejection" && view.DecisionDock?.PauseReason == DecisionDockPauseReason.CommandFailure
                            && view.DecisionDock.Status != DecisionDockStatus.AutoPassing,
                        "An explicit rejection replaces automatic feedback with its retained action error without inventing a connection loss." + Actual());
                else {
                    check(view.Error == "Controlled lost acknowledgement" && view.NeedsRecovery
                            && view.DecisionDock is { Status: DecisionDockStatus.Recovery, Surface: DecisionDockSurface.Workspace }
                            && !frame.Hits.Any(hit => hit.Id is "confirm" or "cancel"),
                        "An uncertain acknowledgement requires recovery until fresh observation and exposes no stale game action." + Actual());
                    recovery.SetResult(engine.State);
                    Until(() => !view.NeedsRecovery && !view.Polling, "A fresh authoritative observation reconciles the pass whose acknowledgement was lost");
                    check(replies == 1 && view.Match?.PriorityPlayerId == 1 && view.DecisionDock is { Surface: DecisionDockSurface.None, ActorId: 1 }
                            && NoTransportLabels(frame),
                        "Recovery publishes the actual opponent handoff without replaying the uncertain pass." + Actual());
                }
            } finally { release.TrySetResult(); recovery.TrySetResult(engine.State); }
        }
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
            frame = app.ReadPresentation();
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
            return app.ReadPresentation();
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
            return app.ReadPresentation();
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
