namespace Mana.Contracts;

// Shared by the adapter and policy-frame boundary. These indices describe one
// complete current ordering prompt and grant no authority for another decision.
public static class DecisionOrderingFacts
{
    public static void Validate(GameSnapshot state, DecisionSemantics semantics)
    {
        if (!Enum.IsDefined(semantics.OrderingPurpose) || semantics.DefaultOrder.IsDefault) throw Invalid();
        if (semantics.OrderingPurpose == DecisionOrderingPurpose.Unknown) {
            if (!semantics.DefaultOrder.IsEmpty) throw Invalid();
            return;
        }
        if (!semantics.SelectionIsCost || state.Decision?.Payload is not OrderingPayload ordering) throw Invalid();
        var selection = ordering.Selection;
        if (selection.Choices.IsEmpty || selection.Range.Min != selection.Choices.Length
            || selection.Range.Max != selection.Choices.Length
            || semantics.DefaultOrder.Length != selection.Choices.Length
            || semantics.DefaultOrder.Any(index => index < 0)
            || semantics.DefaultOrder.Distinct().Count() != selection.Choices.Length
            || !semantics.DefaultOrder.SequenceEqual(selection.Choices.Select(choice => choice.Index))) throw Invalid();
    }

    private static ArgumentException Invalid() => new("A cost-order default must preserve every current offered part in its exact provider sequence.");
}
