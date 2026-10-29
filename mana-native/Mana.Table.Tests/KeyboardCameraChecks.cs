using System.Collections.Immutable;
using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Mana.Scenarios;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Keys = Microsoft.Xna.Framework.Input.Keys;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

internal static class KeyboardCameraChecks
{
    internal static void Run(string profile, Action<bool, string> check)
    {
        var normal = CameraKeyboardPan.Offset(new(Keys.W), TimeSpan.FromSeconds(.05));
        var diagonal = CameraKeyboardPan.Offset(new(Keys.W, Keys.D), TimeSpan.FromSeconds(.05));
        Vector2 Over(int frames) => Enumerable.Range(0, frames).Aggregate(Vector2.Zero,
            (sum, _) => sum + CameraKeyboardPan.Offset(new(Keys.Right), TimeSpan.FromSeconds(1d / frames)));
        check(Near(normal, new(0, 30)) && Near(CameraKeyboardPan.Offset(new(Keys.Up), TimeSpan.FromSeconds(.05)), normal)
                && Near(CameraKeyboardPan.Offset(new(Keys.W, Keys.Up), TimeSpan.FromSeconds(.05)), normal)
                && CameraKeyboardPan.Offset(new(Keys.W, Keys.S, Keys.A, Keys.D), TimeSpan.FromSeconds(.05)) == Vector2.Zero
                && Near(diagonal, new(-30 / MathF.Sqrt(2), 30 / MathF.Sqrt(2)))
                && Near(CameraKeyboardPan.Offset(new(Keys.W, Keys.LeftShift), TimeSpan.FromSeconds(.05)), normal * 2)
                && Near(Over(30), Over(60)) && Near(Over(60), new(-600, 0))
                && Near(CameraKeyboardPan.Offset(new(Keys.W), TimeSpan.FromSeconds(3)), normal),
            "Camera navigation uses identical WASD/arrow directions, cancels opposites, normalizes diagonals, doubles with Shift and keeps bounded elapsed-time speed independent of frame rate");

        using (var run = new CameraRun(profile, "keyboard-camera-board")) {
            var state = run.View.Match;
            var canonical = World(run.Frame).ToDictionary(card => card.Id, card => card.Pose);
            var hand = run.Frame.Cards.Where(card => card.Zone == "Hand").Select(card => (card.Id, card.Rest)).ToArray();
            var portraits = run.Frame.Hits.Where(hit => hit.Id.StartsWith("player:", StringComparison.Ordinal)).ToDictionary(hit => hit.Id, hit => hit.Bounds);
            run.Tick(new(Keys.W), .05);
            string movedDiagnostics = string.Join("; ", run.Frame.Cards.Where(card => card.Zone == "Battlefield")
                .Select(card => new { card.Id, card.Display, card.Layer,
                    Expected = run.View.BoardView.Apply(canonical[card.Id]).Center,
                    Distance = Vector2.Distance(card.Display.Center, run.View.BoardView.Apply(canonical[card.Id]).Center) })
                .Where(card => card.Distance >= .02f).Take(4)
                .Select(card => $"{card.Id}: actual={card.Display.Center}, expected={card.Expected}, delta={card.Distance}, layer={card.Layer}"));
            check(Near(run.View.BoardView.Offset, new(0, 30)) && run.View.BoardView.Scale == 1
                    && run.Frame.Cards.Where(card => card.Zone == "Battlefield").All(card =>
                        Vector2.Distance(card.Display.Center, run.View.BoardView.Apply(canonical[card.Id]).Center) < .02f),
                "Holding W moves the displayed battlefield immediately in camera-up direction even while the pointer is in the fixed header"
                    + $" (camera={run.View.BoardView}, study={run.View.TableStudyEnabled}, overlay={run.View.Overlay}, busy={run.View.Busy}, mismatches={movedDiagnostics})");
            var stopped = run.View.BoardView;
            run.Tick(); run.Tick(elapsed: .7);
            check(run.View.BoardView == stopped && ReferenceEquals(run.View.Match, state)
                    && World(run.Frame).All(card => card.Pose == canonical[card.Id])
                    && run.Frame.Cards.Where(card => card.Zone == "Hand").Select(card => (card.Id, card.Rest)).SequenceEqual(hand)
                    && portraits.All(pair => run.Frame.Hits.Single(hit => hit.Id == pair.Key).Bounds == pair.Value)
                    && run.Engine.Replies.Count == 0,
                "Releasing camera keys stops without a delayed glide while the same authoritative cards, canonical table poses, hand and player controls remain fixed");
            run.Home();
            check(run.View.BoardView.IsIdentity, "Home returns the manually panned battlefield to the same whole-table camera");
            run.View.SetAutomatic(true);
            run.Tick(new(Keys.D), .05);
            check(run.View.DecisionDock is { PauseReason: DecisionDockPauseReason.Interaction },
                "Held camera keys pause automatic advancement through the same production interaction gate as a mouse camera drag");
            run.Tick();
            check(run.View.DecisionDock is { PauseReason: not DecisionDockPauseReason.Interaction },
                "Releasing camera keys retires their temporary automatic-advancement pause");
            var beforeGuard = run.View.BoardView;
            foreach (var modifier in new[] { Keys.LeftControl, Keys.RightControl, Keys.LeftAlt, Keys.RightAlt, Keys.LeftWindows, Keys.RightWindows }) {
                run.Tick(new(Keys.W, modifier), .05); run.Tick();
            }
            run.Tick(new(Keys.W), 3, active: false);
            check(run.View.BoardView == beforeGuard,
                "Control, Alt, Windows combinations and inactive-window key states cannot move the production camera");
            run.Tick(new(Keys.W), .05);
            check(run.View.BoardView == beforeGuard,
                "Returning to an active window cannot replay an already-held navigation key");
            run.Tick(); run.Tick(new(Keys.W), .05);
            check(Near(run.View.BoardView.Offset, beforeGuard.Offset + new Vector2(0, 30)),
                "After neutral foreground input, a fresh navigation key applies only its current bounded camera step");
            run.Home();
            var origin = new Point(800, 400);
            run.Tick(point: origin, middle: ButtonState.Pressed);
            run.Tick(point: origin + new Point(85, 28), middle: ButtonState.Pressed);
            check(Near(run.View.BoardView.Offset, new(85, 28)),
                "The existing middle-button drag also follows the pointer immediately instead of repeatedly restarting a focus transition");
            run.Tick(point: origin + new Point(85, 28));
            var released = run.View.BoardView;
            run.Tick(elapsed: .7);
            check(run.View.BoardView == released && run.Engine.Replies.Count == 0,
                "Releasing a manual pointer pan leaves no delayed camera movement or rules command");
        }

        Guards(profile, check);
        Collections(profile, check);
    }

