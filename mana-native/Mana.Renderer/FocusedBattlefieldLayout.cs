using Mana.Contracts;
using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Renderer;

public sealed class FocusedBattlefieldGroup(string id, string role, IReadOnlyList<Card> cards)
{
    public string Id { get; } = id;
    public string Role { get; } = role;
    public IReadOnlyList<Card> Cards { get; } = cards;
    public Card Front => Cards[^1];
    public int Count => Cards.Count;
    public IReadOnlyList<string> MemberIds { get; } = Array.AsReadOnly(cards.Select(CardViews.Identity)
        .Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).ToArray());
}
public sealed record FocusedBattlefieldTile(FocusedBattlefieldGroup Group, Rectangle Bounds, Rectangle Art, Rectangle Details)
{
    public Pose ArtPose => new(Art.Center.ToVector2(), Art.Size.ToVector2());
}
public sealed record FocusedBattlefieldHeading(string Role, int Permanents, int Groups, Rectangle Bounds);
public sealed record FocusedBattlefieldPlan(Rectangle Viewport, IReadOnlyList<FocusedBattlefieldTile> Tiles,
    IReadOnlyList<FocusedBattlefieldHeading> Headings, int Scroll, int MaximumScroll, int ContentHeight)
{
    public IEnumerable<FocusedBattlefieldTile> VisibleTiles => Tiles.Where(tile => Viewport.Contains(tile.Art));
    public FocusedBattlefieldTile? Tile(string occurrenceId) => Tiles.FirstOrDefault(tile => tile.Group.MemberIds.Contains(occurrenceId));
}

/// <summary>
/// A readable one-seat workspace. Public-state families share an art tile;
/// every family stays in one continuously scrollable document, without pages.
/// Measurements belong to the caller's actual typography.
/// </summary>
public static class FocusedBattlefieldLayout
{
    private static readonly string[] Roles = ["Creatures", "Support", "Lands"];
    public static IReadOnlyList<FocusedBattlefieldGroup> Group(IReadOnlyList<Card> cards, Func<Card, bool> isResource,
        IReadOnlyList<EffectChangeGroup>? reportedChanges = null)
    {
        // Equal final state does not mean these occurrences underwent the same
        // reported effects. Preserve that distinction for the retained history,
        // so a newly animated Forest does not disappear into an unchanged copy.
        // No elapsed-time key or retained face participates in this partition.
        var membership = new Dictionary<string, byte>(StringComparer.Ordinal);
        var effects = reportedChanges?.TakeLast(8).ToArray() ?? [];
        for (int index = 0; index < effects.Length; index++)
            foreach (string target in effects[index].Changes.Select(change => change.TargetId))
                membership[target] = (byte)(membership.GetValueOrDefault(target) | 1 << index);
        var byRole = cards.GroupBy(card => BattlefieldLayout.Role(card, isResource))
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var result = new List<FocusedBattlefieldGroup>();
        foreach (string role in Roles) {
            if (!byRole.TryGetValue(role, out var roleCards)) continue;
            int familyIndex = 0;
            foreach (var family in BattlefieldLayout.Groups(roleCards, null, int.MaxValue)) {
                foreach (var cohort in family.GroupBy(card => membership.GetValueOrDefault(CardViews.Identity(card)))) {
                    var copies = cohort.ToArray();
                    string id = CardViews.Identity(copies[^1]);
                    if (id.Length == 0) id = "anonymous:" + role + ":" + familyIndex;
                    result.Add(new(id, role, Array.AsReadOnly(copies)));
                }
                familyIndex++;
            }
        }
        return result.AsReadOnly();
    }

    public static FocusedBattlefieldPlan Arrange(IReadOnlyList<FocusedBattlefieldGroup> groups, Rectangle viewport,
        int scroll, Func<FocusedBattlefieldGroup, int, int> measureDetailsHeight)
    {
        const int columns = 3, gap = 14, padding = 12, artWidth = 138, artHeight = 193;
        int width = Math.Max(250, (viewport.Width - gap * (columns - 1) - 14) / columns);
        int detailWidth = Math.Max(70, width - padding * 3 - artWidth);
        var tiles = new List<FocusedBattlefieldTile>();
        var headings = new List<FocusedBattlefieldHeading>();
        int y = viewport.Top;
        foreach (string role in Roles) {
            var family = groups.Where(group => group.Role == role).ToArray();
            if (family.Length == 0) continue;
            headings.Add(new(role, family.Sum(group => group.Count), family.Length, new(viewport.Left, y, viewport.Width - 16, 32)));
            y += 40;
            foreach (var row in family.Chunk(columns)) {
                int height = row.Max(group => Math.Max(artHeight, measureDetailsHeight(group, detailWidth)) + padding * 2);
                for (int column = 0; column < row.Length; column++) {
                    int x = viewport.Left + column * (width + gap);
                    tiles.Add(new(row[column], new(x, y, width, height), new(x + padding, y + padding, artWidth, artHeight),
                        new(x + padding * 2 + artWidth, y + padding, detailWidth, height - padding * 2)));
                }
                y += height + gap;
            }
            y += 12;
        }
        int contentHeight = Math.Max(0, y - viewport.Top), maximum = Math.Max(0, contentHeight - viewport.Height);
        scroll = Math.Clamp(scroll, 0, maximum);
        Rectangle Shift(Rectangle rectangle) => new(rectangle.X, rectangle.Y - scroll, rectangle.Width, rectangle.Height);
        return new(viewport, Array.AsReadOnly(tiles.Select(tile => tile with {
            Bounds = Shift(tile.Bounds), Art = Shift(tile.Art), Details = Shift(tile.Details)
        }).ToArray()), Array.AsReadOnly(headings.Select(heading => heading with { Bounds = Shift(heading.Bounds) }).ToArray()),
            scroll, maximum, contentHeight);
    }
}
