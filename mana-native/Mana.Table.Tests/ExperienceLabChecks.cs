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
        void Until(Func<bool> ready, string failure)
        {
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (!ready()) {
                if (deadline.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException(failure);
                Tick(); Thread.Yield();
            }
        }
        bool CardSettled(string id)
        {
            var entry = frame.Cards.SingleOrDefault(card => card.Id == id && card.Zone == "Battlefield");
            var current = lab.State.Viewer!.Zone("Battlefield").Cards.SingleOrDefault(card => card.VisualId == id);
            if (entry == null || current == null || entry.Card.Key != current.Key
                || frame.Stamp.GameId != lab.State.Id || frame.Stamp.Revision != lab.State.Revision
                || !frame.Hits.Any(hit => hit.Id == entry.Hit.Id && hit.Scope == entry.Hit.Scope && app.TestPort.CurrentHit(hit))) return false;
            var target = entry.Focused ? entry.ReaderTarget ?? entry.Rest : entry.Rest;
            return Vector2.Distance(entry.Display.Center, target.Center) < 2 && Vector2.Distance(entry.Display.Size, target.Size) < 2;
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
        Click("battlefield-focus-close");
        var counterState = ClutteredChangesScenario.Retreat(lab.State);
        app.TestPort.Accept(counterState); Tick();
        var countedFront = frame.Scene!.Battlefield(0).SelectMany(cluster => cluster.Cards).First(card => card.GroupFront
            && Mana.Magic.CardPresentation.HasCombatStats(card.Card) && card.Card.Counters.Any(counter => counter.Value > 0));
        app.TestPort.Accept(counterState with {
            Stack = [new("Public ability preview", "", true, countedFront.Card) { Id = "counter-source-preview", ControllerId = 0 }],
            Decision = null
        }); Tick();
        var boardFace = frame.Cards.Last(card => card.Id == countedFront.Id && card.Zone == "Battlefield");
        var counterMarker = app.TestPort.BattlefieldLabels(countedFront.Id).Single(label => label.Kind == "counter").Bounds;
        var affected = app.TestPort.EffectTransitions.Targets.Where(target => target.AffectedIds.Contains(countedFront.Id)).ToArray();
        check(affected.Length > 0 && !frame.Cards.Any(card => card.Zone == "Stack")
            && app.TestPort.StackRows.Any(row => row.Id == "counter-source-preview")
            && affected.All(target => target.Target.Pose == boardFace.Display && counterMarker.Contains(target.Target.CounterLanding.ToPoint())),
            "A pending effect keeps its own surface without copying its source or stealing the battlefield counter arrival anchor");
        foreach (var scenario in ExperienceScenarios.All) {
            LoadScenario(scenario.Id);
            check(lab.Scenario.Id == scenario.Id && frame.Hits.Any(hit => hit.Id == "lab-reset") && frame.Hits.Any(hit => hit.Id == "lab-scenarios"),
                "The case opens through real input and keeps navigation above its decision modal: " + scenario.Id);
            check(lab.State.Players.Where(player => player.Id != lab.State.ViewerId).All(player => player.Zone("Hand").Cards.Length == 0),
                "Canonical projections contain no other player's private hand faces: " + scenario.Id);
            if (scenario.Id == "public-state.v1") check(frame.Hits.Any(hit => hit.Id == "table-state:day-night"),
                "The lab uses the production Day/Night widget alongside its scenario controls");
            if (scenario.Id == "ability-choice.v1") {
                string game = lab.State.Id;
                var beforeQuery = lab.State;
                var elf = beforeQuery.Viewer!.Zone("Battlefield").Cards.Single(card => card.VisualId == "reader-elf");
                var offered = engine.SourceActionsAsync(game, beforeQuery.Decision!.Id, elf.Key).GetAwaiter().GetResult();
                check(lab.StageIndex == 0 && lab.State.Decision?.Intent == DecisionIntent.Priority
                    && ReferenceEquals(lab.State, beforeQuery) && offered.Actions is [{ Kind: "activate", Text: "{T}: Add {G}." }]
                    && offered.SourceCardKey == elf.Key && !frame.Hits.Any(hit => hit.Id == "choice:0"),
                    "The Elf action read leaves the authored priority/source unchanged and returns its one explicit mana ability");
                Until(() => CardSettled("reader-elf"), "The current Elf must finish entering before its initial card click");
                Click(frame.Cards.Single(card => card.Id == "reader-elf" && card.Zone == "Battlefield").Hit.Id);
                Until(() => frame.Hits.Any(hit => hit.Id == "read-card-action"), "The Elf reader did not expose its explicit action");
                CheckUncoveredReader("reader-elf", ["read-card-action"]);
                Click("read-card-action");
                Until(() => !app.TestPort.Busy && lab.StageIndex == 1 && frame.Stamp.Revision == lab.State.Revision,
                    "The direct on-card mana action did not publish its authored result");
                check(lab.State.Id == game && lab.State.Viewer is { } viewer
                    && viewer.Zone("Battlefield").Cards.Single(card => card.VisualId == "reader-elf").Tapped
                    && viewer.Mana.GetValueOrDefault("G") == 1 && lab.State.Decision?.Context != "playAbility"
                    && !frame.Hits.Any(hit => hit.Id is "choice:0" or "choicesdone")
                    && engine.SourceActionsAsync(game, lab.State.Decision!.Id,
                        viewer.Zone("Battlefield").Cards.Single(card => card.VisualId == "reader-elf").Key).GetAwaiter().GetResult().Actions.Length == 0,
                    "The real on-card input reaches the authored tapped Elf and green mana directly, without a generic chooser or another action for the tapped source");
            }
            if (scenario.Id == "ability-targeting.v1") {
                var parent = lab.State;
                var ballista = parent.Viewer!.Zone("Battlefield").Cards.Single(card => card.VisualId == ExperienceScenarios.BallistaId);
                var actions = engine.SourceActionsAsync(parent.Id, parent.Decision!.Id, ballista.Key).GetAwaiter().GetResult();
                check(ReferenceEquals(lab.State, parent) && actions.Actions.Length == 2
                    && actions.Actions[0].Text == "{4}: Put a +1/+1 counter on Walking Ballista."
                    && actions.Actions[1].Text == "Remove a +1/+1 counter from Walking Ballista: It deals 1 damage to any target."
                    && ballista is { Power: 3, Toughness: 3 } && ballista.Counters.GetValueOrDefault("+1/+1") == 3,
                    "Walking Ballista's nonmutating read exposes two distinct authored costs/effects and its current three-counter 3/3 source");
                void OpenDamageAction()
                {
                    Until(() => CardSettled(ExperienceScenarios.BallistaId), "Walking Ballista must settle before reading its source");
                    var current = lab.State.Viewer!.Zone("Battlefield").Cards.Single(card => card.VisualId == ExperienceScenarios.BallistaId);
                    var currentActions = engine.SourceActionsAsync(lab.State.Id, lab.State.Decision!.Id, current.Key).GetAwaiter().GetResult();
                    string damageHit = "source-action:" + currentActions.Actions[1].Id;
                    Click(frame.Cards.Single(card => card.Id == ExperienceScenarios.BallistaId && card.Zone == "Battlefield").Hit.Id);
                    Until(() => frame.Hits.Any(hit => hit.Id == "read-card-action") && frame.Hits.Any(hit => hit.Id == damageHit),
                        "Both original Ballista actions must be available beside its uninterrupted reader together");
                    CheckUncoveredReader(ExperienceScenarios.BallistaId, ["read-card-action", damageHit]);
                    Click(damageHit);
                    Until(() => !app.TestPort.Busy && lab.State.Decision is { Intent: DecisionIntent.Selection, CancelEnabled: true }
                        && frame.Stamp.Revision == lab.State.Revision, "The damage action must enter its authored target decision");
                }
                OpenDamageAction();
                check(lab.State.Viewer!.Mana.GetValueOrDefault("C") == 4 && lab.State.Stack.Length == 0
                    && lab.State.Viewer.Zone("Battlefield").Cards.Single(card => card.VisualId == ExperienceScenarios.BallistaId).Counters.GetValueOrDefault("+1/+1") == 3
                    && lab.State.Players.SelectMany(player => player.Zone("Battlefield").Cards).All(card => card.Selectable)
                    && lab.State.Decision!.PlayerChoices.SequenceEqual([0, 1, 2, 3]),
                    "The mixed target decision exposes actual card/player handles and leaves the counter cost unpaid until a target is committed");
                Click("cancel");
                Until(() => !app.TestPort.Busy && lab.State.Decision?.Intent == DecisionIntent.Priority
                    && frame.Stamp.Revision == lab.State.Revision, "Target Cancel must restore the parent action window");
                check(lab.State.Id == parent.Id && lab.StageIndex == 0 && lab.State.Stack.Length == 0
                    && lab.State.Viewer!.Mana.GetValueOrDefault("C") == 4
                    && lab.State.Viewer.Zone("Battlefield").Cards.Single(card => card.VisualId == ExperienceScenarios.BallistaId) is { Power: 3, Toughness: 3 } restored
                    && restored.Counters.GetValueOrDefault("+1/+1") == 3 && lab.State.Players.All(player => player.Life == 40),
                    "Cancelling target selection restores the same source, mana, counters and life without inventing an activation");
                OpenDamageAction();
                Click("player:1");
                Until(() => !app.TestPort.Busy && lab.StageIndex == 1 && frame.Stamp.Revision == lab.State.Revision,
                    "A committed target must publish the cost and pending stack ability");
                check(lab.State.Stack is [{ Targets: [{ Kind: "player", PlayerId: 1 }] }]
                    && lab.State.Players.Single(player => player.Id == 1).Life == 40
                    && lab.State.Viewer!.Mana.GetValueOrDefault("C") == 4
                    && lab.State.Viewer.Zone("Battlefield").Cards.Single(card => card.VisualId == ExperienceScenarios.BallistaId) is { Power: 2, Toughness: 2 } paid
                    && paid.Counters.GetValueOrDefault("+1/+1") == 2,
                    "Committing the player target pays exactly one counter and puts the damage ability on the stack without dealing damage early");
                var pending = lab.State.Stack.Single();
                var effectRow = app.TestPort.StackRows.Single();
                string effectText = string.Join(" ", frame.Observe().Text.Where(text => effectRow.Bounds.Contains(new Point(text.Bounds.X, text.Bounds.Y)))
                    .Select(text => text.Value));
                check(effectRow.Id == pending.Id && effectText.Contains(pending.Text, StringComparison.Ordinal)
                    && effectText.Contains(pending.Name, StringComparison.Ordinal) && effectText.Contains("You", StringComparison.Ordinal)
                    && pending.Targets.All(target => effectText.Contains(target.Name, StringComparison.Ordinal))
                    && frame.Hits.Count(hit => hit.Id == "stack-source:" + pending.Id) == 1
                    && !frame.Hits.Any(hit => hit.Id == "stack-row:" + pending.Id)
                    && !frame.Hits.Any(hit => hit.Id is "stack" or "action-details") && !frame.Cards.Any(card => card.Zone == "Stack"),
                    "The single Ballista effect exposes its damage, controller and player target with specific references instead of a whole-box action or copied source card");
                Until(() => CardSettled(ExperienceScenarios.BallistaId), "The current Ballista must settle before checking its read-only source preview");
                var unanswered = lab.State;
                var sourcePose = frame.Cards.Single(card => card.Id == ExperienceScenarios.BallistaId && card.Zone == "Battlefield").Display;
                var sourceRest = frame.Cards.Single(card => card.Id == ExperienceScenarios.BallistaId && card.Zone == "Battlefield").Rest;
                var sourceReference = frame.Hits.Single(hit => hit.Id == "stack-source:" + pending.Id);
                Tick(sourceReference.Bounds.Center); Tick(sourceReference.Bounds.Center);
                var afterHover = frame.Cards.Single(card => card.Id == ExperienceScenarios.BallistaId && card.Zone == "Battlefield");
                bool sourceHoverUnchanged = ReferenceEquals(lab.State, unanswered) && app.TestPort.Overlay.Length == 0 && !app.TestPort.Busy
                    && app.TestPort.ReferencePreviewCardId == ExperienceScenarios.BallistaId && app.TestPort.ReferencePreviewBounds is { } preview
                    && !preview.Intersects(effectRow.Bounds) && !preview.Intersects(app.TestPort.DecisionPanelBounds)
                    && afterHover.Rest == sourceRest && !afterHover.Focused
                    && Vector2.Distance(afterHover.Display.Center, sourceRest.Center) < 2 && Vector2.Distance(afterHover.Display.Size, sourceRest.Size) < 2
                    && frame.Hits.All(hit => hit.Id != "stack-card-preview" || hit.Card == null);
                string sourceHoverMessage = "Hovering the effect's source previews its actual current card clear of the effect/dock without changing the battlefield rest pose, opening a reader/overlay or answering the decision";
                if (!sourceHoverUnchanged) sourceHoverMessage += ". Observed: " + System.Text.Json.JsonSerializer.Serialize(new {
                        stateSame = ReferenceEquals(lab.State, unanswered), app.TestPort.Overlay, app.TestPort.Busy,
                        app.TestPort.ReferencePreviewCardId, app.TestPort.ReferencePreviewBounds, effectRow.Bounds, app.TestPort.DecisionPanelBounds,
                        sourcePose, sourceRest, currentPose = afterHover.Display, currentRest = afterHover.Rest, afterHover.Focused,
                        previewHasCard = frame.Hits.Any(hit => hit.Id == "stack-card-preview" && hit.Card != null)
                    }, new System.Text.Json.JsonSerializerOptions { IncludeFields = true });
                check(sourceHoverUnchanged, sourceHoverMessage);
                var targetReference = frame.Hits.Single(hit => hit.Id == "stack-target:" + pending.Id + ":player:1");
                Tick(targetReference.Bounds.Center); Tick(targetReference.Bounds.Center);
                check(ReferenceEquals(lab.State, unanswered) && app.TestPort.Overlay.Length == 0 && !app.TestPort.Busy
                    && app.TestPort.ReferencePreviewCardId == null,
                    "Hovering the reported player target leaves the same unanswered effect and does not invent a card preview for a player");
                Click("confirm");
                Until(() => !app.TestPort.Busy && lab.StageIndex == 2 && frame.Stamp.Revision == lab.State.Revision,
                    "Passing priority must expose the next authored responder");
                check(lab.State.PriorityPlayerId == 1 && lab.State.Decision == null && lab.State.Stack.Length == 1
                    && lab.State.Players.Single(player => player.Id == 1).Life == 40,
                    "Passing local priority preserves the pending effect and exposes the other responder rather than silently resolving it");
                Click("lab-next");
                check(lab.StageIndex == 3 && lab.State.Stack.Length == 0 && lab.State.Players.Single(player => player.Id == 1).Life == 39
                    && lab.State.Viewer!.Zone("Battlefield").Cards.Single(card => card.VisualId == ExperienceScenarios.BallistaId).Counters.GetValueOrDefault("+1/+1") == 2,
                    "The explicit authored continuation shows one damage to the chosen player and retains the already-paid counter cost");
            }
            if (scenario.Id == "card-reading.v1") {
                Click("lab-next"); Click("lab-next");
                check(frame.Observe().Text.Any(text => text.Value == "3/3")
                    && frame.Observe().Cards.Single(card => card.Id == "reader-elf" && card.Zone == "Battlefield").Counters.Single().Value == 2,
                    "The actual ordinary renderer displays the authoritative 3/3 body and retains its two-counter projection");
                Click("lab-next");
                var sourceOccurrence = frame.Cards.Single(card => card.Id == "reader-elf" && card.Zone == "Battlefield");
                var sourceHit = frame.Hits.Single(hit => hit.Id == sourceOccurrence.Hit.Id && hit.Scope == sourceOccurrence.Hit.Scope);
                var choiceText = frame.Observe().Text;
                check(sourceOccurrence.Card is { Power: 6, Toughness: 6 } source && source.Counters.GetValueOrDefault("+1/+1") == 2
                    && choiceText.Any(text => text.Value == "6/6" && sourceHit.Bounds.Contains(new Point(text.Bounds.X + text.Bounds.Width / 2, text.Bounds.Y + text.Bounds.Height / 2)))
                    && choiceText.Any(text => text.Value == "{T}: Add {G}.")
                    && frame.Hits.Any(hit => hit.Id == "choice-cancel") && !frame.Hits.Any(hit => hit.Id is "close-overlay" or "choicesfilter" or "choicesdone"),
                    "Inline ability actions retain the actual current 6/6 battlefield source and two counters despite the older embedded prompt face");
            }
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

        void CheckUncoveredReader(string sourceId, string[] actionIds)
        {
            var source = frame.Cards.Single(card => card.Id == sourceId && card.Focused);
            var actions = frame.Hits.Where(hit => actionIds.Contains(hit.Id)).ToArray();
            check(Vector2.Distance(source.Display.Size, new Vector2(410, 574)) < 2 && actions.Length == actionIds.Length
                && actions.All(hit => !hit.Bounds.Intersects(source.Display.Bounds))
                && !frame.Hits.Any(hit => hit.Id is "source-reader-next" or "source-reader-prev"),
                "The original full-size card keeps its whole reading surface while its concrete actions remain simultaneously reachable outside the face without reader pages: " + sourceId);
        }
    }
}
