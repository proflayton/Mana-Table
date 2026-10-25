using System.Collections.Immutable;
using Mana.Contracts;
using Mana.Renderer;
using Mana.Table.Interaction;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace Mana.Table;

// The shipping application prepares one frame in Update. Reading that frame
// must not perform input, drain a session completion or recreate revoked views.
internal static class PresentationReadChecks
{
    internal static void Run(string profile, Action<bool, string> check)
    {
        string directory = Path.Combine(profile, "presentation-read");
        Directory.CreateDirectory(directory);
        new PlayPreferences { Automatic = false, ReducedMotion = true, Sounds = false }.Save(directory);
        var engine = new InteractionScenarioEngine();
        engine.Present(PresentationSmoke.Example() with {
            Id = "presentation-read", Combat = null, Stack = [],
            Decision = new Decision("", new DecisionContext(), new OptionsPayload(new ChoiceSelection(Enumerable.Range(0, 8).Select(index => new Choice(index, "Option " + index, null)).ToImmutableArray(), new SelectionRange(0, 3), [])))
        });
        using var app = new TableApplication(TestTableServices.For(engine), directory, initialState: engine.State) { Automated = true };
        app.Initialize();
        var view = app.TestPort;
        // Keep the test synchronous; explicit session completions below still
        // travel through the real Update/Pump boundary.
        view.PresentLobby(new("local", "Read boundary", null, false, null, true, "", null, [], []), pausePolling: true);
        double seconds = 0;
        PresentationFrame Tick()
        {
            seconds += .016;
            app.Update(new(TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(.016)),
                new MouseState(20, 60, 0, default, default, default, default, default), new(), new(1600, 900));
            return app.ReadPresentation();
        }
        bool ReadUnavailable()
        {
            try { app.ReadPresentation(); return false; }
            catch (InvalidOperationException) { return true; }
        }
        check(view.Frame == null && ReadUnavailable(),
            "An initialized application cannot manufacture a presentation before its first Update");

        var initial = Tick();
        check(ReferenceEquals(initial, view.Frame) && initial.Drawing.Commands.Count > 0 && initial.Hits.Count > 0,
            "Update publishes the production drawing and interaction frame before any presentation read");
        check(view.Activate(initial.Hits.Single(hit => hit.Id == "choicesnext")),
            "The published choice-page intent navigates through the production dispatcher");
        var paged = Tick();
        var choice = paged.Hits.Single(hit => hit.Id == "choice:7");
        check(view.Activate(choice) && view.Selections.SequenceEqual([7]),
            "A current visible choice creates a nonempty decision draft without submitting it");
        var selected = Tick();
        choice = selected.Hits.Single(hit => hit.Id == choice.Id);
        check(!view.Activate(choice with { Intent = new MatchIntent.SelectChoice(6) }) && view.Selections.SequenceEqual([7]),
            "A fabricated hit cannot borrow a published control's ID and scope to dispatch a different valid intent");

        check(view.Activate(selected.Hits.Single(hit => hit.Id == "close-overlay")),
            "The current modal's battlefield control closes its gallery through the production dispatcher");
        var frame = Tick();
        check(view.CurrentHit(choice) && !frame.Hits.Any(hit => hit.Id == choice.Id)
            && !view.Activate(choice) && view.Selections.SequenceEqual([7]),
            "A removed modal control is rejected even while its game and decision scope remain current");
        check(frame.Cards.Count > 0 && frame.Scene != null && engine.Observations == 0 && engine.Replies.Count == 0,
            "The read-boundary fixture retains real battlefield and hand poses after dismissing its choice gallery");

        var snapshot = view.Match!;
        var navigation = (view.Overlay, view.ZoneName, view.ZonePage, view.TableGuideOpen, view.SetupModal, view.DeckEditor,
            view.StudyFocusOwner, view.BoardView, view.FollowTurnEnabled);
        var poses = frame.Cards.Select(card => (card.Id, card.Display)).ToArray();
        var displayed = frame.Cards.Select(card => (card.Id, Pose: view.DisplayedPose(card.Id))).ToArray();
        var selections = view.Selections.ToArray();
        int observations = engine.Observations, replies = engine.Replies.Count, completions = 0;
        view.Post(() => completions++);
        bool sameFrame = true;
        for (int read = 0; read < 8; read++) sameFrame &= ReferenceEquals(frame, app.ReadPresentation());
        check(sameFrame && ReferenceEquals(view.Frame, frame) && ReferenceEquals(view.Match, snapshot)
            && ReferenceEquals(view.TableScene, frame.Scene) && completions == 0
            && engine.Observations == observations && engine.Replies.Count == replies && !view.Busy && !view.Polling
            && navigation == (view.Overlay, view.ZoneName, view.ZonePage, view.TableGuideOpen, view.SetupModal, view.DeckEditor,
                view.StudyFocusOwner, view.BoardView, view.FollowTurnEnabled)
            && frame.Cards.Select(card => (card.Id, card.Display)).SequenceEqual(poses)
            && displayed.All(item => view.DisplayedPose(item.Id) == item.Pose)
            && view.Selections.SequenceEqual(selections),
            "Repeated presentation reads return the identical frame without advancing navigation, card poses, decision drafts or queued engine work");

        var next = Tick();
        check(!ReferenceEquals(frame, next) && next.Stamp != frame.Stamp && next.Stamp.Time > frame.Stamp.Time
            && completions == 1 && view.Selections.SequenceEqual(selections)
            && engine.Observations == observations && engine.Replies.Count == replies,
            "The next Update alone drains queued session work and publishes a distinct frame and time stamp");

        var oldHit = next.Hits.Single(hit => hit.Id == "settings");
        var switched = snapshot with { ViewerId = 1, Revision = snapshot.Revision + 1,
            Players = snapshot.Players.Select(player => player with { Zones = player.Zones.Select(zone => zone.Name == "Hand"
                ? zone with { Cards = [] } : zone).ToImmutableArray() }).ToImmutableArray() };
        view.Accept(switched);
        check(view.Match is { ViewerId: 1 } accepted && accepted.Id == snapshot.Id && view.Frame == null && view.Hits.Count == 0
            && view.TableScene == null && view.Selections.Count == 0 && displayed.All(item => view.DisplayedPose(item.Id) == null)
            && !view.Activate(oldHit) && ReadUnavailable() && view.Frame == null && view.Hits.Count == 0,
            "A same-game viewer change immediately revokes cached frames, hits, poses and drafts; reading cannot resurrect the prior viewer");
        var current = Tick();
        check(!ReferenceEquals(current, next) && current.Stamp.GameId == next.Stamp.GameId
            && current.Stamp.Scope != next.Stamp.Scope && current.Stamp.Revision == switched.Revision
            && engine.Observations == observations && engine.Replies.Count == replies,
            "Only the following Update publishes the replacement viewer's frame without issuing an engine command");
        CheckQuietPriorityLifetime(profile, check);
    }

