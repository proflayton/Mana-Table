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

    internal static SceneCard Create(Card card, Pose pose, string zone, int owner, int index, Vector2? entrance)
    {
        string id = CardViews.Identity(card);
        // Anonymous objects get positional identities; names never identify an occurrence.
        if (id.Length == 0) id = $"anonymous:{owner}:{zone}:{index}";
        return new(id, card, pose, zone, owner, entrance);
    }
}
public sealed record PilePlan(string Zone, Pose Pose, bool Compact);
public sealed record HandPlan(int Page, int Total, string? Raised, SceneCard[] Cards);

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
    public IReadOnlyList<SceneSeat> Seats { get; }

    private TableScene(HandPlan hand, SceneSeat[] seats)
    {
        Hand = hand;
        Seats = Array.AsReadOnly(seats);
        this.seats = seats.ToDictionary(seat => seat.PlayerId);
    }

    public SceneSeat Seat(int player) => seats[player];
    public SceneCard? BattlefieldCard(int player, string occurrenceId) => seats.GetValueOrDefault(player)?.BattlefieldCard(occurrenceId);
    public BattlefieldCohort? Cohort(string occurrenceId) => Seats.Select(seat => seat.Cohort(occurrenceId)).FirstOrDefault(cohort => cohort != null);
    // These views expose the same prepared seat collections.
    public IReadOnlyList<SceneCard> Commanders(int player) => Seat(player).Geometry.Commanders;
    public IReadOnlyList<PilePlan> Piles(int player) => Seat(player).Geometry.Piles;
    public IReadOnlyList<BattlefieldCluster> Battlefield(int player) => Seat(player).Battlefield;

    public static TableScene Build(GameSnapshot state, int handPage,
        Point pointer, string? previouslyRaised, string? dragged, bool allowHover, Func<Card, bool> isResource,
        Func<Card, int, bool, BattlefieldLabelMetrics>? denseMeasure = null, bool allowInlineOrdering = true,
        bool sharedTableStudy = false, int? studyFocusedOpponent = null)
    {
        var hand = HandLayout.Build(state, handPage, pointer, previouslyRaised, dragged, allowHover);
        var orderedChoices = allowInlineOrdering && state.Decision is { Kind: "choice", Ordered: true } decision
            ? decision.Choices.Where(c => c.Card != null && !string.IsNullOrEmpty(c.CardId)).Select(c => c.CardId).ToHashSet(StringComparer.Ordinal) : null;
        bool duel = sharedTableStudy && state.Players.Length == 2;
        if (duel) studyFocusedOpponent = state.Players.First(player => player.Id != state.ViewerId).Id;
        bool studyFocus = sharedTableStudy && studyFocusedOpponent is { } opponent && opponent != state.ViewerId
            && state.Players.Any(player => player.Id == opponent);
        var geometry = SeatGeometry.Build(state, sharedTableStudy, studyFocus ? studyFocusedOpponent : null);
        var studySeparate = orderedChoices == null ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(orderedChoices, StringComparer.Ordinal);
        if (sharedTableStudy && state.Decision is { } required
            && (required.Intent is DecisionIntent.Selection or DecisionIntent.Attack or DecisionIntent.Block
                || required.Kind is "choice" or "reveal")) {
            foreach (var card in state.Players.SelectMany(player => player.Zone("Battlefield").Cards)
                .Where(card => required.Intent is DecisionIntent.Attack or DecisionIntent.Block || card.Selectable
                    || required.Choices.Any(choice => choice.CardId == CardViews.Identity(card)
                    || choice.Card != null && CardViews.Same(choice.Card, card)))) studySeparate.Add(CardViews.Identity(card));
        }
        var layout = sharedTableStudy ? null : BattlefieldLayoutContext.Build(state, orderedChoices, geometry, isResource);
        var seats = new List<SceneSeat>();
        for (int index = 0; index < state.Players.Length; index++) {
            var player = state.Players[index];
            var seat = geometry[index];
            if (seat.Index is < 0 or > 3) continue;
            var clusters = sharedTableStudy
                ? studyFocus && seat.Index is 0 or 2 ? Array.Empty<BattlefieldCluster>()
                    : SharedTableStudy.Arrange(seat.Index, player.Id, player.Zone("Battlefield").Cards, isResource, studySeparate,
                        duel ? role => DuelTableStudy.MatchLane(seat.Index, role)
                            : studyFocus ? role => DuelTableStudy.Lane(seat.Index, role) : null, studyFocus ? 1.35f : 1,
                        horizontalRows: studyFocus, centerRows: duel)
                : BattlefieldLayout.Arrange(layout!, seat.Index, player.Id,
                    player.Zone("Battlefield").Cards, isResource, orderedChoices, denseMeasure);
            seats.Add(new(seat, clusters));
        }
        return new(hand, seats.ToArray());
    }
}
