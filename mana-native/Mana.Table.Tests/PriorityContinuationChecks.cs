using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Mana.Table.Interaction;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

// These are actual production input journeys over an authored connection. The
// Java quiet-yield checks separately establish the underlying rules authority.
internal static class PriorityContinuationChecks
{
    internal static void Run(string profile, Action<bool, string> check)
    {
        PriorityAcknowledgmentChecks.Run(check);
        foreach (var viewport in new[] { new Point(1000, 740), new Point(1540, 980) })
            foreach (bool ownTurn in new[] { false, true }) {
                CheckContinuation(profile, viewport, ownTurn, check);
                CheckTakeControl(profile, viewport, ownTurn, check);
            }
        foreach (var viewport in new[] { new Point(1000, 740), new Point(1540, 980) }) {
            CheckAutomatic(profile, viewport, false, check);
            CheckAutomatic(profile, viewport, true, check);
            CheckAcknowledgedResolution(profile, viewport, check);
        }
        CheckNextTurn(profile, null, check);
        CheckNextTurn(profile, StackActionKind.Spell, check);
        CheckNextTurn(profile, StackActionKind.TriggeredAbility, check);
        CheckNoUndo(profile, check);
    }

    private static void CheckAcknowledgedResolution(string profile, Point viewport, Action<bool, string> check)
    {
        using var run = new Journey(profile, "acknowledged-resolution", viewport, false, holdInitially: false);
        run.Until(() => run.Replies.Count == 1, "Auto initially waits through routine opponent priority");
        run.Engine.Present(PriorityAcknowledgmentChecks.Stack(run.Engine.State));
        string response = run.Engine.State.Decision!.Id; run.Release(0);
        run.Until(() => !run.View.Busy && run.View.Match?.Decision?.Id == response, "The real response interrupts waiting");
        run.Settle();
        check(run.Replies.Count == 1 && run.Has("confirm") && CanPlayInstant(run.View.Match),
            "A cast pauses Auto before the player passes its actual nonempty-stack response");
        run.Click("confirm");
        run.Until(() => run.Replies.Count == 2, "The physical Pass once gesture answers the observed stack response");
        check(run.Replies.Last() is PassPriority { DecisionId: var id } && id == response,
            "The acknowledgment is the accepted current PassPriority command while that exact effect is still pending");
        run.Engine.Present(PriorityAcknowledgmentChecks.Resolve(run.Engine.State)); run.Release(1);
        run.Until(() => run.Replies.Count == 3, "Ordinary correlated resolution resumes quiet waiting without another click");
        check(run.Replies.Last() is PassForNow { YieldToken: "after-accepted-resolution" }
                && run.View.Match?.Stack.IsEmpty == true && run.Has("quiet-priority-stop") && CanPlayInstant(run.View.Match),
            "After one accepted stack response, Auto uses only the fresh empty-window permission while the instant remains available");
        run.Engine.Present(PriorityAcknowledgmentChecks.Stack(run.Engine.State, "new-trigger", ExecutionKind.Trigger, "accepted-action"));
        string trigger = run.Engine.State.Decision!.Id; run.Release(2);
        run.Until(() => !run.View.Busy && run.View.Match?.Decision?.Id == trigger, "The next triggered action interrupts the resumed wait");
        run.Settle();
        check(run.Replies.Count == 3 && run.Has("confirm") && !run.Has("quiet-priority-stop") && CanPlayInstant(run.View.Match),
            "A later child trigger gets its own response and cannot inherit the player's earlier acknowledgment");
        CheckNoTurnYield(run, check);
    }

