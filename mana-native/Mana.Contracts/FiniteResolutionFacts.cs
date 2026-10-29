using System.Text.Json.Serialization;
using System.Collections.Immutable;

namespace Mana.Contracts;

// Equality is supplied by the provider's rules structure, current source
// incarnation, controller and committed target terms, never rendered prose.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StackRepetition(string GroupKey, string SourceCardId);

// One currently owned optional negative answer. Neither this record nor a
// matching group authorizes accepting a cost or answering a future occurrence.
// Offer lineage follows actual input episodes, including other eligible groups.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OptionalDeclineOffer(string GroupKey, string PaymentKey,
    string ProducingTriggerExecutionId, string SourceCardId, PaymentPurpose Purpose,
    int PayerId, bool Mandatory, string OfferId, string PreviousOfferId);

public enum OptionalManaPaymentAvailability { Unknown, Payable, Unpayable }

// Present only beside an exact current OptionalDeclineOffer. The original bill
// and policy key remain stable while ordinary gestures pay the remaining bill.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OptionalManaPaymentSupport([property: JsonRequired] string PolicyKey,
    [property: JsonRequired] ImmutableArray<ManaBillPart> OriginalManaParts,
    [property: JsonRequired] OptionalManaPaymentAvailability Availability, [property: JsonRequired] bool CanAutoPay);

public static class FiniteResolutionFacts
{
    public static void Validate(GameSnapshot state)
    {
        if (state.StackActionVersion < 0 || state.ResolutionChoiceVersion < 0
            || state.Combat is { } combat && (combat.DeclarationId == null
                || combat.DeclarationId.Length > 0 && !Key(combat.DeclarationId))) throw Invalid();
        foreach (var item in state.Stack) {
            if (item.Repetition is not { } repetition) continue;
            if (!Key(repetition.GroupKey) || !Key(repetition.SourceCardId)
                || item.Kind != StackActionKind.TriggeredAbility || item.ExecutionId.Length == 0
                || item.Card is not { FaceDown: false } card
                || card.VisualId != repetition.SourceCardId && card.CombatId != repetition.SourceCardId
                || item.ControllerId is not { } controller
                || ExecutionFrames.Find(state, item.ExecutionId) is not { Kind: ExecutionKind.Trigger } execution
                || execution.ActorPlayerId != controller || execution.SourceCardId != repetition.SourceCardId) throw Invalid();
        }
        if (state.Decision?.Payload is not PaymentPayload payment) return;
        if (payment.OptionalPaymentSupport is { } support) {
            if (payment.OptionalDecline == null || !Key(support.PolicyKey) || !Enum.IsDefined(support.Availability)
                || support.CanAutoPay && support.Availability != OptionalManaPaymentAvailability.Payable
                || !FixedParts(support.OriginalManaParts) || !FixedParts(payment.UnpaidManaParts)
                || payment.UnpaidManaParts.Any(part => !support.OriginalManaParts.Any(original => PartKind(original) == PartKind(part)
                    && PartUnits(original) >= PartUnits(part))))
                throw Invalid();
        }
        if (payment.OptionalDecline is not { } offer) return;
        if (!Key(offer.GroupKey) || !Key(offer.PaymentKey) || !Key(offer.SourceCardId)
            || !Key(offer.ProducingTriggerExecutionId) || !Key(offer.OfferId)
            || offer.PreviousOfferId == null || offer.PreviousOfferId.Length > 0 && !Key(offer.PreviousOfferId)
            || offer.OfferId == offer.PreviousOfferId || offer.Mandatory || !payment.Cancel.Enabled
            || offer.PayerId != state.ViewerId || payment.PayerId != offer.PayerId
            || offer.Purpose is not (PaymentPurpose.TriggeredAbilityCost or PaymentPurpose.EffectCost)
            || state.StackActionVersion == null || state.ResolutionChoiceVersion == null
            || state.DecisionOwnerId is { } owner && owner != state.ViewerId) throw Invalid();
        var current = ExecutionFrames.Find(state, state.Decision.ExecutionId);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (current != null && seen.Add(current.Id)) {
            if (current.Id == offer.ProducingTriggerExecutionId) {
                if (current.Kind != ExecutionKind.Trigger || current.Step == ExecutionStep.Complete || current.SourceCardId != offer.SourceCardId
                    || CardViews.FindZoneOccurrence(state, offer.SourceCardId) is not { Card.FaceDown: false }) throw Invalid();
                return;
            }
            current = current.ParentId == null ? null : ExecutionFrames.Find(state, current.ParentId);
        }
        throw Invalid();
    }

    private static bool Key(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && !value.Any(char.IsControl);
    private static bool FixedManaSymbol(string? value) => value is "{W}" or "{U}" or "{B}" or "{R}" or "{G}" or "{C}"
        || value is { Length: >= 3 and <= 12 } && value[0] == '{' && value[^1] == '}'
            && int.TryParse(value.AsSpan(1, value.Length - 2), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int generic) && generic > 0
            && generic.ToString(System.Globalization.CultureInfo.InvariantCulture).AsSpan().SequenceEqual(value.AsSpan(1, value.Length - 2));
    private static bool FixedParts(ImmutableArray<ManaBillPart> parts) => !parts.IsDefaultOrEmpty && parts.Length <= 32
        && parts.All(part => part != null && part.Count > 0 && FixedManaSymbol(part.Symbol)
            && (part.Symbol[1] is not (>= '0' and <= '9') || part.Count == 1))
        && parts.Select(PartKind).Distinct(StringComparer.Ordinal).Count() == parts.Length;
    private static string PartKind(ManaBillPart part) => part.Symbol[1] is >= '0' and <= '9' ? "generic" : part.Symbol;
    private static long PartUnits(ManaBillPart part) => PartKind(part) == "generic"
        ? int.Parse(part.Symbol.AsSpan(1, part.Symbol.Length - 2), System.Globalization.CultureInfo.InvariantCulture) : part.Count;
    private static ArgumentException Invalid() => new("Finite resolution facts must retain current source, execution, payment and audience ownership.");
}
