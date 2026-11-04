namespace Mana.Contracts;

// A currently authorized physical source outside the player-zone presentation,
// or explicitly known reading data beside an owned masked battlefield source.
// A stack item's reading face is never authority by itself. This occurrence
// belongs only to the exact decision which disclosed its source action key.
public sealed record ActionSourceOccurrence(string DecisionId, string Zone, int PlayerId, Card Card)
{
    // Reading only: this descriptor has no physical identity or command handle.
    // Card and the player-zone occurrence keep the actual face-down state.
    public Card? KnownFace { get; init; }
}
