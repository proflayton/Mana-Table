using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Mana.Contracts;

// An exact output choice for an independently offered mana action, never a
// source handle, spending permission, or request to create arbitrary mana.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ManaOutputChoice([property: JsonPropertyName("colors")] ImmutableDictionary<string, int> Colors)
{
    public bool IsValid() => Colors is { Count: > 0 and <= 6 }
        && Colors.All(pair => pair.Key is "W" or "U" or "B" or "R" or "G" or "C" && pair.Value > 0)
        && Colors.Values.Sum(amount => (long)amount) <= 1024;
}
