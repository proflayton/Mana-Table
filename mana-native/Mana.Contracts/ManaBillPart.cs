using System.Text.Json.Serialization;

namespace Mana.Contracts;

// Exact unpaid requirements are counted by shard kind, not expanded by mana
// value. This permits very large bills without allocating a symbol per unit.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ManaBillPart(string Symbol, int Count);
