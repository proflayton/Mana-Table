namespace Mana.Contracts;

public enum PaymentPurpose { Unknown, SpellCost, ActivatedAbilityCost, TriggeredAbilityCost, EffectCost }

// Provenance of the currently owned payment input. The bill remains UnpaidMana;
// this record grants neither another payer's resources nor a right to cancel.
public sealed record PaymentDecision(PaymentPurpose Purpose, int PayerId, string SourceCardId, bool Mandatory);
