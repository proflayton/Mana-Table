using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mana.Contracts;

public interface ITableEncounterStart
{
    bool SupportsEncounterStart { get; }
    Task<Lobby> BeginEncounterAsync(string tableId, string matchSetupConsent, string? tempoRevision = null);
    Task<Lobby> RetryEncounterStartAsync(string tableId, string attemptId);
    Task<Lobby> CancelEncounterStartAsync(string tableId, string attemptId);
}

[JsonConverter(typeof(EncounterStartPhaseConverter))]
public enum EncounterStartPhase { Preparing, Recording, Starting, Failed, Cancelled }

public sealed class EncounterStartPhaseConverter : JsonConverter<EncounterStartPhase>
{
    public override EncounterStartPhase Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.String ? reader.GetString() switch {
            "preparing" => EncounterStartPhase.Preparing, "recording" => EncounterStartPhase.Recording,
            "starting" => EncounterStartPhase.Starting, "failed" => EncounterStartPhase.Failed,
            "cancelled" => EncounterStartPhase.Cancelled, _ => throw new JsonException("Unknown encounter start phase.")
        } : throw new JsonException("Unknown encounter start phase.");
    public override void Write(Utf8JsonWriter writer, EncounterStartPhase value, JsonSerializerOptions options)
        => writer.WriteStringValue(value switch {
            EncounterStartPhase.Preparing => "preparing", EncounterStartPhase.Recording => "recording",
            EncounterStartPhase.Starting => "starting", EncounterStartPhase.Failed => "failed",
            EncounterStartPhase.Cancelled => "cancelled", _ => throw new JsonException("Unknown encounter start phase.")
        });
}

// An admission attempt is not a rules snapshot or a completed encounter.
// AttemptId is the provider's encounter identity; no viewer/game ID is invented.
public sealed record EncounterStartState([property: JsonRequired] string AttemptId,
    [property: JsonRequired] EncounterStartPhase Phase, [property: JsonRequired] string? Error,
    [property: JsonRequired] bool RetryAllowed);

public static class EncounterStartFacts
{
    public static void Validate(Lobby lobby)
    {
        if (lobby.StartState is not { } start) {
            if (lobby.EncounterStartSupported && lobby.MatchActive
                && lobby.MatchSetup?.RulesetId == MatchRulesets.CooperativeCommander) throw Invalid();
            return;
        }
        if (!lobby.EncounterStartSupported || string.IsNullOrWhiteSpace(lobby.TableId)
            || string.IsNullOrWhiteSpace(start.AttemptId) || start.AttemptId.Length > 200 || !Enum.IsDefined(start.Phase)
            || start.RetryAllowed && (start.Phase != EncounterStartPhase.Failed || lobby.Mode != "hosting")) throw Invalid();
        if (start.Phase == EncounterStartPhase.Failed) {
            if (string.IsNullOrWhiteSpace(start.Error) || start.Error.Length > 1000 || lobby.MatchActive) throw Invalid();
        } else if (start.Error != null || start.RetryAllowed) throw Invalid();
        if (start.Phase == EncounterStartPhase.Cancelled) {
            if (lobby.MatchActive) throw Invalid();
            return;
        }
        if (lobby.MatchSetup?.RulesetId != MatchRulesets.CooperativeCommander || lobby.PartyProgress == null
            || (start.Phase is EncounterStartPhase.Preparing or EncounterStartPhase.Recording) && lobby.MatchActive
            || !lobby.MatchActive && lobby.CanStart) throw Invalid();
        if (lobby.MatchActive && lobby.PartyProgress.EncounterId != start.AttemptId) throw Invalid();
    }

    public static void ValidateMatch(Lobby lobby, GameSnapshot? state)
    {
        Validate(lobby);
        if (lobby.StartState is not { } admission) return;
        if (!lobby.MatchActive) {
            if (state != null) throw Invalid();
            return;
        }
        if (admission.Phase != EncounterStartPhase.Starting || state?.Encounter is not { } encounter
            || encounter.EncounterId != admission.AttemptId || encounter.PartyId != lobby.PartyProgress?.PartyId) throw Invalid();
    }

    private static InvalidOperationException Invalid() => new("Invalid encounter start facts.");
}

public enum EncounterStartFailure { Failed, Cancelled, TimedOut, ScopeChanged }

public sealed class EncounterStartException(EncounterStartFailure failure, string tableId, string attemptId, string message)
    : IOException(message)
{
    public EncounterStartFailure Failure { get; } = failure;
    public string TableId { get; } = tableId;
    public string AttemptId { get; } = attemptId;
}
