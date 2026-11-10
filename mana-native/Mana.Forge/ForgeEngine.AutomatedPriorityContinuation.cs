using Mana.Contracts;

namespace Mana.Forge;

public sealed partial class ForgeEngine : IAutomatedPriorityContinuation
{
    private EngineProcess? automatedPriorityProcess;
    private long automatedPriorityWorld = -1, automatedPriorityEpoch;
    private readonly Dictionary<(string Game, int Seat), long> automatedPriorityFences = [];
    private readonly Dictionary<(string Game, int Seat), (GameSnapshot Anchor, AutomatedPriorityReceipt Receipt)> automatedPriorityReceipts = [];
    public bool SupportsAutomatedPriorityContinuation => current is {
        AutomationAvailable: true, PriorityContinuationAvailable: true, AutomatedPriorityContinuationAvailable: true, Failure: null } owner
        && !disposed && owner.Ready.Task.IsCompletedSuccessfully && !owner.Process.HasExited;
    public bool SupportsAutomatedPriorityResponses => SupportsAutomatedPriorityContinuation
        && current is { AutomatedPriorityMetadataAvailable: true, AutomatedPriorityResponsesAvailable: true };

    public long AutomatedPriorityEpoch {
        get {
            lock (worldScopeGate) {
                if (current == null) return 0;
                if (!ReferenceEquals(current, automatedPriorityProcess) || automatedPriorityWorld != worldGeneration) {
                    automatedPriorityProcess = current; automatedPriorityWorld = worldGeneration;
                    automatedPriorityFences.Clear();
                    automatedPriorityReceipts.Clear();
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
        GameSnapshot? anchor = null;
        Dictionary<string, object?> args;
        lock (worldScopeGate) {
            if (!SupportsAutomatedPriorityContinuation || epoch != AutomatedPriorityEpoch || current is not { } process)
                return new AutomatedPriorityControlResult.Retired();
            if (control is AutomatedPriorityControl.PrepareResponses && !SupportsAutomatedPriorityResponses)
                return new AutomatedPriorityControlResult.Retired();
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
                    return new AutomatedPriorityControlResult.Retired();
                anchor = scope.Scope.ObservedState;
                if (control is AutomatedPriorityControl.PrepareResponses preparation) {
                    if (anchor is not { Status: "playing", Viewer: { Eliminated: false },
                            Decision: { Payload: PriorityPayload { Pass.Enabled: true } } decision }
                        || anchor.Id != preparation.GameId || anchor.ViewerId != seatId || decision.Id != preparation.DecisionId
                        || anchor.DecisionOwnerId is { } chooser && chooser != seatId
                        || anchor.PriorityPlayerId is { } responder && responder != seatId
                        || anchor.PriorityContinuation is { Status: PriorityContinuationStatus.Active or PriorityContinuationStatus.Held }
                        || anchor.FiniteStackYield is { Status: FiniteStackYieldStatus.Active })
                        return new AutomatedPriorityControlResult.Retired();
                    args = new() { ["sessionId"] = preparation.GameId, ["promptId"] = preparation.DecisionId,
                        ["expectedTask"] = "priority", ["action"] = "preparePriorityResponses" };
                    // Preparation may refresh controller facts. Neither its
                    // receipt nor the preceding ACK can reuse this rich scope.
                    automatedScopes.Remove((control.GameId, seatId));
                    automatedPriorityReceipts.Remove((control.GameId, seatId));
                } else if (control is AutomatedPriorityControl.NoResponse chained && anchor != null
                    && automatedPriorityReceipts.TryGetValue((control.GameId, seatId), out var cursor)
                    && ReferenceEquals(cursor.Anchor, anchor)) {
                    if (cursor.Receipt.Epoch != epoch || !cursor.Receipt.SamePriorityInput(anchor)
                        || chained.DecisionId != cursor.Receipt.PriorityDecisionId || chained.Sequence <= cursor.Receipt.Continuation.Sequence
                        || cursor.Receipt.Continuation.Status is PriorityContinuationStatus.Active or PriorityContinuationStatus.Held
                        || anchor.FiniteStackYield is { Status: FiniteStackYieldStatus.Active })
                        return new AutomatedPriorityControlResult.Retired();
                    // This metadata-only cursor can authorize another closed
                    // control. It never replaces gameplay scopes or domains.
                    args = new() { ["sessionId"] = control.GameId, ["promptId"] = chained.DecisionId,
                        ["expectedTask"] = "priority", ["action"] = "grantPriorityContinuation",
                        ["sequence"] = chained.Sequence, ["grantId"] = chained.GrantId,
                        ["mode"] = "NoResponse", ["phaseStops"] = chained.PhaseStops, ["quietYieldToken"] = "" };
                } else {
                    try { args = scope.Scope.Encode(AutomatedPriorityCommand(control)); }
                    catch (EngineCommandRejectedException) { return new AutomatedPriorityControlResult.Retired(); }
                }
            }
            if (control is AutomatedPriorityControl.NoResponse grant)
                automatedPriorityFences[(control.GameId, seatId)] = Math.Max(
                    automatedPriorityFences.GetValueOrDefault((control.GameId, seatId)), grant.Sequence);
            args["seatId"] = seatId;
        }
        System.Text.Json.JsonElement raw;
        try {
            raw = await Request(owner.AutomatedPriorityMetadataAvailable ? "automatedPriorityControl" : "automatedAction", args, cancellation, owner, () => {
                lock (worldScopeGate) {
                    if (current != owner || epoch != AutomatedPriorityEpoch) throw new EngineObservationSupersededException();
                }
            });
        }
        catch (ForgeAutomatedPriorityRetiredException) { return new AutomatedPriorityControlResult.Retired(); }
        catch (EngineObservationSupersededException) { return new AutomatedPriorityControlResult.Retired(); }
        catch (EngineCommandRejectedException error) {
            lock (worldScopeGate)
                if (current != owner || epoch != AutomatedPriorityEpoch) return new AutomatedPriorityControlResult.Retired();
            // A generic RPC error can follow grant admission or publication.
            // It is not proof that consent was never installed.
            throw new IOException("The automated priority receipt is uncertain.", error);
        }
        lock (worldScopeGate) {
            if (current != owner || owner.Failure != null || epoch != AutomatedPriorityEpoch)
                return new AutomatedPriorityControlResult.Retired();
        }
        AutomatedPriorityReceipt metadata;
        if (control is AutomatedPriorityControl.PrepareResponses requestedPreparation) {
            var receipt = ForgeAutomatedPriorityResponsesDecoder.Decode(raw, epoch);
            if (receipt.GameId != requestedPreparation.GameId || receipt.SeatId != seatId
                || receipt.DecisionId != requestedPreparation.DecisionId || anchor == null
                || receipt.BoardRevision < anchor.BoardRevision
                || anchor.PriorityObservation is { } observed && receipt.PublicActivitySequence < observed.PublicActivitySequence)
                throw new IOException("Priority response preparation belongs to another input or public prefix.");
            lock (worldScopeGate)
                if (current != owner || owner.Failure != null || epoch != AutomatedPriorityEpoch)
                    return new AutomatedPriorityControlResult.Retired();
            return new AutomatedPriorityControlResult.Prepared(receipt);
        }
        if (owner.AutomatedPriorityMetadataAvailable) {
            metadata = ForgeAutomatedPriorityMetadataDecoder.Decode(raw, epoch);
            if (metadata.GameId != control.GameId || metadata.SeatId != seatId)
                throw new IOException("The automated priority receipt belongs to another audience.");
            if (control is AutomatedPriorityControl.NoResponse requested && metadata.Continuation.Sequence > requested.Sequence)
                return new AutomatedPriorityControlResult.Retired();
            bool valid = control switch {
                AutomatedPriorityControl.Acknowledge value => metadata.Observation.Registered
                    && metadata.Observation.AcknowledgedBoardRevision >= value.BoardRevision
                    && metadata.Observation.AcknowledgedPublicActivitySequence >= value.PublicActivitySequence,
                AutomatedPriorityControl.NoResponse value => metadata.Continuation.Sequence == value.Sequence
                    && metadata.Continuation.GrantId == value.GrantId && metadata.Continuation.Mode == PriorityContinuationMode.NoResponse,
                AutomatedPriorityControl.Withdraw value => metadata.Continuation.Sequence >= value.ThroughSequence
                    && (metadata.Continuation.Sequence > value.ThroughSequence
                        || metadata.Continuation.Status is not (PriorityContinuationStatus.Active or PriorityContinuationStatus.Held)),
                _ => false
            };
            if (!valid) throw new IOException("The automated priority receipt did not confirm its owned metadata transition.");
        } else {
            // Older engines retain their complete snapshot decoding and all
            // existing validation. A malformed negotiated reply never falls back.
            var receipt = ForgeSnapshotDecoder.Decode(raw, certifiedUndo: owner.CertifiedUndoAvailable);
            if (receipt?.Id == control.GameId && receipt.ViewerId == seatId
                && control is AutomatedPriorityControl.NoResponse requested
                && receipt.PriorityContinuation is { } later && later.Sequence > requested.Sequence)
                return new AutomatedPriorityControlResult.Retired();
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
            metadata = PriorityReceipt(epoch, receipt!);
        }
        lock (worldScopeGate) {
            if (current != owner || epoch != AutomatedPriorityEpoch) return new AutomatedPriorityControlResult.Retired();
            var key = (control.GameId, seatId);
            if (anchor != null && automatedScopes.TryGetValue(key, out var scope)
                && ReferenceEquals(scope.Scope.ObservedState, anchor)
                && (!automatedPriorityReceipts.TryGetValue(key, out var prior) || !ReferenceEquals(prior.Anchor, anchor)
                    || metadata.Revision >= prior.Receipt.Revision))
                automatedPriorityReceipts[key] = (anchor, metadata);
        }
        return new AutomatedPriorityControlResult.Accepted(metadata);
    }

    private static AutomatedPriorityReceipt PriorityReceipt(long epoch, GameSnapshot state)
    {
        var metadata = new AutomatedPriorityReceipt(epoch, state.Id, state.ViewerId, state.Revision, state.BoardRevision,
            state.StackInterruptionVersion ?? -1, state.Status switch {
                "starting" => AutomatedPriorityMatchStatus.Starting, "playing" => AutomatedPriorityMatchStatus.Playing,
                "resolving" => AutomatedPriorityMatchStatus.Resolving, "finished" => AutomatedPriorityMatchStatus.Finished,
                "error" => AutomatedPriorityMatchStatus.Error, _ => throw new IOException("Unknown automated priority match status.")
            }, state.Viewer is { Eliminated: false }, state.DecisionOwnerId, state.PriorityPlayerId, state.Decision?.Id,
            state.Status == "playing" && (state.DecisionOwnerId == null || state.DecisionOwnerId == state.ViewerId)
                && (state.PriorityPlayerId == null || state.PriorityPlayerId == state.ViewerId)
                && state.Decision is { Payload: PriorityPayload { Pass.Enabled: true } } decision ? decision.Id : null,
            state.PriorityContinuation ?? throw new IOException("Missing automated priority consent receipt."),
            state.PriorityObservation ?? throw new IOException("Missing automated priority observation receipt."));
        try { metadata.Validate(); }
        catch (ArgumentException error) { throw new IOException("Invalid automated priority metadata receipt.", error); }
        return metadata;
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
