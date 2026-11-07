using Mana.Contracts;

namespace Mana.Client;

internal enum CommandFailurePhase { DispatchRejected, DispatchUnknown, Observation, ReadbackUnknown }
internal enum ObservationAdmission { Validated, Cancelled, Retired }

// Carries the actual failed phase to the session owner. The original exception
// remains the reported cause; its type alone cannot describe a whole exchange.
internal sealed class CommandExchangeException(CommandFailurePhase phase, Exception cause)
    : IOException("The current command exchange failed.", cause)
{
    internal CommandFailurePhase Phase { get; } = phase;
}

// One serial operation may contain several commands and locally validated
// observations. Until it returns/publishes, an earlier successful dispatch must
// not disappear merely because a later dispatch was definitely rejected.
internal sealed class CommandExchange(IMatchConnection connection, ClientCommandDiagnostics? diagnostics = null,
    bool commandSubmitted = false)
{
    private bool submitted = commandSubmitted;
    internal CommandFailurePhase ObservationFailurePhase => submitted ? CommandFailurePhase.ReadbackUnknown : CommandFailurePhase.Observation;

    internal async Task ExecuteAsync(GameCommand command, ClientDiagnosticAttempt? attempt = null)
    {
        try {
            if (diagnostics == null) await connection.ExecuteAsync(command);
            else await diagnostics.ExecuteAsync(connection, command, attempt);
            submitted = true;
        } catch (Exception error) {
            throw new CommandExchangeException(!submitted && error is EngineCommandRejectedException
                ? CommandFailurePhase.DispatchRejected : CommandFailurePhase.DispatchUnknown, error);
        }
    }

    internal async Task<GameSnapshot?> ObserveAsync(GameSnapshot? previous = null, ClientDiagnosticAttempt? attempt = null,
        Action<GameSnapshot?>? validate = null)
    {
        try {
            var next = diagnostics == null ? await connection.ObserveAsync() : await diagnostics.ObserveAsync(connection, attempt);
            bool currentPrefix = previous == null || next == null || next.Revision >= previous.Revision;
            if (previous != null) {
                new ObservationAudience(previous).Validate(next);
                if (currentPrefix) UndoFacts.ValidateProgress(previous, next);
            }
            ExecutionCursor.ValidateProgress(currentPrefix ? previous : null, next);
            if (next != null) { FiniteResolutionFacts.Validate(next); FiniteStackYieldFacts.Validate(next);
                PriorityContinuationFacts.Validate(next); HumanParticipationFacts.Validate(next); }
            validate?.Invoke(next);
            return next;
        } catch (Exception error) {
            throw new CommandExchangeException(ObservationFailurePhase, error);
        }
    }
}