    private static void CheckContinuation(string profile, Point viewport, bool ownTurn, Action<bool, string> check)
    {
        using var run = new Journey(profile, "wait-until-action", viewport, ownTurn);
        check(run.View.Automatic && run.View.Held && run.Has("quiet-priority-arm") && run.Has("confirm")
                && run.Text.Contains("Wait for action") && (ownTurn || run.Text.Contains("Pass once")),
            "Own and opponent held priority offer a readable bounded wait and an independent single-pass gesture");
        CheckNoTurnYield(run, check);
        var original = run.Engine.State;
        var armBounds = run.Frame.Hits.Single(hit => hit.Id == "quiet-priority-arm").Bounds;
        run.Click("quiet-priority-arm");
        run.Until(() => run.Replies.Count == 1, "The first bounded pass starts through physical input");
        check(run.Replies.Single() is PassForNow pass && pass.YieldToken == "unchanged-public-permission"
                && pass.DecisionId == original.Decision!.Id && pass.GameId == original.Id
                && !run.View.Held
                && run.Has("quiet-priority-stop") && run.Text.Contains("Take control")
                && run.Frame.Hits.Single(hit => hit.Id == "quiet-priority-stop").Bounds == armBounds,
            "Deliberate waiting overrides Hold, submits one current token-authorized pass and preserves its cancel position");

        // A new prompt in another empty phase has distinct decision authority,
        // while the original unchanged-public-state permission remains bounded.
        run.Engine.Present(original with { Phase = "Second main phase", PhaseKey = "MAIN2" });
        string nextDecision = run.Engine.State.Decision!.Id;
        run.Release(0);
        run.Until(() => run.Replies.Count == 2, "An unchanged empty priority window continues without another click");
        var passes = run.Replies.OfType<PassForNow>().ToArray();
        check(passes.Length == 2 && passes[1].DecisionId == nextDecision && passes[0].DecisionId != passes[1].DecisionId
                && passes.All(pass => pass.YieldToken == "unchanged-public-permission"),
            "Own and opponent continuation use fresh scoped decisions with the original permission rather than engine turn-yield");

        run.Engine.Present(Response(run.Engine.State));
        string responseDecision = run.Engine.State.Decision!.Id;
        run.Release(1);
        run.Until(() => !run.View.Busy && run.View.Match?.Decision?.Id == responseDecision,
            "The submitted pass reconciles a new public spell before restoring the response");
        run.Settle();
        check(run.Replies.Count == 2 && !run.Has("quiet-priority-stop") && run.Has("confirm")
                && run.View.Match?.Decision?.Id == responseDecision && run.Engine.State.Decision?.Id == responseDecision
                && CanPlayInstant(run.View.Match),
            "A new stack object stops waiting and leaves the actual playable response unanswered with Auto enabled");
        CheckNoTurnYield(run, check);

        // Settling the spell must not quietly renew the earlier consent just
        // because the provider can offer another empty-window permission.
        var fresh = run.Engine.State with { Stack = [], StackInterruptionVersion = 2,
            Decision = run.Engine.State.Decision! with { Payload = ((PriorityPayload)run.Engine.State.Decision.Payload)
                with { QuietYieldToken = "new-public-permission" } } };
        run.Present(fresh); run.Settle();
        check(run.Replies.Count == 2 && run.Has("quiet-priority-arm") && !run.Has("quiet-priority-stop")
                && CanPlayInstant(run.View.Match),
            "A later empty window offers a fresh wait gesture without silently rearming the interrupted intent");
    }

