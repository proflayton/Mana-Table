using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Mana.Contracts;

// The remaining bill of the current optional mana-reduction selection. An
// absent progress object is unknown; a present empty bill is exactly reduced.
// These facts never grant a completion or a card-selection command.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ManaReductionProgress
{
    public ImmutableArray<ManaBillPart> RemainingManaParts { get; }

    [JsonConstructor]
    public ManaReductionProgress(ImmutableArray<ManaBillPart> remainingManaParts)
    {
        if (remainingManaParts.IsDefault || remainingManaParts.Length > 128
            || remainingManaParts.Any(part => part == null || part.Count <= 0 || string.IsNullOrWhiteSpace(part.Symbol)
                || part.Symbol.Length is < 3 or > 32 || part.Symbol[0] != '{' || part.Symbol[^1] != '}')
            || remainingManaParts.Select(part => part.Symbol).Distinct(StringComparer.Ordinal).Count() != remainingManaParts.Length)
            throw new ArgumentException("Mana-reduction progress must retain a finite exact counted bill.", nameof(remainingManaParts));
        RemainingManaParts = remainingManaParts;
    }
}
