using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mana.Contracts;

// Cosmetic consideration of public objects, separate from an assigned attack,
// block or rules target. No action handle, coordinates or hidden source identity.
[JsonConverter(typeof(PublicChoiceKindConverter))]
public enum PublicChoiceKind { Attack, Block, Target }
public sealed class PublicChoiceKindConverter : JsonConverter<PublicChoiceKind>
{
    public override PublicChoiceKind Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String ? reader.GetString() switch {
            "attack" => PublicChoiceKind.Attack, "block" => PublicChoiceKind.Block, "target" => PublicChoiceKind.Target,
            _ => throw new JsonException("Unknown public choice kind.")
        } : throw new JsonException("Unknown public choice kind.");
    public override void Write(Utf8JsonWriter writer, PublicChoiceKind value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch {
            PublicChoiceKind.Attack => "attack", PublicChoiceKind.Block => "block", PublicChoiceKind.Target => "target",
            _ => throw new JsonException("Unknown public choice kind.")
        });
}
public sealed record PublicChoiceContext([property: JsonRequired] string Id,
    [property: JsonRequired] long BoardRevision, [property: JsonRequired] int PlayerId,
    [property: JsonRequired] PublicChoiceKind Kind, TableInspectionSubject? Source = null);
[JsonConverter(typeof(TableChoiceTargetKindConverter))]
public enum TableChoiceTargetKind { BattlefieldCard, StackItem, Player }
public sealed class TableChoiceTargetKindConverter : JsonConverter<TableChoiceTargetKind>
{
    public override TableChoiceTargetKind Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String ? reader.GetString() switch {
            "card" => TableChoiceTargetKind.BattlefieldCard, "stack" => TableChoiceTargetKind.StackItem,
            "player" => TableChoiceTargetKind.Player, _ => throw new JsonException("Unknown public choice target.")
        } : throw new JsonException("Unknown public choice target.");
    public override void Write(Utf8JsonWriter writer, TableChoiceTargetKind value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch {
            TableChoiceTargetKind.BattlefieldCard => "card", TableChoiceTargetKind.StackItem => "stack",
            TableChoiceTargetKind.Player => "player", _ => throw new JsonException("Unknown public choice target.")
        });
}
public sealed record TableChoiceTarget([property: JsonRequired] TableChoiceTargetKind Kind,
    [property: JsonRequired] string Id);
public sealed record TableChoicePreview([property: JsonRequired] TableInspectionSubject Source, TableChoiceTarget? Target = null);
public sealed record TableChoiceCue([property: JsonRequired] int PlayerId, [property: JsonRequired] string ContextId,
    [property: JsonRequired] TableChoicePreview Preview, [property: JsonRequired] int RemainingMilliseconds);