    private static void CheckAutomatic(string profile, Point viewport, bool trigger, Action<bool, string> check)
    {
        using var run = new Journey(profile, trigger ? "auto-trigger-attention" : "auto-cast-attention", viewport, false, holdInitially: false);
        run.Until(() => run.Replies.Count == 1, "Default Auto passes routine opponent priority despite an available instant");
        check(run.Replies.Single() is PassForNow { YieldToken: "unchanged-public-permission" }
                && run.View.Automatic && !run.View.Held && run.Has("quiet-priority-stop")
                && CanPlayInstant(run.View.Match),
            "Automatic routine passing uses a bounded original engine permission instead of making the player click for every instant window");
        run.Engine.Present(run.Engine.State with { Phase = "Second main phase", PhaseKey = "MAIN2" });
        run.Release(0);
        run.Until(() => run.Replies.Count == 2, "Default Auto continues an unchanged public situation without another click");
        var response = Response(run.Engine.State);
        if (trigger) response = response with { Stack = response.Stack.Select(item => item with { Kind = StackActionKind.TriggeredAbility }).ToImmutableArray() };
        run.Engine.Present(response); string responsePrompt = run.Engine.State.Decision!.Id;
        run.Release(1);
        run.Until(() => !run.View.Busy && run.View.Match?.Decision?.Id == responsePrompt,
            "The new spell or trigger interrupts default automatic continuation");
        run.Settle();
        check(run.Replies.Count == 2 && run.View.Automatic && !run.View.Held && run.Has("confirm")
                && !run.Has("quiet-priority-stop") && CanPlayInstant(run.View.Match),
            "Auto returns a real spell or trigger response to the player without answering it or switching off their saved setting");

        // A later observation can omit the just-resolved stack and advertise a
        // fresh permission. That is not the player's acknowledgment of it.
        run.Present(run.Engine.State with { Stack = [], Decision = run.Engine.State.Decision! with {
            Payload = ((PriorityPayload)run.Engine.State.Decision.Payload) with { QuietYieldToken = "after-public-action" } } });
        run.Settle();
        check(run.Replies.Count == 2 && run.Has("confirm") && !run.Has("quiet-priority-stop"),
            "A changed prompt or fresh token after the stack disappears cannot silently clear the action-attention stop");

        string acknowledgedPrompt = run.Engine.State.Decision!.Id;
        run.Click("confirm");
        run.Until(() => run.Replies.Count == 3, "The player explicitly acknowledges the current window with Pass once");
        check(run.Replies.Last() is PassPriority { DecisionId: var decision } && decision == acknowledgedPrompt,
            "Pass once acknowledges exactly its own current response without enabling engine turn-yield");
        if (trigger) {
            run.Engine.Present(Response(run.Engine.State) with { StackInterruptionVersion = 2 });
            string newPrompt = run.Engine.State.Decision!.Id;
            run.Release(2);
            run.Until(() => !run.View.Busy && run.View.Match?.Decision?.Id == newPrompt,
                "A second action during the acknowledged pass is reconciled");
            run.Settle();
            check(run.Replies.Count == 3 && run.Has("confirm") && !run.Has("quiet-priority-stop")
                    && run.View.Match?.Decision?.Id == newPrompt && CanPlayInstant(run.View.Match),
                "Acknowledging one action cannot acknowledge a newer action arriving while the pass is in flight");
        } else {
            run.Engine.Present(run.Engine.State);
            run.Release(2);
            run.Until(() => run.Replies.Count == 4, "Routine automatic continuation resumes after the explicit acknowledgment");
            check(run.Replies.Last() is PassForNow { YieldToken: "after-public-action" }
                    && run.Has("quiet-priority-stop"),
                "After Pass once, Auto can use the new unchanged-public-state permission without asking the player to reconfigure modes");
        }
        CheckNoTurnYield(run, check);
    }

