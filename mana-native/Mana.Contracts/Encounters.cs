using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mana.Contracts;

public sealed record EncounterIdentity(string PartyId, string EncounterId);
public sealed record EncounterParticipant([property: JsonRequired] string ParticipantId, [property: JsonRequired] int Index,
    [property: JsonRequired] int PlayerId, [property: JsonRequired] int TeamId, [property: JsonRequired] MatchSeatRole Role,
    [property: JsonRequired] string DeckSelectionId);
public sealed record EncounterReceipt([property: JsonRequired] string ReceiptId, [property: JsonRequired] string PartyId,
    [property: JsonRequired] string EncounterId, [property: JsonRequired] string GameId,
    [property: JsonRequired] string RulesetId, [property: JsonRequired] int PartyTeamId,
    [property: JsonRequired] MatchOutcome Outcome, [property: JsonRequired] ImmutableArray<EncounterParticipant> Participants,
    [property: JsonRequired] DateTimeOffset CompletedUtc);

[JsonConverter(typeof(EncounterSaveStateConverter))]
public enum EncounterSaveState { Ready, Pending, Failed }
public sealed class EncounterSaveStateConverter : JsonConverter<EncounterSaveState>
{
    public override EncounterSaveState Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.String ? reader.GetString() switch {
            "ready" => EncounterSaveState.Ready,
            "pending" => EncounterSaveState.Pending,
            "failed" => EncounterSaveState.Failed,
            _ => throw new JsonException("Unknown encounter save state.")
        } : throw new JsonException("Unknown encounter save state.");
    public override void Write(Utf8JsonWriter writer, EncounterSaveState value, JsonSerializerOptions options)
        => writer.WriteStringValue(value switch {
            EncounterSaveState.Ready => "ready",
            EncounterSaveState.Pending => "pending",
            EncounterSaveState.Failed => "failed",
            _ => throw new JsonException("Unknown encounter save state.")
        });
}

// Public durable progress belongs to the lobby lifecycle, not the rules board.
// LastReceipt is the last committed receipt, never a pending write proposal.
public sealed record PartyProgress([property: JsonRequired] string PartyId, string? EncounterId,
    [property: JsonRequired] int CompletedEncounters, [property: JsonRequired] int Victories,
    [property: JsonRequired] EncounterSaveState SaveState, string? SaveError,
    [property: JsonRequired] bool RetryAllowed, EncounterReceipt? LastReceipt);

public static class EncounterFacts
{
    public static void Validate(GameSnapshot state)
    {
        if (state.Encounter is not { } encounter) return;
        if (string.IsNullOrWhiteSpace(encounter.PartyId) || string.IsNullOrWhiteSpace(encounter.EncounterId) || state.Players.IsDefault) throw Invalid();
        // Initial linkage may outlive a startup failure before any roster is
        // available. Neither empty lifecycle frame supplies action/result authority.
        if ((state.Status is "starting" or "error") && state.Players.IsEmpty && state.Outcome == null && state.Decision == null) return;
        if (state.Players.IsEmpty
            || state.Players.Any(player => player == null || string.IsNullOrWhiteSpace(player.ParticipantId) || player.TeamId is not >= 0)
            || state.Players.Select(player => player.ParticipantId).Distinct(StringComparer.Ordinal).Count() != state.Players.Length)
            throw Invalid();
    }

    public static void Validate(EncounterReceipt receipt)
    {
        if (string.IsNullOrWhiteSpace(receipt.ReceiptId) || string.IsNullOrWhiteSpace(receipt.PartyId)
            || string.IsNullOrWhiteSpace(receipt.EncounterId) || string.IsNullOrWhiteSpace(receipt.GameId)
            || receipt.RulesetId != MatchRulesets.CooperativeCommander || receipt.PartyTeamId < 0
            || receipt.CompletedUtc == default || receipt.CompletedUtc.Offset != TimeSpan.Zero || receipt.Outcome == null
            || receipt.Participants.IsDefaultOrEmpty
            || receipt.Participants.Any(participant => participant == null || string.IsNullOrWhiteSpace(participant.ParticipantId)
                || string.IsNullOrWhiteSpace(participant.DeckSelectionId) || participant.DeckSelectionId.Length > 200 || participant.PlayerId < 0)
            || receipt.Participants.Select(participant => participant.ParticipantId).Distinct(StringComparer.Ordinal).Count() != receipt.Participants.Length
            || receipt.Participants.Select(participant => participant.PlayerId).Distinct().Count() != receipt.Participants.Length) throw Invalid();
        MatchSetupFacts.Validate(new MatchSetupSpecification(receipt.RulesetId,
            receipt.Participants.Select(participant => new MatchSeatAssignment(participant.Index, participant.TeamId, participant.Role)).ToImmutableArray()));
        if (receipt.Participants.Any(participant => participant.Role == MatchSeatRole.Party && participant.TeamId != receipt.PartyTeamId)) throw Invalid();
        MatchOutcomeFacts.Validate(receipt.Outcome, receipt.GameId,
            receipt.Participants.ToImmutableDictionary(participant => participant.PlayerId, participant => (int?)participant.TeamId));
    }

    public static void Validate(PartyProgress progress)
    {
        if (string.IsNullOrWhiteSpace(progress.PartyId) || progress.EncounterId != null && string.IsNullOrWhiteSpace(progress.EncounterId)
            || progress.CompletedEncounters < 0 || progress.Victories < 0 || progress.Victories > progress.CompletedEncounters
            || !Enum.IsDefined(progress.SaveState)
            || progress.SaveState == EncounterSaveState.Ready && (progress.RetryAllowed || !string.IsNullOrEmpty(progress.SaveError))
            || progress.SaveState == EncounterSaveState.Pending && (progress.EncounterId == null || progress.RetryAllowed || !string.IsNullOrEmpty(progress.SaveError))
            || progress.SaveState == EncounterSaveState.Failed && (progress.EncounterId == null || string.IsNullOrWhiteSpace(progress.SaveError))
            || (progress.CompletedEncounters == 0) != (progress.LastReceipt == null)) throw Invalid();
        if (progress.LastReceipt is not { } receipt) return;
        Validate(receipt);
        if (receipt.PartyId != progress.PartyId
            || progress.SaveState != EncounterSaveState.Ready && receipt.EncounterId == progress.EncounterId
            || receipt.Outcome.WinningTeamIds.Contains(receipt.PartyTeamId) && progress.Victories == 0
            || !receipt.Outcome.WinningTeamIds.Contains(receipt.PartyTeamId) && progress.Victories == progress.CompletedEncounters) throw Invalid();
    }

    private static InvalidOperationException Invalid() => new("Invalid encounter facts.");
}
