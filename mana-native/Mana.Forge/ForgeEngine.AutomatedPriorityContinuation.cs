using Mana.Contracts;

namespace Mana.Forge;

public sealed partial class ForgeEngine : IAutomatedPriorityContinuation
{
    private EngineProcess? automatedPriorityProcess;
    private long automatedPriorityWorld = -1, automatedPriorityEpoch;
    private readonly Dictionary<(string Game, int Seat), long> automatedPriorityFences = [];
    public bool SupportsAutomatedPriorityContinuation => current is {
        AutomationAvailable: true, PriorityContinuationAvailable: true, AutomatedPriorityContinuationAvailable: true, Failure: null } owner
        && !disposed && owner.Ready.Task.IsCompletedSuccessfully && !owner.Process.HasExited;

    public long AutomatedPriorityEpoch {
        get {
            lock (worldScopeGate) {
                if (current == null) return 0;
                if (!ReferenceEquals(current, automatedPriorityProcess) || automatedPriorityWorld != worldGeneration) {
                    automatedPriorityProcess = current; automatedPriorityWorld = worldGeneration;
                    automatedPriorityFences.Clear();
                    automatedPriorityEpoch = checked(automatedPriorityEpoch + 1);
                }
                return automatedPriorityEpoch;
            }
        }
    }

    public async Task<AutomatedPriorityControlResult> ExecuteAutomatedPriorityControlAsync(long epoch, int seatId, AutomatedPriorityControl control,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(control); control.Validate();
        if (epoch <= 0 || seatId < 0) throw new ArgumentException("A priority control needs an actual host epoch and owned seat.");
        EngineProcess owner;
        Dictionary<string, object?> args;
        lock (worldScopeGate) {
            if (!SupportsAutomatedPriorityContinuation || epoch != AutomatedPriorityEpoch || current is not { } process)
                return AutomatedPriorityControlResult.Retired;
            owner = process;
            if (control is AutomatedPriorityControl.Withdraw withdraw
                && automatedPriorityFences.TryGetValue((control.GameId, seatId), out long fence)
                && withdraw.ThroughSequence >= fence) {
                // Negative consent survives retirement of discovery scopes. An
                // earlier dispatched grant may still be waiting at the engine.
                args = new() { ["sessionId"] = control.GameId, ["action"] = "revokePriorityContinuation",
                    ["throughSequence"] = withdraw.ThroughSequence };
            } else {
                if (!automatedScopes.TryGetValue((control.GameId, seatId), out var scope))
                    return AutomatedPriorityControlResult.Retired;
                try { args = scope.Scope.Encode(AutomatedPriorityCommand(control)); }
                catch (EngineCommandRejectedException) { return AutomatedPriorityControlResult.Retired; }
            }
            if (control is AutomatedPriorityControl.NoResponse grant)
                automatedPriorityFences[(control.GameId, seatId)] = Math.Max(
                    automatedPriorityFences.GetValueOrDefault((control.GameId, seatId)), grant.Sequence);
            args["seatId"] = seatId;
        }
        System.Text.Json.JsonElement raw;
        try {
            raw = await Request("automatedAction", args, cancellation, owner, () => {
                lock (worldScopeGate) {
                    if (current != owner || epoch != AutomatedPriorityEpoch) throw new EngineObservationSupersededException();
                }
            });
        }
        catch (ForgeAutomatedPriorityRetiredException) { return AutomatedPriorityControlResult.Retired; }
        catch (EngineObservationSupersededException) { return AutomatedPriorityControlResult.Retired; }
        catch (EngineCommandRejectedException error) {
            lock (worldScopeGate)
                if (current != owner || epoch != AutomatedPriorityEpoch) return AutomatedPriorityControlResult.Retired;
            // A generic RPC error can follow grant admission or publication.
            // It is not proof that consent was never installed.
            throw new IOException("The automated priority receipt is uncertain.", error);
        }
        lock (worldScopeGate) {
            if (current != owner || owner.Failure != null || epoch != AutomatedPriorityEpoch)
                return AutomatedPriorityControlResult.Retired;
        }
        var receipt = ForgeSnapshotDecoder.Decode(raw, certifiedUndo: owner.CertifiedUndoAvailable);
        if (receipt?.Id == control.GameId && receipt.ViewerId == seatId
            && control is AutomatedPriorityControl.NoResponse requested
            && receipt.PriorityContinuation is { } later && later.Sequence > requested.Sequence)
            return AutomatedPriorityControlResult.Retired;
        bool valid = receipt?.Id == control.GameId && receipt.ViewerId == seatId && control switch {
            AutomatedPriorityControl.Acknowledge value => receipt.PriorityObservation is { Registered: true } credit
                && credit.AcknowledgedBoardRevision >= value.BoardRevision
                && credit.AcknowledgedPublicActivitySequence >= value.PublicActivitySequence,
            AutomatedPriorityControl.NoResponse value => receipt.PriorityContinuation is { } consent
                && consent.Sequence == value.Sequence
                && consent.GrantId == value.GrantId && consent.Mode == PriorityContinuationMode.NoResponse,
            AutomatedPriorityControl.Withdraw value => receipt.PriorityContinuation is { } consent
                && consent.Sequence >= value.ThroughSequence && (consent.Sequence > value.ThroughSequence
                    || consent.Status is not (PriorityContinuationStatus.Active or PriorityContinuationStatus.Held)),
            _ => false
        };
        if (!valid) throw new IOException("The automated priority receipt did not confirm its owned metadata transition.");
        lock (worldScopeGate)
            if (current != owner || epoch != AutomatedPriorityEpoch) return AutomatedPriorityControlResult.Retired;
        return AutomatedPriorityControlResult.Accepted;
    }

    internal static GameCommand AutomatedPriorityCommand(AutomatedPriorityControl control) => control switch {
        AutomatedPriorityControl.Acknowledge value => new AcknowledgePriorityObservation(value.GameId,
            value.BoardRevision, value.PublicActivitySequence),
        AutomatedPriorityControl.NoResponse value => new GrantPriorityContinuation(value.GameId, value.DecisionId,
            value.Sequence, value.GrantId, PriorityContinuationMode.NoResponse, value.PhaseStops),
        AutomatedPriorityControl.Withdraw value => new RevokePriorityContinuation(value.GameId, value.ThroughSequence),
        _ => throw new ArgumentException("Unknown automated priority control.")
    };
}
