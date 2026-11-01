using Mana.Contracts;
using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Renderer;

/// <summary>A player's personal cards share their mat's physical frame. These
/// are presentation placements, not card ownership or zone-access permissions.</summary>
public sealed class SeatZoneLayout
{
    public int PlayerId { get; }
    public Rectangle Placement { get; }
    public Rectangle CommandName { get; }
    public Rectangle CommandQuantity { get; }
    public Rectangle CommandLabel => Rectangle.Union(CommandName, CommandQuantity);
    public Rectangle CommandActions => CommandLabel;
    public IReadOnlyList<SceneCard> Commanders { get; }
    public IReadOnlyList<PilePlan> Piles { get; }

    internal SeatZoneLayout(Player player, int seat, bool duel, Vector2 hero)
    {
        PlayerId = player.Id;
        bool near = seat == 3;
        // The near seat keeps its life counter centered above the held hand.
        // Its personal row sits alongside the mat, facing the same player.
        Placement = PlayerPlaymat.Project(seat,
            near ? new(-324, 210, 318, 68) : PlayerPlaymat.PersonalArea, duel);
        var origin = PlayerPlaymat.CanonicalBounds.Center.ToVector2();
        Vector2 Point(Vector2 local)
        {
            var delta = local - origin;
            var center = PlayerPlaymat.Bounds(seat, duel).Center.ToVector2();
            return center + (seat switch {
                0 => new Vector2(-delta.Y, delta.X),
                1 => -delta,
                2 => new Vector2(delta.Y, -delta.X),
                _ => delta
            });
        }
        Pose Face(Vector2 local, Vector2 size) => TableSurfaceProjection.Project(
            new Pose(Point(local), size, PlayerPlaymat.Facing(seat, duel)));
        Rectangle Caption(Vector2 local, int width, int height)
        {
            var center = TableSurfaceProjection.Project(Point(local));
            return new((int)MathF.Round(center.X - width / 2f), (int)MathF.Round(center.Y - height / 2f), width, height);
        }

        var leaders = player.Zone("Command").Cards.Take(2).ToArray();
        Commanders = Array.AsReadOnly(leaders.Select((card, index) => SceneCard.Create(card,
            Face(near ? new(leaders.Length == 1 ? -56 : -82 + index * 52, 244)
                : new(leaders.Length == 1 ? 289 : 264 + index * 50, 178), new(48, 67)),
            "Command", player.Id, index, hero) with { TableAnchored = true }).ToArray());
        if (near) {
            var center = Face(new(-56, 244), new(48, 67));
            CommandName = new((int)MathF.Round(center.Center.X) - 62, center.Bounds.Bottom + 3, 68, 19);
            CommandQuantity = new(CommandName.Right, CommandName.Y, 56, 19);
        } else {
            var label = Caption(new(194, 177), 68, 40);
            if (seat == 2) {
                // A mat-local side offset turns into a vertical gap at this
                // seat. Keep upright text just below the painted assembly.
                var poses = Commanders.Select(card => card.Pose)
                    .DefaultIfEmpty(Face(new(289, 178), new(48, 67)));
                var group = poses.Select(pose => Rectangle.Union(new CardPaintGeometry(pose).Bounds,
                    (pose with { Size = pose.Size + new Vector2(13) }).Bounds)).Aggregate(Rectangle.Union);
                label = new(group.Center.X - 34, group.Bottom + 4, 68, 40);
            }
            CommandName = new(label.X, label.Y, label.Width, label.Height / 2);
            CommandQuantity = new(label.X, CommandName.Bottom, label.Width, label.Height / 2);
        }
        Piles = Array.AsReadOnly(new[] { "Library", "Graveyard", "Exile" }.Select((zone, index) => {
            var pose = Face(new((near ? -304 : 184) + index * 72, near ? 244 : 246), new(39, 55));
            var bounds = pose.Bounds;
            // Captions stay upright on the outward side of the actual card.
            // Far piles also reserve their maximum visible layer thickness.
            var label = seat switch {
                0 => new Rectangle(bounds.Left - 72, (int)pose.Center.Y - 9, 68, 19),
                1 => new Rectangle((int)pose.Center.X - 34, bounds.Top - 32, 68, 19),
                2 => new Rectangle(bounds.Right + 4, (int)pose.Center.Y - 9, 68, 19),
                _ => new Rectangle((int)pose.Center.X - 34, bounds.Bottom + 3, 68, 19)
            };
            return new PilePlan(zone, pose, true) {
                TableAnchored = true,
                Caption = label
            };
        }).ToArray());
    }
}
