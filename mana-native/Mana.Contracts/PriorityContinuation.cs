using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mana.Contracts;

[JsonConverter(typeof(PriorityContinuationModeConverter))]
public enum PriorityContinuationMode { NoResponse, Quiet }
[JsonConverter(typeof(PriorityContinuationStatusConverter))]
public enum PriorityContinuationStatus { Idle, Active, Held, Revoked }
[JsonConverter(typeof(PriorityResponseAvailabilityConverter))]
public enum PriorityResponseAvailability { Absent, Available, Unknown }

public sealed class PriorityContinuationModeConverter : ClosedPriorityEnumConverter<PriorityContinuationMode> { }
public sealed class PriorityContinuationStatusConverter : ClosedPriorityEnumConverter<PriorityContinuationStatus> { }
public sealed class PriorityResponseAvailabilityConverter : ClosedPriorityEnumConverter<PriorityResponseAvailability> { }
public class ClosedPriorityEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    public override T Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.String && Enum.TryParse<T>(reader.GetString(), out var value)
            && Enum.IsDefined(value) && reader.GetString() == value.ToString() ? value : throw new JsonException("Unknown priority continuation value.");
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        if (!Enum.IsDefined(value)) throw new JsonException("Unknown priority continuation value.");
        writer.WriteStringValue(value.ToString());
    }
}

// Sequence is the connection's instruction high-water fence, including prompt-
// free withdrawal. A terminal state can retain an older accepted grant's ID.
// Phase stops retain existing saved behavior: these are OWN-TURN phase stops.
public sealed record PriorityContinuationState([property: JsonRequired] long Sequence,
    [property: JsonRequired] string GrantId, [property: JsonRequired] PriorityContinuationMode? Mode,
    [property: JsonRequired] PriorityContinuationStatus Status, [property: JsonRequired] string ReasonCode,
    [property: JsonRequired] int AutomaticPasses, [property: JsonRequired] ImmutableArray<string> PhaseStops);

public sealed record GrantPriorityContinuation(string GameId, string DecisionId, long Sequence, string GrantId,
    PriorityContinuationMode Mode, ImmutableArray<string> PhaseStops, string QuietYieldToken = "") : DecisionCommand(GameId, DecisionId);
public sealed record RevokePriorityContinuation(string GameId, long ThroughSequence) : GameCommand(GameId);
public sealed record AcknowledgePriorityObservation(string GameId, long BoardRevision, long PublicActivitySequence) : GameCommand(GameId);
public sealed record PriorityObservationState([property: JsonRequired] long BoardRevision,
    [property: JsonRequired] long PublicActivitySequence, [property: JsonRequired] long AcknowledgedBoardRevision,
    [property: JsonRequired] long AcknowledgedPublicActivitySequence, [property: JsonRequired] bool Registered,
    [property: JsonRequired] bool Overflowed);

public static class PriorityContinuationFacts
{
    public static bool Identifier(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && !value.Any(char.IsControl);
    public static bool Phase(string? phase) => phase is "UPKEEP" or "DRAW" or "MAIN1" or "COMBAT_BEGIN" or "MAIN2" or "END_OF_TURN";
    public static bool Stops(ImmutableArray<string> stops) => !stops.IsDefault && stops.Length <= 6
        && stops.All(Phase) && stops.Distinct(StringComparer.Ordinal).Count() == stops.Length;
    public static bool CanGrant(GameSnapshot state, GrantPriorityContinuation command)
        => state.PriorityContinuation is { } prior && command.Sequence > prior.Sequence
            && state.PriorityObservation is not { Registered: false } and not { Overflowed: true }
            && state.Status == "playing" && state.Turn > 0 && state.Viewer is { Eliminated: false }
            && state.Decision?.Payload is PriorityPayload { Pass.Enabled: true } priority
            && state.FiniteStackYield is not { Status: FiniteStackYieldStatus.Active }
            && (command.Mode == PriorityContinuationMode.NoResponse
                || state.Stack.IsEmpty && command.QuietYieldToken == priority.QuietYieldToken && Identifier(priority.QuietYieldToken));

    public static void Validate(GameSnapshot state)
    {
        if (state.PriorityObservation is { } observation
            && (observation.BoardRevision != state.BoardRevision || observation.BoardRevision < 0 || observation.PublicActivitySequence < 0
                || observation.AcknowledgedBoardRevision < 0 || observation.AcknowledgedPublicActivitySequence < 0
                || observation.AcknowledgedBoardRevision > observation.BoardRevision
                || observation.AcknowledgedPublicActivitySequence > observation.PublicActivitySequence))
            throw new ArgumentException("Priority observation credit must identify an actual published frame and an acknowledged prefix.", nameof(state));
        if (state.Decision?.Payload is PriorityPayload { ResponseAvailability: { } availability } priority
            && (!Enum.IsDefined(availability) || priority.CanAutoPass != (availability == PriorityResponseAvailability.Absent)))
            throw new ArgumentException("Priority response availability contradicts the published automatic-pass permission.", nameof(state));
        if (state.PriorityContinuation is not { } value) return;
        if (value.Sequence < 0 || value.GrantId == null || !Enum.IsDefined(value.Status)
            || value.Mode is { } mode && !Enum.IsDefined(mode) || value.ReasonCode == null
            || value.ReasonCode.Length > 128 || value.ReasonCode.Any(char.IsControl)
            || value.AutomaticPasses < 0 || !Stops(value.PhaseStops)
            || value.GrantId.Length == 0 && (value.Mode != null || !value.PhaseStops.IsEmpty || value.AutomaticPasses != 0
                || value.Status is PriorityContinuationStatus.Active or PriorityContinuationStatus.Held)
            || value.GrantId.Length > 0 && (!Identifier(value.GrantId) || value.Mode == null || value.Sequence == 0)
            || value.Status == PriorityContinuationStatus.Idle && value.GrantId.Length > 0
            || value.Status is PriorityContinuationStatus.Active or PriorityContinuationStatus.Held
                && state.FiniteStackYield is { Status: FiniteStackYieldStatus.Active })
            throw new ArgumentException("A priority continuation must preserve one audience-owned instruction and withdrawal fence.", nameof(state));
    }
}

public static class HumanParticipationFacts
{
    public static void Validate(GameSnapshot state)
    {
        if (state.HumanPlayerIds is not { } ids) return;
        if (ids.IsDefault || ids.Length > state.Players.Length || ids.Distinct().Count() != ids.Length
            || ids.Any(id => id < 0 || !state.Players.Any(player => player.Id == id)))
            throw new ArgumentException("The human participant roster must identify distinct current registered players.", nameof(state));
    }
    public static bool HasOtherActiveHuman(GameSnapshot state)
        => state.HumanPlayerIds is not { } ids || ids.Any(id => id != state.ViewerId
            && state.Players.Any(player => player.Id == id && !player.Eliminated));
}
