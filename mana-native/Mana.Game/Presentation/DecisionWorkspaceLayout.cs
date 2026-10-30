using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table.Presentation;

// Geometry only: the current scene owner supplies actual occupied rectangles.
internal static class DecisionWorkspaceLayout
{
    internal static Rectangle Place(Rectangle anchor, int requestedWidth, int requestedHeight, IReadOnlyList<Rectangle> occupied, bool bounded,
        int minimumWidth = 320, int minimumHeight = 100)
    {
        if (bounded) return PlaceWorkspace(anchor, requestedWidth, requestedHeight, occupied, minimumWidth, minimumHeight);
        var free = new List<Rectangle> { new(20, 64, 1560, 696) };
        foreach (var obstacle in occupied) {
            for (int index = free.Count - 1; index >= 0; index--) {
                var area = free[index]; var overlap = Rectangle.Intersect(area, obstacle);
                if (overlap.IsEmpty) continue;
                free.RemoveAt(index);
                if (overlap.Left > area.Left) free.Add(new(area.Left, area.Top, overlap.Left - area.Left, area.Height));
                if (overlap.Right < area.Right) free.Add(new(overlap.Right, area.Top, area.Right - overlap.Right, area.Height));
                if (overlap.Top > area.Top) free.Add(new(area.Left, area.Top, area.Width, overlap.Top - area.Top));
                if (overlap.Bottom < area.Bottom) free.Add(new(area.Left, overlap.Bottom, area.Width, area.Bottom - overlap.Bottom));
            }
        }
        var candidates = free.Where(area => area.Width >= Math.Min(260, requestedWidth) && area.Height >= 120).ToArray();
        if (candidates.Length == 0) candidates = free.Where(area => area.Width >= 100 && area.Height >= 110).ToArray();
        return candidates.Select(area => {
            int width = Math.Min(requestedWidth, area.Width), height = Math.Min(400, area.Height);
            int x = Math.Clamp(anchor.Right + 12, area.Left, area.Right - width);
            return new Rectangle(x, area.Top, width, height);
        }).OrderBy(area => area.Height >= requestedHeight ? 0 : 1)
            .ThenBy(area => area.Width >= requestedWidth ? 0 : 1)
            .ThenBy(area => area.Left >= anchor.Right ? 0 : area.Right <= anchor.Left ? 1 : 2)
            .ThenBy(area => {
                // Score where the content will actually sit beside the card,
                // rather than the unused top of its available vertical strip.
                int height = Math.Min(requestedHeight, area.Height);
                int top = Math.Clamp(anchor.Center.Y - height / 2, area.Top, area.Bottom - height);
                return Vector2.DistanceSquared(new Vector2(area.Center.X, top + height / 2), anchor.Center.ToVector2());
            })
            .ThenByDescending(area => area.Width * area.Height).FirstOrDefault();
    }

