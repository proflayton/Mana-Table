using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Renderer;

// Mat packing uses one unchanged, owner-relative plane. This projects that
// plane once, before the existing pan/zoom camera and card-motion controller.
public static class TableSurfaceProjection
{
    public static Vector2 Origin => TableWorld.Origin;
    public static Vector2 Project(Vector2 point) => TableWorld.Project(point - Origin);
    public static Vector2 Unproject(Vector2 point) => TableWorld.Unproject(point) + Origin;
    public static Pose Project(Pose placement) => TableWorld.Card(placement.Center - Origin,
        placement.Size, placement.Angle, placement.Elevation);
    public static Pose Surface(Rectangle placement) => Project(new Pose(
        new(placement.X + placement.Width / 2f, placement.Y + placement.Height / 2f), placement.Size.ToVector2()));
    public static Rectangle Bounds(Rectangle placement) => placement.IsEmpty ? Rectangle.Empty : Surface(placement).Bounds;
}
