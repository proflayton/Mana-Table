using Mana.Contracts;
using Mana.Scenarios;

namespace Mana.Table;

internal static class DecisionFlowLabChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var lab = new ExperienceLabSession(DecisionFlowScenario.Id);
        PassPriority Pass() => new(lab.State.Id, lab.State.Decision?.Id ?? "");
        SelectCard Target(string card) => new(lab.State.Id, lab.State.Decision?.Id ?? "", card);
        UseOfferedAction Offered(string card, string action) => new(lab.State.Id, lab.State.Decision?.Id ?? "", card, action);
        void Rejected(Action action, string description)
        {
            var unchanged = lab.State;
            int stage = lab.StageIndex;
            bool rejected = false;
            try { action(); } catch (EngineCommandRejectedException) { rejected = true; }
            check(rejected && ReferenceEquals(lab.State, unchanged) && lab.StageIndex == stage, description);
        }
        var initial = lab.State;
        var forest = initial.Viewer!.Zone("Hand").Cards.Single();
        var offered = lab.SourceActions(initial.Id, initial.Decision!.Id, forest.Key);
        check(ReferenceEquals(initial, lab.State) && lab.StageIndex == 0 && offered is { Actions: [{ Kind: "play", Text: "Play land" }] }
            && offered.SourceCardId == DecisionFlowScenario.LandId && offered.SourceCardKey == forest.Key,
            "Reading the canonical Forest returns one current land action without advancing or changing the authored game");
        Rejected(() => lab.Reply(new PlayCard(lab.State.Id, lab.State.Decision!.Id, forest.VisualId)),
            "A visual identity cannot substitute for the current Forest play handle");
        Rejected(() => lab.Reply(Offered(forest.Key, "not-an-offered-action")),
            "A made-up source action cannot play the Forest or alter its checkpoint");
        var play = Offered(forest.Key, offered.Actions.Single().Id);
        lab.Reply(play);
        check(lab.StageIndex == 1 && lab.State.Id == initial.Id && lab.State.Viewer is { LandPlaysUsed: 1 } viewer
            && viewer.Zone("Hand").Cards.Length == 0 && viewer.Zone("Battlefield").Cards.Count(card => card.VisualId == forest.VisualId) == 1
            && lab.State.Decision is { Task: DecisionTask.Priority, CanAutoPass: true },
            "The exact Forest reader action advances to its authored land result once and exposes the empty own priority window");
        Rejected(() => lab.Reply(play), "A delayed land action cannot consume the next decision");
        Rejected(() => lab.Reply(new AutoPassPriority(lab.State.Id, lab.State.Decision?.Id ?? "")),
            "Authored automatic permission does not run rules or silently navigate the canonical lab");
        lab.Reply(Pass());
        check(lab.StageIndex == 2 && lab.State.Decision == null && lab.State.DecisionOwnerId == 1 && lab.State.PriorityPlayerId == 1,
            "Manually passing the empty own window reaches only the authored other-seat priority checkpoint");
        Rejected(() => lab.Reply(Pass()), "Another player's priority cannot be answered through the local lab reply lane");
        lab.AdvanceTo(3);
        check(lab.State.Decision == null && lab.State.DecisionOwnerId == 2 && lab.State.ActionClass == "selection",
            "Explicit external Next reveals required-choice ownership without exposing another player's private prompt");
        Rejected(() => lab.Reply(new ChooseOptions(lab.State.Id, lab.State.Decision?.Id ?? "", [0])),
            "The local lab connection cannot choose an option for the other seat");
        lab.AdvanceTo(4);
        var targetState = lab.State;
        var target = targetState.Players[1].Zone("Battlefield").Cards.Single(card => card.VisualId == CounterDamageScenario.TargetId);
        check(targetState.Decision is { Task: DecisionTask.TargetSelection, PlayerChoices.Length: 0, Effect.Amount: 1 }
            && target.Selectable && targetState.Players.SelectMany(player => player.Zone("Battlefield").Cards).Count(card => card.Selectable) == 1,
            "The authored damage choice exposes only its supported Dreadmaw recipient and the captured one-counter damage value");
        Rejected(() => lab.Reply(new SelectPlayer(lab.State.Id, lab.State.Decision?.Id ?? "", 1)),
            "Unsupported player targets cannot produce a fabricated damage outcome");
        var wrong = targetState.Viewer!.Zone("Battlefield").Cards.First();
        Rejected(() => lab.Reply(Target(wrong.Key)), "A different current public card is not an authored legal target");
        Rejected(() => lab.Reply(Target(target.VisualId)), "The target's visual ID is not its current decision key");
        Rejected(() => lab.Reply(new SelectCard("another-game", targetState.Decision!.Id, Card: target.Key)),
            "A target gesture from another game cannot alter the current authored decision");
        Rejected(() => lab.Reply(new SelectCard(targetState.Id, initial.Decision!.Id, Card: target.Key)),
            "An old prompt cannot borrow a currently visible target handle");
        var select = Target(target.Key);
        lab.Reply(select);
        check(lab.StageIndex == 5 && lab.State.Stack is [{ Effect.Amount: 1, Targets: [{ Id: CounterDamageScenario.TargetId }] }]
            && lab.State.Players[1].Zone("Battlefield").Cards.Single(card => card.VisualId == target.VisualId).Damage == 0,
            "Choosing the exact current creature records the one-damage pending effect without applying its result early");
        Rejected(() => lab.Reply(select), "Repeating the accepted target gesture cannot pass priority or resolve the effect");
        lab.Reply(Pass());
        check(lab.StageIndex == 6 && lab.State.Decision == null && lab.State.PriorityPlayerId == 1 && lab.State.Stack.Length == 2,
            "Passing local priority reveals the authored opponent response while leaving its private decision unanswered");
        Rejected(() => lab.Reply(Pass()), "The opponent response remains outside the local command scope");
        lab.AdvanceTo(7);
        check(lab.State.Stack.Length == 1 && lab.State.DecisionOwnerId == 0,
            "Explicit external Next publishes the authored resolved response before offering local priority again");
        lab.Reply(Pass());
        var recipient = lab.State.Players[1].Zone("Battlefield").Cards.Single(card => card.VisualId == target.VisualId);
        check(lab.StageIndex == 8 && lab.State.Stack.Length == 0 && recipient is { Power: 9, Toughness: 9, Damage: 1 }
            && lab.State.Activity.Any(entry => entry.Impact is { Kind: "damage", Amount: 1, CardId: CounterDamageScenario.TargetId })
            && lab.State.Decision is { OkEnabled: false },
            "The explicit last local pass publishes the authored actual damage, preserves current stats and leaves no unsupported final OK gesture");
        Rejected(() => lab.Reply(Pass()), "The completed authored journey cannot invent another game transition");

        lab.Replay(0);
        forest = lab.State.Viewer!.Zone("Hand").Cards.Single();
        var direct = new PlayCard(lab.State.Id, lab.State.Decision!.Id, forest.Key);
        lab.Reply(direct);
        check(lab.StageIndex == 1 && lab.State.Viewer!.Zone("Hand").Cards.Length == 0,
            "The ordinary current hand-card play reply used by dragging reaches the same authored land result");
        Rejected(() => lab.Reply(direct), "A repeated drag-play reply cannot replay the land or advance a second checkpoint");
    }
}
