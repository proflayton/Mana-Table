using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Mana.Contracts;

public enum NumericDecisionPurpose { Unknown, AnnounceX }

// The callback's actual cost owner, rather than an interpretation of its label.
// Costs describe a nominal bill; adjusted costs can remain explicitly unknown.
public sealed record NumericDecision(NumericDecisionPurpose Purpose, string SourceCardId,
    ImmutableArray<ActionCost> Costs, SemanticKnowledge ManaCostKnowledge, ManaFundingContext Funding);

// Read-only planning facts. These routes contain no executable action handles,
// and do not become current priority/payment permissions at a numeric input.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ManaFundingContext(SemanticKnowledge Knowledge,
    ImmutableDictionary<string, int> Pool, ImmutableArray<ManaFundingSource> Sources);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ManaFundingSource(string SourceCardId, ImmutableArray<ManaFundingRoute> Routes);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ManaFundingRoute(ImmutableArray<ActionCost> Costs, ManaProduction Production,
    SemanticKnowledge Eligibility);
