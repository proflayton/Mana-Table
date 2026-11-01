using System.Collections.Immutable;
using Mana.Contracts;
using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Renderer;

/// <summary>Existing board envelopes and fixed controls used to admit a view shift.</summary>
public static class TableViewGeometry
{
    private static IEnumerable<Rectangle> ZoneFootprints(SeatGeometry geometry)
    {
        foreach (var pile in geometry.Piles.Where(pile => pile.TableAnchored))
            yield return PileGeometry.Create(pile, 100).Reservation;
        foreach (var card in geometry.Commanders.Where(card => card.TableAnchored)) {
            yield return (card.Pose with { Size = card.Pose.Size + new Vector2(13) }).Bounds;
            var body = new CardPaintGeometry(card.Pose);
            yield return body.Backing.Bounds;
            yield return body.Shadow.Bounds;
        }
        if (geometry.ZoneStation is { } station) yield return station.CommandLabel;
    }

    public static Rectangle PlayerSetupBounds(SeatGeometry geometry)
    {
        var bounds = geometry.PlaymatBounds ?? Rectangle.Empty;
        foreach (var zone in ZoneFootprints(geometry)) bounds = bounds.IsEmpty ? zone : Rectangle.Union(bounds, zone);
        return bounds;
    }

    public static IReadOnlyList<Rectangle> BoardFootprints(TableScene scene)
    {
        var bounds = new List<Rectangle>();
        foreach (var seat in scene.Seats) {
            bounds.AddRange(ZoneFootprints(seat.Geometry));
            foreach (var cluster in seat.Battlefield) {
                var copies = new List<SceneCard>();
                foreach (var card in cluster.Cards) {
                    copies.Add(card);
                    if (!card.GroupFront) continue;
                    bounds.AddRange(RoleLaneAvoidance.Footprints(copies, seat.Index, null));
                    copies.Clear();
                }
                if (copies.Count > 0) bounds.AddRange(RoleLaneAvoidance.Footprints(copies, seat.Index, null));
            }
        }
        return bounds.AsReadOnly();
    }

    public static IReadOnlyList<Rectangle> FixedReservations(GameSnapshot state, TableScene scene, bool includeDecisionDock = true)
    {
        // A supported viewport can show fewer seats than the rules match owns.
        // Reserve only rendered objects; choices for other seats stay explicit
        // in their decision workspace rather than inventing pile geometry.
        var visible = state with { Players = state.Players.Where(player => scene.Seats.Any(seat => seat.PlayerId == player.Id)).ToImmutableArray() };
        var geometry = visible.Players.Select(player => scene.Seat(player.Id).Geometry).ToArray();
        return Array.AsReadOnly(BattlefieldLayoutContext.ReserveObjects(visible, geometry, includeDecisionDock, includeTableAnchored: false)
            .Concat(new[] { new Rectangle(0, 0, 1600, 54), new Rectangle(244, 778, 970, 122) })
            .Concat(scene.PlayAccess.Bounds.IsEmpty ? [] : new[] { scene.PlayAccess.Bounds }).ToArray());
    }
}