    private static void CheckTakeControl(string profile, Point viewport, bool ownTurn, Action<bool, string> check)
    {
        using var run = new Journey(profile, "take-control-in-flight", viewport, ownTurn);
        run.Click("quiet-priority-arm");
        run.Until(() => run.Replies.Count == 1, "The cancellation journey holds one pass in flight");
        run.Click("quiet-priority-stop");
        check(run.View.Automatic && run.View.Held && run.View.Busy && !run.Has("quiet-priority-stop"),
            "Take control holds the current turn without changing saved Auto, even while a pass is in flight");
        run.Engine.Present(Response(run.Engine.State));
        string prompt = run.Engine.State.Decision!.Id;
        run.Release(0);
        run.Until(() => !run.View.Busy && run.View.Match?.Decision?.Id == prompt,
            "Take control reconciles the result of its already submitted pass");
        run.Settle();
        check(run.Replies.Count == 1 && run.View.Held && run.Has("confirm")
                && run.View.Match?.Decision?.Id == prompt && CanPlayInstant(run.View.Match),
            "The newly playable response survives cancellation and remains available through its presentation beat");

        var noResponse = run.Engine.State with { Stack = [], StackInterruptionVersion = 2,
            Decision = run.Engine.State.Decision! with { Payload = ((PriorityPayload)run.Engine.State.Decision.Payload)
                with { CanAutoPass = true, QuietYieldToken = "fresh-after-take-control" } },
            Players = run.Engine.State.Players.Select(player => player.Id != run.Engine.State.ViewerId ? player : player with {
                Zones = player.Zones.Select(zone => zone.Name != "Hand" ? zone : zone with { Count = 0, Cards = [] }).ToImmutableArray()
            }).ToImmutableArray() };
        run.Present(noResponse); string controlledPrompt = run.Engine.State.Decision!.Id; run.Settle();
        check(run.Replies.Count == 1 && run.View.Automatic && run.View.Held && run.Has("confirm")
                && run.View.Match?.Decision?.Id == controlledPrompt,
            "A fresh engine-authorized automatic window cannot undo the player's explicit Take control");
        CheckNoTurnYield(run, check);
    }

    private static void CheckNextTurn(string profile, StackActionKind? actionKind, Action<bool, string> check)
    {
        using var run = new Journey(profile, "automatic-next-turn-" + (actionKind?.ToString() ?? "quiet"),
            new(1000, 740), false, holdInitially: false);
        run.Until(() => run.Replies.Count == 1, "Automatic continuation starts before the opponent's turn changes");
        var original = run.Engine.State;
        var next = original with { Turn = original.Turn + 1, ActivePlayerId = 2, Phase = "Upkeep", PhaseKey = "UPKEEP",
            Activity = [new(1, original.Turn + 1, "UPKEEP", "turn", 2, "Opponent 2 started a turn.", null, null) {
                InterruptsQuietYield = false }],
            Decision = original.Decision! with { Payload = ((PriorityPayload)original.Decision.Payload)
                with { QuietYieldToken = "new-turn-public-permission" } }
        };
        if (actionKind is { } kind) {
            next = Response(next) with { Stack = [new("New turn action", "A real action started with the next turn.", kind, null) {
                Id = "new-turn-action", ControllerId = 2 }],
                Activity = [.. next.Activity, new(2, next.Turn, "UPKEEP", kind == StackActionKind.Spell ? "cast" : "ability",
                    2, "Opponent 2 added an action to the stack.", null, null) { InterruptsQuietYield = true }]
            };
        }
        run.Engine.Present(next); string prompt = run.Engine.State.Decision!.Id;
        run.Release(0);
        if (actionKind == null) {
            run.Until(() => run.Replies.Count == 2, "Auto resumes on the next quiet opponent turn without another player click");
            check(run.Replies.Last() is PassForNow { YieldToken: "new-turn-public-permission", DecisionId: var decision }
                    && decision == prompt && run.Has("quiet-priority-stop") && run.View.Automatic && !run.View.Held,
                "A provider-classified routine turn marker ends the old turn-scoped intent without demanding a response on the next quiet turn");
        } else {
            run.Until(() => !run.View.Busy && run.View.Match?.Decision?.Id == prompt,
                "A real action sharing the next-turn observation is reconciled without another pass");
            run.Settle();
            check(run.Replies.Count == 1 && run.View.Match?.Turn == next.Turn && run.Has("confirm")
                    && !run.Has("quiet-priority-stop") && CanPlayInstant(run.View.Match),
                "Resetting attention for a new turn preserves an actual spell or trigger reported in that same observation");
        }
        CheckNoTurnYield(run, check);
    }

