using System.Diagnostics;
using Mana.Contracts;
using Mana.Renderer;
using Mana.Scenarios;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

// Scoped input and geometry safety for the optional native study. These checks
// neither simulate Magic nor establish that its framing is understandable.
internal static class StudyFocusChecks
{
    internal static void Run(Action<bool, string> check)
    {
        string profile = Path.Combine(Path.GetTempPath(), "ManaTableStudyFocusChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        new PlayPreferences { Automatic = false, Sounds = false, ReducedMotion = true }.Save(profile);
        var engine = new InteractionScenarioEngine();
        engine.Present(FourPlayerTableScenario.Create()[2].Snapshot);
        using var app = new TableApplication(TestTableServices.For(engine), profile, initialState: engine.State) { Automated = true };
        app.Initialize();
        var view = app.TestPort;
        view.SetTableStudy(true);
        view.PresentLobby(new("local", "Controlled study", null, false, null, true, "", null, [], []), true);
        PresentationFrame frame = null!;
        double seconds = 0;
        void Tick(Point point = default, ButtonState left = ButtonState.Released)
        {
            seconds += .01;
            var time = new GameTime(TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(.01));
            app.Update(time, new MouseState(point.X, point.Y, 0, left, ButtonState.Released,
                ButtonState.Released, ButtonState.Released, ButtonState.Released), new KeyboardState(), new(1600, 900));
            frame = app.ComposePresentation(time);
            if (view.Error.Length > 0) throw new InvalidOperationException("Native study focus: " + view.Error);
        }
        void Click(string id)
        {
            var hit = frame.Hits.LastOrDefault(candidate => candidate.Id == id)
                ?? throw new InvalidOperationException("Missing native study input: " + id);
            var bounds = hit.Bounds;
            var candidates = new List<Point> { bounds.Center };
            for (int row = 0; row < 5; row++)
                for (int column = 0; column < 5; column++)
                    candidates.Add(new(bounds.Left + (column * 2 + 1) * bounds.Width / 10,
                        bounds.Top + (row * 2 + 1) * bounds.Height / 10));
            var point = candidates.Where(candidate => hit.Contains(candidate) && view.HitAt(candidate)?.Id == id)
                .Select(candidate => (Point?)candidate).FirstOrDefault()
                ?? throw new InvalidOperationException("Covered native study input: " + id);
            Tick(point); Tick(point, ButtonState.Pressed); Tick(point);
        }
        void Wait(Func<bool> ready)
        {
            var wall = Stopwatch.StartNew();
            while (!ready()) {
                if (wall.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Native study command did not settle.");
                Tick(new(20, 70)); Thread.Yield();
            }
        }
        static SceneCard[] Battlefield(TableScene scene) => scene.Seats.SelectMany(seat => seat.Battlefield)
            .SelectMany(cluster => cluster.Cards).ToArray();
        void CheckPortraits(string context)
        {
            var portraits = frame.Hits.Where(hit => hit.Id.StartsWith("player:", StringComparison.Ordinal)).ToArray();
            check(portraits.Length == 4 && engine.State.Players.All(player => portraits.SingleOrDefault(hit => hit.Id == "player:" + player.Id)
                    is { PlayerId: { } id } && id == player.Id),
                context + ": all four portrait hit IDs retain their exact actual player IDs");
        }

        Tick();
        var original = view.Match!;
        string originalScope = frame.Stamp.Scope;
        var overview = Battlefield(frame.Scene!).ToDictionary(card => card.Id);
        var hand = frame.Scene!.Hand.Cards.Select(card => (card.Id, card.Card.Key, card.Pose)).ToArray();
        var playerOrder = original.Players.Select(player => player.Id).ToArray();
        CheckPortraits("Overview");

        // Player 3 starts at the right seat, so this exercises actual remapping
        // rather than coincidentally focusing the original top opponent.
        view.OpenBattlefield(3); Tick();
        check(view.StudyFocusOwner == 3 && view.ZoneName == null && view.Overlay.Length == 0
                && frame.Scene!.Seat(3).Index == 1 && frame.Scene.Seat(original.ViewerId).Index == 3,
            "Opening an opponent battlefield uses native top/bottom framing without opening the modal battlefield workspace");
        check(ReferenceEquals(view.Match, original) && frame.Stamp.Scope == originalScope
                && view.Match!.ViewerId == original.ViewerId && view.Match.Players.Select(player => player.Id).SequenceEqual(playerOrder)
                && frame.Scene!.Hand.Cards.Select(card => (card.Id, card.Card.Key, card.Pose)).SequenceEqual(hand)
                && view.Match.Stack.SequenceEqual(original.Stack)
                && view.DecisionDock?.GameId == original.Id && view.DecisionDock.DecisionId == original.Decision?.Id
                && frame.Hits.Any(hit => hit.Id == "confirm") == original.Decision!.OkEnabled
                && engine.Replies.Count == 0,
            "Native focus preserves the exact snapshot, viewer, player order, hand geometry, action keys, stack and decision scope without a command");
        var focused = Battlefield(frame.Scene!).Where(card => card.Owner == 3).ToArray();
        check(focused.Length == original.Players.Single(player => player.Id == 3).Zone("Battlefield").Cards.Length
                && focused.Any(card => card.Pose.Size.X * card.Pose.Size.Y > overview[card.Id].Pose.Size.X * overview[card.Id].Pose.Size.Y),
            "The chosen opponent retains every battlefield occurrence and receives enlarged native faces");
        var offFocus = overview.Values.Where(card => card.Owner != original.ViewerId && card.Owner != 3)
            .Select(card => card.Id).ToHashSet(StringComparer.Ordinal);
        var pending = original.Stack.Single();
        var sourceReference = frame.Hits.SingleOrDefault(hit => hit.Id == "stack-source:" + pending.Id);
        check(sourceReference is { Zone: "StackReference", Card: { } sourceCard }
                && CardViews.Identity(sourceCard) == CardViews.Identity(pending.Card!)
                && offFocus.Contains(CardViews.Identity(sourceCard)),
            "The condensed opponent's exact pending source remains reachable as a stack reference rather than a battlefield occurrence");
        var ghostScene = frame.Scene!.Seats.Where(seat => seat.PlayerId != original.ViewerId && seat.PlayerId != 3)
            .SelectMany(seat => seat.Battlefield).SelectMany(cluster => cluster.Cards).ToArray();
        var ghostHits = frame.Hits.Where(hit => hit.Zone == "Battlefield" && hit.Card is { } card
            && offFocus.Contains(CardViews.Identity(card))).ToArray();
        var ghostCards = frame.Cards.Where(card => card.Zone == "Battlefield" && offFocus.Contains(card.Id)).ToArray();
        check(ghostScene.Length == 0 && ghostHits.Length == 0 && ghostCards.Length == 0,
            "Condensed opponents leave no ghost battlefield faces or card hit regions at their former overview positions"
            + "; scene=" + string.Join(", ", ghostScene.Select(card => card.Id + "@" + card.Pose.Bounds))
            + "; hits=" + string.Join(", ", ghostHits.Select(hit => hit.Id + "/" + hit.Zone + "@" + hit.Bounds))
            + "; cards=" + string.Join(", ", ghostCards.Select(card => card.Id + "/" + card.Zone + "@" + card.Display.Bounds)));
        CheckPortraits("Native focus");

        Click("study-focus-return");
        var restored = Battlefield(frame.Scene!);
        check(view.StudyFocusOwner == null && restored.Length == overview.Count
                && restored.All(card => overview.TryGetValue(card.Id, out var before) && card.Owner == before.Owner
                    && card.Pose == before.Pose && ReferenceEquals(card.Card, before.Card))
                && engine.Replies.Count == 0,
            "The actual whole-table input restores every original occurrence, owner, pose and current card without issuing a game command");

        Click("player:1");
        check(view.StudyFocusOwner == 1 && frame.Scene!.Seat(1).Index == 1 && engine.Replies.Count == 0,
            "A non-target opponent portrait enters native focus through its actual input route");
        engine.ChoosePlayer(3); view.Accept(engine.State); Tick();
        string gameId = view.Match!.Id, decisionId = view.Match.Decision!.Id;
        check(view.StudyFocusOwner == 1 && frame.Scene!.Seat(3).Index != 1,
            "A legal player-selection prompt retains the existing framing and the actual condensed target portrait");
        Click("player:3");
        Wait(() => !view.Busy && engine.Replies.Count == 1);
        check(engine.Replies.Single() is SelectPlayer { Player: 3 } reply
                && reply.GameId == gameId && reply.DecisionId == decisionId && view.StudyFocusOwner == 1,
            "Clicking a legal condensed player target submits its exact current game, decision and player reply instead of navigating");
        CheckPortraits("After player selection");

        engine.Present(engine.State with { Id = "native-study-next-match" }); view.Accept(engine.State); Tick();
        check(view.StudyFocusOwner == null && Battlefield(frame.Scene!).Length == overview.Count
                && frame.Scene!.Seat(3).Index == TableLayout.SeatIndex(view.Match!, 3) && engine.Replies.Count == 1,
            "Accepting a different match clears native focus and restores the new match's complete overview without another command");
    }
}
