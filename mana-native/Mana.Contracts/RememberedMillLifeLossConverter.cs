using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mana.Contracts;

// This optional object's grammar is closed. In particular missing count is
// never a certified zero and duplicate keys cannot choose competing evidence.
public sealed class RememberedMillLifeLossConverter : JsonConverter<RememberedMillLifeLoss>
{
    public override RememberedMillLifeLoss Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var fields = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw Invalid();
        foreach (var field in document.RootElement.EnumerateObject())
            if (!fields.TryAdd(field.Name, field.Value)) throw Invalid();
        if (fields.Count != 5 || !fields.ContainsKey("version") || !fields.ContainsKey("sourceCardId")
            || !fields.ContainsKey("counterType") || !fields.ContainsKey("observedCount") || !fields.ContainsKey("lifeLossOperationId")) throw Invalid();
        try {
            return new(fields["version"].GetInt32(), fields["sourceCardId"].GetString() ?? throw Invalid(),
                fields["counterType"].GetString() ?? throw Invalid(), fields["observedCount"].GetInt32(), fields["lifeLossOperationId"].GetInt32());
        } catch (Exception error) when (error is InvalidOperationException or FormatException or OverflowException) {
            throw new JsonException("Invalid remembered mill/life-loss fields.", error);
        }
    }

    public override void Write(Utf8JsonWriter writer, RememberedMillLifeLoss value, JsonSerializerOptions options)
    {
        writer.WriteStartObject(); writer.WriteNumber("version", value.Version); writer.WriteString("sourceCardId", value.SourceCardId);
        writer.WriteString("counterType", value.CounterType); writer.WriteNumber("observedCount", value.ObservedCount);
        writer.WriteNumber("lifeLossOperationId", value.LifeLossOperationId); writer.WriteEndObject();
    }

    private static JsonException Invalid() => new("Remembered mill/life-loss evidence requires exactly five distinct typed fields.");
}
