using System.Collections.Immutable;

namespace Mana.Forge;

// Private Java-process compatibility belongs to this provider, not the game.
internal static class ForgeProtocol
{
    internal const int Version = 2;
    internal static readonly ImmutableArray<string> RequiredCapabilities =
        ["seat-sync", "snapshots", "command-receipts", "resume", "table-social", "deck-workshop", "deck-analysis", "semantic-priority-actions"];
}
