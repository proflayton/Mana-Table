using Mana.Contracts;
using Mana.Scenarios;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

internal static class ExperienceLabChecks
{
    internal static void Run(Action<bool, string> check)
    {
        check(ExperienceScenarios.All.Count > 0 && ExperienceScenarios.All.Select(item => item.Id).Distinct().Count() == ExperienceScenarios.All.Count
            && ExperienceScenarios.All.All(item => item.Id.EndsWith(".v1", StringComparison.Ordinal)),
            "Named, versioned canonical experiences are shared by the production lab and scoped composition/input checks");
        var lab = new ExperienceLabSession();
        check(lab.State.Id != new ExperienceLabSession().State.Id,
            "Independent lab sessions have distinct game namespaces before any stage navigation");
        var engine = new ExperienceLabEngine(lab);
        string profile = Path.Combine(Path.GetTempPath(), "ManaTableExperienceLabChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        using var app = new TableApplication(engine, profile, initialState: lab.State) { Automated = true };
        app.Initialize();
        PresentationFrame frame = null!;
        double seconds = 0;
        void Tick(Point point = default, ButtonState left = ButtonState.Released)
        {
            seconds += .05;
            var time = new GameTime(TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(.05));
            app.Update(time, new MouseState(point.X, point.Y, 0, left, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released),
                new KeyboardState(), new Point(1600, 900));
            frame = app.ComposePresentation(time);
        }
        void Click(string id)
        {
            var hit = frame.Hits.FirstOrDefault(item => item.Id == id)
                ?? throw new InvalidOperationException("The production lab did not expose " + id);
            var point = hit.Bounds.Center;
            Tick(point, ButtonState.Pressed); Tick(point);
        }
        void LoadScenario(string id)
        {
            Click("lab-scenarios");
            int target = ExperienceScenarios.All.ToList().FindIndex(item => item.Id == id);
            for (int attempts = 0; !frame.Hits.Any(hit => hit.Id == "lab-load:" + id); attempts++) {
                if (attempts >= ExperienceScenarios.All.Count) throw new InvalidOperationException("Lab case is unreachable: " + id);
                int first = ExperienceScenarios.All.ToList().FindIndex(item => frame.Hits.Any(hit => hit.Id == "lab-load:" + item.Id));
                Click(target < first ? "lab-gallery-prev" : "lab-gallery-next");
            }
            Click("lab-load:" + id);
        }
        Tick();
        check(frame.Cards.Count(card => card.Zone == "Battlefield") == 480 && !app.TestPort.Polling && !app.TestPort.StartupVisible,
            "The actual GPU-free app opens all 480 shared permanents immediately without Forge startup or polling");
        string initialGame = lab.State.Id;
        app.TestPort.OpenBattlefield(0); Tick();
        Click("lab-next");
        check(lab.StageIndex == 1 && lab.State.Id == initialGame && app.TestPort.PublicChanges is { Count: 1 }
            && app.TestPort.PublicChanges[0].Cause?.Name == ClutteredChangesScenario.TophName && app.TestPort.ZoneName == "Battlefield",
            "Actual Next input preserves the response epoch and presents the reported Toph transition");
        Click("change-locate");
        check(app.TestPort.ZoneName == "Battlefield" && app.TestPort.EffectTransitions.Targets
                .SelectMany(target => target.AffectedIds).Contains(ClutteredChangesScenario.AnimatedLandId),
            "The source cue's Show cards input brings its current affected public occurrence into focus without a game action");
        Click("lab-next");
        check(lab.StageIndex == 2 && app.TestPort.PublicChanges.Last().Changes.Count == 82
            && app.TestPort.PublicChanges.Last().Cause?.Name == ClutteredChangesScenario.RetreatName,
            "Actual Next input uses the same shared Felidar data and retains every one of its 82 affected creatures");
        Click("lab-previous");
        check(lab.StageIndex == 1 && lab.State.Id != initialGame && app.TestPort.PublicChanges is { Count: 1 }
            && app.TestPort.PublicChanges[0].Cause?.Name == ClutteredChangesScenario.TophName,
            "Previous replays from a fresh baseline and clears the later Felidar effect without losing Toph's cause");
        string priorReset = lab.State.Id;
        Click("lab-reset");
        check(lab.State.Id != priorReset && app.TestPort.PublicChanges is { Count: 1 },
            "Reset creates a fresh game epoch and makes the authored current-stage transition inspectable again");
        foreach (var scenario in ExperienceScenarios.All) {
            LoadScenario(scenario.Id);
            check(lab.Scenario.Id == scenario.Id && frame.Hits.Any(hit => hit.Id == "lab-reset") && frame.Hits.Any(hit => hit.Id == "lab-scenarios"),
                "The case opens through real input and keeps navigation above its decision modal: " + scenario.Id);
            check(lab.State.Players.Where(player => player.Id != lab.State.ViewerId).All(player => player.Zone("Hand").Cards.Length == 0),
                "Canonical projections contain no other player's private hand faces: " + scenario.Id);
            if (scenario.Id == "public-state.v1") check(frame.Hits.Any(hit => hit.Id == "table-state:day-night"),
                "The lab uses the production Day/Night widget alongside its scenario controls");
            if (scenario.Id == "turn-orientation.v1") {
                string initialPrompt = lab.State.Decision!.Id, initialScope = lab.State.Id;
                Click("lab-follow-turn");
                check(app.TestPort.FollowTurnEnabled && lab.StageIndex == 0 && lab.State.Id == initialScope && lab.State.Decision?.Id == initialPrompt,
                    "The actual Follow turn button changes presentation intent without answering the pending game decision");
                Click("lab-next");
                for (int settle = 0; settle < 20; settle++) Tick();
                check(app.TestPort.BoardView.IsIdentity && app.TestPort.BoardViewLimitedByLayout
                    && frame.BoardView == app.TestPort.BoardView && lab.StageIndex == 1
                    && frame.Observe().Text.Any(text => text.Value == "Follow: limited space"),
                    "The current ordinary four-seat layout refuses a pan through fixed controls and publishes its actual stationary view");
                Click("lab-whole-table");
                check(!app.TestPort.FollowTurnEnabled && app.TestPort.BoardView.IsIdentity && lab.StageIndex == 1,
                    "Whole table explicitly restores neutral framing without advancing the authored game");
            }
        }
        LoadScenario("trigger-search.v1");
        // Select the required-choice scenario explicitly. Its private
        // library uses prompt indices, then issues a new visible hand occurrence.
        lab.Reply(new(lab.State.Id, lab.State.Decision!.Id, ReplyAction.Confirm));
        check(lab.State.PriorityPlayerId == 1 && lab.State.DecisionOwnerId == 1 && lab.State.Decision == null
            && lab.State.Stack.Length == 1 && lab.State.Players.Single(player => player.Priority).Id == 1,
            "Passing this viewer's priority retains the pending trigger and exposes only the next responder's public identity");
        lab.AdvanceTo(2);
        lab.Reply(new(lab.State.Id, lab.State.Decision!.Id, ReplyAction.SelectPlayer, Player: 0));
        check(lab.State.Decision is { Context: "librarySearch", LibraryCards.Length: 3 }
            && lab.State.Decision.LibraryCards.All(choice => choice.Card.VisualId.Length == 0 && choice.Card.CombatId.Length == 0),
            "Required recipient selection opens the real library-choice shape without persistent hidden-library identities");
        lab.Reply(new(lab.State.Id, lab.State.Decision!.Id, ReplyAction.Choose, Choices: [1]));
        check(lab.StageIndex == 4 && lab.State.Viewer!.Zone("Hand").Cards.Single() is { Name: "Island", VisualId: "searched-card" },
            "The scoped library choice preserves the selected face in the authored local hand result");
        bool refusedWrite = false;
        try { engine.CreateDeckAsync("Never write real decks").GetAwaiter().GetResult(); }
        catch (EngineCommandRejectedException) { refusedWrite = true; }
        check(refusedWrite, "The lab connection explicitly refuses real deck writes");
    }
}
