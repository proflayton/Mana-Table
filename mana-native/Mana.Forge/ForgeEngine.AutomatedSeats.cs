using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mana.Contracts;
using Mana.Planning;

namespace Mana.Forge;

public sealed partial class ForgeEngine : IAutomatedSimulation
{
    private static readonly JsonSerializerOptions AutomationJson = new() {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    private readonly Dictionary<(string Game, int Seat), (ForgeCommandScope Scope, DecisionActionDomain Domain)> automatedScopes = [];
    private JsonElement? lastDecodedAutomatedWire;

    // Commit provider-owned table replacement while receiving its successful
    // receipt, before asynchronous request continuations can finish out of order.
    // A concession or pending-start cancellation does not replace the table.
    private void CommitAutomatedWorldReplacement(EngineProcess owner, string method)
    {
        if (method is not ("matchStart" or "workshopStartTest" or "multiplayerHost" or "multiplayerJoin"
            or "multiplayerStart" or "startEncounter" or "retryEncounterStart" or "multiplayerReturn" or "multiplayerClose")) return;
        lock (worldScopeGate) {
            if (current != owner || owner.Failure != null) return;
            Interlocked.Increment(ref worldGeneration);
            automatedScopes.Clear();
            owner.Commands.Clear();
        }
    }

    public async Task EnableAutomatedSeatsAsync(CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var owner = current ?? throw new IOException("The engine is not running");
        if (!owner.AutomationAvailable) throw new NotSupportedException("Install a matching engine with automated-seat support.");
        await Request("automatedEnable", cancellation: cancellation);
    }

    public async Task<GameSnapshot> StartSimulationAsync(ImmutableArray<string> deckIds)
    {
        if (deckIds.IsDefault || deckIds.Length != 4 || deckIds.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Choose four Commander decks.", nameof(deckIds));
        var owner = current ?? throw new IOException("The engine is not running");
        await simulationLifecycle.WaitAsync();
        try {
            EnsureSimulationConnection(owner);
            var raw = await Request("automatedSimulationStart", new { decks = deckIds });
            EnsureSimulationConnection(owner);
            lock (worldScopeGate) { Interlocked.Increment(ref worldGeneration); automatedScopes.Clear(); }
            return ForgeSnapshotDecoder.Decode(raw, spectator: true, certifiedUndo: owner.CertifiedUndoAvailable)
                ?? throw new IOException("The simulation did not start.");
        } finally { simulationLifecycle.Release(); }
    }
    public async Task<GameSnapshot?> ObserveSimulationAsync()
    {
        var owner = current ?? throw new IOException("The engine is not running");
        long generation = worldGeneration;
        var raw = await Request("automatedSimulationState");
        EnsureSimulationOwner(owner, generation);
        return ForgeSnapshotDecoder.Decode(raw, spectator: true,
            certifiedUndo: owner.CertifiedUndoAvailable);
    }
    public async Task StopSimulationAsync(string gameId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        var owner = current ?? throw new IOException("The engine is not running");
        await simulationLifecycle.WaitAsync();
        try {
            EnsureSimulationConnection(owner);
            await Request("automatedSimulationStop", new { sessionId = gameId });
            EnsureSimulationConnection(owner);
            lock (worldScopeGate) { Interlocked.Increment(ref worldGeneration); automatedScopes.Clear(); }
        } finally { simulationLifecycle.Release(); }
    }

    public async Task<ImmutableArray<AutomatedSeatObservation>> ObserveAutomatedSeatsAsync(CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var owner = current ?? throw new IOException("The engine is not running");
        if (!owner.AutomationAvailable) throw new NotSupportedException("This engine has no automated-seat port.");
        long generation = worldGeneration;
        JsonElement raw;
        try { raw = await Request("automatedState", cancellation: cancellation); }
        catch (EngineObservationSupersededException) {
            lock (worldScopeGate)
                if (current == owner && owner.Failure == null && worldGeneration == generation) automatedScopes.Clear();
            throw;
        }
        lock (worldScopeGate) EnsureAutomatedObservationOwner(owner, generation, cancellation);
        ImmutableArray<AutomatedSeatObservation> result;
        try {
            cancellation.ThrowIfCancellationRequested();
            result = DecodeAutomatedObservations(raw, owner.CertifiedUndoAvailable);
            cancellation.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (Exception error) {
            lock (worldScopeGate) {
                EnsureAutomatedObservationOwner(owner, generation, cancellation);
                automatedScopes.Clear();
            }
            PreserveRejectedAutomatedObservation(paths.Profile, raw, error);
            throw;
        }
        lock (worldScopeGate) {
            EnsureAutomatedObservationOwner(owner, generation, cancellation);
            automatedScopes.Clear();
            // Request already returned an owned immutable JSON clone.
            lastDecodedAutomatedWire = raw;
            foreach (var item in result) {
                var scope = new ForgeCommandScope(); scope.Observe(item.State);
                automatedScopes.Add((item.State.Id, item.SeatId), (scope, item.Domain));
            }
        }
        return result;
    }

    // Background discovery spans ordinary host/start/return lifecycle changes.
    // A healthy process replacing its world produced no current observation;
    // connection failure remains fatal, and command/startup owners stay strict.
    private void EnsureAutomatedObservationOwner(EngineProcess owner, long generation, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        EnsureSimulationConnection(owner);
        if (worldGeneration != generation) throw new EngineObservationSupersededException();
    }

    // Failure-only local quarantine, separate from engine.log and public errors.
    // Replaces one file instead of accumulating every bot's private observations.
    // The rejected wire is evidence, never an accepted frame or replay authority.
    internal static void PreserveRejectedAutomatedObservation(string profile, JsonElement raw, Exception error)
    {
        try {
            string directory = Path.Combine(profile, "diagnostics");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "private-rejected-ai-observation.json"),
                JsonSerializer.Serialize(new { capturedAt = DateTimeOffset.UtcNow, authority = "none",
                    privacy = "Private bot observations; may contain hands and library faces. Review before sharing.",
                    error = error.ToString(), raw }));
        } catch (Exception) { /* Preserve the original failure even if diagnostic storage is unavailable. */ }
    }

    internal static ImmutableArray<AutomatedSeatObservation> DecodeAutomatedObservations(JsonElement raw, bool certifiedUndo = false)
    {
        try {
            var result = ImmutableArray.CreateBuilder<AutomatedSeatObservation>();
            foreach (var item in raw.EnumerateArray()) {
                int seat = item.GetProperty("seatId").GetInt32();
                var observation = DecodeOwnedDecisionObservation(item, certifiedUndo)
                    ?? throw new IOException("Missing automated seat state.");
                if (seat < 0 || seat != observation.State.ViewerId)
                    throw new IOException("Invalid automated seat observation.");
                result.Add(new(seat, observation.State, observation.Domain, observation.Semantics));
            }
            if (result.Select(item => (item.State.Id, item.SeatId)).Distinct().Count() != result.Count)
                throw new IOException("Duplicate automated seat observation.");
            return result.ToImmutable();
        } catch (Exception error) when (error is IOException or JsonException or InvalidOperationException
            or ArgumentException or FormatException or OverflowException or KeyNotFoundException) {
            throw new ForgeAutomatedObservationException(raw, error);
        }
    }

    public async Task ExecuteAutomatedAsync(int seatId, GameCommand command, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (command is GrantPriorityContinuation or RevokePriorityContinuation or AcknowledgePriorityObservation
            or GrantFiniteStackYield or RevokeFiniteStackYield)
            throw new EngineCommandRejectedException("Automatic priority consent belongs to the host control port, not a policy answer.");
        Dictionary<string, object?> args;
        lock (worldScopeGate) {
            if (command is UndoAction && current?.CertifiedUndoAvailable != true)
                throw new EngineCommandRejectedException("This engine does not support certified gameplay Undo.");
            if (!automatedScopes.TryGetValue((command.GameId, seatId), out var currentScope))
                throw new EngineCommandRejectedException("Observe this automated seat before answering it.");
            args = currentScope.Scope.Encode(command);
            if (command is UseOfferedAction offer && !currentScope.Domain.Sources.Any(source => source.SourceCardKey == offer.Card
                    && source.DecisionId == offer.DecisionId && source.Actions.Any(action => action.Id == offer.ActionId)))
                throw new EngineCommandRejectedException("That action was not offered to this automated seat.");
            if (command is UseOfferedAction { ManaOutput: { } output } production
                && !currentScope.Domain.Actions.Any(action => action.ActionId == production.ActionId && action.ManaAbility
                    && ManaFundingFacts.MatchesOutput(action.Production, output)))
                throw new EngineCommandRejectedException("That mana output was not offered by this current ability.");
            args["seatId"] = seatId;
        }
        await Request("automatedAction", args, cancellation);
    }

    // Friend-test seam: the ordinary public method correctly rejects this at
    // its current client scope. Release checks also need the authoritative JVM
    // to reject a previously accepted payload under a fresh transport request.
    // Neither the renderer nor IAiPolicy receives this internal capability.
    internal async Task ReplayPriorAutomatedCommandForCheckAsync(int seatId, GameSnapshot prior,
        GameSnapshot currentState, DecisionCommand command, string probeId)
    {
        if (prior.ViewerId != seatId || currentState.ViewerId != seatId || prior.Id != currentState.Id
            || currentState.Status != "playing" || currentState.Decision == null || prior.Decision == null
            || prior.Decision.Id != command.DecisionId || currentState.Decision.Id == command.DecisionId
            || string.IsNullOrWhiteSpace(probeId))
            throw new ArgumentException("A stale-scope check requires the same seat's distinct current decision.");
        var historical = new ForgeCommandScope(); historical.Observe(prior);
        var args = historical.Encode(command);
        args["seatId"] = seatId;
        args["testProbeId"] = probeId;
        // Request always assigns a new transport sequence id. No prior command
        // receipt is replayed or treated as rejection evidence.
        await Request("automatedAction", args);
    }

    public async Task ReportAutomationFaultAsync(string gameId, int seatId, string reasonCode, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        // Only stable diagnostics cross the host port, never policy exception
        // messages or cards private to a different seat.
        if (string.IsNullOrWhiteSpace(reasonCode) || reasonCode.Length > 96
            || reasonCode.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '-')))
            throw new ArgumentException("Use a bounded diagnostic reason code.", nameof(reasonCode));
        // Provider decoding can succeed before frame/strategy validation fails.
        // Keep that accepted observation distinct from a rejected wire batch.
        JsonElement? last;
        lock (worldScopeGate) last = lastDecodedAutomatedWire;
        PreserveLastAutomatedObservation(paths.Profile, last, gameId, seatId, reasonCode);
        await Request("automatedFault", new { sessionId = gameId, seatId, reasonCode }, cancellation);
    }

    internal static void PreserveLastAutomatedObservation(string profile, JsonElement? last, string gameId, int seatId, string reasonCode)
    {
        try {
            bool matches = last is { ValueKind: JsonValueKind.Array } batch && batch.EnumerateArray().Any(item =>
                item.GetProperty("seatId").GetInt32() == seatId && Text(item.GetProperty("state"), "id") == gameId);
            string directory = Path.Combine(profile, "diagnostics");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "private-last-ai-observation.json"),
                JsonSerializer.Serialize(new { capturedAt = DateTimeOffset.UtcNow, gameId, seatId, reasonCode,
                    authority = "none", phase = matches ? "last-decoded-observation" : "no-matching-decoded-observation",
                    privacy = "Private bot observations; may contain hands and library faces. Review before sharing.", raw = matches ? last : null }));
        } catch (Exception) { /* Diagnostics cannot replace the actual automation fault. */ }
    }
}