    private static void CheckNoUndo(string profile, Action<bool, string> check)
    {
        using var run = new Journey(profile, "legacy-game-undo-unavailable", new(1000, 740), true);
        var state = run.Engine.State;
        run.Present(state with { Decision = state.Decision! with { Payload = ((PriorityPayload)state.Decision.Payload)
            with { Alternative = new(PriorityAlternative.UndoLastAction, new("Undo", true)) } } });
        string prompt = run.Engine.State.Decision!.Id;
        run.Settle();
        check(!run.Has("cancel") && !run.Text.Contains("Undo") && run.Has("confirm") && run.Has("quiet-priority-arm")
                && run.Frame.Hits.All(hit => hit.Intent is not MatchIntent.CancelDecision
                    && hit.Intent is not MatchIntent.SubmitCommand { Command: UndoLastAction })
                && run.Replies.IsEmpty && run.View.Match?.Decision?.Id == prompt,
            "A legacy engine Undo offer creates no game-action Undo control or command while phase advance and safe waiting remain available");
    }

    private static void CheckNoTurnYield(Journey run, Action<bool, string> check) => check(
        !run.Text.Contains("Pass turn") && !run.Text.Contains("Pass for now")
            && run.Frame.Hits.All(hit => hit.Intent is not MatchIntent.SubmitCommand { Command: PassTurn }
                && hit.Intent is not MatchIntent.CancelDecision)
            && run.Replies.All(reply => reply is not PassTurn),
        "The normal priority surface exposes no engine turn-yield or ambiguous old passing label");

    private static bool CanPlayInstant(GameSnapshot? state) => state?.Decision is { } decision
        && state.Viewer?.Zone("Hand").Cards.SingleOrDefault() is { Name: "Lightning Bolt", Selectable: true } instant
        && GameCommandPermissions.Allows(state, new PlayCard(state.Id, decision.Id, instant.Key));

    private static GameSnapshot Response(GameSnapshot state) => state with {
        Stack = [new("Opponent spell", "A player cast a spell.", false, null) { Id = "new-response-spell", ControllerId = 2 }],
        StackInterruptionVersion = 1,
        Decision = state.Decision! with { Payload = ((PriorityPayload)state.Decision.Payload)
            with { QuietYieldToken = "", CanAutoPass = false } }
    };

