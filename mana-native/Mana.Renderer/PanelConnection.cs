using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Renderer;

/// <summary>Short world connections around one reading surface, never through its text.</summary>
public static class PanelConnection
{
    public static Vector2[] Route(Rectangle occupied, Vector2 port, Pose external, float clearance = 32)
    {
        if (occupied.Width <= 0 || occupied.Height <= 0 || external.Size.X <= 0 || external.Size.Y <= 0) return [];
        float gap = float.IsFinite(clearance) ? Math.Max(12, clearance) : 32;
        var blocked = occupied; blocked.Inflate(8, 8);
        var outside = external.Bounds;
        bool left = Math.Abs(port.X - occupied.Left) <= Math.Abs(port.X - occupied.Right);
        float shoulderX = left ? occupied.Left - gap : occupied.Right + gap;
        var shoulder = new Vector2(shoulderX, port.Y);
        // A nearby card can sit closer than the preferred shoulder distance.
        // Use the available gap, while keeping the shoulder outside both faces.
        if (Enters(port, shoulder, outside)) {
            float available = left ? occupied.Left - outside.Right : outside.Left - occupied.Right;
            if (available <= 10) return [];
            float distance = Math.Min(gap, (8 + available) / 2);
            shoulder = new(left ? occupied.Left - distance : occupied.Right + distance, port.Y);
        }
        if (Inside(shoulder, blocked) || Inside(shoulder, outside) || Enters(port, shoulder, outside)) return [];
        float top = occupied.Top - gap, bottom = occupied.Bottom + gap;
        float opposite = left ? occupied.Right + gap : occupied.Left - gap;
        Vector2[] best = []; float shortest = float.PositiveInfinity;
        void Consider(params Vector2[] waypoints)
        {
            var points = new List<Vector2> { port };
            foreach (var point in waypoints) {
                if (Inside(point, blocked) || Inside(point, outside)) return;
                if (Vector2.DistanceSquared(points[^1], point) > .01f) points.Add(point);
            }
            var end = external.EdgeToward(points[^1], 4);
            if (Vector2.DistanceSquared(points[^1], end) < .01f) return;
            points.Add(end);
            float length = 0;
            for (int i = 1; i < points.Count; i++) {
                // Only the initial outward stub may cross the panel margin.
                if (i > 1 && Enters(points[i - 1], points[i], blocked)) return;
                if (i < points.Count - 1 && Enters(points[i - 1], points[i], outside)) return;
                length += Vector2.Distance(points[i - 1], points[i]);
            }
            if (length >= shortest - .01f) return;
            // Keep the shoulder explicit; discard only redundant later bends.
            for (int i = points.Count - 2; i >= 2; i--) {
                var a = points[i] - points[i - 1]; var b = points[i + 1] - points[i];
                if (Math.Abs(a.X * b.Y - a.Y * b.X) < .01f && Vector2.Dot(a, b) >= 0) points.RemoveAt(i);
            }
            best = points.ToArray(); shortest = length;
        }
        Consider(shoulder);
        Consider(shoulder, new(shoulder.X, top));
        Consider(shoulder, new(shoulder.X, bottom));
        Consider(shoulder, new(shoulder.X, top), new(opposite, top));
        Consider(shoulder, new(shoulder.X, bottom), new(opposite, bottom));
        // External corners cover close cards whose bounding box blocks the
        // otherwise shortest shoulder or panel-corner approach.
        foreach (float x in new[] { outside.Left - gap, outside.Right + gap }) {
            Consider(shoulder, new(x, shoulder.Y));
            Consider(shoulder, new(shoulder.X, top), new(x, top));
            Consider(shoulder, new(shoulder.X, bottom), new(x, bottom));
            foreach (float y in new[] { outside.Top - gap, outside.Bottom + gap })
                Consider(shoulder, new(shoulder.X, y), new(x, y));
        }
        return best;
    }

    private static bool Inside(Vector2 point, Rectangle bounds) => point.X > bounds.Left && point.X < bounds.Right
        && point.Y > bounds.Top && point.Y < bounds.Bottom;

    private static bool Enters(Vector2 start, Vector2 end, Rectangle bounds)
    {
        float enter = 0, leave = 1;
        for (int axis = 0; axis < 2; axis++) {
            float origin = axis == 0 ? start.X : start.Y;
            float delta = axis == 0 ? end.X - start.X : end.Y - start.Y;
            float low = (axis == 0 ? bounds.Left : bounds.Top) + .001f;
            float high = (axis == 0 ? bounds.Right : bounds.Bottom) - .001f;
            if (Math.Abs(delta) < .001f) {
                if (origin <= low || origin >= high) return false;
            } else {
                float first = (low - origin) / delta, last = (high - origin) / delta;
                if (first > last) (first, last) = (last, first);
                enter = Math.Max(enter, first); leave = Math.Min(leave, last);
                if (enter > leave) return false;
            }
        }
        return leave > 0 && enter < 1;
    }
}
