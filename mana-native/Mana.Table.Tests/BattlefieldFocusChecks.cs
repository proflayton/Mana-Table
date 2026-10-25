using System.Diagnostics;
using Mana.Contracts;
using Mana.Renderer;
using Mana.Scenarios;
using Mana.Table.Interaction;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

// Command/input regression through the real GPU-free application. The shared
// authored projection is not a Magic simulation or a visual/usability verdict.
internal static class BattlefieldFocusChecks
{
    internal static void Run(Action<bool, string> check)
    {
        string profile = Path.Combine(Path.GetTempPath(), "ManaTableBattlefieldFocusChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        new PlayPreferences { Automatic = false, Sounds = false, ReducedMotion = true, TextScale = 1.3f }.Save(profile);
        var engine = new InteractionScenarioEngine();
        engine.Present(ClutteredChangesScenario.Initial());
        using var app = new TableApplication(TestTableServices.For(engine), profile, initialState: engine.State) { Automated = true };
        app.Initialize();
        var view = app.TestPort;
        PresentationFrame frame = null!;
        double seconds = 0; int wheel = 0;
        void Tick(Point point = default, ButtonState left = ButtonState.Released)
        {
            seconds += .05;
            var time = new GameTime(TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(.05));
            app.Update(time, new MouseState(point.X, point.Y, wheel, left, ButtonState.Released,
                ButtonState.Released, ButtonState.Released, ButtonState.Released), new KeyboardState(), new(1600, 900));
            frame = app.ReadPresentation();
            if (view.Error.Length > 0) throw new InvalidOperationException("Battlefield focus: " + view.Error);
        }
        TableHit? Alias(string identity) => frame.Hits.LastOrDefault(hit => hit.Id == "battlefield-focus-card:" + identity);
        void Click(string id)
        {
            var hit = frame.Hits.LastOrDefault(hit => hit.Id == id)
                ?? throw new InvalidOperationException("Missing focused battlefield input: " + id);
            var bounds = hit.Bounds;
            var candidates = new List<Point> { bounds.Center };
            for (int row = 0; row < 5; row++)
                for (int column = 0; column < 5; column++)
                    candidates.Add(new(bounds.Left + (column * 2 + 1) * bounds.Width / 10,
                        bounds.Top + (row * 2 + 1) * bounds.Height / 10));
            var point = candidates.Where(point => hit.Contains(point) && view.HitAt(point)?.Id == id)
                .Select(point => (Point?)point).FirstOrDefault()
                ?? throw new InvalidOperationException("Covered focused battlefield input: " + id);
            Tick(point); Tick(point, ButtonState.Pressed); Tick(point);
        }
        void Wait(Func<bool> ready)
        {
            var wall = Stopwatch.StartNew();
            while (!ready()) {
                if (wall.Elapsed > TimeSpan.FromSeconds(5))
                    throw new TimeoutException("Focused battlefield did not reach its required command state.");
                Tick(new(20, 70)); Thread.Yield();
            }
        }

        Tick();
        view.OpenBattlefield(0); Tick();
        Click("battlefield-focus-role:Support");
        check(view.ZoneName == "Battlefield" && Alias(ClutteredChangesScenario.RetreatId(0)) != null && engine.Replies.Count == 0,
            "Actual Support input brings the local Felidar Retreat into the focused workspace without submitting an action");
        Click("battlefield-focus-role:Creatures");
        var cat = frame.Hits.First(hit => hit.Zone == "BattlefieldFocus" && hit.Card?.Name == "Cat Beast Token");
        string catIdentity = CardViews.Identity(cat.Card!);
        var beforeWheel = cat.Bounds;
        wheel -= 120; Tick(new(1180, 690));
        check(view.ZoneName == "Battlefield" && (Alias(catIdentity) == null || Alias(catIdentity)!.Bounds != beforeWheel)
                && engine.Replies.Count == 0,
            "Actual mouse-wheel input scrolls the focused document without playing a card");
        wheel += 120; Tick(new(1180, 690));
        check(Alias(catIdentity)?.Bounds == beforeWheel,
            "Reversing the focused mouse-wheel input returns to the previous document position");

        Click("battlefield-focus-owner:1");
        check(frame.Hits.Where(hit => hit.Zone == "BattlefieldFocus").All(hit => hit.PlayerId == 1)
                && frame.Hits.Any(hit => hit.Zone == "BattlefieldFocus" && hit.PlayerId == 1) && engine.Replies.Count == 0,
            "The opponent tab changes the inspected owner through actual input without issuing a command for that player");
        Click("battlefield-focus-role:Support");
        check(Alias(ClutteredChangesScenario.RetreatId(1)) is { PlayerId: 1 },
            "The same Support control now targets the selected opponent's public battlefield");
        Click("battlefield-focus-owner:0");
        Click("battlefield-focus-role:Creatures");
        Click("battlefield-focus-card:" + catIdentity);
        check(frame.Cards.Any(card => card.Id == catIdentity && card.Focused) && view.ZoneName == "Battlefield"
                && view.Overlay.Length == 0 && engine.Replies.Count == 0,
            "Clicking a focus card alias opens its existing card reader without an inspection command or modal");
        Click(frame.Cards.Single(card => card.Id == catIdentity).Hit.Id);
        check(frame.Cards.All(card => !card.Focused) && view.ZoneName == "Battlefield" && engine.Replies.Count == 0,
            "A second actual click on the selected card collapses its reader and keeps the focused battlefield open");

        int scrolls = 0;
        while (Alias(ClutteredChangesScenario.ActionCardId) == null && scrolls++ < 40) {
            wheel -= 120; Tick(new(1180, 690));
        }
        Click("battlefield-focus-card:" + ClutteredChangesScenario.ActionCardId);
        // The release frame first reconciles the original compact occurrence.
        // The action belongs to the settled reader, not that initial frame.
        Wait(() => frame.Cards.Any(card => card.Id == ClutteredChangesScenario.ActionCardId && card.Focused)
            && frame.Hits.Any(hit => hit.Id == "read-card-action"));
        check(frame.Cards.Any(card => card.Id == ClutteredChangesScenario.ActionCardId && card.Focused)
                && frame.Hits.Any(hit => hit.Id == "read-card-action") && engine.Replies.Count == 0,
            "Finding and reading Llanowar Elves exposes its explicit activation control without activating it");

        Wait(() => !view.Busy && !view.Polling);
        string oldDecision = view.Match!.Decision!.Id;
        string oldKey = view.Match.Viewer!.Zone("Battlefield").Cards.Single(card => card.VisualId == ClutteredChangesScenario.ActionCardId).Key;
        engine.Present(engine.State); view.Accept(engine.State); Tick();
        string game = view.Match!.Id, decision = view.Match.Decision!.Id;
        string key = view.Match.Viewer!.Zone("Battlefield").Cards.Single(card => card.VisualId == ClutteredChangesScenario.ActionCardId).Key;
        check(decision != oldDecision && key != oldKey && frame.Cards.Any(card => card.Id == ClutteredChangesScenario.ActionCardId && card.Focused)
                && engine.Replies.Count == 0,
            "A refreshed priority prompt keeps the same public card reader focused while rotating its actionable decision and card handles");
        Click("read-card-action");
        Wait(() => !view.Busy && engine.Replies.Count == 1 && view.Match?.Decision?.Payload is AbilityChoicePayload);
        check(engine.Replies.Single() is ActivateCard reply
                && reply.GameId == game && reply.DecisionId == decision && reply.Card == key
                && view.Match!.Decision?.Context.Source?.Card?.VisualId == ClutteredChangesScenario.ActionCardId,
            "Only explicit activation submits the refreshed game/decision/card handles for the currently read Llanowar Elves");
        string option = ((AbilityChoicePayload)view.Match!.Decision!.Payload).Selection.Choices.Single().Label;
        var choice = frame.Hits.LastOrDefault(hit => hit.Id == "choice:0");
        var source = frame.Cards.SingleOrDefault(card => card.Id == ClutteredChangesScenario.ActionCardId && card.Zone == "Battlefield" && card.Focused);
        check(view.ZoneName == "Battlefield" && frame.Hits.Any(hit => hit.Id == "battlefield-focus-close")
                && source != null && source.Card.Key == view.Match.Viewer!.Zone("Battlefield").Cards.Single(card => card.VisualId == ClutteredChangesScenario.ActionCardId).Key
                && frame.Hits.Any(hit => hit.Id == source.Hit.Id && hit.Scope == source.Hit.Scope && view.CurrentHit(hit))
                && choice != null && view.CurrentHit(choice) && view.HitAt(choice.Bounds.Center)?.Id == choice.Id
                && frame.Drawing.Commands.Any(command => command switch {
                    DrawCommand.Text text => text.Value == option,
                    DrawCommand.CenterText text => text.Value == option,
                    _ => false
                }),
            "The explicit ability request retains the same source reader and battlefield workspace while exposing the original option through a reachable current input control");
    }
}
