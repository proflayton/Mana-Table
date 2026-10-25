using Mana.Contracts;

namespace Mana.Scenarios;

public sealed partial class ExperienceLabSession
{
    private GameSnapshot ReplyOptionalDecision(DecisionCommand reply)
    {
        if (!GameCommandPermissions.Allows(State, reply)) throw Unsupported();
        if (reply is SelectPlayer target && State.Decision?.Payload is TargetSelectionPayload) {
            if (target.Player == State.ViewerId) {
                StageIndex = 2;
                return State = Stamp(Stages[2].Snapshot);
            }
            return State = Stamp(State with {
                Decision = null, DecisionOwnerId = target.Player,
                PriorityPlayerId = null, ActionClass = "confirm"
            });
        }
        if (reply is MulliganOpeningHand) {
            StageIndex = 5;
            return State = Stamp(Stages[5].Snapshot);
        }
        if (reply is KeepOpeningHand or AcceptConfirmation or DeclineConfirmation) {
            Complete("Choice recorded in the Experience Lab. No game rules were simulated. Reset to repeat.");
            return State;
        }
        throw Unsupported();
    }
}