    private static void Guards(string profile, Action<bool, string> check)
    {
        using var run = new CameraRun(profile, "keyboard-camera-guards");
        run.Tick(new(Keys.Enter)); run.Tick();
        if (!run.View.ChatOpen) throw new InvalidOperationException("The connected camera fixture did not open actual table chat.");
        run.View.TypeText("wasd");
        var before = run.View.BoardView;
        run.Tick(new(Keys.W), .05);
        run.Tick(new(Keys.Escape, Keys.W), .05);
        check(run.View.BoardView == before && !run.View.ChatOpen && run.View.ChatDraft == "wasd",
            "Typing in actual table chat and its Escape-close frame consume camera keys without losing the chat draft");
        run.Tick(new(Keys.W), .05);
        check(!run.View.BoardView.IsIdentity, "Keyboard camera navigation resumes after the chat gesture has completed");
        run.Home();
        run.Click("settings");
        before = run.View.BoardView; run.Tick(new(Keys.W), .05);
        check(run.View.Overlay == "settings" && run.View.BoardView == before,
            "An actual settings overlay owns keyboard input and keeps the battlefield camera steady");
        run.Tick(new(Keys.Escape)); run.Tick();
        var card = run.Frame.Cards.First(card => card.Zone == "Hand");
        var at = run.At(card.Hit.Id);
        run.Tick(point: new(800, 400), middle: ButtonState.Pressed);
        run.Tick(point: at, left: ButtonState.Pressed, middle: ButtonState.Pressed);
        before = run.View.BoardView;
        run.Tick(new(Keys.W), .05, at + new Point(1, 1), left: ButtonState.Pressed, middle: ButtonState.Pressed);
        check(run.View.BoardView == before,
            "Capturing a card during a middle-button pan freezes that camera despite continued middle drag or held navigation keys");
        run.Tick(point: at); run.Tick(elapsed: .7);
        if (!run.Frame.Cards.Any(card => card.Focused)) throw new InvalidOperationException("The camera fixture did not open its actual card reader.");
        before = run.View.BoardView; run.Tick(new(Keys.W), .05);
        check(run.View.BoardView == before && run.Engine.Replies.Count == 0,
            "Reading an enlarged card keeps its world and pointer geometry steady while camera keys are held");

        var authored = FourPlayerTableScenario.Create()[0].Snapshot;
        using var text = new CameraRun(profile, "keyboard-camera-text", authored with {
            Decision = new("text-camera", new(Message: "Name a card."), new TextPayload("")) });
        text.Click("text-input"); text.View.TypeText("wasd");
        before = text.View.BoardView; text.Tick(new(Keys.W), .05);
        var painted = text.Frame.Drawing.Commands.Select(command => command switch {
            DrawCommand.Text value => value.Value, DrawCommand.CenterText value => value.Value, _ => null }).OfType<string>().ToArray();
        check(text.View.BoardView == before && painted.Contains("wasd")
                && text.Engine.Replies.Count == 0,
            "An actual engine text-choice field accepts WASD as text without moving the camera or answering the choice"
                + $" (before={before}, after={text.View.BoardView}, replies={text.Engine.Replies.Count}, rendered={string.Join(" | ", painted)})");
    }

