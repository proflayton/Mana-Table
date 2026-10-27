using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Mana.Contracts;

// Read-only context from the actual payment which owns a color-choice callback.
// It keeps the unpaid cost visible without granting parent payment commands.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ManaColorPaymentContext(int PayerId, ImmutableArray<ManaBillPart> UnpaidManaParts,
    DecisionSource? Source);
