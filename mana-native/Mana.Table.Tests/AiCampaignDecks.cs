using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace Mana.Table;

// Java import regressions and the native campaign consume the same decklists.
internal sealed record AiCampaignDeck(string Id, string List, ImmutableArray<string> IntendedMechanics)
{
    private static string[] Lines(string text) => text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    private static string Name(string row) => row[(row.IndexOf(' ') + 1)..];
    internal ImmutableArray<string> Commanders => Lines(List).SkipWhile(line => line != "Commander").Skip(1).Select(Name).ToImmutableArray();
    internal ImmutableArray<string> SpellNames => Lines(List).SkipWhile(line => line != "// Spells").Skip(1)
        .TakeWhile(line => line != "Commander").Select(Name).ToImmutableArray();
    internal string Hash => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(List)));
    internal int LandCount => Lines(List).SkipWhile(line => line != "// Lands").Skip(1).TakeWhile(line => line != "// Spells")
        .Sum(line => int.Parse(line[..line.IndexOf(' ')]));
    internal void Validate()
    {
        if (LandCount + SpellNames.Length + Commanders.Length != 100
            || SpellNames.Distinct(StringComparer.Ordinal).Count() != SpellNames.Length || LandCount is < 36 or > 42)
            throw new InvalidDataException($"Campaign deck {Id} must contain 100 singleton cards with a normal resource ratio; found {LandCount} lands + {SpellNames.Length} spells + {Commanders.Length} commanders.");
    }
}

internal static class AiCampaignDecks
{
    internal const int Version = 1;
    internal static ImmutableArray<AiCampaignDeck> All { get; } = [
        Deck("rhys-token-equipment.v1", ["creatures", "tokens", "ramp", "removal", "attachments", "counters"]),
        Deck("pir-toothy-counter-ramp.v1", ["creatures", "ramp", "draw", "counters", "counter-cost", "counterspell", "partner-search", "attachments"]),
        Deck("balmor-spells-multitarget.v1", ["creatures", "damage", "removal", "counterspell", "modal", "multitarget", "tokens"]),
        Deck("traxos-colorless-artifacts.v1", ["creatures", "true-colorless", "ramp", "removal", "attachments", "counters", "draw"])
    ];
    private static AiCampaignDeck Deck(string id, ImmutableArray<string> mechanics) =>
        new(id, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "AiCampaign", id + ".txt")), mechanics);
}
