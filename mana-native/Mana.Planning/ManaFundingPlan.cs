using System.Collections.Immutable;

namespace Mana.Planning;

// Caller-owned work is charged directly, including a shared causal AI budget.
public interface IManaPlanningWork
{
    int Remaining { get; }
    bool TrySpend(int units = 1);
}
public enum ManaFundingStatus { Funded, Shortfall, Unknown, BudgetExceeded }
public abstract record ManaFundingStep
{
    private protected ManaFundingStep() { }
    public sealed record Pool(string Color, int Amount = 1) : ManaFundingStep;
    public sealed record Source(string SourceCardId, int RouteIndex, int AlternativeIndex,
        ImmutableDictionary<string, int> Output) : ManaFundingStep;
}
// A nominal witness contains read-only resource positions, never action keys.
public sealed record ManaFundingPlan(ManaFundingStatus Status, ImmutableArray<ManaFundingStep> Steps,
    int WorkSpent, string ReasonCode);
