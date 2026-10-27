using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Mana.Contracts;

// Reading a proposed bill and resource witness never grants payment authority.
// Effective costs and current spending controls still belong to the live input.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ManaCostPreview(ImmutableArray<ActionCost> Costs, SemanticKnowledge ManaCostKnowledge,
    ManaFundingContext Funding);
