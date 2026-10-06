using Microsoft.Xna.Framework;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Renderer;

// Coordinates are on a single physical plane: X across the table, Y toward the
// viewer. Seats rotate their local rows toward the common center. No game rules
// or hidden information enter this camera/layout layer.
public static class TableWorld
{
    public const float Depth = 2100, Pitch = .72f;
    public static Vector2 Project(Vector2 point)
    {
        float scale = 1 / (1 - point.Y / Depth);
        return new(800 + point.X * scale, 415 + point.Y * Pitch * scale);
    }
    public static Vector2 Unproject(Vector2 screen)
    {
        float y = (screen.Y - 415) / (Pitch + (screen.Y - 415) / Depth);
        return new((screen.X - 800) * (1 - y / Depth), y);
    }
    public static Pose Card(Vector2 center, Vector2 size, float angle = 0, float lift = 0)
    {
        float scale = 1 / (1 - center.Y / Depth);
        var point = Project(center);
        return new(point - new Vector2(0, lift), size * scale, angle, Pitch * scale, -1 / Depth, (point.X - 800) / Depth, lift);
    }
    public static float SeatAngle(int seat) => seat switch { 0 => MathF.PI / 2, 1 => MathF.PI, 2 => -MathF.PI / 2, _ => 0 };
    public static Vector2 SeatPoint(int seat, float across, float outward) => seat switch {
        0 => new(-outward, -55 + across),
        1 => new(-across, -outward),
        2 => new(outward, -55 - across),
        _ => new(across, outward)
    };
    public static Pose Rank(int seat, bool lands, int index, int count, bool tapped, bool advancing = false)
    {
        bool side = seat is 0 or 2, own = seat == 3;
        float stride = Math.Min(lands ? (side ? 61 : 76) : (side ? 82 : 114), (own ? lands ? 700 : 760 : side ? 390 : 550) / Math.Max(1, count));
        float across = (index - (count - 1) / 2f) * stride;
        float outward = own ? (lands ? 278 : 172) : side ? (lands ? 575 : 405) : (lands ? 365 : 230);
        if (advancing) outward -= 26;
        var size = lands ? new Vector2(62, 87) : new Vector2(own ? 94 : side ? 74 : 88, own ? 132 : side ? 104 : 123);
        return Card(SeatPoint(seat, across, outward), size, SeatAngle(seat) + (tapped ? MathF.PI / 2 : 0));
    }
    public static bool CanPlayAt(Point pointer)
    {
        var point = Unproject(pointer.ToVector2());
        return point.X is > -420 and < 420 && point.Y is > 90 and < 375;
    }
    public static Pose Pile(int seat, int index)
    {
        var center = seat == 3 ? new Vector2(-594 + index * 82, 319)
            : seat is 0 or 2 ? Unproject(new(seat == 0 ? 85 : 1515, 490 + index * 58))
            : SeatPoint(seat, 305 + index * 77, 405);
        return Card(center, new(49, 69), SeatAngle(seat));
    }
    // Spells rise out of the table plane so the response window has a readable focal point.
    public static Pose Spell(int index) => new(new(790 + index * 24, 408 + index * 8), new(148, 207), -.025f + index * .07f, .94f, -.00025f, 0, 32);
    public static Vector2[] Rim(float inset = 0, float drop = 0)
    {
        return Enumerable.Range(0, 128).Select(i => {
            float angle = i * MathF.Tau / 128;
            return Project(new((860 - inset) * MathF.Cos(angle), (500 - inset * .5f) * MathF.Sin(angle))) + new Vector2(0, drop);
        }).ToArray();
    }
}
