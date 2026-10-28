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

    public async Task EnableAutomatedSeatsAsync()
    {
        var owner = current ?? throw new IOException("The engine is not running");
        if (!owner.AutomationAvailable) throw new NotSupportedException("Install a matching engine with automated-seat support.");
        await Request("automatedEnable");
    }

    public async Task<GameSnapshot> StartSimulationAsync(ImmutableArray<string> deckIds)
    {
        if (deckIds.IsDefault || deckIds.Length != 4 || deckIds.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Choose four Commander decks.", nameof(deckIds));
        lock (automatedScopes) automatedScopes.Clear();
        return ForgeSnapshotDecoder.Decode(await Request("automatedSimulationStart", new { decks = deckIds }), spectator: true)
            ?? throw new IOException("The simulation did not start.");
    }
    public async Task<GameSnapshot?> ObserveSimulationAsync() =>
        ForgeSnapshotDecoder.Decode(await Request("automatedSimulationState"), spectator: true);
    public async Task StopSimulationAsync(string gameId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        await Request("automatedSimulationStop", new { sessionId = gameId });
        lock (automatedScopes) automatedScopes.Clear();
    }

    public async Task<ImmutableArray<AutomatedSeatObservation>> ObserveAutomatedSeatsAsync()
    {
        var owner = current ?? throw new IOException("The engine is not running");
        if (!owner.AutomationAvailable) throw new NotSupportedException("This engine has no automated-seat port.");
        JsonElement raw;
        try { raw = await Request("automatedState"); }
        catch (EngineObservationSupersededException) {
            lock (automatedScopes) automatedScopes.Clear();
            throw;
        }
        if (current != owner || owner.Failure != null) throw new IOException("The engine changed during automated observation.");
        ImmutableArray<AutomatedSeatObservation> result;
        try { result = DecodeAutomatedObservations(raw); }
        catch (Exception error) {
            lock (automatedScopes) automatedScopes.Clear();
            PreserveRejectedAutomatedObservation(paths.Profile, raw, error);
            throw;
        }
        lock (automatedScopes) {
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

    internal static ImmutableArray<AutomatedSeatObservation> DecodeAutomatedObservations(JsonElement raw)
    {
        try {
            var result = ImmutableArray.CreateBuilder<AutomatedSeatObservation>();
            foreach (var item in raw.EnumerateArray()) {
                int seat = item.GetProperty("seatId").GetInt32();
                var state = ForgeSnapshotDecoder.Decode(item.GetProperty("state")) ?? throw new IOException("Missing automated seat state.");
                var domain = item.GetProperty("domain").Deserialize<DecisionActionDomain>(AutomationJson) ?? throw new IOException("Missing action domain.");
                var semantics = item.GetProperty("semantics").Deserialize<DecisionSemantics>(AutomationJson) ?? throw new IOException("Missing decision semantics.");
                if (seat < 0 || seat != state.ViewerId || domain.Version < 0 || domain.Sources.IsDefault || domain.Actions.IsDefault
                    || semantics.Options.IsDefault || semantics.LegalTextValues.IsDefault || semantics.UnpaidMana.IsDefault)
                    throw new IOException("Invalid automated seat observation.");
                DecisionOrderingFacts.Validate(state, semantics);
                result.Add(new(seat, state, domain, semantics));
            }
            if (result.Select(item => (item.State.Id, item.SeatId)).Distinct().Count() != result.Count)
                throw new IOException("Duplicate automated seat observation.");
            return result.ToImmutable();
        } catch (Exception error) when (error is IOException or JsonException or InvalidOperationException
            or ArgumentException or FormatException or OverflowException or KeyNotFoundException) {
            throw new ForgeAutomatedObservationException(raw, error);
        }
    }

    public async Task ExecuteAutomatedAsync(int seatId, GameCommand command)
    {
        Dictionary<string, object?> args;
        lock (automatedScopes) {
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
        await Request("automatedAction", args);
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

    public async Task ReportAutomationFaultAsync(string gameId, int seatId, string reasonCode)
    {
        // Only stable diagnostics cross the host port, never policy exception
        // messages or cards private to a different seat.
        if (string.IsNullOrWhiteSpace(reasonCode) || reasonCode.Length > 96
            || reasonCode.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '-')))
            throw new ArgumentException("Use a bounded diagnostic reason code.", nameof(reasonCode));
        // Provider decoding can succeed before frame/strategy validation fails.
        // Keep that accepted observation distinct from a rejected wire batch.
        JsonElement? last;
        lock (automatedScopes) last = lastDecodedAutomatedWire;
        PreserveLastAutomatedObservation(paths.Profile, last, gameId, seatId, reasonCode);
        await Request("automatedFault", new { sessionId = gameId, seatId, reasonCode });
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