    private sealed class Journey : IDisposable
    {
        private readonly TableApplication app;
        private readonly Point viewport;
        private readonly ConcurrentQueue<TaskCompletionSource> releases = new();
        private double seconds;
        internal InteractionScenarioEngine Engine { get; } = new();
        internal ConcurrentQueue<GameCommand> Replies { get; } = new();
        internal TableApplication.Probe View => app.TestPort;
        internal PresentationFrame Frame { get; private set; } = null!;
        internal IEnumerable<string> Text => Frame.Drawing.Commands.Select(command => command switch {
            DrawCommand.Text text => text.Value, DrawCommand.CenterText text => text.Value, _ => null }).OfType<string>();
        internal Journey(string profile, string name, Point viewport, bool ownTurn, bool holdInitially = true)
        {
            this.viewport = viewport;
            var path = Path.Combine(profile, $"{name}-{viewport.X}x{viewport.Y}-{(ownTurn ? "own" : "opponent")}");
            Directory.CreateDirectory(path);
            new PlayPreferences { Automatic = true, ReducedMotion = true, Sounds = false, TextScale = 1.3f }.Save(path);
            var state = PriorityAbilitySmoke.Example();
            var instant = new Card { VisualId = "response-instant", CombatId = "response-instant", Name = "Lightning Bolt",
                Type = "Instant", ManaCost = "{R}", Text = "Lightning Bolt deals 3 damage to any target.", Selectable = true };
            Engine.Present(state with { Id = "bounded-priority-" + name, ActivePlayerId = ownTurn ? state.ViewerId : 1,
                Phase = "Main phase", PhaseKey = "MAIN1", PriorityPlayerId = state.ViewerId, DecisionOwnerId = state.ViewerId,
                ActionClass = "priority", StackInterruptionVersion = 0,
                Activity = [], Executions = new(1, 0, [], []),
                Players = state.Players.Select(player => player.Id != state.ViewerId ? player : player with {
                    LandPlaysUsed = 1,
                    Zones = player.Zones.Select(zone => zone.Name == "Hand" ? zone with { Count = 1, Cards = [instant] }
                        : zone.Name == "Battlefield" ? zone with { Count = 0, Cards = [] } : zone).ToImmutableArray()
                }).ToImmutableArray(),
                Decision = new("", new(), new PriorityPayload(new("Pass priority", true), new(PriorityAlternative.PassTurn, new("Pass turn", true)),
                    CanAutoPass: false, QuietYieldToken: "unchanged-public-permission", Abilities: [])) });
            Engine.ReplyOverride = async reply => {
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                releases.Enqueue(release); Replies.Enqueue(reply);
                await release.Task;
            };
            app = new(TestTableServices.For(Engine), path, initialState: Engine.State) { Automated = true };
            app.Initialize(); app.EnableLiveTableStudy();
            View.PresentLobby(new("local", "Bounded priority fixture", null, false, null, true, "", null, [], []), true);
            Tick();
            View.SetHeld(holdInitially);
            View.SetAutomatic(true);
            Settle();
        }
        internal bool Has(string id) => Frame.Hits.Any(hit => hit.Id == id);
        internal void Present(GameSnapshot state) { Engine.Present(state); View.Accept(Engine.State); Tick(); }
        internal void Tick(Point? at = null, ButtonState left = ButtonState.Released)
        {
            seconds += .05; var point = at ?? new Point(10, 30);
            app.Update(new(TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(.05)),
                new(point.X, point.Y, 0, left, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released),
                new(), viewport);
            Frame = app.ReadPresentation();
            if (View.Error.Length > 0) throw new InvalidOperationException(View.Error);
        }
        internal void Settle() { for (int frame = 0; frame < 60; frame++) { Tick(); Thread.Yield(); } }
        internal void Until(Func<bool> ready, string description)
        {
            var watch = Stopwatch.StartNew();
            while (!ready()) {
                if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException(description);
                Tick(); Thread.Yield();
            }
            Tick();
        }
        internal void Release(int index) => releases.ToArray()[index].TrySetResult();
        internal void Click(string id)
        {
            var hit = Frame.Hits.Last(candidate => candidate.Id == id);
            float scale = Math.Min(viewport.X / 1600f, viewport.Y / (float)View.DesignHeight);
            Point Pixel(Point logical) => new((int)MathF.Round((viewport.X - 1600 * scale) / 2 + logical.X * scale),
                (int)MathF.Round((viewport.Y - View.DesignHeight * scale) / 2 + logical.Y * scale));
            Point Logical(Point physical) => new((int)((physical.X - (viewport.X - 1600 * scale) / 2) / scale),
                (int)((physical.Y - (viewport.Y - View.DesignHeight * scale) / 2) / scale));
            bool Matches(Point physical) => physical.X >= 0 && physical.Y >= 0 && physical.X < viewport.X && physical.Y < viewport.Y
                && View.HitAt(Logical(physical)) is { } actual && actual.Id == hit.Id && actual.Scope == hit.Scope;
            var point = Pixel(hit.Bounds.Center);
            if (!Matches(point)) {
                point = (from y in Enumerable.Range(hit.Bounds.Top, hit.Bounds.Height)
                    from x in Enumerable.Range(hit.Bounds.Left, hit.Bounds.Width)
                    let physical = Pixel(new(x, y)) where Matches(physical) select (Point?)physical).FirstOrDefault()
                    ?? throw new InvalidOperationException("Covered bounded-priority input target: " + id);
            }
            Tick(point, ButtonState.Pressed); Tick(point);
            if (app.AcceptedHit != id) throw new InvalidOperationException("Bounded-priority click was not accepted: " + id);
        }
        public void Dispose()
        {
            foreach (var release in releases) release.TrySetResult();
            app.Dispose();
        }
    }
}
