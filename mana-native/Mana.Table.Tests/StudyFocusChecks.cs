using System.Diagnostics;
using System.Collections.Immutable;
using Mana.Contracts;
using Mana.Renderer;
using Mana.Scenarios;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;
using Keys = Microsoft.Xna.Framework.Input.Keys;

namespace Mana.Table;

// Scoped input and geometry safety for the optional native study. These checks
// neither simulate Magic nor establish that its framing is understandable.
internal static class StudyFocusChecks
{
    internal static void Run(Action<bool, string> check)
    {
        string profile = Path.Combine(Path.GetTempPath(), "ManaTableStudyFocusChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        new PlayPreferences { Automatic = false, Sounds = false, ReducedMotion = false }.Save(profile);
        var engine = new InteractionScenarioEngine();
        engine.Present(FourPlayerTableScenario.Create()[2].Snapshot);
        using var app = new TableApplication(TestTableServices.For(engine), profile, initialState: engine.State) { Automated = true };
        app.Initialize();
        var view = app.TestPort;
        view.SetTableStudy(true);
        view.PresentLobby(new("local", "Controlled study", null, false, null, true, "", null, [], []), true);
        PresentationFrame frame = null!;
        double seconds = 0;
        int wheel = 0;
        void Tick(Point point = default, ButtonState left = ButtonState.Released, double elapsed = .01, KeyboardState keyboard = default,
            int wheelDelta = 0, ButtonState middle = ButtonState.Released)
        {
            seconds += elapsed; wheel += wheelDelta;
            app.Update(new(TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(elapsed)),
                new(point.X, point.Y, wheel, left, middle, default, default, default), keyboard, new(1600, 900));
            frame = app.ReadPresentation();
            if (view.Error.Length > 0) throw new InvalidOperationException(view.Error);
        }
        void Click(string id)
        {
            var hit = frame.Hits.Last(candidate => candidate.Id == id);
            var point = hit.Bounds.Center;
            if (!hit.Contains(point) || view.HitAt(point)?.Id != id) throw new InvalidOperationException("Covered input: " + id);
            Tick(point); Tick(point, ButtonState.Pressed); Tick(point);
        }
        static SceneCard[] Battlefield(TableScene scene) => scene.Seats.SelectMany(seat => seat.Battlefield)
            .SelectMany(cluster => cluster.Cards).ToArray();
        Tick();
        for (int frameIndex = 0; frameIndex < 16; frameIndex++) Tick(elapsed: .1);
        var original = view.Match!;
        string scope = frame.Stamp.Scope;
        var overview = Battlefield(frame.Scene!).ToDictionary(card => card.Id);
        var seats = frame.Scene!.Seats.ToDictionary(seat => seat.PlayerId, seat => seat.Index);
        var hand = frame.Cards.Where(card => card.Zone == "Hand").Select(card => card.Rest).ToArray();
        var portraits = frame.Hits.Where(hit => hit.Id.StartsWith("player:", StringComparison.Ordinal)).ToDictionary(hit => hit.Id, hit => hit.Bounds);
        // Follow an actual right-hand opponent without reseating them at the top.
        view.OpenBattlefield(3); Tick();
        check(view.StudyFocusOwner == 3 && view.ZoneName == null && view.Overlay.Length == 0
                && view.BoardView.IsIdentity && frame.Scene!.Seat(3).Index == seats[3],
            "Opening a player battlefield starts at the existing camera and retains that player's actual table seat");
        Tick(elapsed: .22);
        var traveling = view.BoardView;
        check(!traveling.IsIdentity && traveling.Scale > 1
                && ReferenceEquals(view.Match, original) && frame.Stamp.Scope == scope && engine.Replies.Count == 0
                && Battlefield(frame.Scene!).Length == overview.Count
                && Battlefield(frame.Scene!).All(card => card.Pose == overview[card.Id].Pose && ReferenceEquals(card.Card, overview[card.Id].Card))
                && frame.Cards.Where(card => card.Zone == "Battlefield").All(card =>
                    Vector2.Distance(card.Display.Center, traveling.Apply(overview[card.Id].Pose).Center) < .01f)
                && frame.Cards.Where(card => card.Zone == "Hand").Select(card => card.Rest).SequenceEqual(hand)
                && portraits.All(pair => frame.Hits.Single(hit => hit.Id == pair.Key).Bounds == pair.Value),
            "Mid-flight pan and zoom share one transform across every battlefield occurrence while the snapshot, canonical poses, hand and all four scoped player targets remain unchanged");
        engine.Present(original with { Revision = original.Revision + 1,
            Players = original.Players.Select(player => player with {
                Zones = player.Zones.Select(zone => zone.Name != "Battlefield" ? zone : zone with {
                    Cards = zone.Cards.Select(card => card.VisualId != FourPlayerTableScenario.TargetId ? card
                        : card with { Power = 6, Toughness = 5, Counters = card.Counters.SetItem("+1/+1", 2) }).ToImmutableArray()
                }).ToImmutableArray()
            }).ToImmutableArray() });
        view.Accept(engine.State); Tick();
        var changed = frame.Cards.Single(card => card.Zone == "Battlefield" && card.Id == FourPlayerTableScenario.TargetId);
        check(changed.Card is { Power: 6, Toughness: 5 } && changed.Card.Counters["+1/+1"] == 2
                && view.BoardView.Scale > traveling.Scale && engine.Replies.Count == 0,
            "A fresh authoritative P/T and counter update remains live during camera travel without restarting the camera or replaying a game action");
        Tick(elapsed: .7);
        check(view.BoardView.Scale > traveling.Scale
                && frame.Cards.Where(card => card.Zone == "Battlefield" && card.Request.Owner == 3).All(card =>
                    card.Display.Size.X > overview[card.Id].Pose.Size.X && card.Hit.Contains(card.Display.Center.ToPoint())),
            "The settled camera enlarges the chosen battlefield and its exact displayed pointer geometry without rebuilding or dropping other battlefields");
        Click("study-focus-return");
        check(!view.BoardView.IsIdentity && view.StudyFocusOwner == null,
            "Whole table begins a return journey from the displayed camera instead of snapping the battlefield to its overview");
        Tick(elapsed: .7);
        check(view.BoardView.IsIdentity && Battlefield(frame.Scene!).All(card => card.Pose == overview[card.Id].Pose)
                && engine.Replies.Count == 0,
            "Returning restores the same table positions without game commands or reconstructed card state");
        Click("player:1"); Tick(elapsed: .7);
        check(view.StudyFocusOwner == 1 && frame.Scene!.Seats.Count == 4,
            "A non-target portrait inspects its battlefield while all four seats remain present");
        Tick(keyboard: new KeyboardState(Keys.Escape));
        check(view.StudyFocusOwner == null && !view.BoardView.IsIdentity && engine.Replies.Count == 0,
            "Escape starts the same noncommitting return movement as Whole table");
        Tick(elapsed: .7); Click("player:1"); Tick(elapsed: .7);
        engine.ChoosePlayer(3); view.Accept(engine.State); Tick();
        string game = view.Match!.Id, decision = view.Match.Decision!.Id;
        Click("player:3");
        var wait = Stopwatch.StartNew();
        while (view.Busy && wait.Elapsed < TimeSpan.FromSeconds(5)) { Tick(); Thread.Yield(); }
        check(!view.Busy && engine.Replies.SingleOrDefault() is SelectPlayer { Player: 3 } reply
                && reply.GameId == game && reply.DecisionId == decision && view.StudyFocusOwner == 1,
            "A legal player target submits its current exact decision and player ID while retaining the inspection camera");
        engine.Present(engine.State with { Id = "native-study-next-match" }); view.Accept(engine.State); Tick();
        check(view.StudyFocusOwner == null && view.BoardView.IsIdentity && Battlefield(frame.Scene!).Length == overview.Count,
            "A different game drops the previous inspection camera rather than replaying its movement");
        // This idle authored observation has no reply capability. Arm Auto so
        // the visible pause reason reflects camera capture rather than Manual.
        view.SetAutomatic(true);
        var cameraState = view.Match;
        var handBeforeCamera = frame.Cards.Where(card => card.Zone == "Hand").Select(card => card.Rest).ToArray();
        Tick(new(800, 400), wheelDelta: 240); Tick(elapsed: .7);
        check(view.BoardView.Scale > 1 && ReferenceEquals(view.Match, cameraState)
                && Battlefield(frame.Scene!).All(card => card.Pose == overview[card.Id].Pose)
                && frame.Cards.Where(card => card.Zone == "Hand").Select(card => card.Rest).SequenceEqual(handBeforeCamera),
            "Wheel zoom changes the shared camera while game state, canonical card poses and the local hand remain unchanged");
        var beforePan = view.BoardView;
        Tick(new(700, 400), middle: ButtonState.Pressed);
        Tick(new(780, 430), middle: ButtonState.Pressed);
        check(view.DecisionDock is { PauseReason: Mana.Magic.DecisionDockPauseReason.Interaction },
            "An active camera drag temporarily pauses automatic advancement");
        Tick(new(1680, 430), middle: ButtonState.Pressed); Tick(new(1680, 430)); Tick(new(1680, 430), elapsed: .7);
        check(view.DecisionDock is { PauseReason: not Mana.Magic.DecisionDockPauseReason.Interaction },
            "Releasing camera capture outside the board retires its interaction pause");
        check(view.BoardView.Scale == beforePan.Scale && view.BoardView.Offset != beforePan.Offset
                && ReferenceEquals(view.Match, cameraState) && engine.Replies.Count == 1,
            "Middle-drag pans the normal battlefield without changing scale, decision scope or submitting another command");
        Tick(keyboard: new(Keys.Home)); Tick(elapsed: .7);
        check(view.BoardView.IsIdentity && engine.Replies.Count == 1,
            "Home restores the shared table after manual camera movement without a rules action");
        Tick(new(800, 400), wheelDelta: 240); Tick(elapsed: .7);
        engine.Present(engine.State with { Id = "native-manual-camera-next-match" }); view.Accept(engine.State); Tick();
        check(view.BoardView.IsIdentity && view.StudyFocusOwner == null,
            "A new match retires the manually zoomed camera as well as seat focus");
        TwoPlayerMatch(check);
    }
    private static void TwoPlayerMatch(Action<bool, string> check)
    {
        var stages = ExperienceScenarios.Find("two-player-table.v1").Create();
        string profile = Path.Combine(Path.GetTempPath(), "ManaTableDuelChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        new PlayPreferences { Automatic = false, Sounds = false, ReducedMotion = true }.Save(profile);
        var engine = new InteractionScenarioEngine(); engine.Present(stages[0].Snapshot);
        using var app = new TableApplication(TestTableServices.For(engine), profile, initialState: engine.State) { Automated = true };
        app.EnableLiveTableStudy(); app.Initialize();
        var view = app.TestPort;
        view.PresentLobby(new("local", "Controlled two-player table", null, false, null, true, "", null, [], []), true);
        PresentationFrame frame = null!; double seconds = 0;
        void Tick(Point point = default, ButtonState left = ButtonState.Released) {
            seconds += .02;
            app.Update(new(TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(.02)),
                new(point.X, point.Y, 0, left, default, default, default, default), new(), new(1600, 900));
            frame = app.ReadPresentation();
            if (view.Error.Length > 0) throw new InvalidOperationException(view.Error);
        }
        Tick();
        var baseline = frame.Cards.Where(card => card.Zone == "Battlefield" && card.Request.Owner == 1)
            .ToDictionary(card => card.Id, card => card.Rest);
        engine.Present(stages[1].Snapshot); view.Accept(engine.State); Tick(); Tick();
        check(frame.Scene!.Seat(1).Index == 1 && frame.Scene.Seat(0).Index == 3
                && frame.Cards.Where(card => card.Zone == "Battlefield" && card.Request.Owner == 1).All(card => baseline[card.Id] == card.Rest)
                && !frame.Hits.Any(hit => hit.Id is "focus:0" or "study-focus-return")
                && engine.Replies.Count == 0,
            "The shipping live-table entry keeps two-player source faces and the opponent's exact framing when the eighth local group arrives, without an overflow HUD or navigation command");
        engine.ChoosePlayer(1); view.Accept(engine.State); Tick();
        string game = view.Match!.Id, prompt = view.Match.Decision!.Id;
        var portrait = frame.Hits.Single(hit => hit.Id == "player:1");
        check(portrait.Contains(frame.Scene.Seat(1).Geometry.Hero.ToPoint())
                && view.HitAt(portrait.Bounds.Center)?.Id == portrait.Id,
            "The required opponent target uses the exposed top portrait's real player ID");
        Tick(portrait.Bounds.Center, ButtonState.Pressed); Tick(portrait.Bounds.Center);
        var deadline = Stopwatch.StartNew();
        while (view.Busy && deadline.Elapsed < TimeSpan.FromSeconds(5)) { Tick(); Thread.Yield(); }
        check(!view.Busy && engine.Replies.SingleOrDefault() is SelectPlayer { Player: 1 } reply
                && reply.GameId == game && reply.DecisionId == prompt && view.StudyFocusOwner == null,
            "A required two-player portrait click submits its exact current target command rather than changing table focus");
        view.OpenBattlefield(1); Tick();
        check(view.ZoneName == "Battlefield" && view.StudyFocusOwner == null && engine.Replies.Count == 1,
            "The two-player battlefield inspection remains reachable instead of adding a redundant multiplayer focus toggle");
    }
}