    private static Rectangle PlaceWorkspace(Rectangle anchor, int width, int height, IReadOnlyList<Rectangle> occupied, int minimumWidth, int minimumHeight)
    {
        var viewport = new Rectangle(20, 64, 1560, 696);
        width = Math.Clamp(width, Math.Clamp(minimumWidth, 1, viewport.Width), viewport.Width);
        height = Math.Clamp(height, Math.Clamp(minimumHeight, 1, viewport.Height), viewport.Height);
        Rectangle At(int x, int y) => new(Math.Clamp(x, viewport.Left, viewport.Right - width),
            Math.Clamp(y, viewport.Top, viewport.Bottom - height), width, height);
        long Overlap(Rectangle candidate, bool bestClear, long ceiling, out bool clear)
        {
            candidate.Inflate(8, 8);
            // A required choice must not bury the object it is asking about.
            // On a crowded table prefer overlapping unrelated background cards
            // over the source, even when those cards occupy more total area.
            var sourceOverlap = Rectangle.Intersect(candidate, anchor);
            clear = sourceOverlap.Width == 0 || sourceOverlap.Height == 0;
            long total = (long)sourceOverlap.Width * sourceOverlap.Height * 1024
                + (long)(Vector2.DistanceSquared(candidate.Center.ToVector2(), anchor.Center.ToVector2()) * .15f);
            foreach (var obstacle in occupied) {
                var overlap = Rectangle.Intersect(candidate, obstacle);
                clear &= overlap.Width == 0 || overlap.Height == 0;
                total += (long)overlap.Width * overlap.Height;
                if (!clear && (bestClear || total >= ceiling)) break;
            }
            return total;
        }
        var preferred = new[] {
            At(anchor.Center.X - width / 2, anchor.Top - height - 16),
            At(anchor.Left - width - 16, anchor.Center.Y - height / 2),
            At(anchor.Right + 16, anchor.Center.Y - height / 2),
            At(800 - width / 2, 470 - height / 2),
            At(anchor.Center.X - width / 2, anchor.Bottom + 16)
        };
        int? NearestGap(int low, int high, int extent, int target, IEnumerable<(int Start, int End)> orderedBlocked)
        {
            int cursor = low, distance = int.MaxValue; int? best = null;
            void Consider(int end)
            {
                if (end - cursor < extent) return;
                int origin = Math.Clamp(target - extent / 2, cursor, end - extent);
                int nextDistance = Math.Abs(origin + extent / 2 - target);
                if (nextDistance < distance) { best = origin; distance = nextDistance; }
            }
            foreach (var interval in orderedBlocked) {
                if (interval.End <= cursor) continue;
                Consider(Math.Min(high, interval.Start));
                cursor = Math.Max(cursor, interval.End);
                if (cursor > high) break;
            }
            Consider(high); return best;
        }
        // Project only the five preferred rows/columns onto their nearest clear
        // gap. Dense obstacle edges cannot prune these intervals from the grid.
        var solids = occupied.Prepend(anchor).Where(area => area.Width > 0 && area.Height > 0).ToArray();
        var horizontalSolids = solids.OrderBy(area => area.Left).ToArray();
        var verticalSolids = solids.OrderBy(area => area.Top).ToArray();
        preferred = preferred.Select(candidate => {
            int? x = NearestGap(viewport.Left, viewport.Right, width, anchor.Center.X, horizontalSolids
                .Where(area => area.Top < candidate.Bottom + 8 && area.Bottom > candidate.Top - 8)
                .Select(area => (area.Left - 8, area.Right + 8)));
            int? y = NearestGap(viewport.Top, viewport.Bottom, height, anchor.Center.Y, verticalSolids
                .Where(area => area.Left < candidate.Right + 8 && area.Right > candidate.Left - 8)
                .Select(area => (area.Top - 8, area.Bottom + 8)));
            var horizontal = x is { } left ? At(left, candidate.Y) : candidate;
            var vertical = y is { } top ? At(candidate.X, top) : candidate;
            if (x == null) return y == null ? candidate : vertical;
            if (y == null) return horizontal;
            return Vector2.DistanceSquared(horizontal.Center.ToVector2(), anchor.Center.ToVector2())
                <= Vector2.DistanceSquared(vertical.Center.ToVector2(), anchor.Center.ToVector2()) ? horizontal : vertical;
        }).ToArray();
        // A fully clear region beats any collision; distance and source/overlap
        // costs still choose between clear regions or the crowded fallback.
        var best = preferred[0]; long bestOverlap = long.MaxValue; bool bestClear = false;
        foreach (var candidate in preferred) {
            long overlap = Overlap(candidate, bestClear, bestOverlap, out bool clear);
            if (clear && !bestClear || clear == bestClear && overlap < bestOverlap) {
                best = candidate; bestOverlap = overlap; bestClear = clear;
            }
        }
        // Nearby obstacle edges and viewport edges supplement the source/center
        // candidates. Bound both axes so a crowded table does not multiply all
        // permanent positions every frame. The fallback stays fully reachable
        // when physical space requires some overlap; names never get squeezed.
        var xs = occupied.SelectMany(obstacle => new[] { obstacle.Right + 8, obstacle.Left - width - 8 })
            .Append(viewport.Left).Append(viewport.Right - width).Select(x => At(x, 64).X).Distinct()
            .OrderBy(x => Math.Abs(x + width / 2 - anchor.Center.X)).Take(24)
            .Append(viewport.Left).Append(viewport.Right - width).Distinct().ToArray();
        var ys = occupied.SelectMany(obstacle => new[] { obstacle.Bottom + 8, obstacle.Top - height - 8 })
            .Append(viewport.Top).Append(viewport.Bottom - height).Select(y => At(20, y).Y).Distinct()
            .OrderBy(y => Math.Abs(y + height / 2 - anchor.Center.Y)).Take(24)
            .Append(viewport.Top).Append(viewport.Bottom - height).Distinct().ToArray();
        foreach (int x in xs) foreach (int y in ys) {
            var candidate = At(x, y); long overlap = Overlap(candidate, bestClear, bestOverlap, out bool clear);
            if (clear && !bestClear || clear == bestClear && overlap < bestOverlap) {
                best = candidate; bestOverlap = overlap; bestClear = clear;
            }
        }
        return best;
    }}
