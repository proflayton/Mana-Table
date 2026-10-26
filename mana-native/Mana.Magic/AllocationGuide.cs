using Mana.Contracts;
using System.Collections.Immutable;

namespace Mana.Magic;

// Human explanations of the same published constraints used by command permission.
public static class AllocationGuide
{
    public static string Unit(AllocationPayload allocation) => !string.IsNullOrWhiteSpace(allocation.AmountLabel)
        ? allocation.AmountLabel.Trim() : allocation.Kind == AllocationKind.Combat ? "combat damage" : "amount";
    public static int Minimum(AllocationPayload allocation, int index) => allocation.Recipients[index].Minimum;
    public static int Maximum(AllocationPayload allocation, int index) => allocation.Recipients[index].Maximum;
    public static long Total(IReadOnlyList<int> amounts) => amounts.Sum(value => (long)value);
    public static string? Problem(AllocationPayload allocation, IReadOnlyList<int> amounts)
    {
        var issue = AllocationConstraints.Check(allocation, amounts);
        return issue?.Kind switch {
            null => null,
            AllocationIssueKind.MissingRecipients => "Assign an amount to every recipient.",
            AllocationIssueKind.BelowMinimum => $"Assign at least {issue.Amount} to {Reference(allocation.Recipients[issue.Recipient], issue.Recipient)}.",
            AllocationIssueKind.AboveMaximum => $"{Reference(allocation.Recipients[issue.Recipient], issue.Recipient)} can receive at most {issue.Amount}.",
            AllocationIssueKind.Unassigned => $"{issue.Amount} left to assign.",
            AllocationIssueKind.Excess => $"{issue.Amount} too many assigned.",
            AllocationIssueKind.BeforeDefender => $"Assign at least {issue.Amount} to {Reference(allocation.Recipients[issue.Recipient], issue.Recipient)} before the defender.",
            _ => throw new ArgumentOutOfRangeException(nameof(issue))
        };
    }
    public static AllocateAmounts? Reply(GameSnapshot state, IReadOnlyList<int> amounts)
        => state.Decision is { Id.Length: > 0, Payload: AllocationPayload } decision
            && GameCommandPermissions.Allows(state, new AllocateAmounts(state.Id, decision.Id, amounts.ToImmutableArray()))
            ? new(state.Id, decision.Id, amounts.ToImmutableArray()) : null;
    public static string Name(AllocationRecipient recipient, int index) => !string.IsNullOrWhiteSpace(recipient.Label) ? recipient.Label : "Recipient " + (index + 1);
    private static string Reference(AllocationRecipient recipient, int index) => Name(recipient, index) + " (recipient " + (index + 1) + ")";
    public static string Seat(GameSnapshot state, AllocationRecipient recipient) => Seat(state, recipient.PlayerId ?? recipient.ControllerId);
    public static string Seat(GameSnapshot state, int? playerId)
    {
        int seat = state.Players.IndexOf(state.Players.FirstOrDefault(player => player.Id == playerId)!);
        return seat < 0 ? "" : (state.Players[seat].Id == state.ViewerId ? "You" : state.Players[seat].Name) + " · Seat " + (seat + 1);
    }
    public static string Stats(Card? card) => card == null || card.Type.Length > 0 && !card.Type.Contains("Creature", StringComparison.Ordinal)
        ? "" : card.Power.HasValue || card.Toughness.HasValue ? $"{card.Power?.ToString() ?? "?"}/{card.Toughness?.ToString() ?? "?"}"
        : card.Type.Contains("Creature", StringComparison.Ordinal) ? "P/T unknown" : "";
    public static string MarkedDamage(Card? card) => card is { Damage: > 0 } ? card.Damage + " damage marked" : "";
    public static string Counters(Card? card) => card == null ? "" : string.Join(" · ", CardPresentation.Counters(card).Select(counter => counter.Key + ": " + counter.Value));
    public static string Abilities(Card? card) => card == null ? "" : string.Join(" · ", CardPresentation.AbilityLabels(card));
    public static string RecipientState(AllocationRecipient recipient) => string.Join(" · ", new[] { Stats(recipient.Card), MarkedDamage(recipient.Card), Counters(recipient.Card), Abilities(recipient.Card) }.Where(value => value.Length > 0));
    public static string ThresholdLabel(AllocationPayload allocation, AllocationRecipient recipient) => allocation.Kind == AllocationKind.Combat
        && allocation.Recipients.Any(item => item.IsDefender) && !recipient.IsDefender && recipient.Lethal is { } required ? required + " required before defender" : "";
}
