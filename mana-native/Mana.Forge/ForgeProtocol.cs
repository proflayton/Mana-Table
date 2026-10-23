using System.Collections.Immutable;
using System.Text.Json;

namespace Mana.Forge;

// Private Java-process compatibility belongs to this provider, not the game.
internal static class ForgeProtocol
{
    internal const int Version = 2;
    internal static readonly ImmutableArray<string> RequiredCapabilities =
        ["seat-sync", "snapshots", "command-receipts", "resume", "table-social", "deck-workshop", "deck-analysis", "semantic-priority-actions", "explicit-deck-setup"];

    internal static ImmutableHashSet<string> ReadyCapabilities(JsonElement message)
    {
        if (!message.TryGetProperty("protocolVersion", out var version) || version.GetInt32() != Version)
            throw new IOException("The client and rules adapter use different protocol versions. Install the complete matching build.");
        var capabilities = message.GetProperty("capabilities").EnumerateArray().Select(value => value.GetString())
            .OfType<string>().ToImmutableHashSet(StringComparer.Ordinal);
        if (RequiredCapabilities.Any(capability => !capabilities.Contains(capability)))
            throw new IOException("The rules adapter is missing required synchronization capabilities. Install the complete matching build.");
        return capabilities;
    }
}
