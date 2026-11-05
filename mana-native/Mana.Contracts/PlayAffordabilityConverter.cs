using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mana.Contracts;

// This optional presentation fact must never inherit enum zero (Payable) from
// absent, future or malformed provider data. It does not authorize a command.
internal sealed class PlayAffordabilityConverter : JsonConverter<ActionAffordability>
{
    public PlayAffordabilityConverter() { }
    public override bool HandleNull => true;

    public override ActionAffordability Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return reader.GetString() switch {
            "Payable" => ActionAffordability.Payable,
            "Unavailable" => ActionAffordability.Unavailable,
            _ => ActionAffordability.Unknown
        };
        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) {
            using var ignored = JsonDocument.ParseValue(ref reader);
        }
        return ActionAffordability.Unknown;
    }

    public override void Write(Utf8JsonWriter writer, ActionAffordability value, JsonSerializerOptions options)
        => writer.WriteStringValue(Enum.IsDefined(value) ? value.ToString() : nameof(ActionAffordability.Unknown));
}
