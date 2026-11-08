using Mana.Contracts;

namespace Mana.Client;

/// <summary>The failed part of an operation, independently of its provider exception type.</summary>
public enum ClientFailurePhase { DispatchRejected, DispatchUnknown, Observation, ReadbackUnknown }
public enum ClientRecoveryState { Ready, ReconciliationRequired, CommandUncertain }

/// <summary>Immutable owner context captured before starting work. A standalone
/// continuation has no session epoch; no command or action capability is retained.</summary>
public sealed record ClientFailureContext(long? Epoch, string? GameId, int? ViewerId,
    long? Revision, long? BoardRevision, string? DecisionId)
{
    internal static ClientFailureContext Capture(long? epoch, GameSnapshot? state)
        => new(epoch, state?.Id, state?.ViewerId, state?.Revision, state?.BoardRevision, state?.Decision?.Id);
}

/// <summary>Operational evidence, never permission to replay a command.</summary>
public sealed record ClientOperationFailure(ClientFailurePhase Phase, ClientFailureContext Context, Exception Cause)
{
    public bool IsCommand => Phase != ClientFailurePhase.Observation;
    public bool CommandOutcomeUncertain => Phase is ClientFailurePhase.DispatchUnknown or ClientFailurePhase.ReadbackUnknown;
    public string Message => Cause.Message;
    public override string ToString()
        => $"{Phase} (epoch {Context.Epoch?.ToString() ?? "unowned"}, game {Context.GameId ?? "none"}, viewer {Context.ViewerId?.ToString() ?? "none"}, revision {Context.Revision?.ToString() ?? "none"}, board {Context.BoardRevision?.ToString() ?? "none"}, decision {Context.DecisionId ?? "none"}): {Cause}";
}

// One owner for recovery state and its provenance. Later failures can escalate
// uncertainty and update diagnostics without erasing the episode's first cause.
internal sealed record ClientRecovery(ClientRecoveryState State, ClientOperationFailure? Origin, ClientOperationFailure? LastFailure)
{
    internal static readonly ClientRecovery Empty = new(ClientRecoveryState.Ready, null, null);
    internal ClientRecovery Fail(ClientOperationFailure failure, bool reconcile)
    {
        var next = failure.Phase == ClientFailurePhase.DispatchRejected || !reconcile ? State
            : failure.CommandOutcomeUncertain ? ClientRecoveryState.CommandUncertain
            : State == ClientRecoveryState.CommandUncertain ? State : ClientRecoveryState.ReconciliationRequired;
        return new(next, next == ClientRecoveryState.Ready ? null : Origin ?? failure, failure);
    }
    internal ClientRecovery Reconciled() => new(ClientRecoveryState.Ready, null, LastFailure);
}
