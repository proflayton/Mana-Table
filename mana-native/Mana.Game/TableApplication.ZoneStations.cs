using Mana.Renderer;
using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private PilePlan? ZonePile(int owner, string zone) => tableScene?.Seats.FirstOrDefault(seat => seat.PlayerId == owner)
        ?.Geometry.Piles.FirstOrDefault(pile => pile.Zone == zone);

    private Pose DisplayZonePose(PilePlan pile, Pose pose) => pile.TableAnchored ? boardView.Apply(pose) : pose;
    private Rectangle DisplaySeatZoneBounds(SeatGeometry seat, Rectangle bounds) => seat.ZoneStation == null ? bounds : boardView.Apply(bounds);

    // Map the prepared pile once. Paint, exact pointer shapes, labels, transfer
    // endpoints and reservations all consume this same displayed geometry.
    private PileGeometry DisplayPileGeometry(PilePlan pile, int count)
    {
        var geometry = PileGeometry.Create(pile, count);
        if (!pile.TableAnchored) return geometry;
        return geometry with {
            Shadow = boardView.Apply(geometry.Shadow),
            Layers = Array.AsReadOnly(geometry.Layers.Select(boardView.Apply).ToArray()),
            Top = boardView.Apply(geometry.Top),
            Label = boardView.Apply(geometry.Label), Count = boardView.Apply(geometry.Count),
            CountCenter = boardView.Apply(geometry.CountCenter), HitBounds = boardView.Apply(geometry.HitBounds),
            Reservation = boardView.Apply(geometry.Reservation)
        };
    }

    private Rectangle DisplayCommandLabel(SeatGeometry seat) => DisplaySeatZoneBounds(seat,
        seat.ZoneStation?.CommandLabel ?? new((int)seat.CommandLink.X - 56, (int)seat.CommandLink.Y - 3, 112, 30));
    private Rectangle DisplayCommandActions(SeatGeometry seat) => DisplaySeatZoneBounds(seat,
        seat.ZoneStation?.CommandActions ?? new((int)seat.CommandLink.X - 51, (int)seat.CommandLink.Y + 25, 102, 22));
}
