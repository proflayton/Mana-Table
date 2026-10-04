using Mana.Contracts;
using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Renderer;

// A card can lie on the perspective table or stand upright in the UI. The same
// homography drives its texture, outline, pointer test, and combat connections.
public readonly record struct Pose(Vector2 Center, Vector2 Size, float Angle = 0, float Tilt = 1, float Perspective = 0, float Shear = 0, float Elevation = 0)
{
    public bool IsProjected => Tilt != 1 || Perspective != 0 || Shear != 0;
    public Vector2 Point(float u, float v)
    {
        var local = new Vector2((u - .5f) * Size.X, (v - .5f) * Size.Y);
        float c = MathF.Cos(Angle), s = MathF.Sin(Angle);
        var d = new Vector2(local.X * c - local.Y * s, local.X * s + local.Y * c);
        return Center + new Vector2(d.X + Shear * d.Y, d.Y * Tilt) / (1 + d.Y * Perspective);
    }
    public Vector2[] Corners => [Point(0, 0), Point(1, 0), Point(1, 1), Point(0, 1)];
    public Rectangle Bounds {
        get {
            var corners = Corners;
            int x = (int)MathF.Floor(corners.Min(p => p.X)), y = (int)MathF.Floor(corners.Min(p => p.Y));
            return new(x, y, (int)MathF.Ceiling(corners.Max(p => p.X)) - x, (int)MathF.Ceiling(corners.Max(p => p.Y)) - y);
        }
    }
    public bool Contains(Point point)
    {
        var corners = Corners; float sign = 0;
        for (int i = 0; i < 4; i++) {
            float cross = Cross(corners[(i + 1) % 4] - corners[i], point.ToVector2() - corners[i]);
            if (Math.Abs(cross) < .01f) continue;
            if (sign != 0 && Math.Sign(cross) != Math.Sign(sign)) return false;
            sign = cross;
        }
        return true;
    }
    public Vector2 EdgeToward(Vector2 point, float margin = 0)
    {
        var delta = point - Center; if (delta.LengthSquared() < .001f) return Center;
        var corners = Corners;
        for (int i = 0; i < 4; i++) {
            var side = corners[(i + 1) % 4] - corners[i]; float denominator = Cross(delta, side);
            if (Math.Abs(denominator) < .001f) continue;
            float t = Cross(corners[i] - Center, side) / denominator;
            float u = Cross(corners[i] - Center, delta) / denominator;
            if (t >= 0 && u >= 0 && u <= 1) return Center + delta * t + Vector2.Normalize(delta) * margin;
        }
        return Center;
    }
    private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;
}

// Stable presentation identities survive new decision handles. Hidden objects are
// removed at reconciliation, never retained for a disappearance animation.
public sealed class SceneMotion
{
    private sealed class Item(Pose pose) { public Pose Current = pose, Target = pose, Display = pose; public long Seen; public float Flight = 1, Height, Delay; }
    private readonly Dictionary<string, Item> items = [];
    private long frame;
    private float elapsed;
    private bool reduced;
    public int Count => items.Count;
    public bool TryGet(string id, out Pose pose) { if (items.TryGetValue(id, out var item) && item.Seen == frame) { pose = item.Display; return true; } pose = default; return false; }
    public void Begin(float elapsedSeconds, bool reducedMotion) { frame++; elapsed = Math.Clamp(elapsedSeconds, 0, .1f); reduced = reducedMotion; }
    public Pose Hold(string id, Pose pose)
    {
        // Direct manipulation stays attached to the pointer. Subsequent layout
        // reconciliation can continue from this pose without inventing a move.
        items[id] = new(pose) { Seen = frame };
        return pose;
    }
    public Pose Place(string id, Pose target, Vector2? entrance = null, float entranceDelay = 0)
    {
        if (!items.TryGetValue(id, out var item)) items[id] = item = new(reduced || entrance == null ? target : target with { Center = entrance.Value, Size = target.Size * .65f }) { Delay = reduced || entrance == null ? 0 : entranceDelay };
        item.Seen = frame;
        if (reduced) item.Delay = 0;
        if (item.Delay > 0) { item.Delay -= elapsed; return item.Display; }
        float travel = Vector2.Distance(item.Target.Center, target.Center);
        if (travel > 100) { item.Flight = 0; item.Height = Math.Clamp(travel * .12f, 18, 48); }
        item.Flight = reduced ? 1 : Math.Min(1, item.Flight + elapsed / .42f);
        item.Seen = frame; item.Target = target;
        float amount = reduced ? 1 : 1 - MathF.Exp(-14 * elapsed);
        float angle = item.Current.Angle + MathHelper.WrapAngle(target.Angle - item.Current.Angle) * amount;
        item.Current = new(Vector2.Lerp(item.Current.Center, target.Center, amount), Vector2.Lerp(item.Current.Size, target.Size, amount), angle,
            MathHelper.Lerp(item.Current.Tilt, target.Tilt, amount), MathHelper.Lerp(item.Current.Perspective, target.Perspective, amount), MathHelper.Lerp(item.Current.Shear, target.Shear, amount), MathHelper.Lerp(item.Current.Elevation, target.Elevation, amount));
        float lift = item.Flight >= 1 ? 0 : MathF.Sin(item.Flight * MathF.PI) * item.Height;
        item.Display = item.Current with { Center = item.Current.Center - new Vector2(0, lift), Elevation = item.Current.Elevation + lift };
        return item.Display;
    }
    public void End() { foreach (var key in items.Where(p => p.Value.Seen != frame).Select(p => p.Key).ToArray()) items.Remove(key); }
    public void Clear() => items.Clear();
}

public static class TableLayout
{
    public const int Width = 1600, Height = 900;
    public static readonly Vector2 LocalHand = new(800, 876);
    public static Rectangle LocalSeatBounds => new((int)Hero(3).X - 62, (int)Hero(3).Y - 62, 124, 124);
    public static Pose Hand(int index, int count, bool raised = false)
    {
        float stride = Math.Min(128, 700f / Math.Max(1, count - 1));
        float offset = index - (count - 1) / 2f;
        float bend = count <= 1 ? 0 : offset / Math.Max(1, (count - 1) / 2f);
        return raised ? new(LocalHand + new Vector2(offset * stride, -60), new(186, 260), Elevation: 24)
            : new(LocalHand + new Vector2(offset * stride, bend * bend * 28), new(137, 192), bend * .13f);
    }
    public static Player[] Opponents(GameSnapshot state)
    {
        int viewer = Array.FindIndex(state.Players, p => p.Id == state.ViewerId);
        if (viewer < 0) return state.Players;
        return Enumerable.Range(1, state.Players.Length - 1).Select(offset => state.Players[(viewer + offset) % state.Players.Length]).ToArray();
    }
    public static int SeatIndex(GameSnapshot state, int playerId) => Array.FindIndex(Opponents(state), p => p.Id == playerId);
    public static Vector2 Hero(int index) => index switch { 0 => new(102, 370), 1 => new(800, 108), 2 => new(1498, 370), _ => new(800, 744) };

    public static Vector2 SeatHand(int seat) => seat == 3 ? LocalHand
        : Hero(seat) + (seat == 1 ? new Vector2(-146, -8) : new Vector2(0, -100));
    public static Pose HiddenHand(int seat, int index, int count)
    {
        float offset = index - (count - 1) / 2f;
        return new(SeatHand(seat) + new Vector2(offset * 17, Math.Abs(offset) * 3), new(34, 48), offset * .075f);
    }

}
