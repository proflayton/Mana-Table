using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Mana.Contracts;

/// <summary>Bounded direct continuous modifier, shared with the portable
/// attachment-capability/1.0 contract. Knowledge is mechanical coverage, never
/// a prediction that the spell resolves or an estimate of strategic value.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AttachmentModifier(SemanticKnowledge Knowledge, int? PowerDelta,
    int? ToughnessDelta, ImmutableArray<string> GrantedKeywords)
{
    public const int MaximumDelta = 1000;
    public static AttachmentModifier Unknown { get; } = new(SemanticKnowledge.Unknown, null, null, []);

    public static bool SupportedKeyword(string value) => value is "flying" or "trample" or "reach"
        or "vigilance" or "lifelink" or "deathtouch" or "first strike" or "double strike" or "indestructible";

    public static bool IsCurrentDecisionSource(GameSnapshot state, string id, int? actor)
        => !string.IsNullOrWhiteSpace(id) && id.Length <= 256 && actor == state.ViewerId
            && state.Decision?.Payload is TargetSelectionPayload
            && state.Decision.Context.Source is { Card: { FaceDown: false, Selectable: false, Highlighted: false,
                PlayAffordability: ActionAffordability.Unknown } card } source
            && string.IsNullOrEmpty(card.Key) && CardViews.Identity(card) == id
            && (string.IsNullOrEmpty(source.CardId) || source.CardId == id)
            && (source.ControllerId == null || source.ControllerId == actor);

    [JsonIgnore]
    public bool IsValid => Enum.IsDefined(Knowledge) && !GrantedKeywords.IsDefault
        && GrantedKeywords.Length <= 9 && GrantedKeywords.All(SupportedKeyword)
        && GrantedKeywords.Distinct(StringComparer.Ordinal).Count() == GrantedKeywords.Length
        && (Knowledge == SemanticKnowledge.Known
            ? PowerDelta is >= -MaximumDelta and <= MaximumDelta
                && ToughnessDelta is >= -MaximumDelta and <= MaximumDelta
                && (PowerDelta != 0 || ToughnessDelta != 0 || !GrantedKeywords.IsEmpty)
            : PowerDelta == null && ToughnessDelta == null && GrantedKeywords.IsEmpty);
}
