using System.Collections.Immutable;
using Mana.Contracts;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Keys = Microsoft.Xna.Framework.Input.Keys;

namespace Mana.Table;

internal static partial class HeadlessPresentationSmoke
{
    private static void CheckTurnPass(string profile, Action<bool, string> check, List<object> trace, List<object> performance)
    {
        var initial = Example();
        var state = initial with { Combat = null, PriorityPlayerId = initial.ViewerId, DecisionOwnerId = initial.ViewerId,
            Players = initial.Players.Select(player => player.Id != initial.ViewerId ? player
                : player with { LandPlaysUsed = 0, LandPlayLimit = 1 }).ToImmutableArray(),
            Decision = new("turn-pass", new DecisionContext(), new PriorityPayload(new("Pass", true),
                new(PriorityAlternative.PassTurn, new("Pass turn", true)), CanAutoPass: false,
                QuietYieldToken: "first-land-wait-permission")) };
        void RecordCommands(Runner run) => run.Engine.ReplyOverride = command => {
            if (!GameCommandPermissions.Allows(run.Engine.State, command)) throw new InvalidOperationException("Unpermitted turn-pass command.");
            run.Engine.ObserveOverride = null;
            run.Engine.Replies.Add(command);
            run.Engine.Present(run.Engine.State with { Decision = null });
            return Task.CompletedTask;
        };
        using (var run = new Runner(profile, "first-land-turn-pass", state, trace, performance)) {
            RecordCommands(run); run.Tick("ready");
            string prompt = run.View.Match!.Decision!.Id;
            run.ClickHit("quiet-priority-arm");
            check(run.Engine.Replies.Count == 0 && run.Frame.Hits.Any(hit => hit.Id == "turn-pass-keep")
                    && run.Frame.Hits.Any(hit => hit.Id == "turn-pass-confirm") && Text(run.Frame).Contains("No land played this turn"),
                "Waiting for action before the first land opens a local choice beside the portrait without yielding priority");
            run.Input(new(20, 60), keys: new(Keys.Space)); run.Tick("space-released");
            check(run.Engine.Replies.Count == 0, "Space cannot accidentally accept the first-land warning");
            run.ClickHit("turn-pass-keep");
            check(run.Engine.Replies.Count == 0 && run.Frame.Hits.Any(hit => hit.Id == "quiet-priority-arm"),
                "Keep playing returns to the same priority decision without sending a command");
            run.ClickHit("quiet-priority-arm"); run.Input(new(20, 60), keys: new(Keys.Escape)); run.Tick("escape-released");
            check(run.Engine.Replies.Count == 0 && run.Frame.Hits.Any(hit => hit.Id == "quiet-priority-arm"),
                "Escape dismisses the first-land warning without ending the turn");
            run.ClickHit("quiet-priority-arm");
            var autoState = run.View.Match! with { Decision = run.View.Match!.Decision! with {
                Payload = ((PriorityPayload)run.View.Match.Decision.Payload) with { CanAutoPass = true } } };
            run.Engine.ObserveOverride = () => Task.FromResult<GameSnapshot?>(autoState);
            run.QueueExact(autoState);
            run.Input(new(20, 60), keys: new(Keys.F)); run.Tick("auto-key-released");
            for (int n = 0; n < 4; n++) run.Tick("waiting-for-confirmation");
            check(run.Engine.Replies.Count == 0 && run.Frame.Hits.Any(hit => hit.Id == "turn-pass-confirm"),
                "Enabling Auto cannot bypass an outstanding first-land confirmation");
            run.ClickHit("turn-pass-confirm");
            run.Until(() => run.Engine.Replies.Count == 1 && !run.View.Busy, "confirmed-turn-pass");
            check(run.Engine.Replies.Single() is PassForNow { DecisionId: var decision, YieldToken: "first-land-wait-permission" }
                    && decision == prompt,
                "Pass anyway submits exactly the captured current bounded wait permission once");
        }
        using (var run = new Runner(profile, "first-land-stale-confirmation", state, trace, performance)) {
            RecordCommands(run); run.Tick("ready");
            run.ClickHit("quiet-priority-arm");
            var old = run.Hit("turn-pass-confirm");
            var point = run.PointFor(old);
            run.Input(point, left: ButtonState.Pressed);
            run.Present(run.Engine.State); run.Input(point);
            check(run.Engine.Replies.Count == 0 && !run.View.CurrentHit(old)
                    && run.Frame.Hits.All(hit => hit.Id != "turn-pass-confirm"),
                "Replacing priority between press and release revokes the captured confirmation instead of passing a new prompt");
        }
        int factCase = 0;
        foreach (var changed in new[] {
            state with { Turn = state.Turn + 1 },
            state with { ActivePlayerId = 1 },
            state with { Players = state.Players.Select(player => player.Id != state.ViewerId ? player
                : player with { LandPlaysUsed = 1 }).ToImmutableArray() },
            state with { PhaseKey = "MAIN2" },
            state with { ViewerId = 1 },
            state with { Id = "different-game" }
        }) {
            using var run = new Runner(profile, "first-land-changed-fact-" + factCase++, state, trace, performance);
            RecordCommands(run); run.Tick("ready-again"); run.ClickHit("quiet-priority-arm");
            // The fixture owner authors a scope handoff; an old audience's
            // ingress cannot claim authority to publish another game/viewer.
            // Preserve prompt and revision to isolate these facts from renewal.
            var transition = changed with { Revision = run.View.Match!.Revision, Decision = run.View.Match.Decision };
            run.View.Post(() => run.View.Accept(transition)); run.Tick("changed-turn-facts");
            check(run.Engine.Replies.Count == 0 && run.Frame.Hits.All(hit => hit.Id != "turn-pass-confirm"),
                "A changed game, turn, active seat or first land retires the pending pass intent");
        }
        using (var run = new Runner(profile, "first-land-second-main", state with { PhaseKey = "MAIN2", Phase = "Second main phase" }, trace, performance)) {
            RecordCommands(run); run.Tick("ready");
            run.Input(new(20, 60), keys: new(Keys.Space)); run.Tick("space-released");
            check(run.Engine.Replies.Count == 0 && run.Frame.Hits.Any(hit => hit.Id == "turn-pass-confirm"),
                "Leaving second main with Space also protects the unused first land play");
            run.ClickHit("turn-pass-confirm"); run.Until(() => run.Engine.Replies.Count == 1 && !run.View.Busy, "confirmed-main-two");
            check(run.Engine.Replies.Single() is PassPriority,
                "Confirming the end-step gesture preserves its PassPriority semantics rather than changing it to PassTurn");
        }
        foreach (var example in new[] {
            (Name: "land-already-played", State: state with { Players = state.Players.Select(player => player.Id != state.ViewerId ? player
                : player with { LandPlaysUsed = 1 }).ToImmutableArray() }, Control: "quiet-priority-arm", Kind: typeof(PassForNow)),
            (Name: "land-count-unavailable", State: state with { Players = state.Players.Select(player => player.Id != state.ViewerId ? player
                : player with { LandPlaysUsed = null }).ToImmutableArray() }, Control: "quiet-priority-arm", Kind: typeof(PassForNow)),
            (Name: "continue-to-combat", State: state, Control: "confirm", Kind: typeof(PassPriority)),
            (Name: "ordinary-response", State: state with { PhaseKey = "MAIN2", Stack = [new("An effect", "Draw a card.", true, null)] },
                Control: "confirm", Kind: typeof(PassPriority)),
            (Name: "opponents-turn", State: state with { ActivePlayerId = 1 }, Control: "confirm", Kind: typeof(PassPriority))
        }) {
            using var run = new Runner(profile, "first-land-" + example.Name, example.State, trace, performance);
            RecordCommands(run); run.Tick("ready"); run.ClickHit(example.Control);
            run.Until(() => run.Engine.Replies.Count == 1 && !run.View.Busy, "unchanged-gesture");
            check(run.Engine.Replies.Single().GetType() == example.Kind && run.Frame.Hits.All(hit => hit.Id != "turn-pass-confirm"),
                example.Name + " retains its existing command without a first-land warning");
        }
        var legacyUndo = state with { Decision = state.Decision! with { Payload = ((PriorityPayload)state.Decision.Payload) with {
            Alternative = new(PriorityAlternative.UndoLastAction, new("Undo", true)) } } };
        using (var run = new Runner(profile, "first-land-legacy-undo-unavailable", legacyUndo, trace, performance)) {
            RecordCommands(run); run.Tick("legacy-undo-offer");
            string prompt = run.View.Match!.Decision!.Id;
            run.Input(new(20, 60), keys: new(Keys.Escape)); run.Tick("escape-released");
            check(run.Engine.Replies.Count == 0 && run.View.Match?.Decision?.Id == prompt
                    && run.Frame.Hits.All(hit => hit.Id != "cancel") && !Text(run.Frame).Contains("Undo")
                    && run.Frame.Hits.Any(hit => hit.Id == "confirm") && run.Frame.Hits.Any(hit => hit.Id == "quiet-priority-arm"),
                "A legacy game-action Undo offer exposes no Undo or Cancel gesture and leaves current phase and wait actions intact");
        }
    }
}
