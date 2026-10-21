using Mana.Contracts;

namespace Mana.Magic;

// Presentation parts, not additional game state or a replacement wire model.
public sealed record PublicResourcePart(string Key, string Name, string Symbol, int Amount);

public static class PublicResources
{
    private static readonly (string Key, string Name)[] ManaColors = [
        ("W", "White"), ("U", "Blue"), ("B", "Black"),
        ("R", "Red"), ("G", "Green"), ("C", "Colorless")
    ];
    public const string ManaDetail = "This is mana already in this player's pool, separate from untapped lands and other mana sources. "
        + "W means white, U blue, B black, R red, G green, and C colorless. Colorless mana is not a generic mana cost. "
        + "Unspent mana normally empties as steps and phases end. Effects can retain or change it; the displayed amounts follow the game.";

    public static PublicResourcePart[] Mana(Player player, bool includeEmpty = false)
    {
        var known = ManaColors.Select(color => new PublicResourcePart(color.Key, color.Name, color.Key,
            player.Mana.GetValueOrDefault(color.Key)));
        var extra = player.Mana.Where(part => !ManaColors.Any(color => color.Key == part.Key))
            .OrderBy(part => part.Key, StringComparer.Ordinal)
            .Select(part => new PublicResourcePart(part.Key, part.Key, "", part.Value));
        return known.Concat(extra).Where(part => includeEmpty || part.Amount > 0).ToArray();
    }

    public static long ManaTotal(Player player) => Mana(player).Sum(part => (long)part.Amount);
}
