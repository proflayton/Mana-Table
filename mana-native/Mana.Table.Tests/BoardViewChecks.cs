using Mana.Table.Interaction;
using System.Collections.Immutable;
using Mana.Contracts;
using Mana.Renderer;
using Mana.Scenarios;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

// Mechanical coordinate, interruption and command-safety checks. Human review
// of the real moving table remains separate from these assertions.
internal static class BoardViewChecks
{
    internal static void Run(Action<bool, string> check)
    {
        Geometry(check);
        Reading(check);
        Motion(check);
        InspectionCamera(check);
        ProductionControls(check);
    }

    private static void InspectionCamera(Action<bool, string> check)
    {
        var pose = new Pose(new(640, 410), new(120, 168), .21f, .7f, .0002f, .12f, 4);
        var zoom = new BoardView(new(-180, -60), 1.65f);
        var moved = zoom.Apply(pose);
        check(pose.Corners.Zip(moved.Corners).All(pair => Near(pair.Second, zoom.Apply(pair.First)))
                && Near(zoom.Inverse(moved).Center, pose.Center) && Near(zoom.Inverse(moved).Size, pose.Size)
                && moved.Contains(zoom.Apply(pose.Center).ToPoint()) && default(BoardView).Apply(pose) == pose,
            "Pan/zoom preserves every projected corner and the inverse pose, including perspective and default identity");
        var camera = new BoardViewMotion();
        var target = BoardView.Frame(SharedTableStudy.Territory(2), new(195, 170, 995, 530));
        BoardView At(BoardView destination, double time, bool reduced = false, bool blocked = false, string game = "camera")
            => camera.Navigate(game, 0, destination, time, reduced, blocked);
        var start = At(target, 0); var middle = At(target, .2);
        var retarget = At(BoardView.Identity, .2); var returned = At(BoardView.Identity, 1);
        check(start.IsIdentity && middle.Scale > 1 && middle.Scale < target.Scale && retarget == middle && returned.IsIdentity,
            "Explicit camera navigation eases both pan and zoom; reversing mid-flight retains the exact displayed view and returns home");
        At(target, 2); var beforePress = At(target, 2.2);
        var held = At(target, 5, blocked: true); var resumed = At(target, 5.01);
        var uninterrupted = new BoardViewMotion();
        uninterrupted.Navigate("comparison", 0, target, 2, false, false);
        var expected = uninterrupted.Navigate("comparison", 0, target, 2.21, false, false);
        check(held == beforePress && Near(resumed.Offset, expected.Offset) && Math.Abs(resumed.Scale - expected.Scale) < .0001f
                && At(target, 6, reduced: true) == target && At(BoardView.Identity, 7, game: "another-game").IsIdentity,
            "Pressing a moving card freezes the camera without a delayed jump; reduced motion settles directly and a new game starts at overview");
    }

    private static void Geometry(Action<bool, string> check)
    {
        var view = new BoardView(new(32, -12));
        var pose = new Pose(new(640, 410), new(120, 168), .21f, .7f, .0002f, .12f, 4);
        var moved = view.Apply(pose);
        var points = new[] { pose.Center.ToPoint(), new Point(10, 10), pose.Point(.2f, .3f).ToPoint() };
        check(view.Inverse(moved) == pose && moved.Size == pose.Size && moved.Angle == pose.Angle
            && pose.Corners.Zip(moved.Corners).All(pair => Near(pair.Second, view.Apply(pair.First)))
            && points.All(point => moved.Contains(view.Apply(point)) == pose.Contains(point))
            && view.Inverse(view.Apply(pose.Bounds)) == pose.Bounds,
            "One board translation preserves the projected face and inverse geometry used by its pointer surface");

        var ordinary = TurnOrientationScenario.Ordinary()[0].Snapshot;
        var crowded = TurnOrientationScenario.Crowded()[0].Snapshot;
        bool Preserved(GameSnapshot state)
        {
            var scene = TableScene.Build(state, 0, new(-100, -100), null, null, false, Mana.Magic.CardPresentation.IsResource);
            var cards = scene.Seats.SelectMany(seat => seat.Battlefield).SelectMany(cluster => cluster.Cards).ToArray();
            var projected = cards.Select(card => card with { Pose = view.Apply(card.Pose) }).ToArray();
            return cards.Select(card => (card.Id, card.Owner, card.Zone, card.GroupCount, card.GroupFront))
                .SequenceEqual(projected.Select(card => (card.Id, card.Owner, card.Zone, card.GroupCount, card.GroupFront)))
                && cards.Zip(projected).All(pair => ReferenceEquals(pair.First.Card, pair.Second.Card))
                && cards.Length == state.Players.Sum(player => player.Zone("Battlefield").Cards.Length);
        }
        check(Preserved(ordinary) && Preserved(crowded),
            "Translating ordinary and crowded board geometry preserves every public occurrence, grouping and engine card reference");
    }

