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
    public Rectangle CommandLabel { get; }
    public Rectangle CommandActions => CommandLabel;
    public IReadOnlyList<SceneCard> Commanders { get; }
    public IReadOnlyList<PilePlan> Piles { get; }

    internal SeatZoneLayout(Player player, int seat, bool duel, Vector2 hero)
    {
        PlayerId = player.Id;
        Placement = PlayerPlaymat.Project(seat, PlayerPlaymat.PersonalArea, duel);
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
            Face(new(leaders.Length == 1 ? 289 : 264 + index * 50, 178), new(48, 67)),
            "Command", player.Id, index, hero) with { TableAnchored = true }).ToArray());
        CommandLabel = Caption(new(194, 177), 68, 40);
        Piles = Array.AsReadOnly(new[] { "Library", "Graveyard", "Exile" }.Select((zone, index) => {
            var pose = Face(new(184 + index * 72, 246), new(39, 55));
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