    private static void Collections(string profile, Action<bool, string> check)
    {
        var authored = FourPlayerTableScenario.Create()[0].Snapshot;
        var template = authored.Viewer!.Zone("Hand").Cards[0];
        var hand = Enumerable.Range(0, 40).Select(index => template with {
            VisualId = "keyboard-spread-" + index, CombatId = "keyboard-spread-" + index, Name = "Camera card " + index }).ToImmutableArray();
        var state = authored with { Players = authored.Players.Select(player => player.Id != authored.ViewerId ? player : player with {
            Zones = player.Zones.Select(zone => zone.Name != "Hand" ? zone : zone with { Count = hand.Length, Cards = hand }).ToImmutableArray()
        }).ToImmutableArray() };
        using var run = new CameraRun(profile, "keyboard-camera-collection", state);
        run.Tick(new(Keys.H)); run.Tick();
        var spread = run.Frame.Scene?.Spread ?? throw new InvalidOperationException("H did not open the actual large hand collection.");
        var world = spread.WorldCards.Select(card => (card.Id, card.Pose)).ToArray();
        var anchors = run.Frame.Scene!.Hand.Cards.Select(card => (card.Id, card.Pose)).ToArray();
        var board = run.View.BoardView;
        run.Tick(new(Keys.D, Keys.S), .05);
        var moved = run.Frame.Scene!.Spread!;
        check(moved.View != spread.View && moved.WorldCards.Select(card => (card.Id, card.Pose)).SequenceEqual(world)
                && run.Frame.Cards.Where(card => moved.Contains(card.Id) && card.Zone == "Hand").All(card =>
                    Vector2.Distance(card.Display.Center, moved.View.Apply(moved.WorldCards.Single(worldCard => worldCard.Id == card.Id).Pose).Center) < .02f)
                && run.View.BoardView == board && run.Frame.Scene.Hand.Cards.Select(card => (card.Id, card.Pose)).SequenceEqual(anchors)
                && run.Engine.Replies.Count == 0,
            "The same keyboard gesture pans the displayed large-hand collection immediately over unchanged physical cards and hand anchors without moving the battlefield or issuing a game action");
        var stopped = moved.View;
        run.Tick(); run.Tick(elapsed: .7);
        check(run.Frame.Scene!.Spread!.View == stopped,
            "Releasing collection camera keys leaves no residual drift or collection repacking");
        run.Tick(new(Keys.Home)); run.Tick(elapsed: .7);
        check(run.Frame.Scene!.Spread!.View == run.Frame.Scene.Spread.Overview && run.Engine.Replies.Count == 0,
            "Home returns the same expanded collection to its deliberate overview without folding or playing a card");
    }

