namespace Mana.Contracts;

// A currently authorized physical source outside the player-zone presentation.
// A stack item's reading face is never authority by itself. This occurrence
// belongs only to the exact decision which disclosed its source action key.
public sealed record ActionSourceOccurrence(string DecisionId, string Zone, int PlayerId, Card Card);
