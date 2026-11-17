using System.Collections.Immutable;

namespace Mana.Contracts;

/// <summary>Bounded direct continuous modifier, shared with the portable
/// attachment-capability/1.0 contract. Knowledge is mechanical coverage, never
/// a prediction that the spell resolves or an estimate of strategic value.</summary>
public sealed record AttachmentModifier(SemanticKnowledge Knowledge, int? PowerDelta,
    int? ToughnessDelta, ImmutableArray<string> GrantedKeywords)
{
    public const int MaximumDelta = 1000;
    public static AttachmentModifier Unknown { get; } = new(SemanticKnowledge.Unknown, null, null, []);

    public static bool SupportedKeyword(string value) => value is "flying" or "trample" or "reach"
        or "vigilance" or "lifelink" or "deathtouch" or "first strike" or "double strike" or "indestructible";

    public bool IsValid => Enum.IsDefined(Knowledge) && !GrantedKeywords.IsDefault
        && GrantedKeywords.Length <= 9 && GrantedKeywords.All(SupportedKeyword)
        && GrantedKeywords.Distinct(StringComparer.Ordinal).Count() == GrantedKeywords.Length
        && (Knowledge == SemanticKnowledge.Known
            ? PowerDelta is >= -MaximumDelta and <= MaximumDelta
                && ToughnessDelta is >= -MaximumDelta and <= MaximumDelta
                && (PowerDelta != 0 || ToughnessDelta != 0 || !GrantedKeywords.IsEmpty)
            : PowerDelta == null && ToughnessDelta == null && GrantedKeywords.IsEmpty);
}
