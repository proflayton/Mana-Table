using System.Text.Json;
using Mana.Contracts;

namespace Mana.Forge;

public sealed partial class ForgeEngine
{
    private string presenceUnsupportedGame = "";
    public bool SupportsTablePresence => current is { TablePresenceAvailable: true, Failure: null } owner
        && owner.Ready.Task.IsCompletedSuccessfully && !disposed
        && owner.Commands.ObservedGameId is { Length: > 0 } game && presenceUnsupportedGame != game;
    public bool SupportsTableChoices => SupportsTablePresence && current is { TableChoicesAvailable: true };

    public async Task<TablePresenceResult> ExchangePresenceAsync(TablePresenceUpdate update, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        cancellation.ThrowIfCancellationRequested();
        if (!SupportsTablePresence) return new TablePresenceResult.Unsupported();
        if (!InspectionId(update.GameId) || update.BoardRevision < 0 || update.Sequence <= 0
            || update.ContextId != null && !InspectionId(update.ContextId)
            || update.Subject is { } subject && (!InspectionId(subject.Id) || !Enum.IsDefined(subject.Kind) || update.ContextId == null)
            || update.ChoiceContextId != null && !InspectionId(update.ChoiceContextId)
            || update.Choice is { } choice && (update.ChoiceContextId == null || !ValidChoice(choice)))
            throw new ArgumentException("Invalid table inspection update.", nameof(update));
        var owner = current!;
        long audience = owner.Commands.AudienceGeneration;
        var observed = owner.Commands.ObservedState;
        if (observed == null || observed.Id != update.GameId) return new TablePresenceResult.Superseded();
        // Caller retirement never interrupts a shared protocol write or closes
        // the rules pipe. The provider's ordinary bounded request owns the work.
        JsonElement raw;
        try {
            raw = await Request("tablePresence", new {
                gameId = update.GameId, boardRevision = update.BoardRevision, sequence = update.Sequence, contextId = update.ContextId,
                subject = update.Subject == null ? null : new {
                    kind = update.Subject.Kind == TableInspectionKind.BattlefieldCard ? "card" : "stack", id = update.Subject.Id
                },
                choiceContextId = SupportsTableChoices ? update.ChoiceContextId : null,
                choice = !SupportsTableChoices || update.Choice == null ? null : new {
                    source = new { kind = update.Choice.Source.Kind == TableInspectionKind.BattlefieldCard ? "card" : "stack", id = update.Choice.Source.Id },
                    target = update.Choice.Target == null ? null : new {
                        kind = update.Choice.Target.Kind switch { TableChoiceTargetKind.BattlefieldCard => "card", TableChoiceTargetKind.StackItem => "stack", _ => "player" },
                        id = update.Choice.Target.Id
                    }
                }
            }).ConfigureAwait(false);
        } catch (EngineObservationSupersededException) { return new TablePresenceResult.Superseded(); }
        cancellation.ThrowIfCancellationRequested();
        if (current != owner || owner.Failure != null || owner.Commands.AudienceGeneration != audience
            || owner.Commands.ObservedState is not { } latest || latest.Id != update.GameId || latest.ViewerId != observed.ViewerId)
            return new TablePresenceResult.Superseded();
        if (raw.ValueKind == JsonValueKind.Null) {
            // The local adapter can relay an older TCP host. Its handshake,
            // rather than the local process's feature list, controls support.
            presenceUnsupportedGame = update.GameId; return new TablePresenceResult.Unsupported();
        }
        return new TablePresenceResult.Current(DecodeTablePresence(raw, update.GameId));
    }

    internal static TablePresenceFrame DecodeTablePresence(JsonElement raw, string gameId)
    {
        try {
            if (raw.ValueKind != JsonValueKind.Object || raw.GetRawText().Length > 8192) throw new JsonException();
            var frame = raw.Deserialize<TablePresenceFrame>(Json) ?? throw new JsonException();
            if (frame.GameId != gameId || !InspectionId(frame.GameId) || frame.BoardRevision < 0 || frame.Revision < 0
                || frame.Cues.IsDefault || frame.Cues.Length > 6 || frame.Cues.Any(cue => cue == null || cue.PlayerId < 0
                    || !InspectionId(cue.ContextId) || cue.RemainingMilliseconds is < 1 or > 1500
                    || cue.Subject == null || !Enum.IsDefined(cue.Subject.Kind) || !InspectionId(cue.Subject.Id))
                || frame.Cues.Select(cue => cue.PlayerId).Distinct().Count() != frame.Cues.Length
                || frame.Choices.IsDefault || frame.Choices.Length > 6 || frame.Choices.Any(cue => cue == null || cue.PlayerId < 0
                    || !InspectionId(cue.ContextId) || cue.RemainingMilliseconds is < 1 or > 1500 || !ValidChoice(cue.Preview))
                || frame.Choices.Select(cue => cue.PlayerId).Distinct().Count() != frame.Choices.Length) throw new JsonException();
            return frame;
        } catch (JsonException error) { throw new IOException("The rules adapter returned invalid public table activity.", error); }
    }
    private static bool InspectionId(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128
        && !value.Any(char.IsControl);
    private static bool ValidChoice(TableChoicePreview? preview) => preview?.Source is { } source
        && InspectionId(source.Id) && Enum.IsDefined(source.Kind) && (preview.Target == null
            || InspectionId(preview.Target.Id) && Enum.IsDefined(preview.Target.Kind)
                && (preview.Target.Kind != TableChoiceTargetKind.Player || PublicChoiceFacts.PlayerId(preview.Target.Id, out _)));
}