    private static void Reading(Action<bool, string> check)
    {
        Card Card(string id) => new() { VisualId = id, CombatId = id, Key = "prompt:" + id, Name = "Same printed name", Type = "Creature", Power = 2, Toughness = 2 };
        var body = Card("board-view-body"); var hand = Card("board-view-hand"); var commander = Card("board-view-command");
        var state = new GameSnapshot("board-reader", "playing", null, null, 0, 8, "Main phase", "MAIN1", 0,
            [new(0, "You", 40, true, false, ImmutableDictionary<string, int>.Empty, [new("Battlefield", 1, [body], null), new("Hand", 1, [hand], null), new("Command", 1, [commander], null)])],
            [], null, new() { Id = "prompt", Kind = "input", Intent = DecisionIntent.Priority, OkEnabled = true });
        var context = new CardPresentationContext(state, "board-reader:prompt", false, false, false, false);
        var owner = new CardPresentationController();
        var rest = new Pose(new(600, 430), new(120, 168), .15f, .8f, .0001f);
        var handRest = new Pose(new(760, 825), new(137, 192));
        var commandRest = new Pose(new(175, 660), new(62, 87));
        var stackRest = new Pose(new(850, 410), new(124, 174));
        var firstView = new BoardView(new(28, -10)); var nextView = new BoardView(new(-20, 8));
        CardPresentationRequest Request(string id, Card card, Pose pose, string zone) => new(id, card, pose, zone, 0, "hit:" + id, UiIntent.None);
        PresentedCard Compose(BoardView view)
        {
            owner.BeginFrame(context, null, .016f, true, view);
            var card = owner.Prepare(Request(body.VisualId, body, rest, "Battlefield"));
            owner.Prepare(Request(hand.VisualId, hand, handRest, "Hand"));
            owner.Prepare(Request(commander.VisualId, commander, commandRest, "Command"));
            owner.Prepare(Request("stack:body", body, stackRest, "Stack"));
            owner.EndFrame(); return card;
        }
        var normal = Compose(firstView);
        check(normal.Request.Rest == rest && normal.Rest == firstView.Apply(rest) && normal.Display == firstView.Apply(rest)
            && normal.Hit.Contains(normal.Display.Center.ToPoint())
            && normal.Hit.Scope == context.Scope && ReferenceEquals(normal.Hit.Card, body)
            && owner.Cards.Single(card => card.Zone == "Command").Display == commandRest
            && owner.Cards.Single(card => card.Zone == "Hand").Display == handRest
            && owner.Cards.Single(card => card.Zone == "Stack").Display == stackRest,
            "Prepared board cards and their scoped hits share one transform while hand and stack anchors stay fixed");

        owner.Toggle(normal.Hit, context); Compose(firstView); var pinned = Compose(firstView);
        var reader = pinned.Display;
        var shifted = Compose(nextView);
        check(pinned.Focused && shifted.Focused && shifted.Display == reader
            && shifted.Rest == nextView.Apply(rest) && owner.ReaderBounds == reader.Bounds
            && owner.ResolveHit(shifted.Rest.Center.ToPoint(), [shifted.Hit], null, context)?.Id == shifted.Hit.Id
           ,
            "A pinned reader remains in fixed screen space while its current board anchor stays mapped without invoking its action");
        owner.Toggle(shifted.Hit, context);
        var collapsed = Compose(nextView);
        check(!owner.HasPinnedCard && collapsed.Display == nextView.Apply(rest) && collapsed.Request.Rest == rest
            && collapsed.Hit.Contains(collapsed.Display.Center.ToPoint()) && owner.ReaderBounds == null,
            "Unpinning restores the current translated board pose exactly once instead of translating a stored display pose again");

        owner.Reset(); normal = Compose(firstView);
        owner.BeginFrame(context, null, .016f, false, nextView);
        bool oldPoseHidden = !owner.TryGetPose(body.VisualId, out _);
        var held = new Pose(new(1000, 600), rest.Size);
        owner.Hold(body.VisualId, held);
        bool heldPoseCurrent = owner.TryGetPose(body.VisualId, out var heldPose) && heldPose == held;
        var returned = owner.Prepare(Request(body.VisualId, body, rest, "Battlefield"));
        var repeated = owner.Prepare(Request(body.VisualId, body, rest, "Battlefield"));
        owner.EndFrame();
        check(oldPoseHidden && heldPoseCurrent && Vector2.Distance(returned.Display.Center, normal.Display.Center) < 20
            && Vector2.Distance(repeated.Display.Center, nextView.Apply(rest).Center)
                < Vector2.Distance(returned.Display.Center, nextView.Apply(rest).Center)
            && owner.TryGetPose(body.VisualId, out var currentPose) && currentPose == repeated.Display,
            "A held card exposes only its current pose; same-frame board preparation rebases from the previous displayed frame once and then continues toward the board without replaying that rebase");
    }

