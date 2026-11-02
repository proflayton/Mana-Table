using Mana.Contracts;
using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Renderer;

public sealed record PlayAccessCard(SceneCard Scene, PlayAccess Access);
public sealed record PlayAccessGroup(string Id, int PlayerId, string Zone,
    IReadOnlyList<string> SourceIds, Rectangle Bounds, IReadOnlyList<PlayAccessCard> Cards);

/// <summary>The local fan anchors and the decision space immediately above them.
/// Access references retain their real zones; their placement grants no action.</summary>
public sealed class PlayAccessPlan
{
    public static Rectangle Fan => new(1244, 756, 340, 144);
    public Rectangle SourcePocket { get; }
    public Rectangle SourceRail { get; }
    public IReadOnlyList<PlayAccessGroup> Groups { get; }
    public Rectangle Bounds => Groups.Count == 0 ? Rectangle.Empty : Fan;
    public Rectangle UnfoldBounds { get; }
    public int DockOffsetY => Groups.Count == 0 ? 0 : UnfoldBounds.Top - 12 - 900;
    public int DockBottom => 900 + DockOffsetY;

    private PlayAccessPlan(PlayAccessGroup[] groups, Rectangle sourceRail, Rectangle sourcePocket, Rectangle unfoldBounds)
    {
        Groups = Array.AsReadOnly(groups);
        SourceRail = sourceRail;
        SourcePocket = groups.Length == 0 ? Rectangle.Empty : sourcePocket;
        UnfoldBounds = groups.Length == 0 ? Rectangle.Empty : unfoldBounds;
    }

    // Identity plans claim their owner-local space before this physical fan.
    // Complete the fan against those measured facts, then use the same plan for
    // paint, hits, collection anchors and priority avoidance.
    public PlayAccessPlan PlaceBesideHand(IReadOnlyList<Rectangle> ownerFurniture, Func<string, int, Point> measureCaption)
    {
        if (SourcePocket.IsEmpty) return this;
        string caption = string.Join(" \u00b7 ", Groups.Select(group => group.Zone).Distinct());
        var rail = new Rectangle(SourcePocket.Left + 4, Fan.Top, SourcePocket.Width - 8, 32);
        int captionHeight = measureCaption(caption, rail.Width - 24).Y + 8;
        int captionTop = ownerFurniture.Where(bounds => bounds.Left < rail.Right && bounds.Right > rail.Left)
            .Select(bounds => bounds.Bottom + 4).Append(Fan.Top - 27).Max();
        var unfold = new Rectangle(rail.Left + 8, captionTop, rail.Width - 16, captionHeight);
        rail.Y = Math.Max(rail.Y, unfold.Bottom + 7);
        int ownerBottom = ownerFurniture.Where(bounds => bounds.Left < Fan.Right && bounds.Right > Fan.Left)
            .Select(bounds => bounds.Bottom + 4).DefaultIfEmpty(0).Max();
        int top = Math.Max(ownerBottom, rail.Top + 43);
        var cards = Groups.SelectMany(group => group.Cards).ToArray();
        int offset = Math.Max(0, top - cards.Min(card => card.Scene.Pose.Bounds.Top));
        var groups = Groups.Select(group => group with { Cards = Array.AsReadOnly(group.Cards.Select(card => card with {
            Scene = card.Scene with { Pose = card.Scene.Pose with { Center = card.Scene.Pose.Center + new Vector2(0, offset) } }
        }).ToArray()) }).ToArray();
        return new(groups, rail, SourcePocket, unfold);
    }

    public bool Contains(int playerId, string zone, Card card) => Groups.Any(group => group.PlayerId == playerId
        && group.Zone == zone && group.Cards.Any(item => CardViews.Same(item.Scene.Card, card)));

    public Rectangle PreferredPriorityArea(Rectangle requested) => Groups.Count > 0
        ? new(Fan.Left + 6, Fan.Top - 128, Fan.Width - 12, requested.Height) : requested;

    public Rectangle ReservePriority(Rectangle requested, IEnumerable<Rectangle>? readers = null)
    {
        if (requested.IsEmpty) return Rectangle.Empty;
        var footprint = new Rectangle(requested.X, requested.Top - 35, requested.Width, requested.Height + 35 + 48);
        if (readers?.Any(reader => !reader.IsEmpty && footprint.Intersects(reader)) == true) return Rectangle.Empty;
        return requested;
    }

    public static PlayAccessPlan Build(GameSnapshot state)
    {
        var cards = state.PlayAccesses.Where(access => access.Zone is not ("Hand" or "Battlefield" or "Stack" or "Command")
                && !access.Routes.IsDefaultOrEmpty)
            .GroupBy(access => (access.PlayerId, access.Zone, access.CardId))
            .Select(group => new PlayAccess(group.Key.PlayerId, group.Key.Zone, group.Key.CardId,
                [.. group.SelectMany(access => access.Routes).DistinctBy(route => route.Id)]))
            .Select(access => (Access: access, Card: Find(state, access)))
            .Where(item => item.Card is { FaceDown: false })
            .GroupBy(item => (item.Access.PlayerId, item.Access.Zone,
                Sources: string.Join("\n", item.Access.Routes.Select(route => route.SourceCardId)
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))))
            .OrderBy(group => group.Key.PlayerId == state.ViewerId ? 0 : 1)
            .ThenBy(group => group.Key.PlayerId).ThenBy(group => group.Key.Zone, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Sources, StringComparer.Ordinal).ToArray();
        const int cardWidth = 137;
        var sourcePocket = state.Players.Length > 4 ? new Rectangle(1424, 704, 168, 196) : Rectangle.Empty;
        int total = cards.Sum(group => group.Count()), next = 0;
        float stride = Math.Min(82, 196f / Math.Max(1, total - 1));
        var groups = new List<PlayAccessGroup>();
        foreach (var group in cards) {
            int first = next;
            var members = group.OrderBy(item => item.Access.CardId, StringComparer.Ordinal).Select((item, index) => {
                int position = next++;
                float bend = total <= 1 ? 0 : position / (float)(total - 1) * 2 - 1;
                var pose = new Pose(new(Fan.Left + 74 + position * stride, 882 + bend * bend * 8), new(cardWidth, 192), bend * .06f);
                return new PlayAccessCard(SceneCard.Create(item.Card!, pose, item.Access.Zone,
                    item.Access.PlayerId, index, null), item.Access);
            }).ToArray();
            string id = group.Key.PlayerId + ":" + group.Key.Zone + ":" + group.Key.Sources;
            groups.Add(new(id, group.Key.PlayerId, group.Key.Zone,
                Array.AsReadOnly(group.Key.Sources.Split('\n').Where(source => source.Length > 0).ToArray()),
                new((int)(Fan.Left + 6 + first * stride), Fan.Top, (int)(cardWidth + Math.Max(0, members.Length - 1) * stride), Fan.Height),
                Array.AsReadOnly(members)));
        }
        return new(groups.ToArray(), Fan, sourcePocket, new(Fan.Left + 8, Fan.Top - 27, Fan.Width - 16, 26));
    }

    private static Card? Find(GameSnapshot state, PlayAccess access)
    {
        var zone = state.Players.FirstOrDefault(player => player.Id == access.PlayerId)?.Zone(access.Zone);
        return zone?.Cards.FirstOrDefault(card => CardViews.Identity(card) == access.CardId)
            ?? (zone?.TopCard is { } top && CardViews.Identity(top) == access.CardId ? top : null);
    }
}
