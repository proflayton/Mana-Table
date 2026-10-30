using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private (Rectangle Anchor, Pose? Pose, Card? Card, string Zone) DecisionSourceAnchor(GameSnapshot state, Decision decision)
    {
        var reported = CardViews.Source(state, decision);
        var location = reported == null ? null : CardViews.InZones(state)
            .Where(occurrence => SameVisibleCard(occurrence.Card, reported)).Select(occurrence => (ZoneCardOccurrence?)occurrence).FirstOrDefault();
        var source = location?.Card;
        string identity = source == null ? "" : CardViews.Identity(source);
        var sourcePose = location == null ? null : DecisionSourcePose(identity, location.Value.Zone, location.Value.PlayerId);
        var owner = state.Players.FirstOrDefault(player => player.Id == location?.PlayerId)
            ?? state.Players.FirstOrDefault(player => player.Id == decision.Context.Source?.ControllerId) ?? state.Viewer;
        var geometry = tableScene?.Seats.FirstOrDefault(seat => seat.PlayerId == owner?.Id)?.Geometry;
        string zone = location?.Zone ?? decision.Context.Source?.Zone ?? "";
        var pile = geometry?.Piles.FirstOrDefault(candidate => candidate.Zone == zone);
        var anchor = sourcePose?.Bounds ?? pile?.Pose.Bounds
            ?? new Rectangle((int)(geometry?.Hero.X ?? 800) - 55, (int)(geometry?.Hero.Y ?? 744) - 55, 110, 110);
        return (anchor, sourcePose, reported, zone);
    }

    private Pose? DecisionSourcePose(string identity, string zone, int owner)
    {
        if (tableScene == null || identity.Length == 0) return null;
        SceneCard? node = tableScene.Spread?.Cards.FirstOrDefault(card => card.Id == identity)
            ?? (zone == "Battlefield" ? tableScene.Cohort(identity)?.Front
            : zone == "Hand" ? tableScene.Hand.Cards.FirstOrDefault(card => card.Id == identity)
            : tableScene.PlayAccess.Groups.SelectMany(group => group.Cards).FirstOrDefault(card => card.Scene.Id == identity)?.Scene
            ?? (zone == "Command" ? tableScene.Seats.FirstOrDefault(seat => seat.PlayerId == owner)?.Geometry.Commanders.FirstOrDefault(card => card.Id == identity) : null));
        Pose? pose = node == null ? null : BoardView.AffectsZone(zone) ? boardView.Apply(node.Pose) : node.Pose;
        if (BattlefieldFocused && zone == "Battlefield" && battlefieldFocusPlan?.Tile(identity) is { } tile) pose = tile.ArtPose;
        return matchPresentation.Cards.Pinned is { Focused: true, ReaderTarget: { } target } reader
            && reader.Id == identity && reader.Zone == zone ? target : pose;
    }

    private Rectangle DecisionActionArea(Rectangle anchor, int requestedWidth, int requestedHeight, bool reserveBattlefield = false,
        IReadOnlyList<Rectangle>? additionalReservations = null, int minimumWidth = 320, bool boundedWorkspace = false)
        => DecisionWorkspaceLayout.Place(anchor, requestedWidth, requestedHeight,
            DecisionActionReservations(anchor, reserveBattlefield, additionalReservations), reserveBattlefield || boundedWorkspace, minimumWidth);

    private List<Rectangle> DecisionActionReservations(Rectangle anchor, bool reserveBattlefield,
        IReadOnlyList<Rectangle>? additionalReservations = null, int sourcePadding = 12)
    {
        var source = anchor; source.Inflate(sourcePadding, sourcePadding);
        bool includeDecisionDock = DecisionDockCanPaint;
        var occupied = new List<Rectangle> { source };
        if (includeDecisionDock) occupied.Add(new(1240, 550, 350, 245));
        if (additionalReservations != null) occupied.AddRange(additionalReservations);
        if (match is { } state && tableScene != null) {
            occupied.AddRange(TableViewGeometry.FixedReservations(state, tableScene, includeDecisionDock));
            var turn = TurnControlLayout.For(SeatHero(state.ViewerId));
            var controls = turn.Markers.Append(turn.AutoCenter).Append(turn.HoldCenter).ToArray();
            int left = (int)controls.Min(point => point.X) - 22, top = (int)controls.Min(point => point.Y) - 22;
            occupied.Add(new(left, top, (int)controls.Max(point => point.X) + 22 - left,
                (int)controls.Max(point => point.Y) + 22 - top));
            if (reserveBattlefield) occupied.AddRange(tableScene.BattlefieldDrawOrder.Select(card => {
                var protectedBounds = matchPresentation.Cards.TryGetPose(card.Id, out var displayed)
                    ? displayed.Bounds : boardView.Apply(card.Pose).Bounds;
                protectedBounds.Inflate(8, 8); return protectedBounds;
            }));
            occupied.AddRange(state.Players.Select(player => PlanManaHalo(player, SeatDisplayIndex(player.Id)).Bounds).Where(bounds => !bounds.IsEmpty));
            if (TableStudyEnabled && matchPresentation.Cards.Pinned is { Zone: "Battlefield" } && anchor.Width >= 300)
                occupied.Add(new(anchor.Right + Mana.Table.Presentation.CardPresentationController.StudyDetailsGap, anchor.Top,
                    Mana.Table.Presentation.CardPresentationController.StudyDetailsWidth, anchor.Height));
        }
        return occupied;
    }
}
