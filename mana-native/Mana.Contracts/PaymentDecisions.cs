using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Mana.Contracts;

public enum PaymentPurpose { Unknown, SpellCost, ActivatedAbilityCost, TriggeredAbilityCost, EffectCost }

// Provenance of the currently owned payment input. The bill remains UnpaidMana;
// this record grants neither another payer's resources nor a right to cancel.
public sealed record PaymentDecision(PaymentPurpose Purpose, int PayerId, string SourceCardId, bool Mandatory);

// Parent payment of this current nonmana cost selection. Already-paid parts
// are absent. The remaining nominal bill and resource subset do not promise
// modified affordability or authorize activating any of these sources.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CostPaymentFunding(string SourceCardId, int PayerId,
    ImmutableArray<ActionCost> RemainingCosts, ManaFundingContext Funding)
{
    public SemanticKnowledge ManaCostKnowledge { get; init; } = SemanticKnowledge.Unknown;
    // Exact currently selected part within RemainingCosts. Omission from an
    // older provider cannot establish a distinct outstanding source commitment.
    public int? CurrentCostIndex { get; init; }
}
