using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Renderer;

/// <summary>The physical card body shared by painting, reading and camera framing.</summary>
public readonly record struct CardPaintGeometry(Pose Face)
{
    public const int FocusFrameWidth = 2;
    public Pose Shadow => Face with {
        Center = Face.Center + new Vector2(4 + Face.Elevation * .18f, (Face.IsProjected ? 5 : 10) + Face.Elevation),
        Size = Face.Size + new Vector2(7)
    };
    public Pose Backing => Face with { Size = Face.Size + new Vector2(4) };
    public Pose FocusFrame => Backing;
    public Rectangle FrameBounds {
        get {
            var frame = FocusFrame.Bounds;
            frame.Inflate(FocusFrameWidth / 2, FocusFrameWidth / 2);
            return frame;
        }
    }
    public Rectangle Bounds => Rectangle.Union(FrameBounds, Shadow.Bounds);
}
