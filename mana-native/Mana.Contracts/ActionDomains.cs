using System.Collections.Immutable;

namespace Mana.Contracts;

// Complete means every timing-legal route in this owned input was enumerated.
// Affordability is separate: an unknown payment plan never becomes an absent move.
public enum ActionDomainCoverage { NotApplicable, Complete, Unsupported }
public enum ActionAffordability { Payable, Unavailable, Unknown }
public enum ActionEffectKind
{
    Unknown, Permanent, Land, Mana, Draw, Damage, Destroy, Exile, Bounce,
    CounterSpell, AddCounters, RemoveCounters, LifeGain, LifeLoss, Token,
    Tap, Untap, Search, Discard, Mill, ChangeControl,
    Scry, Proliferate, MultiplyCounters, Choose, Conditional, Repeat, Cleanup, MoveCard, Amass
}
public enum ActionCostKind
{
    Mana, Tap, Untap, Life, Sacrifice, Discard, Exile, RemoveCounters, AddCounters, Other
}
public sealed record ActionCost(ActionCostKind Kind, int? Amount, bool FromSource, string CounterType)
{
    // Rules symbols, not localized card prose. Generic mana has its own amount;
    // hybrid, snow, phyrexian and X symbols remain explicit without flattening.
    public ImmutableArray<string> ManaSymbols { get; init; } = [];
}
public sealed record ActionSemantics(string ActionId, ActionEffectKind Effect,
    ImmutableArray<ActionEffectKind> Effects, ImmutableArray<ActionCost> Costs,
    ActionAffordability Affordability)
{
    // Stable for an occurrence and equivalent route across fresh decision offers;
    // never accepted as command authority in place of ActionId.
    public string StableId { get; init; } = "";
    public bool ManaAbility { get; init; }
    public int ManaValue { get; init; }
    public ImmutableArray<string> ManaColors { get; init; } = [];
    // During a payment input this narrower set also satisfies the current
    // unpaid shards and the spell's spending restrictions. Null outside payment.
    public ImmutableArray<string>? PaymentManaColors { get; init; }
    public int ManaAmount { get; init; }
    public bool Targeted { get; init; }
    // False proves too few legal targets; null preserves unresolved combination
    // constraints. A mere candidate count is not proof of a legal target set.
    public bool? TargetsAvailable { get; init; }
    public bool Repeatable { get; init; }
    public int? EffectAmount { get; init; }
    public ActionOperationGraph OperationGraph { get; init; } = ActionOperationGraph.Unknown;
    public ManaProduction Production { get; init; } = ManaProduction.Unknown;
    // Base route costs are not the adjusted bill. Reductions, alternative costs
    // and payment choices require the engine's actual payment input.
    public SemanticKnowledge ManaCostKnowledge { get; init; }
    // Prospective read-only applicability for a mana-bearing cost. X uses the
    // same funding facts as the later numeric callback, without creating that
    // callback or granting permission to execute any resource route.
    public ManaFundingContext? ProspectiveFunding { get; init; }
}
public sealed record DecisionActionDomain(long Version, ActionDomainCoverage Coverage,
    ImmutableArray<SourceActionSet> Sources, ImmutableArray<ActionSemantics> Actions)
{
    // These exact current colors can spend one existing unit on this bill.
    // Counts alone do not establish spending restrictions or shard compatibility.
    public ImmutableArray<string> PoolManaColors { get; init; } = [];
    public static DecisionActionDomain None { get; } = new(0, ActionDomainCoverage.NotApplicable, [], []);
}
