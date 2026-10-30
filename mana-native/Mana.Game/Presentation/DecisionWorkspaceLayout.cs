using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table.Presentation;

// Geometry only: the current scene owner supplies actual occupied rectangles.
internal static class DecisionWorkspaceLayout
{
    internal static Rectangle Place(Rectangle anchor, int requestedWidth, int requestedHeight, IReadOnlyList<Rectangle> occupied, bool bounded, int minimumWidth = 320)
    {
        if (bounded) return PlaceWorkspace(anchor, requestedWidth, requestedHeight, occupied, minimumWidth);
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

    private static Rectangle PlaceWorkspace(Rectangle anchor, int width, int height, IReadOnlyList<Rectangle> occupied, int minimumWidth)
    {
        var viewport = new Rectangle(20, 64, 1560, 696);
        width = Math.Clamp(width, Math.Clamp(minimumWidth, 1, viewport.Width), viewport.Width); height = Math.Clamp(height, 100, viewport.Height);
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
        IEnumerable<int> NearbyEdges(IEnumerable<int> edges, int extent, int center)
        {
            const int budget = 24;
            var ordered = edges.Distinct().OrderBy(edge => Math.Abs(edge + extent / 2 - center)).ToArray();
            // Dense edges on one side must not consume the other side's gap
            // candidates. Return unused quota to the original nearest order.
            var selected = ordered.Where(edge => edge + extent / 2 < center).Take(budget / 2)
                .Concat(ordered.Where(edge => edge + extent / 2 >= center).Take(budget / 2))
                .Concat(ordered).Distinct().Take(budget).ToHashSet();
            return ordered.Where(selected.Contains);
        }
        var xs = NearbyEdges(occupied.SelectMany(obstacle => new[] { obstacle.Right + 8, obstacle.Left - width - 8 })
            .Append(viewport.Left).Append(viewport.Right - width).Select(x => At(x, 64).X), width, anchor.Center.X)
            .Append(viewport.Left).Append(viewport.Right - width).Distinct().ToArray();
        var ys = NearbyEdges(occupied.SelectMany(obstacle => new[] { obstacle.Bottom + 8, obstacle.Top - height - 8 })
            .Append(viewport.Top).Append(viewport.Bottom - height).Select(y => At(20, y).Y), height, anchor.Center.Y)
            .Append(viewport.Top).Append(viewport.Bottom - height).Distinct().ToArray();
        foreach (int x in xs) foreach (int y in ys) {
            var candidate = At(x, y); long overlap = Overlap(candidate, bestClear, bestOverlap, out bool clear);
            if (clear && !bestClear || clear == bestClear && overlap < bestOverlap) {
                best = candidate; bestOverlap = overlap; bestClear = clear;
            }
        }
        return best;
    }}
