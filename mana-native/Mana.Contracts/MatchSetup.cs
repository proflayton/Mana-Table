using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mana.Contracts;

public interface ITableMatchSetup
{
    bool SupportsMatchSetup { get; }
    Task<Lobby> ConfigureMatchAsync(string tableId, long expectedLobbyRevision, MatchSetupSpecification specification);
    Task<Lobby> RetryEncounterSaveAsync(string tableId, string encounterId);
    Task SetMatchReadyAsync(string tableId, string matchSetupConsent, bool ready, string? tempoRevision = null);
}

public static class MatchRulesets
{
    public const string FreeForAllCommander = "commander.free-for-all.v1";
    // Individual turns and independent life/resources. This is not Two-Headed Giant.
    public const string CooperativeCommander = "commander.cooperative.v1";
}

[JsonConverter(typeof(MatchSeatRoleConverter))]
public enum MatchSeatRole { Participant, Party, Opposition }

public sealed class MatchSeatRoleConverter : JsonConverter<MatchSeatRole>
{
    public override MatchSeatRole Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.String ? reader.GetString() switch {
            "participant" => MatchSeatRole.Participant,
            "party" => MatchSeatRole.Party,
            "opposition" => MatchSeatRole.Opposition,
            _ => throw new JsonException("Unknown match seat role.")
        } : throw new JsonException("Unknown match seat role.");

    public override void Write(Utf8JsonWriter writer, MatchSeatRole value, JsonSerializerOptions options)
        => writer.WriteStringValue(value switch {
            MatchSeatRole.Participant => "participant",
            MatchSeatRole.Party => "party",
            MatchSeatRole.Opposition => "opposition",
            _ => throw new JsonException("Unknown match seat role.")
        });
}

public sealed record MatchSeatAssignment([property: JsonRequired] int Index,
    [property: JsonRequired] int TeamId, [property: JsonRequired] MatchSeatRole Role);
public sealed record MatchSetupSpecification([property: JsonRequired] string RulesetId,
    [property: JsonRequired] ImmutableArray<MatchSeatAssignment> Seats);
public sealed record MatchSetupCapabilities([property: JsonRequired] ImmutableArray<string> RulesetIds,
    [property: JsonRequired] int MaximumSeats, [property: JsonRequired] int MaximumPartySeats,
    [property: JsonRequired] int MaximumOppositionSeats);

public static class MatchSetupFacts
{
    public static void Validate(MatchSetupCapabilities capabilities)
    {
        if (capabilities.RulesetIds.IsDefaultOrEmpty || capabilities.RulesetIds.Any(string.IsNullOrWhiteSpace)
            || capabilities.RulesetIds.Distinct(StringComparer.Ordinal).Count() != capabilities.RulesetIds.Length
            || capabilities.MaximumSeats is < 2 or > 6 || capabilities.MaximumPartySeats is < 1 or > 4
            || capabilities.MaximumOppositionSeats is < 1 or > 2
            || capabilities.MaximumPartySeats >= capabilities.MaximumSeats
            || capabilities.MaximumOppositionSeats >= capabilities.MaximumSeats) throw Invalid();
    }

    public static void Validate(MatchSetupSpecification specification, MatchSetupCapabilities? capabilities = null)
    {
        if (capabilities != null) {
            Validate(capabilities);
            if (!capabilities.RulesetIds.Contains(specification.RulesetId)) throw Invalid();
        }
        if (string.IsNullOrWhiteSpace(specification.RulesetId) || specification.Seats.IsDefault
            || specification.Seats.Length < 2 || specification.Seats.Length > (capabilities?.MaximumSeats ?? 6)
            || specification.Seats.Any(seat => seat == null || seat.TeamId < 0 || !Enum.IsDefined(seat.Role))
            || specification.Seats.Where((seat, index) => seat.Index != index).Any())
            throw Invalid();
        if (specification.RulesetId == MatchRulesets.FreeForAllCommander) {
            if (specification.Seats.Any(seat => seat.Role != MatchSeatRole.Participant)
                || specification.Seats.Select(seat => seat.TeamId).Distinct().Count() != specification.Seats.Length) throw Invalid();
        } else if (specification.RulesetId == MatchRulesets.CooperativeCommander) {
            var party = specification.Seats.Where(seat => seat.Role == MatchSeatRole.Party).ToArray();
            var opposition = specification.Seats.Where(seat => seat.Role == MatchSeatRole.Opposition).ToArray();
            if (party.Length < 1 || party.Length > (capabilities?.MaximumPartySeats ?? 4)
                || opposition.Length < 1 || opposition.Length > (capabilities?.MaximumOppositionSeats ?? 2)
                || specification.Seats[0].Role != MatchSeatRole.Party
                || party.Length + opposition.Length != specification.Seats.Length
                || party.Select(seat => seat.TeamId).Distinct().Count() != 1
                || opposition.Select(seat => seat.TeamId).Distinct().Count() != 1
                || party[0].TeamId == opposition[0].TeamId) throw Invalid();
        }
    }

    public static void Validate(Lobby lobby)
    {
        if (lobby.MatchSetupCapabilities is not { } capabilities) {
            if (lobby.MatchSetup != null) throw Invalid();
            return;
        }
        Validate(capabilities);
        if (lobby.Mode == "idle" && lobby.MatchSetup == null && lobby.Seats.IsEmpty) return;
        if (lobby.MatchSetup is not { } specification || string.IsNullOrWhiteSpace(lobby.TableId)) throw Invalid();
        if (specification.RulesetId == MatchRulesets.CooperativeCommander && string.IsNullOrWhiteSpace(lobby.MatchSetupConsent)) throw Invalid();
        Validate(specification, capabilities);
        if (lobby.Seats.IsDefault || lobby.Seats.Length != specification.Seats.Length
            || lobby.Seats.Any(seat => seat == null || string.IsNullOrWhiteSpace(seat.ParticipantId) || seat.TeamId is not >= 0 || seat.Role == null)
            || lobby.Seats.Select(seat => seat.ParticipantId).Distinct(StringComparer.Ordinal).Count() != lobby.Seats.Length) throw Invalid();
        foreach (var assignment in specification.Seats) {
            var seats = lobby.Seats.Where(seat => seat.Index == assignment.Index).ToArray();
            if (seats.Length != 1 || seats[0].TeamId != assignment.TeamId || seats[0].Role != assignment.Role
                || assignment.Role == MatchSeatRole.Opposition && seats[0].Type != "AI") throw Invalid();
        }
    }

    private static InvalidOperationException Invalid() => new("Invalid match setup facts.");
}
