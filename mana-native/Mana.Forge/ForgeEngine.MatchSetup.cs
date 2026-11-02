using Mana.Contracts;

namespace Mana.Forge;

public sealed partial class ForgeEngine
{
    public bool SupportsMatchSetup => current is { MatchSetupAvailable: true, Failure: null };

    public async Task<Lobby> ConfigureMatchAsync(string tableId, long expectedLobbyRevision, MatchSetupSpecification specification)
    {
        if (string.IsNullOrWhiteSpace(tableId) || expectedLobbyRevision < 0) throw new ArgumentException("Match setup needs the current table and lobby revision.");
        ArgumentNullException.ThrowIfNull(specification);
        MatchSetupFacts.Validate(specification);
        var owner = current;
        if (owner is not { MatchSetupAvailable: true, Failure: null }) throw new NotSupportedException("This provider does not expose match setup.");
        var state = MapLobby(await Request("configureMatch", EncodeMatchSetup(tableId, expectedLobbyRevision, specification)));
        if (current != owner || owner.Failure != null || state.TableId != tableId || state.Revision < expectedLobbyRevision
            || state.MatchSetup is not { } actual || actual.RulesetId != specification.RulesetId
            || !actual.Seats.OrderBy(seat => seat.Index).SequenceEqual(specification.Seats.OrderBy(seat => seat.Index)))
            throw new IOException("The match setup provider or table changed.");
        return state;
    }

    internal static object EncodeMatchSetup(string tableId, long expectedLobbyRevision, MatchSetupSpecification specification)
        => new { tableId, expectedLobbyRevision, specification = new {
            rulesetId = specification.RulesetId,
            seats = specification.Seats.Select(seat => new { index = seat.Index, teamId = seat.TeamId, role = seat.Role }).ToArray()
        } };

    public async Task<Lobby> RetryEncounterSaveAsync(string tableId, string encounterId)
    {
        if (string.IsNullOrWhiteSpace(tableId) || string.IsNullOrWhiteSpace(encounterId)) throw new ArgumentException("Retry needs the current table and encounter.");
        var owner = current;
        if (owner is not { MatchSetupAvailable: true, Failure: null }) throw new NotSupportedException("This provider does not expose encounter checkpoints.");
        var state = MapLobby(await Request("retryEncounterSave", new { tableId, encounterId }));
        if (current != owner || owner.Failure != null || state.TableId != tableId || state.PartyProgress?.EncounterId != encounterId)
            throw new IOException("The encounter save provider or scope changed.");
        return state;
    }

    public async Task SetMatchReadyAsync(string tableId, string matchSetupConsent, bool ready, string? tempoRevision = null)
    {
        if (string.IsNullOrWhiteSpace(tableId) || ready && string.IsNullOrWhiteSpace(matchSetupConsent))
            throw new ArgumentException("Ready needs the current table and reviewed match setup.");
        var owner = current;
        if (owner is not { MatchSetupAvailable: true, Failure: null }) throw new NotSupportedException("This provider does not expose match setup consent.");
        await Request("multiplayerReady", EncodeMatchReady(tableId, matchSetupConsent, ready, tempoRevision));
        if (current != owner || owner.Failure != null) throw new IOException("The match setup provider changed.");
    }

    internal static object EncodeMatchReady(string tableId, string matchSetupConsent, bool ready, string? tempoRevision)
        => new { tableId, matchSetupConsent, ready, tempoRevision };
}
