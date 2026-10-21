using Mana.Contracts;

namespace Mana.Scenarios;

public static partial class ExperienceScenarios
{
    public const string BallistaId = "ability-ballista";
    public const string BallistaTargetId = "ability-target-bear";
    internal const string BallistaCounterAction = "ballista-counter";
    internal const string BallistaDamageAction = "ballista-damage";
    internal const string BallistaCounterText = "{4}: Put a +1/+1 counter on Walking Ballista.";
    internal const string BallistaDamageText = "Remove a +1/+1 counter from Walking Ballista: It deals 1 damage to any target.";

    // Costs/effects transcribed from forge-gui/res/cardsfolder/w/walking_ballista.txt.
    // The following states are authored presentation data, not a second rules engine.
    private static IReadOnlyList<ExperienceStage> BallistaFlow() => [
        new("Choose an ability", "Walking Ballista has three counters and four colorless mana is available. Choose an action on its card.", BallistaSnapshot(0)),
        new("Ability on the stack", "The cost is paid; the effect has not resolved. You may pass priority to the next responder.", BallistaSnapshot(1)),
        new("Another player can respond", "Opponent 1 has priority. Next shows the authored result after the remaining response windows.", BallistaSnapshot(2)),
        new("Ability resolved", "The authored result preserves the paid cost and shows the chosen target's damage or the added counter.", BallistaSnapshot(3))
    ];

    internal static GameSnapshot BallistaSnapshot(int stage, string mode = BallistaDamageAction, StackTarget? chosenTarget = null)
    {
        bool adding = mode == BallistaCounterAction;
        var target = chosenTarget ?? new StackTarget("card", BallistaTargetId, "Grizzly Bears", 1);
        int counters = stage == 0 ? 3 : adding ? stage == 3 ? 4 : 3 : 2;
        var source = Body(BallistaId, "Walking Ballista", "Artifact Creature — Construct", counters, counters) with {
            Selectable = stage == 0, Counters = new() { ["+1/+1"] = counters },
            Text = "Walking Ballista enters with X +1/+1 counters on it.\n" + BallistaCounterText + "\n" + BallistaDamageText,
            Damage = stage == 3 && !adding && target.Kind == "card" && target.Id == BallistaId ? 1 : 0
        };
        var bear = Body(BallistaTargetId, "Grizzly Bears", "Creature — Bear") with {
            Damage = stage == 3 && !adding && target.Kind == "card" && target.Id == BallistaTargetId ? 1 : 0
        };
        var state = Table("ability-targeting", [source], opposing: [bear]);
        return state with {
            Players = state.Players.Select(player => player with {
                Life = stage == 3 && !adding && target.Kind == "player" && target.PlayerId == player.Id ? 39 : 40,
                Mana = player.Id == state.ViewerId && (stage == 0 || !adding) ? new() { ["C"] = 4 } : []
            }).ToArray(),
            Stack = stage is 1 or 2 ? [new("Walking Ballista", adding ? BallistaCounterText : "Walking Ballista deals 1 damage to " + target.Name + ".", true, source) {
                Id = "ballista-pending-ability", ControllerId = state.ViewerId, Targets = adding ? [] : [target]
            }] : [],
            DecisionOwnerId = stage == 2 ? 1 : state.ViewerId,
            PriorityPlayerId = stage == 2 ? 1 : state.ViewerId,
            ActionClass = "priority",
            Decision = stage == 2 ? null : new() {
                Kind = "input", Intent = DecisionIntent.Priority, OkEnabled = stage == 1,
                Message = stage switch {
                    0 => "Choose Walking Ballista's ability. Four colorless mana is available.",
                    1 => "The ability is waiting on the stack. Pass priority when you are ready.",
                    _ => adding ? "Walking Ballista now has four counters and is 4/4." : "Walking Ballista's ability dealt 1 damage to " + target.Name + "."
                }
            }
        };
    }
}
