using Mana.Contracts;
using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Renderer;

public sealed record PlayAccessCard(SceneCard Scene, PlayAccess Access);
public sealed record PlayAccessGroup(string Id, int PlayerId, string Zone,
    IReadOnlyList<string> SourceIds, Rectangle Bounds, IReadOnlyList<PlayAccessCard> Cards);

/// <summary>The local fan anchors and the decision space immediately above them.
/// Access references retain their real zones; their placement grants no action.</summary>
public sealed class PlayAccessPlan
{
    public static Rectangle Fan => new(1244, 756, 340, 144);
    public IReadOnlyList<PlayAccessGroup> Groups { get; }
    public Rectangle Bounds => Groups.Count == 0 ? Rectangle.Empty : Fan;
    public Rectangle UnfoldBounds => Groups.Count == 0 ? Rectangle.Empty : new(1536, 756, 46, 39);
    public int DockOffsetY => Groups.Count == 0 ? 0 : -152;
    public int DockBottom => 900 + DockOffsetY;

    private PlayAccessPlan(PlayAccessGroup[] groups) => Groups = Array.AsReadOnly(groups);

    public bool Contains(int playerId, string zone, Card card) => Groups.Any(group => group.PlayerId == playerId
        && group.Zone == zone && group.Cards.Any(item => CardViews.Same(item.Scene.Card, card)));

    public Rectangle ReservePriority(Rectangle requested, IEnumerable<Rectangle>? readers = null)
    {
        if (Groups.Count > 0) requested = new(Fan.Left + 6, Fan.Top - 128, Fan.Width - 12, requested.Height);
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
        return new(groups.ToArray());
    }

    private static Card? Find(GameSnapshot state, PlayAccess access)
    {
        var zone = state.Players.FirstOrDefault(player => player.Id == access.PlayerId)?.Zone(access.Zone);
        return zone?.Cards.FirstOrDefault(card => CardViews.Identity(card) == access.CardId)
            ?? (zone?.TopCard is { } top && CardViews.Identity(top) == access.CardId ? top : null);
    }
}
