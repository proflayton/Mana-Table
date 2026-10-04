using Mana.Contracts;
using Microsoft.Xna.Framework;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Renderer;

public sealed record SceneCard(string Id, Card Card, Pose Pose, string Zone, int Owner, Vector2? Entrance = null, float EntranceDelay = 0)
{
    public bool Contains(Point point) => Pose.Contains(point);
}
public sealed record RankPlan(string Key, int First, int Total, int Capacity, Vector2 Pager, SceneCard[] Cards);
public sealed record PilePlan(string Zone, Pose Pose, bool Compact);
public sealed record HandPlan(int Page, int Total, string? Raised, SceneCard[] Cards);

/// <summary>Immutable table layout shared by painting, hit testing and layout checks.</summary>
public sealed class TableScene
{
    private readonly Dictionary<(int Player, bool Resources), RankPlan> ranks = [];
    private readonly Dictionary<int, SceneCard[]> commanders = [];
    private readonly Dictionary<int, PilePlan[]> piles = [];
    public required HandPlan Hand { get; init; }
    public RankPlan Rank(int player, bool resources) => ranks[(player, resources)];
    public IReadOnlyList<SceneCard> Commanders(int player) => commanders[player];
    public IReadOnlyList<PilePlan> Piles(int player) => piles[player];

    public static TableScene Build(GameSnapshot state, IReadOnlyDictionary<string, int> pages, int handPage,
        Point pointer, string? previouslyRaised, string? dragged, bool allowHover, Func<Card, bool> isResource)
    {
        var hand = state.Viewer?.Zone("Hand").Cards ?? [];
        int page = Math.Clamp(handPage, 0, Math.Max(0, (hand.Length - 1) / 11));
        var visibleHand = hand.Skip(page * 11).Take(11).ToArray(); int raised = -1;
        if (allowHover) {
            for (int i = 0; i < visibleHand.Length; i++) if (TableLayout.Hand(i, visibleHand.Length).Contains(pointer)) raised = i;
            int old = Array.FindIndex(visibleHand, c => CardViews.Identity(c) == previouslyRaised);
            if (raised < 0 && old >= 0 && TableLayout.Hand(old, visibleHand.Length, true).Contains(pointer)) raised = old;
        }
        var handNodes = visibleHand.Select((card, i) => Node(card, TableLayout.Hand(i, visibleHand.Length, i == raised), "Hand", state.ViewerId, i, TableWorld.Pile(3, 0).Center) with { EntranceDelay = i * .035f })
            .Where(n => CardViews.Identity(n.Card) != dragged).OrderBy(n => n.Card == visibleHand.ElementAtOrDefault(raised) ? 1 : 0).ToArray();
        var scene = new TableScene { Hand = new(page, hand.Length, raised < 0 ? null : CardViews.Identity(visibleHand[raised]), handNodes) };
        foreach (var player in state.Players) {
            int seat = player.Id == state.ViewerId ? 3 : TableLayout.SeatIndex(state, player.Id);
            if (seat is < 0 or > 3) continue;
            foreach (bool resources in new[] { true, false }) {
                var cards = player.Zone("Battlefield").Cards.Where(c => isResource(c) == resources).ToArray();
                int capacity = seat == 3 ? (resources ? 11 : 8) : seat == 1 ? (resources ? 8 : 5) : (resources ? 6 : 5);
                string key = player.Id + (resources ? ":lands" : ":permanents");
                int first = Math.Clamp(pages.GetValueOrDefault(key), 0, Math.Max(0, cards.Length - capacity));
                var entrance = TableLayout.SeatHand(seat);
                var cardsInRank = cards.Skip(first).Take(capacity).Select((card, i) => Node(card,
                    TableWorld.Rank(seat, resources, i, Math.Min(capacity, cards.Length), card.Tapped, card.Attacking || card.Blocking), "Battlefield", player.Id, first + i, entrance))
                    .OrderBy(c => c.Pose.Center.Y).ToArray();
                var pager = seat switch { 0 => new Vector2(resources ? 179 : 550, resources ? 208 : 344), 1 => new Vector2(1090, resources ? 110 : 65),
                    2 => new Vector2(resources ? 1421 : 1050, resources ? 208 : 344), _ => new Vector2(330, resources ? 680 : 638) };
                scene.ranks[(player.Id, resources)] = new(key, first, cards.Length, capacity, pager, cardsInRank);
            }
            bool own = seat == 3, side = seat is 0 or 2;
            var leaders = player.Zone("Command").Cards.Take(2).ToArray();
            scene.commanders[player.Id] = leaders.Select((card, i) => {
                Vector2 center = own ? new(-516 + i * 82, 202) : side ? TableWorld.SeatPoint(seat, -262 + i * 91, 525) : TableWorld.SeatPoint(seat, -359 - i * 85, 302);
                var size = own ? leaders.Length > 1 ? new Vector2(62, 87) : new Vector2(81, 114) : new Vector2(69, 97);
                return Node(card, TableWorld.Card(center, size, TableWorld.SeatAngle(seat)), "Command", player.Id, i, TableLayout.Hero(seat));
            }).ToArray();
            scene.piles[player.Id] = new[] { "Library", "Graveyard", "Exile" }.Select((zone, i) => {
                return new PilePlan(zone, TableWorld.Pile(seat, i), side);
            }).ToArray();
        }
        return scene;
    }
    private static SceneCard Node(Card card, Pose pose, string zone, int owner, int index, Vector2? entrance)
    {
        string id = CardViews.Identity(card);
        // Anonymous test/placeholder objects get positional identities; names never identify an occurrence.
        if (id.Length == 0) id = $"anonymous:{owner}:{zone}:{index}";
        return new(id, card, pose, zone, owner, entrance);
    }
}