    private static void Motion(Action<bool, string> check)
    {
        Rectangle[] board = [new(400, 300, 100, 100), new(650, 200, 80, 100)];
        Rectangle[] reserves = [new(1240, 550, 350, 245), new(600, 760, 400, 140)];
        var viewport = new Rectangle(0, 0, 1600, 900);
        var request = new BoardViewRequest("board-motion", 8, 0, 0, 4, true, false, false, false, new(30, -12));
        var motion = new BoardViewMotion();
        BoardView At(BoardViewRequest current, double now) => motion.Update(current, now, board, reserves, viewport);
        var baseline = At(request, 0);
        var nextTurn = request with { Turn = 9, ActivePlayerId = 1 };
        At(nextTurn, 1); var traveling = At(nextTurn, 1.25); var settled = At(nextTurn, 2);
        var unchangedTurn = At(nextTurn with { RequestedOffset = new(-30, 12) }, 3);
        var stages = TurnOrientationScenario.Ordinary();
        check(baseline.IsIdentity && !traveling.IsIdentity && settled.Offset == nextTurn.RequestedOffset
            && unchangedTurn == settled && stages[1].Snapshot.Turn == stages[2].Snapshot.Turn
            && stages[1].Snapshot.ActivePlayerId == stages[2].Snapshot.ActivePlayerId
            && stages[1].Snapshot.PriorityPlayerId != stages[2].Snapshot.PriorityPlayerId,
            "Following establishes a neutral baseline and responds to a real turn epoch rather than another player's response window");

        var third = nextTurn with { Turn = 10, ActivePlayerId = 2, RequestedOffset = new(-24, 10) };
        At(third, 4); var beforeInterrupt = At(third, 4.2);
        var frozen = At(third with { InteractionBlocked = true }, 4.3);
        var resumed = At(third, 20);
        motion.Pause(); var manual = At(third with { Turn = 11 }, 21); motion.Resume();
        var afterResume = At(third with { Turn = 11 }, 30);
        check(frozen == beforeInterrupt && resumed == frozen && manual == frozen && afterResume == frozen && !motion.Paused,
            "Pointer or required-choice interruption and manual pause retain the displayed pose and consume the turn change without later catch-up");
        var freshTurn = third with { Turn = 12 };
        At(freshTurn, 31); var restarted = At(freshTurn, 32);
        check(restarted.Offset == freshTurn.RequestedOffset,
            "A later actual turn can follow again after an interrupted or manually paused transition");

        bool Resets(BoardViewRequest suppressed)
        {
            motion.Reset(); At(request, 0); At(nextTurn, 1); At(nextTurn, 2);
            return At(suppressed, 3).IsIdentity && At(suppressed with { ReducedMotion = false, NeedsRecovery = false, PlayerCount = 4, Enabled = true }, 10).IsIdentity;
        }
        check(Resets(nextTurn with { ReducedMotion = true }) && Resets(nextTurn with { PlayerCount = 2 })
            && Resets(nextTurn with { NeedsRecovery = true }) && Resets(nextTurn with { Enabled = false })
            && Resets(nextTurn with { GameId = "another-game" }),
            "Reduced motion, one-on-one play, recovery, disabled following and a different game reset to neutral without replaying a missed turn");

        var guarded = new BoardViewMotion();
        Rectangle[] path = [new(450, 300, 10, 20)]; Rectangle[] middle = [new(470, 300, 4, 20)];
        guarded.Update(request, 0, path, middle, viewport);
        var across = nextTurn with { RequestedOffset = new(36, 0) };
        guarded.Update(across, 1, path, middle, viewport);
        var rejectedPath = guarded.Update(across, 2, path, middle, viewport);
        guarded.Reset(); Rectangle[] edge = [new(1590, 300, 10, 20)];
        guarded.Update(request, 0, edge, [], viewport); guarded.Update(across, 1, edge, [], viewport);
        var rejectedEdge = guarded.Update(across, 2, edge, [], viewport);
        check(rejectedPath.IsIdentity && rejectedEdge.IsIdentity,
            "Following rejects a swept path through fixed controls and a target outside the viewport, even when a path's endpoints would miss the control");

        bool RecoversRetainedView(bool blocked, bool changedLayout, bool paused = false)
        {
            var retained = new BoardViewMotion();
            retained.Update(request, 0, board, reserves, viewport);
            retained.Update(nextTurn, 1, board, reserves, viewport);
            var before = retained.Update(nextTurn, 2, board, reserves, viewport);
            if (paused) retained.Pause();
            Rectangle[] currentBoard = changedLayout ? [new(510, 300, 10, 20)] : board;
            Rectangle[] currentReserves = changedLayout ? [new(535, 300, 15, 20)] : [new(510, 300, 10, 20)];
            var interruptedTurn = third with { InteractionBlocked = blocked };
            var recovered = retained.Update(interruptedTurn, 3, currentBoard, currentReserves, viewport);
            retained.Resume();
            var quiet = retained.Update(third, 20, board, reserves, viewport);
            var laterTurn = third with { Turn = 11 };
            retained.Update(laterTurn, 21, board, reserves, viewport);
            var later = retained.Update(laterTurn, 22, board, reserves, viewport);
            return !before.IsIdentity && recovered.IsIdentity && quiet.IsIdentity
                && later.Offset == laterTurn.RequestedOffset;
        }
        check(RecoversRetainedView(false, false) && RecoversRetainedView(true, false)
            && RecoversRetainedView(false, true) && RecoversRetainedView(true, false, true),
            "New fixed reservations or layout changes revoke an unsafe retained view even when settled, interaction-blocked or paused, without catching up after recovery");
    }

