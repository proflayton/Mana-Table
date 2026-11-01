using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private void DrawPlaymatTable()
    {
        // Cloth and cards share the same seated plane before the outer camera.
        canvas.BeginTransform(boardView);
        // The continuous oval edge supplies the seated table silhouette, even
        // beneath the fixed hand. Its material uses the same world projection.
        canvas.Polygon(TableWorld.Rim(-9, 32), Color.Black * .6f);
        canvas.Polygon(TableWorld.Rim(0, 17), new(23, 25, 28));
        canvas.Polygon(TableWorld.Rim(), new(89, 80, 65));
        canvas.Polygon(TableWorld.Rim(5), new(48, 46, 43));
        canvas.Polygon(TableWorld.Rim(24), new(67, 51, 40));
        canvas.Polygon(TableWorld.Rim(28), new(49, 40, 33));
        // Long, low-contrast grain belongs to the table, not to a screen overlay.
        for (int i = 0; i < 100; i++) {
            float y = -470 + i * 9.4f;
            float extent = 820 * MathF.Sqrt(Math.Max(0, 1 - y * y / (480 * 480)));
            PlaneLine(TableWorld.Origin + new Vector2(-extent, y), TableWorld.Origin + new Vector2(extent, y),
                (i % 3 == 0 ? new Color(117, 88, 57) : Color.Black) * .075f, 1);
        }
        var inlay = TableWorld.Rim(53);
        for (int i = 0; i < inlay.Length; i++) canvas.Line(inlay[i], inlay[(i + 1) % inlay.Length], new Color(162, 150, 119) * .25f, 1);
        canvas.EndTransform();
        canvas.BeginClip(BoardViewport);
        canvas.BeginTransform(boardView);
        bool duel = match!.Players.Length == 2;
        foreach (var seat in tableScene!.Seats)
            if (seat.Geometry.PlaymatPlacement is { } bounds) DrawPlayerPlaymat(seat.Index, duel, bounds);
        canvas.EndTransform();
        canvas.EndClip();
    }

    private void DrawPlayerPlaymat(int seat, bool duel, Rectangle bounds)
    {
        Color cloth = seat switch {
            0 => new(44, 57, 68),
            1 => new(61, 48, 61),
            2 => new(65, 56, 41),
            _ => new(36, 62, 57)
        };
        Color thread = Color.Lerp(cloth, new Color(196, 181, 145), .42f);
        const int radius = 12;
        PlaneRounded(new Rectangle(bounds.X + 2, bounds.Y + 6, bounds.Width, bounds.Height), Color.Black * .6f, radius);
        PlaneRounded(bounds, new(13, 17, 18), radius);
        var face = bounds; face.Inflate(-3, -3);
        PlaneRounded(face, Color.Lerp(cloth, Color.Black, .15f), radius - 1);
        face.Inflate(-3, -3);
        PlaneRounded(face, cloth, radius - 2);

        // A quiet weave and a sewn hem read as cloth without putting artwork or
        // interface boxes behind the card faces. Their positions are constant.
        for (int i = 0; i < 90; i++) {
            var p = new Vector2(face.Left + 9 + i * 113 % (face.Width - 18), face.Top + 9 + i * 137 % (face.Height - 18));
            PlaneLine(p, p + new Vector2(3, .6f), new Color(211, 211, 181) * .055f, .65f);
        }
        var hem = bounds; hem.Inflate(-8, -8);
        for (int x = hem.Left + 8; x < hem.Right - 8; x += 14) {
            Stitch(new(x, hem.Top), new(Math.Min(x + 5, hem.Right - 8), hem.Top));
            Stitch(new(x, hem.Bottom), new(Math.Min(x + 5, hem.Right - 8), hem.Bottom));
        }
        for (int y = hem.Top + 8; y < hem.Bottom - 8; y += 14) {
            Stitch(new(hem.Left, y), new(hem.Left, Math.Min(y + 5, hem.Bottom - 8)));
            Stitch(new(hem.Right, y), new(hem.Right, Math.Min(y + 5, hem.Bottom - 8)));
        }

        // A woven crest is a second, non-color ownership cue. It faces the seat;
        // turn and priority retain their existing temporary hero indicators.
        var center = bounds.Center.ToVector2();
        var outward = duel || seat is 1 or 3 ? new Vector2(0, seat == 1 ? -1 : 1) : new Vector2(seat == 0 ? -1 : 1, 0);
        float reach = outward.X == 0 ? bounds.Height / 2f : bounds.Width / 2f;
        var crest = center + outward * (reach - 20);
        var across = new Vector2(-outward.Y, outward.X);
        var tip = crest - outward * 6;
        var left = crest - across * 5;
        var right = crest + across * 5;
        var foot = crest + outward * 6;
        Stitch(tip, left); Stitch(left, foot); Stitch(foot, right); Stitch(right, tip);
        for (int i = 0; i <= seat; i++) {
            var knot = crest + across * ((i - seat / 2f) * 5) + outward * 10;
            Stitch(knot - outward, knot + outward);
        }

        void Stitch(Vector2 from, Vector2 to) => PlaneLine(from, to, thread * .65f, 1);
    }

    private void PlaneLine(Vector2 from, Vector2 to, Color color, float width) =>
        canvas.Line(TableSurfaceProjection.Project(from), TableSurfaceProjection.Project(to), color, width);

    private void PlaneRounded(Rectangle bounds, Color color, int radius)
    {
        radius = Math.Clamp(radius, 0, Math.Min(bounds.Width, bounds.Height) / 2);
        var centers = new[] {
            new Vector2(bounds.Right - radius, bounds.Top + radius),
            new Vector2(bounds.Right - radius, bounds.Bottom - radius),
            new Vector2(bounds.Left + radius, bounds.Bottom - radius),
            new Vector2(bounds.Left + radius, bounds.Top + radius)
        };
        var points = new List<Vector2>(28);
        for (int corner = 0; corner < centers.Length; corner++)
            for (int step = 0; step <= 6; step++) {
                float angle = -MathF.PI / 2 + corner * MathF.PI / 2 + step * MathF.PI / 12;
                points.Add(TableSurfaceProjection.Project(centers[corner] + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius));
            }
        canvas.Polygon(points, color);
    }
}
