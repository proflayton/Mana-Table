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

internal static class AiPolicySeeds
{
    private const long SeatFactor = 1442695040888963407L, EpisodeFactor = 6364136223846793005L;
    internal static long Episode(long seed, int seatId, long sequence)
        => unchecked(seed + (seatId + 1L) * SeatFactor + sequence * EpisodeFactor);
    internal static long FirstEpisodeBase(long evaluationSeed, int seatId)
        => unchecked(evaluationSeed - (seatId + 1L) * SeatFactor - EpisodeFactor);
}

/// <summary>One shared work allowance for an input episode, including refreshed observations.</summary>
public sealed class AiWorkBudget
{
    private int remaining;
    private int exceeded;
    private readonly AiWorkBudget? parent;
    private readonly int reserve;
    public int Remaining => Volatile.Read(ref remaining);
    // Spending the final available unit is valid. A refused request is a
    // distinct fact, so bounded planners cannot mistake it for a normal pass.
    public bool WasExceeded => Volatile.Read(ref exceeded) != 0;
    internal AiWorkBudget(int units) => remaining = units;
    private AiWorkBudget(int units, AiWorkBudget parent, int reserve)
        => (remaining, this.parent, this.reserve) = (units, parent, reserve);

    // A local optimization limit charges only actual work. Reaching that limit
    // does not exhaust the owning decision or consume its submission reserve.
    public AiWorkBudget CreateSlice(int workUnits, int reserve = 0)
    {
        if (workUnits <= 0 || reserve < 0) throw new ArgumentOutOfRangeException(nameof(workUnits));
        return new(workUnits, this, reserve);
    }
    public bool TrySpend(int units = 1)
    {
        if (units <= 0) throw new ArgumentOutOfRangeException(nameof(units));
        return Spend(units, 0, true);
    }
    private bool Spend(int units, int retained, bool reportExceeded)
    {
        while (true) {
            int before = Remaining;
            if (before < (long)units + retained) {
                if (reportExceeded) Interlocked.Exchange(ref exceeded, 1);
                return false;
            }
            if (Interlocked.CompareExchange(ref remaining, before - units, before) != before) continue;
            if (parent == null || parent.Spend(units, reserve, false)) return true;
            Interlocked.Add(ref remaining, units);
            if (reportExceeded) Interlocked.Exchange(ref exceeded, 1);
            return false;
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