    private static void ProductionControls(Action<bool, string> check)
    {
        string profile = Path.Combine(Path.GetTempPath(), "ManaTableBoardViewChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        new PlayPreferences { Automatic = false, ReducedMotion = false, Sounds = false }.Save(profile);
        var stages = TurnOrientationScenario.Ordinary();
        var engine = new InteractionScenarioEngine(); engine.Present(stages[0].Snapshot);
        using var app = new TableApplication(TestTableServices.For(engine), profile, initialState: engine.State);
        app.Automated = true; app.Initialize(); var view = app.TestPort;
        double seconds = 0;
        PresentationFrame Tick()
        {
            seconds += 1.0 / 60;
            var time = new GameTime(TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(1.0 / 60));
            app.Update(time, new(-100, -100, 0, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released), new KeyboardState(), new(1600, 900));
            return app.ReadPresentation();
        }
        Tick();
        string game = view.Match!.Id; string? prompt = view.Match!.Decision?.Id;
        view.SetTurnFollow(true); Tick();
        bool enabled = view.FollowTurnEnabled;
        view.SetTurnFollow(false); Tick();
        check(enabled && !view.FollowTurnEnabled && view.BoardView.IsIdentity && engine.Replies.Count == 0
            && view.Match is { } current && current.Id == game && current.Decision?.Id == prompt,
            "Follow and Whole table change only local presentation state and submit no scoped engine command");
    }

    private static bool Near(Vector2 first, Vector2 second) => Vector2.DistanceSquared(first, second) < .0001f;
}
