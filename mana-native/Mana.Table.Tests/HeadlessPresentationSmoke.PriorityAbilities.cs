using System.Collections.Immutable;
using Mana.Contracts;
using Mana.Table.Interaction;
using Microsoft.Xna.Framework.Input;
using Keys = Microsoft.Xna.Framework.Input.Keys;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

internal static partial class HeadlessPresentationSmoke
{
    // Runner composes the production application without an artwork-only
    // fixture, so the actual pointer path must submit and adopt each setting.
    private static void CheckPriorityAbilityPreferences(string profile, Action<bool, string> check,
        List<object> trace, List<object> performance)
    {
        var state = PriorityAbilitySmoke.Example();
        var priority = (PriorityPayload)state.Decision!.Payload;
        var first = priority.Abilities.Single();
        var second = first with { Id = "mesa-second", Text = "A second independent activation on the same source." };
        state = state with { Decision = state.Decision! with { Payload = priority with { Abilities = [first, second] } } };
        using var run = new Runner(profile, "priority-ability-consecutive-preferences", state, trace, performance);
        run.Tick("initial"); run.View.SetTableStudy(true);
        run.View.SetHeld(true); run.View.SetAutomatic(true);
        run.Until(() => !run.View.Busy && !run.View.ActivityOpen
            && run.Frame.Hits.Any(hit => hit.Id == "ability-auto-pass-open"), "ability-preferences-ready");
        var battlefield = run.Frame.Hits.First(hit => hit.Card?.VisualId == "mesa" && hit.Zone == "Battlefield");
        var battlefieldPoint = run.PointFor(battlefield);
        string game = run.Engine.State.Id, firstDecision = run.Engine.State.Decision!.Id;
        run.ClickHit("ability-auto-pass-open");
        CheckList(0, 0, "Opening the list");
        var staleSecondSkip = run.Hit("ability-pass:mesa-second");

        run.ClickHit("ability-pass:mesa-fetch");
        Adopt(1, "first-skip-acknowledged");
        CheckList(1, 1, "Skipping the first activation");
        check(run.Engine.Replies is [IgnorePriorityAbility { AbilityId: "mesa-fetch" } command]
                && command.GameId == game && command.DecisionId == firstDecision
                && run.Engine.State.IgnoredPriorityAbilities is [{ Id: "mesa-fetch", IgnoredUntilNextTurn: true }]
                && run.Frame.Hits.Any(hit => hit.Id == "ability-undo:mesa-fetch")
                && run.Frame.Hits.Any(hit => hit.Id == "ability-pass:mesa-second"),
            "The first physical Skip submits only its exact current activation and replaces that row with Stop while the other Skip remains available");
        check(run.Hit("ability-pass:mesa-second").Scope != staleSecondSkip.Scope
                && !run.View.Activate(staleSecondSkip) && run.Engine.Replies.Count == 1,
            "The acknowledged input publishes a fresh second Skip target and rejects the captured preceding decision's target");
        string secondDecision = run.Engine.State.Decision!.Id;
        run.ClickHit("ability-pass:mesa-second");
        Adopt(2, "second-skip-acknowledged-without-reopening");
        CheckList(2, 2, "Skipping the second activation without reopening");
        check(run.Engine.Replies[1] is IgnorePriorityAbility { AbilityId: "mesa-second" } secondCommand
                && secondCommand.GameId == game && secondCommand.DecisionId == secondDecision
                && run.Engine.State.IgnoredPriorityAbilities.Select(ability => ability.Id).ToImmutableHashSet()
                    .SetEquals(["mesa-fetch", "mesa-second"])
                && run.Engine.State.Decision?.Payload is PriorityPayload { CanAutoPass: true },
            "The second physical Skip uses its fresh acknowledged decision and independently enables only the engine-reported all-skipped availability");

        run.ClickHit("ability-undo:mesa-fetch");
        Adopt(3, "first-activation-restored-without-reopening");
        CheckList(3, 1, "Restoring the first activation without reopening");
        check(run.Engine.Replies[2] is RestorePriorityAbility { AbilityId: "mesa-fetch" } restore
                && restore.GameId == game
                && run.Engine.State.IgnoredPriorityAbilities is [{ Id: "mesa-second", IgnoredUntilNextTurn: true }]
                && run.Engine.State.Decision?.Payload is PriorityPayload { CanAutoPass: false }
                && run.Frame.Hits.Any(hit => hit.Id == "ability-pass:mesa-fetch")
                && run.Frame.Hits.Any(hit => hit.Id == "ability-undo:mesa-second"),
            "The physical Stop restores only the first activation and refreshes both current row statuses in the retained list");
        run.ClickHit("ability-list-backdrop"); run.Settle("explicit-outside-dismissal");
        check(run.View.Overlay.Length == 0 && run.Engine.Replies.Count == 3
                && run.Frame.Hits.Any(hit => hit.Id == "ability-auto-pass-open")
                && run.Frame.Hits.Any(hit => hit.Id == "confirm") && run.View.Automatic && run.View.Held,
            "Only an explicit outside click dismisses the completed preference list and returns the unchanged held priority controls");
        run.ClickHit("ability-auto-pass-open");
        run.Input(new Point(20, 60), keys: new KeyboardState(Keys.Escape), label: "escape-preference-list");
        run.Input(new Point(20, 60)); run.Settle("escape-preference-list-settled");
        check(run.View.Overlay.Length == 0 && run.Engine.Replies.Count == 3 && run.View.Automatic && run.View.Held,
            "Escape still explicitly dismisses the retained list without changing Auto, Hold or any activation preference");

        run.ClickHit("ability-auto-pass-open");
        var oldReader = run.Hit("ability-read:mesa-fetch");
        var oldRestore = run.Hit("ability-undo:mesa-second");
        check(run.View.Overlay == "priority-stops", "The preference list is open before its owning game is replaced");
        run.Engine.Present(state with { Id = game + "-replacement" });
        run.View.Post(() => run.View.Accept(run.Engine.State)); run.Settle("replacement-world");
        check(!run.View.Activate(oldReader) && !run.View.Activate(oldRestore)
                && run.Engine.Replies.Count == 3 && run.View.Overlay.Length == 0,
            "Retaining the workspace does not let a reader or restore target survive replacement of its owning game");
        check(run.Engine.Replies is [IgnorePriorityAbility, IgnorePriorityAbility, RestorePriorityAbility],
            "The consecutive native-pointer journey submits exactly two skip preferences and one restore, with no pass, card activation or other gameplay command");

        void Adopt(int replies, string label) => run.Until(() => !run.View.Busy
            && run.Engine.Replies.Count == replies && run.View.Match?.Revision == run.Engine.State.Revision
            && run.View.Match?.Decision?.Id == run.Engine.State.Decision?.Id, label);
        void CheckList(int replies, int ignored, string gesture)
        {
            check(run.View.Overlay == "priority-stops" && run.Engine.Replies.Count == replies
                    && run.View.Automatic && run.View.Held
                    && Text(run.Frame).Any(text => text.EndsWith($"{ignored} of 2 skipping until your next turn", StringComparison.Ordinal))
                    && run.Frame.Hits.Any(hit => hit.Id == "ability-read:mesa-fetch")
                    && run.Frame.Hits.Any(hit => hit.Id == "ability-read:mesa-second"),
                gesture + " keeps the same readable Auto list open with acknowledged counts and both independent activations, preserving Auto and Hold");
            check(!run.Frame.Hits.Any(hit => hit.Id is "confirm" or "ability-auto-pass-open")
                    && run.View.HitAt(battlefieldPoint)?.Id != battlefield.Id,
                gesture + " retains overlay input ownership instead of exposing underlying priority or battlefield targets");
        }
    }
}
