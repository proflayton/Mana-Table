using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mana.Contracts;

// Optional, ephemeral table activity. These values grant no game command,
// target, private inspection, history entry or extra decision time.
public interface ITablePresence
{
    bool SupportsTablePresence => true;
    bool SupportsTableChoices => false;
    Task<TablePresenceResult> ExchangePresenceAsync(TablePresenceUpdate update, CancellationToken cancellation = default);
}
public abstract record TablePresenceResult
{
    private TablePresenceResult() { }
    public sealed record Current(TablePresenceFrame Frame) : TablePresenceResult;
    public sealed record Unsupported : TablePresenceResult;
    public sealed record Superseded : TablePresenceResult;
}

// The authority publishes the same current public-priority context to every
// audience. Only PlayerId may publish; all recipients fence cues by this value.
public sealed record PublicInspectionContext([property: JsonRequired] string Id,
    [property: JsonRequired] long BoardRevision, [property: JsonRequired] int PlayerId);
public enum TableInspectionKind { BattlefieldCard, StackItem }
public sealed record TableInspectionSubject(
    [property: JsonRequired, JsonConverter(typeof(TableInspectionKindConverter))] TableInspectionKind Kind,
    [property: JsonRequired] string Id);
public sealed class TableInspectionKindConverter : JsonConverter<TableInspectionKind>
{
    public override TableInspectionKind Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String ? reader.GetString() switch {
            "card" => TableInspectionKind.BattlefieldCard,
            "stack" => TableInspectionKind.StackItem,
            _ => throw new JsonException("Unknown public inspection kind.")
        } : throw new JsonException("Unknown public inspection kind.");
    public override void Write(Utf8JsonWriter writer, TableInspectionKind value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch {
            TableInspectionKind.BattlefieldCard => "card",
            TableInspectionKind.StackItem => "stack",
            _ => throw new JsonException("Unknown public inspection kind.")
        });
}
public sealed record TablePresenceUpdate(string GameId, long BoardRevision, long Sequence,
    string? ContextId, TableInspectionSubject? Subject, string? ChoiceContextId = null, TableChoicePreview? Choice = null);
public sealed record TableInspectionCue([property: JsonRequired] int PlayerId, [property: JsonRequired] string ContextId,
    [property: JsonRequired] TableInspectionSubject Subject, [property: JsonRequired] int RemainingMilliseconds);
public sealed record TablePresenceFrame([property: JsonRequired] string GameId, [property: JsonRequired] long BoardRevision,
    [property: JsonRequired] long Revision, [property: JsonRequired] ImmutableArray<TableInspectionCue> Cues)
{
    // Omitted by older hosts. Never infer draft-sharing support from inspection.
    public ImmutableArray<TableChoiceCue> Choices { get; init; } = [];
}
