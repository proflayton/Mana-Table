namespace Mana.Contracts;

public enum AllocationIssueKind { MissingRecipients, BelowMinimum, AboveMaximum, Unassigned, Excess, BeforeDefender }
public sealed record AllocationIssue(AllocationIssueKind Kind, int Recipient = -1, long Amount = 0);

// Validates only the engine's published assignment constraints. Current card
// toughness, printed abilities and predicted deaths never establish a bound.
public static class AllocationConstraints
{
    public static AllocationIssue? Check(AllocationPayload allocation, IReadOnlyList<int> amounts)
    {
        if (amounts.Count != allocation.Recipients.Length) return new(AllocationIssueKind.MissingRecipients);
        for (int index = 0; index < amounts.Count; index++) {
            var recipient = allocation.Recipients[index];
            if (amounts[index] < recipient.Minimum) return new(AllocationIssueKind.BelowMinimum, index, recipient.Minimum);
            if (amounts[index] > recipient.Maximum) return new(AllocationIssueKind.AboveMaximum, index, recipient.Maximum);
        }
        long remaining = allocation.Amount - amounts.Sum(amount => (long)amount);
        if (remaining > 0) return new(AllocationIssueKind.Unassigned, Amount: remaining);
        if (remaining < 0) return new(AllocationIssueKind.Excess, Amount: -remaining);
        if (allocation.Kind == AllocationKind.Combat && allocation.Recipients.Where((recipient, index) => recipient.IsDefender && amounts[index] > 0).Any())
            for (int index = 0; index < amounts.Count; index++)
                if (!allocation.Recipients[index].IsDefender && allocation.Recipients[index].Lethal is { } threshold && amounts[index] < threshold)
                    return new(AllocationIssueKind.BeforeDefender, index, threshold);
        return null;
    }
}
