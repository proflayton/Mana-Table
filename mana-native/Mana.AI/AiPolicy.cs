using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.AI;

public enum AiTask
{
    Priority, OpeningHand, Payment, Attack, Block, Targets, Entities, Confirmation,
    Abilities, Options, Ordering, Reveal, Number, Text, Allocation
}

public static class AiTasks
{
    public static ImmutableHashSet<AiTask> All { get; } = Enum.GetValues<AiTask>().ToImmutableHashSet();
    public static AiTask From(DecisionPayload payload) => payload switch {
        PriorityPayload => AiTask.Priority, OpeningHandPayload => AiTask.OpeningHand, PaymentPayload => AiTask.Payment,
        AttackPayload => AiTask.Attack, BlockPayload => AiTask.Block, TargetSelectionPayload => AiTask.Targets,
        EntitySelectionPayload => AiTask.Entities, ConfirmationPayload => AiTask.Confirmation,
        AbilityChoicePayload => AiTask.Abilities, OptionsPayload => AiTask.Options, OrderingPayload => AiTask.Ordering,
        RevealPayload => AiTask.Reveal, NumberPayload => AiTask.Number, TextPayload => AiTask.Text,
        AllocationPayload => AiTask.Allocation, _ => throw new NotSupportedException("Unknown AI decision task.")
    };
}

/// <summary>A whole-seat policy must declare every task. Unsupported situations return Unavailable explicitly.</summary>
public sealed record AiPolicyDescriptor
{
    public string Id { get; }
    public ImmutableHashSet<AiTask> Tasks { get; }
    public AiPolicyDescriptor(string id, ImmutableHashSet<AiTask> tasks)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A policy needs a versioned identity.", nameof(id));
        ArgumentNullException.ThrowIfNull(tasks);
        if (!tasks.SetEquals(AiTasks.All))
            throw new ArgumentException("A seat policy must cover every decision task, including required choices.", nameof(tasks));
        Id = id; Tasks = tasks;
    }
}

/// <summary>
/// Evaluations consume only immutable seat knowledge and return proposals. They
/// must not mutate private policy state or hold a connection; history and seed
/// arrive in each request. Cancelled computations cannot publish memory changes.
/// </summary>
public interface IAiPolicy
{
    AiPolicyDescriptor Descriptor { get; }
    Task<AiProposal> EvaluateAsync(AiDecisionRequest request, CancellationToken cancellation);
}

public abstract record AiProposal
{
    private protected AiProposal() { }
    public sealed record Answer(DecisionCommand Command, string ReasonCode) : AiProposal;
    public sealed record Unavailable(string Capability, string Detail) : AiProposal;
}

public sealed record AiDecisionRequest(AiDecisionFrame Frame, AiSeatHistory History,
    long Seed, TimeSpan RemainingTime, AiWorkBudget Work);

/// <summary>One shared work allowance for an input episode, including refreshed observations.</summary>
public sealed class AiWorkBudget
{
    private int remaining;
    public int Remaining => Volatile.Read(ref remaining);
    internal AiWorkBudget(int units) => remaining = units;
    public bool TrySpend(int units = 1)
    {
        if (units <= 0) throw new ArgumentOutOfRangeException(nameof(units));
        while (true) {
            int before = Remaining;
            if (before < units) return false;
            if (Interlocked.CompareExchange(ref remaining, before - units, before) == before) return true;
        }
    }
}

public sealed record AiSeatLimits
{
    public TimeSpan DecisionTime { get; }
    public int WorkUnits { get; }
    public int HistoryCapacity { get; }
    public AiSeatLimits(TimeSpan decisionTime, int workUnits = 256, int historyCapacity = 128)
    {
        if (decisionTime <= TimeSpan.Zero || decisionTime.TotalMilliseconds > uint.MaxValue - 1) throw new ArgumentOutOfRangeException(nameof(decisionTime));
        if (workUnits <= 0) throw new ArgumentOutOfRangeException(nameof(workUnits));
        if (historyCapacity is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(historyCapacity));
        DecisionTime = decisionTime; WorkUnits = workUnits; HistoryCapacity = historyCapacity;
    }
}
