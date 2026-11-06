using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;

namespace Mana.Renderer;

// A quiet, stationary pointing gesture. Open head and gaps remain distinct
// from the filled arrows used for assigned combat, including reduced motion.
public static class TableChoiceArrow
{
    public static void Draw(DrawList drawing, Vector2 from, Vector2 to, Color color)
    {
        var delta = to - from;
        float length = delta.Length();
        if (!float.IsFinite(length) || length < 12 || color.A == 0) return;
        var direction = delta / length;
        var side = new Vector2(-direction.Y, direction.X);
        float head = Math.Min(13, length / 3), body = length - head;
        const float dash = 9, gap = 6;
        // Screen-space endpoints keep work bounded even when the camera clips
        // most of the connection. No scrolling animation or pointer trail.
        int pieces = Math.Min(256, (int)MathF.Ceiling(body / (dash + gap)));
        float step = pieces == 0 ? dash + gap : Math.Max(dash + gap, body / pieces);
        for (int index = 0; index < pieces; index++) {
            float start = index * step, end = Math.Min(body, start + dash);
            drawing.Line(from + direction * start, from + direction * end, Color.Black * .6f, 4);
            drawing.Line(from + direction * start, from + direction * end, color, 2);
        }
        for (int sign = -1; sign <= 1; sign += 2) {
            var wing = to - direction * head + side * (head * .5f * sign);
            drawing.Line(wing, to, Color.Black * .6f, 4);
            drawing.Line(wing, to, color, 2);
        }
    }
}