    private static void CheckQuietPriorityLifetime(string profile, Action<bool, string> check)
    {
        string directory = Path.Combine(profile, "presentation-quiet-lifetime");
        Directory.CreateDirectory(directory);
        new PlayPreferences { Automatic = false, ReducedMotion = true, Sounds = false }.Save(directory);
        var engine = new InteractionScenarioEngine();
        var example = PriorityAbilitySmoke.Example();
        engine.Present(example with { Id = "presentation-quiet-lifetime", PriorityPlayerId = example.ViewerId,
            DecisionOwnerId = example.ViewerId, ActionClass = "priority",
            Decision = example.Decision! with { Payload = ((PriorityPayload)(example.Decision!).Payload) with { QuietYieldToken = "original-quiet-token" } } });
        using var app = new TableApplication(TestTableServices.For(engine), directory, initialState: engine.State) { Automated = true };
        app.Initialize();
        var view = app.TestPort;
        view.PresentLobby(new("local", "Quiet lifetime", null, false, null, true, "", null, [], []), pausePolling: true);
        double seconds = 0;
        PresentationFrame Tick()
        {
            // Stay inside the normal opening presentation beat so arming can be
            // tested without starting an automatic worker or external service.
            seconds += .001;
            app.Update(new(TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(.001)),
                new MouseState(20, 60, 0, default, default, default, default, default), new(), new(1600, 900));
            return app.ReadPresentation();
        }
        var frame = Tick();
        var oldArm = frame.Hits.Single(hit => hit.Id == "quiet-priority-arm");
        var refreshed = view.Match! with { Revision = view.Match!.Revision + 1,
            Decision = view.Match.Decision! with { Id = "refreshed-quiet-arm", Payload = ((PriorityPayload)(view.Match.Decision!).Payload) with { QuietYieldToken = "replacement-quiet-token" } } };
        view.Accept(refreshed);
        check(!view.CurrentHit(oldArm) && !view.Activate(oldArm) && ReferenceEquals(view.Frame, frame)
            && engine.Replies.Count == 0 && engine.Observations == 0,
            "A refreshed priority decision revokes the preceding Pass for now arm before another frame can replace its token authority");

        frame = Tick();
        check(view.Activate(frame.Hits.Single(hit => hit.Id == "quiet-priority-arm")),
            "The replacement decision exposes its own current Pass for now intent");
        frame = Tick();
        var stop = frame.Hits.Single(hit => hit.Id == "quiet-priority-stop");
        view.Accept(refreshed with { Revision = refreshed.Revision + 1,
            Decision = refreshed.Decision! with { Id = "refreshed-quiet-stop" } });
        check(view.CurrentHit(stop) && view.Activate(stop) && engine.Replies.Count == 0 && engine.Observations == 0,
            "Take control retains its match lifetime across a refreshed decision and cancels the existing policy without a command");
        frame = Tick();
        check(!frame.Hits.Any(hit => hit.Id == "quiet-priority-stop") && frame.Hits.Any(hit => hit.Id == "quiet-priority-arm")
            && engine.Replies.Count == 0 && engine.Observations == 0 && !view.Busy && !view.Polling,
            "Cancelling quiet passing exposes the current arm again without starting engine work");
    }
}
