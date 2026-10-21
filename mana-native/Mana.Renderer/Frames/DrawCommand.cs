using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Renderer;

/// <summary>Copied drawing data. Commands cannot capture application state or execute game actions.</summary>
public abstract record DrawCommand
{
    internal abstract void Paint(Canvas canvas);
    public sealed record Fill(Rectangle Bounds, Color Color) : DrawCommand
    {
        internal override void Paint(Canvas canvas) => canvas.Fill(Bounds, Color);
    }
    public sealed record Rounded(Rectangle Bounds, Color Color, int Radius) : DrawCommand
    {
        internal override void Paint(Canvas canvas) => canvas.Rounded(Bounds, Color, Radius);
    }
    public sealed record Border(Rectangle Bounds, Color Color, int Width) : DrawCommand
    {
        internal override void Paint(Canvas canvas) => canvas.Border(Bounds, Color, Width);
    }
    public sealed record Panel(Rectangle Bounds, Color FillColor, Color Trim, int Radius) : DrawCommand
    {
        internal override void Paint(Canvas canvas) => canvas.Panel(Bounds, FillColor, Trim, Radius);
    }
    public sealed record Gradient(Rectangle Bounds, Color Top, Color Bottom) : DrawCommand
    {
        internal override void Paint(Canvas canvas) => canvas.Gradient(Bounds, Top, Bottom);
    }
    public sealed record Circle(Vector2 Center, float Radius, Color Color) : DrawCommand
    {
        internal override void Paint(Canvas canvas) => canvas.Circle(Center, Radius, Color);
    }
    public sealed record Glow(Vector2 Center, Vector2 Size, Color Color) : DrawCommand
    {
        internal override void Paint(Canvas canvas) => canvas.Glow(Center, Size, Color);
    }
    public sealed record Ring(Vector2 Center, float Radius, Color Color, float Width, float Start, float Sweep) : DrawCommand
    {
        internal override void Paint(Canvas canvas) => canvas.Ring(Center, Radius, Color, Width, Start, Sweep);
    }
    public sealed record Line(Vector2 From, Vector2 To, Color Color, float Width) : DrawCommand
    {
        internal override void Paint(Canvas canvas) => canvas.Line(From, To, Color, Width);
    }
    public sealed record Arrow(Vector2 From, Vector2 To, Color Color, float Width) : DrawCommand
    {
        internal override void Paint(Canvas canvas) => canvas.Arrow(From, To, Color, Width);
    }
    public sealed record Image(Texture2D Texture, Rectangle Bounds, float Alpha) : DrawCommand
    {
        internal override void Paint(Canvas canvas) => canvas.Image(Texture, Bounds, Alpha);
    }
    public sealed record Sprite(Texture2D Texture, Vector2 Center, Vector2 Size, float Rotation, Color? Tint, Rectangle? Crop) : DrawCommand
    {
        internal override void Paint(Canvas canvas) => canvas.Sprite(Texture, Center, Size, Rotation, Tint, Crop);
    }
    public sealed record Quad(Vector2 Center, Vector2 Size, float Angle, Color Tint) : DrawCommand
    {
        internal override void Paint(Canvas canvas) => canvas.Quad(Center, Size, Angle, Tint);
    }
    public sealed record Surface(Texture2D? Texture, Pose Pose, Color Tint) : DrawCommand
    {
        internal override void Paint(Canvas canvas) => canvas.Surface(Texture, Pose, Tint);
    }
    public sealed record Polygon(IReadOnlyList<Vector2> Points, Color Color) : DrawCommand
    {
        internal override void Paint(Canvas canvas) => canvas.Polygon(Points, Color);
    }
    public sealed record Portrait(Texture2D Texture, Vector2 Center, float Radius, Color Tint) : DrawCommand
    {
        internal override void Paint(Canvas canvas) => canvas.Portrait(Texture, Center, Radius, Tint);
    }
    public sealed record ProjectedEllipse(Texture2D Texture, Pose Pose, Color Tint) : DrawCommand
    {
        internal override void Paint(Canvas canvas) => canvas.ProjectedEllipse(Texture, Pose, Tint);
    }
    public sealed record CardFrame(Pose Pose, Color Color, int Width) : DrawCommand
    {
        internal override void Paint(Canvas canvas) => canvas.CardFrame(Pose, Color, Width);
    }
    public sealed record CardFocus(Pose Pose, Color Accent) : DrawCommand
    {
        internal override void Paint(Canvas canvas) => canvas.CardFocus(Pose, Accent);
    }
    public sealed record CardAction(Pose Pose, Color Accent, float GlowAmount) : DrawCommand
    {
        internal override void Paint(Canvas canvas) => canvas.CardAction(Pose, Accent, GlowAmount);
    }
    public sealed record FlatCardFrame(Vector2 Center, Vector2 Size, float Angle, Color Color, int Width) : DrawCommand
    {
        internal override void Paint(Canvas canvas) => canvas.CardFrame(Center, Size, Angle, Color, Width);
    }
    public sealed record Text(string? Value, int X, int Y, Color Color, float Size, int Width, int MaxLines, bool Bold, bool Serif) : DrawCommand
    {
        internal override void Paint(Canvas canvas) => canvas.Text(Value, X, Y, Color, Size, Width, MaxLines, Bold, Serif);
    }
    public sealed record CenterText(string? Value, Rectangle Bounds, Color Color, float Size, bool Bold, bool Serif) : DrawCommand
    {
        internal override void Paint(Canvas canvas) => canvas.CenterText(Value, Bounds, Color, Size, Bold, Serif);
    }
}
