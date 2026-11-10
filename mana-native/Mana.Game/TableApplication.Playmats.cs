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
        // The actual rectangular perimeter follows the same seated plane as
        // the cloth. Its fixed apron supports the unchanged side/near card
        // rails; no content count or camera focus can resize the furniture.
        var tabletop = tableScene!.TabletopPlacement;
        PlaneRounded(tabletop, Color.Black * .6f, 18, 22);
        PlaneRounded(tabletop, new(23, 25, 28), 18, 12);
        PlaneRounded(tabletop, new(89, 80, 65), 18);
        var material = tabletop; material.Inflate(-4, -4);
        PlaneRounded(material, new(48, 46, 43), 14);
        material.Inflate(-4, -4);
        PlaneRounded(material, new(49, 40, 33), 10);
        // Long, low-contrast grain belongs to the table, not to a screen overlay.
        for (int i = 0; i < 80; i++) {
            float y = material.Top + 10 + i * (material.Height - 20) / 80f;
            PlaneLine(new(material.Left + 10, y), new(material.Right - 10, y),
                (i % 3 == 0 ? new Color(117, 88, 57) : Color.Black) * .075f, 1);
        }
        canvas.EndTransform();
        canvas.BeginClip(BoardViewport);
        canvas.BeginTransform(boardView);
        foreach (var seat in tableScene!.Seats)
            if (seat.Geometry.PlaymatPlacement is { } bounds) DrawPlayerPlaymat(seat.Geometry.Frame, bounds);
        foreach (var seat in tableScene.Seats)
            if (seat.Geometry.ZoneStation?.Tray is { } tray) DrawPersonalStation(seat.Geometry.Frame, tray);
        canvas.EndTransform();
        canvas.EndClip();
    }

    private void DrawPlayerPlaymat(TableSeatFrame frame, Rectangle bounds)
    {
        int seat = frame.Index;
        Color cloth = seat switch {
            0 => new(44, 57, 68),
            1 => new(61, 48, 61),
            2 => new(65, 56, 41),
            4 => new(62, 47, 42),
            5 => new(44, 55, 65),
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
        var outward = frame.Outward;
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

    private void DrawPersonalStation(TableSeatFrame frame, SeatZoneTrayGeometry tray)
    {
        // A raised wooden card station is a physical object, distinct from the
        // cloth occupied by played permanents. Its prepared placement is also
        // used by inspection geometry; no card or pile is repositioned here.
        canvas.Polygon(tray.Outline(offset: new(3, 5)), Color.Black * .55f);
        canvas.Polygon(tray.Outline(), new Color(35, 24, 17));
        canvas.Polygon(tray.Outline(2), new Color(123, 89, 54));
        canvas.Polygon(tray.Outline(5), new Color(69, 47, 31));
        canvas.Polygon(tray.Outline(7), new Color(47, 33, 24));
        var area = PlayerPlaymat.PersonalArea;
        for (int index = 0; index < 14; index++) {
            float y = area.Top + 12 + index * (area.Height - 24) / 14f;
            PlaneLine(frame.Project(new Vector2(area.Left + 11, y)),
                frame.Project(new Vector2(area.Right - 11, y)), new Color(153, 107, 60) * .1f, .8f);
        }
        // The shallow groove separates waiting commanders from the lower
        // piles without introducing another dashboard row or control.
        PlaneLine(frame.Project(new Vector2(area.Left + 9, 216)),
            frame.Project(new Vector2(area.Right - 9, 216)), new Color(20, 14, 10) * .8f, 2);
    }

    private void PlaneLine(Vector2 from, Vector2 to, Color color, float width) =>
        canvas.Line(TableSurfaceProjection.Project(from), TableSurfaceProjection.Project(to), color, width);

    private void PlaneRounded(Rectangle bounds, Color color, int radius, float drop = 0)
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
                points.Add(TableSurfaceProjection.Project(centers[corner] + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius) + new Vector2(0, drop));
            }
        canvas.Polygon(points, color);
    }
}
