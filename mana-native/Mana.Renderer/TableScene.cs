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
    public bool ScanFace { get; init; }
    public StudyBattlefieldFamily? StudyFamily { get; init; }
    public BattlefieldAttachment? Attachment { get; init; }

    internal static SceneCard Create(Card card, Pose pose, string zone, int owner, int index, Vector2? entrance)
    {
        string id = CardViews.Identity(card);
        // Anonymous objects get positional identities; names never identify an occurrence.
        if (id.Length == 0) id = $"anonymous:{owner}:{zone}:{index}";
        return new(id, card, pose, zone, owner, entrance);
    }
}
public sealed record PilePlan(string Zone, Pose Pose, bool Compact);
public sealed record HandPlan(int Total, string? Raised, SceneCard[] Cards, Rectangle UnfoldBounds);

/// <summary>A painted front and the exact public occurrences represented behind it.
/// Membership is presentation geometry, never permission to act on a group.</summary>
public sealed record BattlefieldCohort(SceneCard Front, IReadOnlyList<string> MemberIds);

/// <summary>One player's prepared geometry and complete public battlefield.</summary>
public sealed class SceneSeat
{
    private readonly Dictionary<string, SceneCard> occurrences = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BattlefieldCohort> cohortsByOccurrence = new(StringComparer.Ordinal);
    public int PlayerId => Geometry.PlayerId;
    public int Index => Geometry.Index;
    public SeatGeometry Geometry { get; }
    public IReadOnlyList<BattlefieldCluster> Battlefield { get; }
    public IReadOnlyList<BattlefieldCohort> Cohorts { get; }
    public bool IsDense { get; }

    internal SceneSeat(SeatGeometry geometry, BattlefieldCluster[] battlefield)
    {
        Geometry = geometry;
        Battlefield = Array.AsReadOnly(battlefield);
        IsDense = battlefield.Any(cluster => cluster.Cards.Any(card => card.Dense));
        var cohorts = new List<BattlefieldCohort>();
        foreach (var cluster in battlefield) {
            var members = new List<string>();
            foreach (var card in cluster.Cards) {
                occurrences.TryAdd(card.Id, card);
                members.Add(card.Id);
                if (!card.GroupFront) continue;
                var cohort = new BattlefieldCohort(card, Array.AsReadOnly(members.ToArray()));
                cohorts.Add(cohort);
                foreach (string id in members) cohortsByOccurrence.TryAdd(id, cohort);
                members.Clear();
            }
        }
        Cohorts = cohorts.AsReadOnly();
    }

    public SceneCard? BattlefieldCard(string occurrenceId) => occurrences.GetValueOrDefault(occurrenceId);
    public BattlefieldCohort? Cohort(string occurrenceId) => cohortsByOccurrence.GetValueOrDefault(occurrenceId);
}

/// <summary>Prepared table layout shared by painting, hit testing and layout checks.</summary>
public sealed class TableScene
{
    private readonly Dictionary<int, SceneSeat> seats;
    public HandPlan Hand { get; }
    public PlayAccessPlan PlayAccess { get; }
    public CardSpreadPlan? Spread { get; }
    public IReadOnlyList<SceneSeat> Seats { get; }
    public IReadOnlyList<SceneCard> BattlefieldDrawOrder { get; }

    private TableScene(HandPlan hand, PlayAccessPlan playAccess, SceneSeat[] seats, CardSpreadPlan? spread)
    {
        Hand = hand;
        PlayAccess = playAccess;
        Spread = spread;
        Seats = Array.AsReadOnly(seats);
        this.seats = seats.ToDictionary(seat => seat.PlayerId);
        BattlefieldDrawOrder = Array.AsReadOnly(seats.SelectMany(seat => seat.Battlefield).SelectMany(cluster => cluster.Cards)
            .OrderByDescending(card => card.Attachment?.Layer ?? 0).ToArray());
    }

    public SceneSeat Seat(int player) => seats[player];
    public SceneCard? BattlefieldCard(int player, string occurrenceId) => seats.GetValueOrDefault(player)?.BattlefieldCard(occurrenceId);
    public BattlefieldCohort? Cohort(string occurrenceId) => Seats.Select(seat => seat.Cohort(occurrenceId)).FirstOrDefault(cohort => cohort != null);
    // These views expose the same prepared seat collections.
    public IReadOnlyList<SceneCard> Commanders(int player) => Seat(player).Geometry.Commanders;
    public IReadOnlyList<PilePlan> Piles(int player) => Seat(player).Geometry.Piles;
    public IReadOnlyList<BattlefieldCluster> Battlefield(int player) => Seat(player).Battlefield;

