using Mana.Contracts;
using Microsoft.Xna.Framework;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Renderer;

public sealed record SceneCard(string Id, Card Card, Pose Pose, string Zone, int Owner, Vector2? Entrance = null, float EntranceDelay = 0)
{
    public bool Contains(Point point) => Pose.Contains(point);
    public bool Compact { get; init; }
    public bool GroupFront { get; init; } = true;
    public int GroupCount { get; init; } = 1;
    public bool Dense { get; init; }
    public Rectangle DenseLabels { get; init; }
    public float DenseScale { get; init; } = 1;
    public int DenseRowHeight { get; init; } = 20;
    public bool DenseMiniature { get; init; }

    internal static SceneCard Create(Card card, Pose pose, string zone, int owner, int index, Vector2? entrance)
    {
        string id = CardViews.Identity(card);
        // Anonymous objects get positional identities; names never identify an occurrence.
        if (id.Length == 0) id = $"anonymous:{owner}:{zone}:{index}";
        return new(id, card, pose, zone, owner, entrance);
    }
}
public sealed record RankPlan(string Key, int Total, SceneCard[] Cards);
public sealed record PilePlan(string Zone, Pose Pose, bool Compact);
public sealed record HandPlan(int Page, int Total, string? Raised, SceneCard[] Cards);

/// <summary>One player's prepared geometry and complete public battlefield.</summary>
public sealed class SceneSeat
{
    private readonly Dictionary<string, SceneCard> occurrences = new(StringComparer.Ordinal);
    private readonly RankPlan lands, permanents;
    public int PlayerId => Geometry.PlayerId;
    public int Index => Geometry.Index;
    public SeatGeometry Geometry { get; }
    public IReadOnlyList<BattlefieldCluster> Battlefield { get; }
    public bool IsDense { get; }

    internal SceneSeat(SeatGeometry geometry, BattlefieldCluster[] battlefield)
    {
        Geometry = geometry;
        Battlefield = Array.AsReadOnly(battlefield);
        IsDense = battlefield.Any(cluster => cluster.Cards.Any(card => card.Dense));
        foreach (var card in battlefield.SelectMany(cluster => cluster.Cards)) occurrences.TryAdd(card.Id, card);
        lands = BuildRank(true);
        permanents = BuildRank(false);

        RankPlan BuildRank(bool resources)
        {
            var cards = battlefield.Where(cluster => (cluster.Kind == "Lands") == resources).SelectMany(cluster => cluster.Cards).ToArray();
            return new(PlayerId + (resources ? ":lands" : ":permanents"), cards.Length, cards);
        }
    }

    public SceneCard? BattlefieldCard(string occurrenceId) => occurrences.GetValueOrDefault(occurrenceId);
    internal RankPlan Rank(bool resources) => resources ? lands : permanents;
}

/// <summary>Prepared table layout shared by painting, hit testing and layout checks.</summary>
public sealed class TableScene
{
    private readonly Dictionary<int, SceneSeat> seats;
    public HandPlan Hand { get; }
    public IReadOnlyList<SceneSeat> Seats { get; }

    private TableScene(HandPlan hand, SceneSeat[] seats)
    {
        Hand = hand;
        Seats = Array.AsReadOnly(seats);
        this.seats = seats.ToDictionary(seat => seat.PlayerId);
    }

    public SceneSeat Seat(int player) => seats[player];
    public SceneCard? BattlefieldCard(int player, string occurrenceId) => seats.GetValueOrDefault(player)?.BattlefieldCard(occurrenceId);
    // Compatibility views keep existing pose consumers on the same seat plan.
    public RankPlan Rank(int player, bool resources) => Seat(player).Rank(resources);
    public IReadOnlyList<SceneCard> Commanders(int player) => Seat(player).Geometry.Commanders;
    public IReadOnlyList<PilePlan> Piles(int player) => Seat(player).Geometry.Piles;
    public IReadOnlyList<BattlefieldCluster> Battlefield(int player) => Seat(player).Battlefield;

    public static TableScene Build(GameSnapshot state, int handPage,
        Point pointer, string? previouslyRaised, string? dragged, bool allowHover, Func<Card, bool> isResource,
        Func<Card, int, bool, BattlefieldLabelMetrics>? denseMeasure = null, bool allowInlineOrdering = true)
    {
        var hand = HandLayout.Build(state, handPage, pointer, previouslyRaised, dragged, allowHover);
        var orderedChoices = allowInlineOrdering && state.Decision is { Kind: "choice", Ordered: true } decision
            ? decision.Choices.Where(c => c.Card != null && !string.IsNullOrEmpty(c.CardId)).Select(c => c.CardId).ToHashSet(StringComparer.Ordinal) : null;
        var geometry = SeatGeometry.Build(state);
        var layout = BattlefieldLayoutContext.Build(state, orderedChoices, geometry);
        var seats = new List<SceneSeat>();
        for (int index = 0; index < state.Players.Length; index++) {
            var player = state.Players[index];
            var seat = geometry[index];
            if (seat.Index is < 0 or > 3) continue;
            var clusters = BattlefieldLayout.Arrange(layout, seat.Index, player.Id,
                player.Zone("Battlefield").Cards, isResource, orderedChoices, denseMeasure);
            seats.Add(new(seat, clusters));
        }
        return new(hand, seats.ToArray());
    }
}
