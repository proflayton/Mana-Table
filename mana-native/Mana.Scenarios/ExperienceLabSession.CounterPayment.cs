using Mana.Contracts;

namespace Mana.Scenarios;

public sealed partial class ExperienceLabSession
{
    private string counterPaymentDonor = CounterPaymentScenario.MareId;
    private int counterPaymentType = 12;

    private SourceActionSet CounterPaymentActions(Card source)
    {
        SourceAction[] actions = CounterPaymentScenario.IsInitial(StageIndex) && State.Decision?.Payload is PriorityPayload
            && source.VisualId == CounterPaymentScenario.SourceId(StageIndex) && source.Selectable
            ? [new(State.Decision.Id + ":" + source.Key + ":" + CounterPaymentScenario.Action, "activate",
                StageIndex >= 9 ? CounterPaymentScenario.DivinerAbility : CounterPaymentScenario.MareAbility)] : [];
        return new(State.Id, State.Decision!.Id, source.Key, source.VisualId, [.. actions]);
    }

    private GameSnapshot ReplyCounterPayment(DecisionCommand reply)
    {
        reply.Validate();
        if (!GameCommandPermissions.Allows(State, reply)) throw Unsupported();
        if (reply is UseOfferedAction offered) {
            var actions = CounterPaymentActions(State.Viewer!.Zone("Battlefield").Cards.SingleOrDefault(card => card.Key == offered.Card)
                ?? throw Unsupported());
            if (!actions.Actions.Any(action => action.Id == offered.ActionId)) throw Unsupported();
            return AdvanceCounterPayment(StageIndex + 1);
        }
        if (reply is PayMana && StageIndex is 1 or 5) return AdvanceCounterPayment(StageIndex + 1);
        if (reply is SelectCard chosen && StageIndex is 10 or 14 && State.Decision?.Payload is EntitySelectionPayload) {
            var donor = State.Viewer!.Zone("Battlefield").Cards.SingleOrDefault(card => card.Key == chosen.Card && card.Selectable)
                ?? throw Unsupported();
            if (donor.VisualId is not (CounterPaymentScenario.MareId or CounterPaymentScenario.OtherMareId)) throw Unsupported();
            counterPaymentDonor = donor.VisualId;
            return AdvanceCounterPayment(StageIndex + 1);
        }
        if (reply is ChooseOptions { Choices.Length: 1 } selected && StageIndex == 6) {
            if (selected.Choices[0] is not (7 or 12)) throw Unsupported();
            counterPaymentType = selected.Choices[0];
            return AdvanceCounterPayment(7);
        }
        if (reply is PassPriority && CounterPaymentScenario.IsPending(StageIndex)) return AdvanceCounterPayment(StageIndex + 1);
        if (reply is CancelPayment or CancelSelection && StageIndex is 1 or 5 or 10 or 14)
            return AdvanceCounterPayment(StageIndex - 1);
        throw Unsupported();
    }

    private GameSnapshot AdvanceCounterPayment(int stage)
    {
        StageIndex = stage;
        return State = Stamp(CounterPaymentScenario.Snapshot(stage, counterPaymentDonor, counterPaymentType));
    }
}