    public static TableScene Build(GameSnapshot state,
        Point pointer, string? previouslyRaised, string? dragged, bool allowHover, Func<Card, bool> isResource,
        Func<Card, int, bool, BattlefieldLabelMetrics>? denseMeasure = null, bool allowInlineOrdering = true,
        bool sharedTableStudy = false, CardSpreadRequest? spread = null, Func<CardSpreadPacket, int, int>? spreadMeasure = null)
    {
        var geometry = SeatGeometry.Build(state, sharedTableStudy);
        var hand = HandLayout.Build(state, pointer, previouslyRaised, dragged, allowHover);
        var playAccess = PlayAccessPlan.Build(state);
        CardSpreadPacket[] packets = spread?.Target is CardCollectionTarget.HandCollection
            ? [new(CardSpreadPlan.HandId, "", "", [], hand.Cards)]
            : spread?.Target is CardCollectionTarget.PublicZone publicZone ? PublicZonePacket(state, geometry, playAccess, publicZone)
            : playAccess.Groups.Where(group => spread?.Target is CardCollectionTarget.AccessCollection
                    || spread?.Target is CardCollectionTarget.AccessPacket packet && group.Id == packet.PacketId)
                .Select(group => new CardSpreadPacket(group.Id,
                    string.Join(" · ", group.Cards.SelectMany(item => item.Access.Routes).Select(route => route.SourceName)
                        .Where(name => name.Length > 0).Distinct()),
                    group.Zone + (group.PlayerId == state.ViewerId ? "" : " · " + state.Players.First(player => player.Id == group.PlayerId).Name),
                    group.SourceIds, group.Cards.Select(item => item.Scene).ToArray())).ToArray();
        var spreadPlan = spread == null ? null : CardSpreadPlan.Build(spread.Target, packets, spread.View, spread.Top, spreadMeasure,
            spread.Target is CardCollectionTarget.PublicZone ? packets.SelectMany(packet => packet.Cards).LastOrDefault()?.Id : null);
        var orderedChoices = allowInlineOrdering && state.Decision?.Payload is OrderingPayload order
            ? order.Selection.Choices.Where(choice => choice.Card != null && choice.CardId.Length > 0).Select(choice => choice.CardId).ToHashSet(StringComparer.Ordinal) : null;
        bool duel = sharedTableStudy && state.Players.Length == 2;
        var studySeparate = orderedChoices == null ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(orderedChoices, StringComparer.Ordinal);
        if (sharedTableStudy && state.Decision?.Payload is { } required
            && required is TargetSelectionPayload or EntitySelectionPayload or AttackPayload or BlockPayload or OptionsPayload or AbilityChoicePayload or OrderingPayload or RevealPayload) {
            var choices = required switch { OptionsPayload options => options.Selection.Choices, OrderingPayload ordering => ordering.Selection.Choices,
                AbilityChoicePayload ability => ability.Selection.Choices, _ => [] };
            var disclosed = required is RevealPayload reveal ? reveal.Cards : [];
            foreach (var card in state.Players.SelectMany(player => player.Zone("Battlefield").Cards)
                .Where(card => required is AttackPayload or BlockPayload || card.Selectable
                    || choices.Any(choice => choice.CardId == CardViews.Identity(card) || choice.Card != null && CardViews.Same(choice.Card, card))
                    || disclosed.Any(face => face.CardId == CardViews.Identity(card) || CardViews.Same(face.Card, card)))) studySeparate.Add(CardViews.Identity(card));
        }
        var layout = sharedTableStudy ? null : BattlefieldLayoutContext.Build(state, orderedChoices, geometry, isResource);
        var attachments = sharedTableStudy ? BattlefieldAttachments.Build(state,
            geometry.Where(seat => seat.Index is >= 0 and <= 3).Select(seat => seat.PlayerId).ToHashSet()) : null;
        var seats = new List<SceneSeat>();
        for (int index = 0; index < state.Players.Length; index++) {
            var player = state.Players[index];
            var seat = geometry[index];
            if (seat.Index is < 0 or > 3) continue;
            var clusters = sharedTableStudy
                ? SharedTableStudy.Arrange(seat.Index, player.Id, player.Zone("Battlefield").Cards.Where(card => !attachments!.IsTucked(card)).ToArray(), isResource, studySeparate,
                        duel ? role => DuelTableStudy.MatchLane(seat.Index, role) : null, duel ? 1.35f : 1,
                        horizontalRows: duel, centerRows: duel, attachments: attachments, seatFacing: !duel)
                : BattlefieldLayout.Arrange(layout!, seat.Index, player.Id,
                    player.Zone("Battlefield").Cards, isResource, orderedChoices, denseMeasure);
            seats.Add(new(seat, clusters));
        }
        if (sharedTableStudy) {
            // A foreign-controlled Aura follows its host spatially, while lookup,
            // input permissions and controller collections retain its actual owner.
            var all = seats.SelectMany(seat => seat.Battlefield).SelectMany(cluster => cluster.Cards.Select(node => (cluster.Kind, Node: node))).ToArray();
            seats = seats.Select(seat => new SceneSeat(seat.Geometry, all.Where(item => item.Node.Owner == seat.PlayerId)
                .GroupBy(item => item.Kind).Select(group => new BattlefieldCluster(group.Key, group.Select(item => item.Node).ToArray())).ToArray())).ToList();
        }
        return new(hand, playAccess, seats.ToArray(), spreadPlan);
    }

    private static CardSpreadPacket[] PublicZonePacket(GameSnapshot state, IReadOnlyList<SeatGeometry> geometry,
        PlayAccessPlan access, CardCollectionTarget.PublicZone target)
    {
        var zone = state.Players.FirstOrDefault(player => player.Id == target.PlayerId)?.Zone(target.ZoneName);
        if (zone == null) return [];
        var pile = geometry.FirstOrDefault(seat => seat.PlayerId == target.PlayerId)?.Piles.FirstOrDefault(pile => pile.Zone == target.ZoneName);
        var anchor = pile == null ? new Pose(new(800, 744), new(49, 69)) : PileGeometry.Create(pile.Pose, zone.Count, pile.Compact).Top;
        var cards = zone.Cards.Where(card => !card.FaceDown && CardViews.Identity(card).Length > 0).Select((card, index) => SceneCard.Create(card,
            access.Groups.SelectMany(group => group.Cards).FirstOrDefault(item => CardViews.Same(item.Scene.Card, card))?.Scene.Pose ?? anchor,
            target.ZoneName, target.PlayerId, index, anchor.Center)).ToArray();
        return [new(target.Id, "", "", [], cards)];
    }
}
