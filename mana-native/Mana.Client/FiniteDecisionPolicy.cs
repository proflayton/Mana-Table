using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.Client;

// A wait describes who already owns progress; it grants no command authority.
// Original instruction terms stay stable while its remaining queue changes.
internal sealed record FiniteServerProgressScope(string GameId, int ViewerId, string GrantId,
    ImmutableArray<string> ExecutionIds, long StackActionVersion, long ResolutionChoiceVersion);

internal abstract record FiniteNextStep
{
    private FiniteNextStep() { }
    internal sealed record DispatchCommand : FiniteNextStep
    {
        internal DecisionCommand Command { get; }
        internal DispatchCommand(DecisionCommand command) => Command = command ?? throw new ArgumentNullException(nameof(command));
    }
    internal sealed record ObserveCurrentInput : FiniteNextStep;
    internal sealed record AwaitAcknowledgedServerProgress : FiniteNextStep
    {
        internal FiniteServerProgressScope Scope { get; }
        internal AwaitAcknowledgedServerProgress(FiniteServerProgressScope scope) => Scope = scope ?? throw new ArgumentNullException(nameof(scope));
    }
    internal static ObserveCurrentInput Observe { get; } = new();
    internal static FiniteNextStep FromCommand(DecisionCommand? command)
        => command == null ? Observe : new DispatchCommand(command);
}

// Policies own their finite consent and current matching rules. The common
// runner owns only bounded serial delivery, observation and cancellation.
internal interface IFiniteDecisionPolicy
{
    bool IsActive { get; }
    bool IsPaused => false;
    bool CanContinue(GameSnapshot state);
    FiniteNextStep NextStep(GameSnapshot state);
    void Submitted(GameSnapshot state, DecisionCommand command);
    void Stop();
}
