using System.Text.Json;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

// Automation must hit the same physical pixel that the game's input code reads.
// A logical point can cross an overlap edge when converted to integer pixels.
internal static class SmokePointer
{
    internal static Point Screen(TableGame.Probe view, Point logical)
    {
        var viewport = view.GraphicsDevice.Viewport;
        float scale = Math.Min(viewport.Width / 1600f, viewport.Height / (float)view.DesignHeight);
        return new((int)((viewport.Width - 1600 * scale) / 2 + logical.X * scale),
            (int)((viewport.Height - view.DesignHeight * scale) / 2 + logical.Y * scale));
    }

    internal static Point Table(TableGame.Probe view, Point physical)
    {
        var viewport = view.GraphicsDevice.Viewport;
        float scale = Math.Min(viewport.Width / 1600f, viewport.Height / (float)view.DesignHeight);
        return new((int)((physical.X - (viewport.Width - 1600 * scale) / 2) / scale),
            (int)((physical.Y - (viewport.Height - view.DesignHeight * scale) / 2) / scale));
    }

    internal static bool TryPoint(TableGame.Probe view, Mana.Table.Interaction.TableHit hit, Point logical, out Point physical)
    {
        physical = Screen(view, logical);
        return view.CurrentHit(hit) && Matches(view, hit, physical);
    }

    private static bool Matches(TableGame.Probe view, Mana.Table.Interaction.TableHit hit, Point physical)
    {
        var viewport = view.GraphicsDevice.Viewport;
        if (physical.X < 0 || physical.Y < 0 || physical.X >= viewport.Width || physical.Y >= viewport.Height) return false;
        var actual = view.HitAt(Table(view, physical));
        return actual?.Id == hit.Id && actual.Scope == hit.Scope;
    }

    internal static Point Hit(TableGame.Probe view, Mana.Table.Interaction.TableHit hit)
    {
        if (!view.CurrentHit(hit)) throw new InvalidOperationException("Stale pointer target: " + hit.Id);
        var viewport = view.GraphicsDevice.Viewport;
        float scale = Math.Min(viewport.Width / 1600f, viewport.Height / (float)view.DesignHeight);
        float left = (viewport.Width - 1600 * scale) / 2, top = (viewport.Height - view.DesignHeight * scale) / 2;
        int minX = Math.Max(0, (int)Math.Floor(left + hit.Bounds.Left * scale));
        int maxX = Math.Min(viewport.Width - 1, (int)Math.Ceiling(left + hit.Bounds.Right * scale));
        int minY = Math.Max(0, (int)Math.Floor(top + hit.Bounds.Top * scale));
        int maxY = Math.Min(viewport.Height - 1, (int)Math.Ceiling(top + hit.Bounds.Bottom * scale));
        var center = Screen(view, hit.Bounds.Center);
        bool Reachable(Point point, bool interior) => Matches(view, hit, point) && (!interior
            || Matches(view, hit, new(point.X - 1, point.Y)) && Matches(view, hit, new(point.X + 1, point.Y))
            && Matches(view, hit, new(point.X, point.Y - 1)) && Matches(view, hit, new(point.X, point.Y + 1)));
        for (int pass = 0; pass < 2; pass++) {
            bool interior = pass == 0;
            if (Reachable(center, interior)) return Record(center);
            for (int y = minY; y <= maxY; y++)
                for (int x = minX; x <= maxX; x++) {
                    var point = new Point(x, y);
                    if (Reachable(point, interior)) return Record(point);
                }
        }
        throw new InvalidOperationException("No exposed physical pixel for " + hit.Id);

        Point Record(Point physical)
        {
            var logical = Table(view, physical);
            var old = view.HitAt(Table(view, center));
            File.AppendAllText(Path.Combine(view.Profile, "pointer-targets.log"), JsonSerializer.Serialize(new {
                expected = hit.Id, scope = hit.Scope, physical, logical,
                actual = view.HitAt(logical)?.Id, oldCenterPhysical = center, oldCenterHit = old?.Id
            }, new JsonSerializerOptions { IncludeFields = true }) + Environment.NewLine);
            return physical;
        }
    }
}
