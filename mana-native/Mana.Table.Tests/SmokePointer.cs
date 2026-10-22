using System.Text.Json;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

// Automation must hit the same physical pixel that the game's input code reads.
// A logical point can cross an overlap edge when converted to integer pixels.
internal static class SmokePointer
{
    internal static Point Screen(TableWindow.Probe view, Point logical)
    {
        var viewport = view.GraphicsDevice.Viewport;
        return RenderViewport.Fit(new(viewport.Width, viewport.Height), view.DesignHeight).ToPhysical(logical.ToVector2()).ToPoint();
    }

    internal static Point Table(TableWindow.Probe view, Point physical)
    {
        var viewport = view.GraphicsDevice.Viewport;
        return RenderViewport.Fit(new(viewport.Width, viewport.Height), view.DesignHeight).ToLogical(physical);
    }

    internal static bool TryPoint(TableWindow.Probe view, Mana.Table.Interaction.TableHit hit, Point logical, out Point physical)
    {
        physical = Screen(view, logical);
        return view.CurrentHit(hit) && Matches(view, hit, physical);
    }

    private static bool Matches(TableWindow.Probe view, Mana.Table.Interaction.TableHit hit, Point physical)
    {
        var viewport = view.GraphicsDevice.Viewport;
        if (physical.X < 0 || physical.Y < 0 || physical.X >= viewport.Width || physical.Y >= viewport.Height) return false;
        var actual = view.HitAt(Table(view, physical));
        return actual?.Id == hit.Id && actual.Scope == hit.Scope;
    }

    internal static Point Hit(TableWindow.Probe view, Mana.Table.Interaction.TableHit hit)
    {
        if (!view.CurrentHit(hit)) throw new InvalidOperationException("Stale pointer target: " + hit.Id);
        var viewport = view.GraphicsDevice.Viewport;
        var mapping = RenderViewport.Fit(new(viewport.Width, viewport.Height), view.DesignHeight);
        var pixels = mapping.ToSurface(hit.Bounds);
        int minX = Math.Max(0, pixels.Left + mapping.Bounds.Left);
        int maxX = Math.Min(viewport.Width - 1, pixels.Right + mapping.Bounds.Left);
        int minY = Math.Max(0, pixels.Top + mapping.Bounds.Top);
        int maxY = Math.Min(viewport.Height - 1, pixels.Bottom + mapping.Bounds.Top);
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
