using System.Text.Json;
using Mana.Contracts;

namespace Mana.Forge;

public sealed partial class ForgeEngine : IOwnedDecisionObservations
{
    public bool SupportsOwnedDecisionObservations => !disposed
        && current is { OwnedDecisionAvailable: true, Failure: null } owner
        && owner.Ready.Task.IsCompletedSuccessfully;

    public async Task<OwnedDecisionObservation?> ObserveDecisionAsync(CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(disposed, this);
        var owner = current ?? throw new IOException("The engine is not running");
        if (!owner.OwnedDecisionAvailable)
            throw new NotSupportedException("This provider does not expose owned decision observations.");
        long generation, audience;
        GameSnapshot? anchor;
        lock (worldScopeGate) {
            generation = worldGeneration; audience = owner.Commands.AudienceGeneration; anchor = owner.Commands.ObservedState;
        }
        JsonElement raw;
        try { raw = await Request("matchDecisionObservation", owner.PrivateActionFacesAvailable ? new { privateActionFaces = true } : null,
            cancellation: cancellation); }
        catch (EngineObservationSupersededException) {
            owner.Commands.ClearIfCurrent(anchor, audience);
            throw;
        }
        lock (worldScopeGate) {
            cancellation.ThrowIfCancellationRequested();
            if (current != owner || owner.Failure != null || worldGeneration != generation)
                throw new IOException("The decision observation connection or match changed.");
            if (owner.Commands.AudienceGeneration != audience) throw new EngineObservationSupersededException();
            OwnedDecisionObservation? result;
            try { result = DecodeOwnedDecisionObservation(raw, owner.CertifiedUndoAvailable); }
            catch {
                // A malformed current observation withdraws its authority. An
                // old read can never clear a newly bound audience's scope.
                owner.Commands.ClearIfCurrent(anchor, audience);
                throw;
            }
            cancellation.ThrowIfCancellationRequested();
            bool admitted = result == null ? owner.Commands.ClearIfCurrent(anchor, audience)
                : owner.Commands.Observe(result.State, audience);
            if (!admitted) throw new EngineObservationSupersededException();
            return result;
        }
    }

    // The same immutable row crosses the human and automated observation ports.
    // Port owners select audiences and enforce authority; this decoder grants
    // no ability to choose a seat and creates no gameplay command.
    internal static OwnedDecisionObservation? DecodeOwnedDecisionObservation(JsonElement raw, bool certifiedUndo = false)
    {
        if (raw.ValueKind == JsonValueKind.Null) return null;
        try {
            var state = ForgeSnapshotDecoder.Decode(raw.GetProperty("state"), certifiedUndo: certifiedUndo)
                ?? throw new IOException("Missing owned decision state.");
            var domain = raw.GetProperty("domain").Deserialize<DecisionActionDomain>(AutomationJson)
                ?? throw new IOException("Missing owned action domain.");
            var semantics = raw.GetProperty("semantics").Deserialize<DecisionSemantics>(AutomationJson)
                ?? throw new IOException("Missing owned decision semantics.");
            if (state.ViewerId < 0 || domain.Version < 0 || !Enum.IsDefined(domain.Coverage)
                || domain.Sources.IsDefault || domain.Actions.IsDefault || domain.PoolManaColors.IsDefault
                || semantics.Options.IsDefault || semantics.LegalTextValues.IsDefault || semantics.UnpaidMana.IsDefault)
                throw new IOException("Invalid owned decision observation.");
            if (state.Decision == null && (domain.Coverage != ActionDomainCoverage.NotApplicable
                || !domain.Sources.IsEmpty || !domain.Actions.IsEmpty || !domain.PoolManaColors.IsEmpty))
                throw new IOException("An audience without a decision cannot acquire action offers.");
            DecisionOrderingFacts.Validate(state, semantics);
            return new(state, domain, semantics);
        } catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException
            or FormatException or OverflowException or KeyNotFoundException) {
            throw new IOException("The rules provider published an invalid owned decision observation.", error);
        }
    }
}
