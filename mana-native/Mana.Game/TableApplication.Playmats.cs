using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private void DrawPlaymatTable()
    {
        // Study cards already live on this plane. Projecting through TableWorld
        // again would detach the cloth from its cards during camera movement.
        canvas.BeginClip(BoardViewport);
        canvas.BeginTransform(boardView);
        var table = new Rectangle(12, 40, 1576, 744);
        canvas.Rounded(new Rectangle(table.X, table.Y + 10, table.Width, table.Height), Color.Black * .6f, 30);
        canvas.Rounded(table, new(67, 51, 40), 28);
        table.Inflate(-5, -5);
        canvas.Rounded(table, new(37, 32, 29), 24);
        table.Inflate(-3, -3);
        canvas.Rounded(table, new(49, 40, 33), 22);
        // Long, low-contrast grain belongs to the table, not to a screen overlay.
        for (int i = 0; i < 100; i++) {
            float y = table.Y + 12 + i * (table.Height - 24) / 100f;
            float x = table.X + 12 + i * 157 % 190;
            float length = table.Right - 16 - x - i * 71 % 180;
            canvas.Line(new Vector2(x, y), new Vector2(x + length, y + 1.5f),
                (i % 3 == 0 ? new Color(117, 88, 57) : Color.Black) * .075f, 1);
        }
        bool duel = match!.Players.Length == 2;
        foreach (var seat in tableScene!.Seats)
            if (seat.Geometry.PlaymatBounds is { } bounds) DrawPlayerPlaymat(seat.Index, duel, bounds);
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
        canvas.Rounded(new Rectangle(bounds.X + 2, bounds.Y + 6, bounds.Width, bounds.Height), Color.Black * .6f, radius);
        canvas.Rounded(bounds, new(13, 17, 18), radius);
        var face = bounds; face.Inflate(-3, -3);
        canvas.Rounded(face, Color.Lerp(cloth, Color.Black, .15f), radius - 1);
        face.Inflate(-3, -3);
        canvas.Rounded(face, cloth, radius - 2);

        // A quiet weave and a sewn hem read as cloth without putting artwork or
        // interface boxes behind the card faces. Their positions are constant.
        for (int i = 0; i < 90; i++) {
            var p = new Vector2(face.Left + 9 + i * 113 % (face.Width - 18), face.Top + 9 + i * 137 % (face.Height - 18));
            canvas.Line(p, p + new Vector2(3, .6f), new Color(211, 211, 181) * .055f, .65f);
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

        void Stitch(Vector2 from, Vector2 to) => canvas.Line(from, to, thread * .65f, 1);
    }
}
