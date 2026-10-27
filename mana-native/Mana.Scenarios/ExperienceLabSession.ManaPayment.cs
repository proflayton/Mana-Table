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
            else if (source.VisualId == ManaPaymentScenario.Temple && StageIndex == 8)
                actions = [new(prefix + "temple", "activate", "{T}: Add {U} or {R}.")];
            else if (source.VisualId == ManaPaymentScenario.Island && StageIndex == 10)
                actions = [new(prefix + "island", "activate", "{T}: Add {U}.")];
            else if (StageIndex is 17 or 18 or 19 && source.VisualId is ManaPaymentScenario.ConvokeOne or ManaPaymentScenario.ConvokeTwo)
                actions = [new(prefix + "elf-green", "activate", "{T}: Add {G}.")];
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
                8 when source.VisualId == ManaPaymentScenario.Temple => 9,
                10 when source.VisualId == ManaPaymentScenario.Island => 11,
                17 when source.VisualId == ManaPaymentScenario.ConvokeOne => 18,
                17 when source.VisualId == ManaPaymentScenario.ConvokeTwo => 19,
                18 when source.VisualId == ManaPaymentScenario.ConvokeTwo => 15,
                19 when source.VisualId == ManaPaymentScenario.ConvokeOne => 15,
                _ => throw Unsupported()
            };
        } else if (reply is SelectCard selected && StageIndex is 12 or 13 or 14 or 16) {
            var source = State.Viewer!.Zone("Battlefield").Cards.SingleOrDefault(card => card.Key == selected.Card && card.Selectable) ?? throw Unsupported();
            next = StageIndex switch {
                12 when source.VisualId == ManaPaymentScenario.ConvokeOne => 13,
                12 when source.VisualId == ManaPaymentScenario.ConvokeTwo => 16,
                13 when source.VisualId == ManaPaymentScenario.ConvokeOne => 12,
                13 when source.VisualId == ManaPaymentScenario.ConvokeTwo => 14,
                16 when source.VisualId == ManaPaymentScenario.ConvokeTwo => 12,
                16 when source.VisualId == ManaPaymentScenario.ConvokeOne => 14,
                14 when source.VisualId == ManaPaymentScenario.ConvokeOne => 16,
                14 when source.VisualId == ManaPaymentScenario.ConvokeTwo => 13,
                _ => throw Unsupported()
            };
        } else if (reply is CompleteSelection && State.Decision?.Payload is EntitySelectionPayload { Complete.Enabled: true })
            next = StageIndex switch { 12 => 17, 13 => 18, 16 => 19, 14 => 15, _ => throw Unsupported() };
        else if (reply is ChooseOptions { Choices: var indices } && StageIndex == 9 && indices.SequenceEqual([11])) next = 10;
        else if (reply is SpendPoolMana { Color: "C" } && StageIndex == 5
            && State.Decision?.Payload is PaymentPayload { UsablePoolManaColors: var usable } && usable?.Contains("C") == true) next = 6;
        else if (reply is PassPriority && StageIndex == 3) next = 4;
        else if (reply is PayMana && State.Decision?.Payload is PaymentPayload { Pay.Enabled: true }) next = StageIndex is 17 or 18 or 19 ? 15 : 3;
        else if (reply is CancelPayment && State.Decision?.Payload is PaymentPayload { Cancel.Enabled: true }) next = 0;
        else throw Unsupported();
        manaPaymentReplies.Add(reply); StageIndex = next;
        return State = Stamp(Stages[next].Snapshot);
    }
}
