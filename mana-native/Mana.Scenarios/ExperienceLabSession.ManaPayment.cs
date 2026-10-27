using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.Scenarios;

public sealed partial class ExperienceLabSession
{
    private readonly List<GameCommand> manaPaymentReplies = [];
    public IReadOnlyList<GameCommand> ManaPaymentReplies => manaPaymentReplies.AsReadOnly();

    private SourceActionSet ManaPaymentActions(Card source)
    {
        string prefix = State.Decision!.Id + ":" + source.Key + ":";
        SourceAction[] actions = [];
        if (State.Decision.Payload is PriorityPayload && source.VisualId == ManaPaymentScenario.Spell && StageIndex is 0 or 7) {
            var funding = ManaPaymentScenario.Funding(State.Viewer!.Zone("Battlefield").Cards, State.Viewer.Mana, StageIndex == 7);
            actions = [new(prefix + "cast", "cast", "Cast Faerie Vandal") {
                CostPreview = new([new(ActionCostKind.Mana, 1, false, "") { ManaSymbols = ["{U}"] }], SemanticKnowledge.Unknown, funding) }];
        } else if (!source.Tapped && State.Decision.Payload is PaymentPayload) {
            if (source.VisualId == ManaPaymentScenario.Ring && StageIndex == 1)
                actions = [new(prefix + "ring", "activate", "{T}: Add {C}{C}.")];
            else if (source.VisualId == ManaPaymentScenario.Dual && StageIndex == 2)
                actions = [new(prefix + "blue", "activate", "{T}: Add {U}."), new(prefix + "green", "activate", "{T}: Add {G}.")];
            else if (source.VisualId == ManaPaymentScenario.Plains && StageIndex == 6)
                actions = [new(prefix + "white", "activate", "{T}: Add {W}.")];
        }
        return new(State.Id, State.Decision.Id, source.Key, source.VisualId, actions.ToImmutableArray()) { Revision = State.Revision };
    }

    private GameSnapshot ReplyManaPayment(DecisionCommand reply)
    {
        int next;
        if (reply is UseOfferedAction use) {
            var source = State.Viewer!.Zones.SelectMany(zone => zone.Cards).SingleOrDefault(card => card.Key == use.Card) ?? throw Unsupported();
            var action = ManaPaymentActions(source).Actions.SingleOrDefault(action => action.Id == use.ActionId) ?? throw Unsupported();
            next = StageIndex switch {
                0 or 7 when source.VisualId == ManaPaymentScenario.Spell => 1,
                1 when source.VisualId == ManaPaymentScenario.Ring => 2,
                2 when source.VisualId == ManaPaymentScenario.Dual && action.Id.EndsWith(":blue", StringComparison.Ordinal) => 3,
                6 when source.VisualId == ManaPaymentScenario.Plains => 4,
                _ => throw Unsupported()
            };
        } else if (reply is SpendPoolMana { Color: "C" } && StageIndex == 5
            && State.Decision?.Payload is PaymentPayload { UsablePoolManaColors: var usable } && usable?.Contains("C") == true) next = 6;
        else if (reply is PassPriority && StageIndex == 3) next = 4;
        else if (reply is PayMana && State.Decision?.Payload is PaymentPayload) next = 3;
        else if (reply is CancelPayment && State.Decision?.Payload is PaymentPayload) next = 0;
        else throw Unsupported();
        manaPaymentReplies.Add(reply); StageIndex = next;
        return State = Stamp(Stages[next].Snapshot);
    }
}
