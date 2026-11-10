using Mana.Contracts;
using Microsoft.Xna.Framework;
using Point = Microsoft.Xna.Framework.Point;
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
    public SeatZoneTrayGeometry? Tray { get; }

    internal SeatZoneLayout(Player player, TableSeatFrame frame, Vector2 hero)
    {
        PlayerId = player.Id;
        bool near = frame.LocalApron;
        // The near seat keeps its life counter centered above the held hand.
        // Its personal row sits alongside the mat, facing the same player.
        Placement = frame.Project(near ? new(-324, 210, 318, 68) : PlayerPlaymat.PersonalArea);
        Tray = near ? null : new(Placement);
        Vector2 Point(Vector2 local) => frame.Project(local);
        Pose Face(Vector2 local, Vector2 size) => TableSurfaceProjection.Project(
            new Pose(Point(local), size, frame.Facing));
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
            if (frame.QuarterTurns == 3) {
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
        Piles = PreparePiles(frame);
    }

    internal static IReadOnlyList<PilePlan> PreparePiles(TableSeatFrame frame)
    {
        bool near = frame.LocalApron;
        return Array.AsReadOnly(new[] { "Library", "Graveyard", "Exile" }.Select((zone, index) => {
            var pose = TableSurfaceProjection.Project(new Pose(frame.Project(
                new Vector2((near ? -304 : 184) + index * 72, near ? 244 : 246)), new(39, 55), frame.Facing));
            var bounds = pose.Bounds;
            // Captions stay upright on the outward side of the actual card.
            // Far piles also reserve their maximum visible layer thickness.
            var label = frame.QuarterTurns switch {
                1 => new Rectangle(bounds.Left - 72, (int)pose.Center.Y - 9, 68, 19),
                2 => new Rectangle((int)pose.Center.X - 34, bounds.Top - 32, 68, 19),
                3 => new Rectangle(bounds.Right + 4, (int)pose.Center.Y - 9, 68, 19),
                _ => new Rectangle((int)pose.Center.X - 34, bounds.Bottom + 3, 68, 19)
            };
            return new PilePlan(zone, pose, true) {
                TableAnchored = true,
                Caption = label
            };
        }).ToArray());
    }
}

/// <summary>The immutable physical tray face shared by painting and review.
/// It adds no zone access, action scope or independent input surface.</summary>
public sealed record SeatZoneTrayGeometry(Rectangle Placement)
{
    public IReadOnlyList<Vector2> Outline(int inset = 0, Point offset = default)
    {
        var bounds = Placement; bounds.Inflate(-inset, -inset); bounds.Offset(offset);
        int chamfer = Math.Max(2, 7 - inset);
        var points = new[] {
            new Vector2(bounds.Left + chamfer, bounds.Top), new Vector2(bounds.Right - chamfer, bounds.Top),
            new Vector2(bounds.Right, bounds.Top + chamfer), new Vector2(bounds.Right, bounds.Bottom - chamfer),
            new Vector2(bounds.Right - chamfer, bounds.Bottom), new Vector2(bounds.Left + chamfer, bounds.Bottom),
            new Vector2(bounds.Left, bounds.Bottom - chamfer), new Vector2(bounds.Left, bounds.Top + chamfer)
        };
        return Array.AsReadOnly(points.Select(TableSurfaceProjection.Project).ToArray());
    }
}
