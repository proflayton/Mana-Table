using Mana.Contracts;

namespace Mana.Magic;

// Presents and validates the engine's allocation constraints. It does not
// calculate combat damage, lethal thresholds, prevention or resulting deaths.
public static class AllocationGuide
{
    public static string Unit(Decision decision) => !string.IsNullOrWhiteSpace(decision.AmountLabel)
        ? decision.AmountLabel.Trim() : decision.AllocationKind == "combat" ? "combat damage" : "amount";

    public static int Minimum(Decision decision, int index) => Math.Max(0,
        decision.Choices[index].Minimum ?? (decision.AtLeastOne ? 1 : 0));

    public static int Maximum(Decision decision, int index) => Math.Max(0, Math.Min(decision.Amount,
        decision.Choices[index].Maximum ?? (index < decision.Limits.Length ? decision.Limits[index] : decision.Amount)));

    public static long Total(IReadOnlyList<int> amounts) => amounts.Sum(value => (long)value);

    public static string? Problem(Decision decision, IReadOnlyList<int> amounts)
    {
        if (decision.Kind != "allocate" || decision.Amount < 0 || amounts.Count != decision.Choices.Length)
            return "Assign an amount to every recipient.";
        for (int i = 0; i < amounts.Count; i++) {
            if (amounts[i] < Minimum(decision, i)) return $"Assign at least {Minimum(decision, i)} to {Reference(decision.Choices[i], i)}.";
            if (amounts[i] > Maximum(decision, i)) return $"{Reference(decision.Choices[i], i)} can receive at most {Maximum(decision, i)}.";
        }
        long remaining = decision.Amount - Total(amounts);
        if (remaining > 0) return $"{remaining} left to assign.";
        if (remaining < 0) return $"{-remaining} too many assigned.";
        if (decision.AllocationKind == "combat" && decision.Choices.Where((choice, index) => choice.IsDefender && amounts[index] > 0).Any()) {
            for (int i = 0; i < decision.Choices.Length; i++)
                if (!decision.Choices[i].IsDefender && decision.Choices[i].Lethal is { } required && amounts[i] < required)
                    return $"Assign at least {required} to {Reference(decision.Choices[i], i)} before the defender.";
        }
        return null;
    }

    public static DecisionReply? Reply(GameSnapshot state, IReadOnlyList<int> amounts)
        => state.Status == "playing" && state.Decision is { Id.Length: > 0 } decision && Problem(decision, amounts) == null
            ? new(state.Id, decision.Id, ReplyAction.Allocate, Amounts: amounts.ToArray()) : null;

    public static string Name(Choice choice, int index) => !string.IsNullOrWhiteSpace(choice.Label)
        ? choice.Label : "Recipient " + (index + 1);

    private static string Reference(Choice choice, int index) => Name(choice, index) + " (recipient " + (index + 1) + ")";

    public static string Seat(GameSnapshot state, Choice choice)
    {
        int? id = choice.PlayerId ?? choice.ControllerId;
        int seat = Array.FindIndex(state.Players, player => player.Id == id);
        return seat < 0 ? "" : (state.Players[seat].Id == state.ViewerId ? "You" : state.Players[seat].Name) + " · Seat " + (seat + 1);
    }

    public static string Stats(Card? card) => card == null || card.Type.Length > 0 && !card.Type.Contains("Creature", StringComparison.Ordinal)
        ? "" : card.Power.HasValue || card.Toughness.HasValue
        ? $"{card.Power?.ToString() ?? "?"}/{card.Toughness?.ToString() ?? "?"}"
        : card.Type.Contains("Creature", StringComparison.Ordinal) ? "P/T unknown" : "";

    public static string MarkedDamage(Card? card) => card is { Damage: > 0 } ? card.Damage + " damage marked" : "";

    public static string Counters(Card? card) => card == null ? "" : string.Join(" · ", CardPresentation.Counters(card).Select(counter => counter.Key + ": " + counter.Value));

    // Allocation snapshots expose only current, public keywords, including the
    // anonymous characteristics of face-down battlefield permanents.
    public static string Abilities(Card? card) => card == null ? "" : string.Join(" · ", CardPresentation.AbilityLabels(card));

    public static string RecipientState(Choice choice) => string.Join(" · ", new[] {
        Stats(choice.Card), MarkedDamage(choice.Card), Counters(choice.Card), Abilities(choice.Card)
    }.Where(value => value.Length > 0));

    public static string ThresholdLabel(Decision decision, Choice choice) => decision.AllocationKind == "combat"
        && decision.Choices.Any(recipient => recipient.IsDefender) && !choice.IsDefender && choice.Lethal is { } required
            ? required + " required before defender" : "";
}
