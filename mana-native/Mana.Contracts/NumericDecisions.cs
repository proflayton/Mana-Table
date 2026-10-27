using System.Collections.Immutable;

namespace Mana.Contracts;

public enum NumericDecisionPurpose { Unknown, AnnounceX }

// The callback's actual cost owner, rather than an interpretation of its label.
// Costs describe a nominal bill; adjusted costs can remain explicitly unknown.
public sealed record NumericDecision(NumericDecisionPurpose Purpose, string SourceCardId,
    ImmutableArray<ActionCost> Costs, SemanticKnowledge ManaCostKnowledge, ManaFundingContext Funding);

// Read-only planning facts. These routes contain no executable action handles,
// and do not become current priority/payment permissions at a numeric input.
public sealed record ManaFundingContext(SemanticKnowledge Knowledge,
    ImmutableDictionary<string, int> Pool, ImmutableArray<ManaFundingSource> Sources);
public sealed record ManaFundingSource(string SourceCardId, ImmutableArray<ManaFundingRoute> Routes);
public sealed record ManaFundingRoute(ImmutableArray<ActionCost> Costs, ManaProduction Production,
    SemanticKnowledge Eligibility);