    private static IEnumerable<SceneCard> World(PresentationFrame frame) => frame.Scene!.Seats
        .SelectMany(seat => seat.Battlefield).SelectMany(cluster => cluster.Cards);
    private static bool Near(Vector2 first, Vector2 second) => Vector2.Distance(first, second) < .03f;

    private sealed class CameraRun : IDisposable
    {
        private readonly PresentationRun presentation;
        internal InteractionScenarioEngine Engine { get; } = new();
        internal TableApplication.Probe View => presentation.View;
        internal PresentationFrame Frame => presentation.Frame;
        internal CameraRun(string profile, string name, GameSnapshot? state = null)
        {
            string path = Path.Combine(profile, name); Directory.CreateDirectory(path);
            new PlayPreferences { Automatic = false, ReducedMotion = false, Sounds = false }.Save(path);
            Engine.Present(state ?? FourPlayerTableScenario.Create()[0].Snapshot);
            presentation = new(TestTableServices.For(Engine) with { Social = new Conversation() }, path, Engine.State, new(1600, 900));
            presentation.EnableLiveTableStudy();
            var lobby = new Lobby("joined", "Camera fixture", null, false, null, true, "", null, [], [], "keyboard-camera-table");
            View.SetConnectedTable(lobby); View.PresentLobby(lobby, true);
            Tick();
            if (Engine.State.Decision?.Payload is TextPayload) {
                if (!Frame.Hits.Any(hit => hit.Id == "text-input"))
                    throw new InvalidOperationException("The authored camera text fixture did not expose its actual input field.");
                return;
            }
            bool Settled()
            {
                var canonical = World(Frame).ToDictionary(card => card.Id, card => card.Pose);
                return Frame.Cards.Any(card => card.Zone == "Battlefield")
                    && Frame.Cards.Where(card => card.Zone == "Battlefield").All(card =>
                        Vector2.Distance(card.Display.Center, canonical[card.Id].Center) < .005f
                        && Vector2.Distance(card.Display.Size, canonical[card.Id].Size) < .005f);
            }
            // Initial arrival staggering and pose smoothing advance through
            // ordinary frames; one large clock jump does not settle their delay.
            for (int frame = 0; frame < 90 && !Settled(); frame++) Tick(elapsed: .05);
            if (!Settled()) throw new InvalidOperationException("The authored camera fixture's initial battlefield cards did not settle.");
        }
        internal void Tick(KeyboardState keys = default, double elapsed = 1d / 60, Point? point = null,
            ButtonState left = ButtonState.Released, ButtonState middle = ButtonState.Released, bool active = true)
        {
            var at = point ?? new Point(20, 30);
            presentation.Tick(TimeSpan.FromSeconds(elapsed),
                new(at.X, at.Y, 0, left, middle, default, default, default), keys, active: active);
        }
        internal void Home() { Tick(new(Keys.Home)); Tick(); Tick(elapsed: .7); }
        internal Point At(string id)
        {
            var hit = Frame.Hits.Last(hit => hit.Id == id);
            for (int y = Math.Max(0, hit.Bounds.Top + 2); y < Math.Min(900, hit.Bounds.Bottom - 2); y += 3)
                for (int x = Math.Max(0, hit.Bounds.Left + 2); x < Math.Min(1600, hit.Bounds.Right - 2); x += 3)
                    if (View.HitAt(new(x, y))?.Id == id) return new(x, y);
            throw new InvalidOperationException("Covered camera fixture control: " + id);
        }
        internal void Click(string id)
        {
            var at = At(id); Tick(point: at, left: ButtonState.Pressed); Tick(point: at);
            if (presentation.AcceptedHit != id) throw new InvalidOperationException("The production application did not accept the camera fixture gesture: " + id);
        }
        public void Dispose() => presentation.Dispose();
    }

    private sealed class Conversation : ITableSocial
    {
        public Task<TableConversation> ConversationAsync(string tableId) => Task.FromResult(new TableConversation(tableId, 0, 0, [], []));
        public Task<TableConversation> SendMessageAsync(string tableId, string kind, string text) => ConversationAsync(tableId);
        public Task<TableConversation> SetPlayerNameAsync(string tableId, string name) => ConversationAsync(tableId);
    }
}
