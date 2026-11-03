using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Mana.Contracts;

namespace Mana.Forge;

public sealed partial class ForgeEngine
{
    private long simulationGeneration;
    private readonly SemaphoreSlim simulationLifecycle = new(1);
    public bool SupportsConfiguredSimulation => current is {
        AutomationAvailable: true, ConfiguredSimulationAvailable: true, Failure: null };

    public async Task<ConfiguredSimulationStart> StartConfiguredSimulationAsync(ConfiguredSimulationSetup setup)
    {
        ConfiguredSimulationFacts.ValidateSetup(setup);
        var owner = current;
        if (owner is not { AutomationAvailable: true, ConfiguredSimulationAvailable: true, Failure: null })
            throw new NotSupportedException("This provider does not expose configured simulations.");
        long generation = 0;
        JsonElement? receipt = null, observed = null;
        try {
            ConfiguredSimulationStart start;
            await simulationLifecycle.WaitAsync();
            try {
                EnsureSimulationConnection(owner);
                receipt = await Request("automatedSimulationStartConfigured", EncodeConfiguredSimulation(setup));
                EnsureSimulationConnection(owner);
                lock (automatedScopes) {
                    generation = Interlocked.Increment(ref simulationGeneration);
                    automatedScopes.Clear();
                }
                start = DecodeConfiguredSimulation(receipt.Value, setup, owner.CertifiedUndoAvailable);
            } finally { simulationLifecycle.Release(); }
            var elapsed = Stopwatch.StartNew();
            var budget = TimeSpan.FromSeconds(30);
            TimeSpan Remaining() => elapsed.Elapsed < budget ? budget - elapsed.Elapsed
                : throw new TimeoutException("The configured simulation did not publish its actual player roster.");
            var state = start.State;
            while (!ConfiguredSimulationFacts.IsReady(start, state)) {
                await Task.Delay(TimeSpan.FromMilliseconds(25)).WaitAsync(Remaining());
                EnsureSimulationOwner(owner, generation);
                observed = await Request("automatedSimulationState").WaitAsync(Remaining());
                EnsureSimulationOwner(owner, generation);
                state = ForgeSnapshotDecoder.Decode(observed.Value, spectator: true, certifiedUndo: owner.CertifiedUndoAvailable)
                    ?? throw new IOException("The configured simulation disappeared before its player roster was published.");
            }
            EnsureSimulationOwner(owner, generation);
            return start with { State = state };
        } catch (Exception error) {
            lock (automatedScopes)
                if (generation != 0 && current == owner && simulationGeneration == generation) automatedScopes.Clear();
            PreserveRejectedConfiguredSimulation(paths.Profile, receipt, observed, error);
            if (error is EngineCommandRejectedException) throw;
            if (error is InvalidOperationException or JsonException or ArgumentException or FormatException or OverflowException)
                throw new IOException("The configured simulation changed its accepted rules-player publication.", error);
            throw;
        }
    }

    private void EnsureSimulationOwner(EngineProcess owner, long generation)
    {
        EnsureSimulationConnection(owner);
        if (simulationGeneration != generation)
            throw new IOException("The configured simulation or its engine process changed during startup.");
    }

    private void EnsureSimulationConnection(EngineProcess owner)
    {
        if (current != owner || owner.Failure != null)
            throw new IOException("The simulation engine process changed.");
    }

    internal static object EncodeConfiguredSimulation(ConfiguredSimulationSetup setup)
    {
        ConfiguredSimulationFacts.ValidateSetup(setup);
        return new { specification = new {
            rulesetId = setup.Specification.RulesetId,
            seats = setup.Specification.Seats.Select(seat => new { index = seat.Index, teamId = seat.TeamId, role = seat.Role }).ToArray()
        }, decks = setup.Decks };
    }

    internal static ConfiguredSimulationStart DecodeConfiguredSimulation(JsonElement raw, ConfiguredSimulationSetup expected,
        bool certifiedUndo = false)
    {
        try {
            var setupWire = ConfiguredProperty(raw, "setup");
            var specification = ConfiguredProperty(setupWire, "specification");
            _ = ConfiguredProperty(specification, "rulesetId");
            foreach (var seat in ConfiguredProperty(specification, "seats").EnumerateArray()) {
                _ = ConfiguredInteger(seat, "index"); _ = ConfiguredInteger(seat, "teamId");
                _ = ConfiguredProperty(seat, "role");
            }
            _ = ConfiguredProperty(setupWire, "decks");
            var setup = Read<ConfiguredSimulationSetup>(setupWire);
            var participants = ConfiguredProperty(raw, "participants").EnumerateArray().Select(participant =>
                new ConfiguredSimulationParticipant(ConfiguredInteger(participant, "index"), ConfiguredInteger(participant, "playerId"))).ToImmutableArray();
            var state = ForgeSnapshotDecoder.Decode(ConfiguredProperty(raw, "state"), spectator: true, certifiedUndo: certifiedUndo)
                ?? throw new IOException("Missing configured simulation state.");
            var result = new ConfiguredSimulationStart(setup, participants, state);
            ConfiguredSimulationFacts.ValidateReceipt(result, expected);
            return result;
        } catch (Exception error) when (error is IOException or JsonException or InvalidOperationException
            or ArgumentException or FormatException or OverflowException or KeyNotFoundException) {
            throw new IOException("The rules provider published an invalid configured simulation receipt.", error);
        }
    }

    private static int ConfiguredInteger(JsonElement raw, string name)
    {
        var value = ConfiguredProperty(raw, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int result) ? result
            : throw new IOException("Missing configured simulation integer identity.");
    }

    private static JsonElement ConfiguredProperty(JsonElement raw, string name)
    {
        if (raw.ValueKind != JsonValueKind.Object) throw new IOException("Missing configured simulation object.");
        bool found = false; JsonElement result = default;
        foreach (var property in raw.EnumerateObject()) {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            if (found) throw new IOException("Ambiguous configured simulation identity.");
            found = true; result = property.Value;
        }
        return found ? result : throw new IOException("Missing configured simulation identity.");
    }

    private static void PreserveRejectedConfiguredSimulation(string profile, JsonElement? receipt, JsonElement? observed, Exception error)
    {
        try {
            string directory = Path.Combine(profile, "diagnostics"); Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "private-rejected-configured-simulation.json"), JsonSerializer.Serialize(new {
                capturedAt = DateTimeOffset.UtcNow, authority = "none",
                privacy = "Private setup and rejected provider evidence; review before sharing.", receipt, observed, error = error.ToString()
            }));
        } catch (Exception) { /* Diagnostic failure must not replace the rejected boundary result. */ }
    }
}
