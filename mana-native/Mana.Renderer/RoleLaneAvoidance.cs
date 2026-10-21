using Mana.Contracts;
using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Renderer;

internal sealed record RoleLaneSeat(int Index, int Owner, BattlefieldCluster[] Clusters);

/// <summary>
/// Keeps ordinary, full-size role lanes clear of the stack and seat objects.
/// Only an obstructed public-state family moves; its copies and poses retain
/// their exact sizes, angles, perspective and relative fan positions.
/// </summary>
internal static class RoleLaneAvoidance
{
    private sealed record Family(int Seat, int Owner, SceneCard[] Cards, Rectangle[] Footprints)
    {
        internal Rectangle Bounds => Footprints.Aggregate(Rectangle.Union);
    }

    internal static IReadOnlyDictionary<int, BattlefieldCluster[]> Arrange(IReadOnlyList<RoleLaneSeat> seats,
        IReadOnlyList<Rectangle> protectedObjects, IReadOnlySet<string>? ordered)
    {
        var families = seats.SelectMany(seat => Families(seat, ordered)).ToArray();
        var moving = families.Where(family => family.Footprints.Any(bounds => protectedObjects.Any(bounds.Intersects))).ToArray();
        if (moving.Length == 0) return seats.ToDictionary(seat => seat.Owner, seat => seat.Clusters);
        var movingSet = moving.ToHashSet();
        var occupied = protectedObjects.Concat(families.Where(family => !movingSet.Contains(family)).SelectMany(family => family.Footprints)).ToList();
        var offsets = new Dictionary<SceneCard, Vector2>(ReferenceEqualityComparer.Instance);
        foreach (var family in moving.OrderByDescending(family => (long)family.Bounds.Width * family.Bounds.Height)) {
            if (!TryPlace(family, occupied, out var offset)) {
                // Never commit a partial rearrangement that buries a later
                // family's original location. A genuinely overfull field needs
                // its dedicated bounded layout or one-player workspace.
                return seats.ToDictionary(seat => seat.Owner, seat => seat.Clusters);
            }
            foreach (var card in family.Cards) offsets.Add(card, offset);
            occupied.AddRange(family.Footprints.Select(bounds => Shift(bounds, offset)));
        }
        return seats.ToDictionary(seat => seat.Owner, seat => seat.Clusters.Select(cluster => cluster with {
            Cards = cluster.Cards.Select(card => offsets.TryGetValue(card, out var offset)
                ? card with { Pose = card.Pose with { Center = card.Pose.Center + offset } } : card).ToArray()
        }).ToArray());
    }

    private static IEnumerable<Family> Families(RoleLaneSeat seat, IReadOnlySet<string>? ordered)
    {
        foreach (var cluster in seat.Clusters) {
            var copies = new List<SceneCard>();
            foreach (var card in cluster.Cards) {
                copies.Add(card);
                if (!card.GroupFront) continue;
                yield return new(seat.Index, seat.Owner, copies.ToArray(), Footprints(copies, seat.Index, ordered));
                copies.Clear();
            }
            if (copies.Count > 0) yield return new(seat.Index, seat.Owner, copies.ToArray(), Footprints(copies, seat.Index, ordered));
        }
    }

    // Ordinary labels have fixed rows beside or within the projected face.
    // Reserve their envelope too, so moving the art cannot place its stats or
    // counters beneath the protected panel. This does not change typography.
    private static Rectangle[] Footprints(IReadOnlyList<SceneCard> copies, int seat, IReadOnlySet<string>? ordered)
    {
        var bounds = copies.Select(node => {
            var face = node.Pose.Bounds; face.Inflate(6, 8); return face;
        }).ToList();
        var front = copies[^1]; var card = front.Card; var pose = front.Pose; var faceBounds = pose.Bounds;
        bool creature = card.Type.Contains("Creature", StringComparison.Ordinal);
        int counters = card.Counters.Count(counter => counter.Value > 0);
        bool states = card.PhasedOut || card.Sick && creature || (card.States?.Length ?? card.CombatKeywords.Length) > 0;
        if (front.Compact) {
            const int width = 110, row = 22;
            int top = seat == 1 ? faceBounds.Bottom + row + 6 : seat is 0 or 2 ? (int)pose.Center.Y - 10 : faceBounds.Top;
            int left = seat == 0 ? faceBounds.Right + 8 : seat == 2 ? faceBounds.Left - 8 - width : (int)pose.Center.X - width / 2;
            int rows = 1 + (creature || copies.Count > 1 || card.Damage > 0 ? 2 : 0) + (counters > 0 ? 1 : 0)
                + (counters > 1 ? 1 : 0) + (states ? 1 : 0);
            bool ordering = ordered?.Contains(card.VisualId) == true;
            bounds.Add(new(left, ordering ? faceBounds.Top - row : top - row, width, rows * row + (ordering ? 66 : 4)));
        } else {
            int width = Math.Max(110, faceBounds.Width);
            if (seat is 0 or 2 && (creature || counters > 0 || states || card.Damage > 0))
                bounds.Add(new(seat == 0 ? faceBounds.Right - 12 : faceBounds.Left - (counters > 0 ? 158 : 98),
                    (int)pose.Center.Y - 28, counters > 0 ? 170 : 110, 84));
            if (counters > 0 && seat is not (0 or 2))
                bounds.Add(new((int)pose.Center.X - width / 2, faceBounds.Top - Math.Min(2, counters) * 27 - 12,
                    width, Math.Min(2, counters) * 27 + 12));
            if (creature && seat is not (0 or 2))
                bounds.Add(new((int)pose.Center.X - width / 2, faceBounds.Bottom - 16, width, 34));
        }
        return bounds.ToArray();
    }

    private static bool TryPlace(Family family, IReadOnlyList<Rectangle> occupied, out Vector2 offset)
    {
        var bounds = family.Bounds;
        var free = new List<Rectangle> { Region(family.Seat) };
        foreach (var obstacle in occupied) Subtract(free, obstacle);
        Vector2 outward = family.Seat switch { 0 => new(-1, 0), 1 => new(0, -1), 2 => new(1, 0), _ => new(0, 1) };
        var candidates = free.Where(area => area.Width >= bounds.Width && area.Height >= bounds.Height).Select(area =>
            new Vector2(Math.Clamp(bounds.Left, area.Left, area.Right - bounds.Width) - bounds.Left,
                Math.Clamp(bounds.Top, area.Top, area.Bottom - bounds.Height) - bounds.Top));
        var nearest = candidates.Select(candidate => (Offset: candidate,
            Cost: candidate.LengthSquared() + Math.Max(0, -Vector2.Dot(candidate, outward)) * 8))
            .OrderBy(candidate => candidate.Cost).ThenBy(candidate => candidate.Offset.Y).ThenBy(candidate => candidate.Offset.X).FirstOrDefault();
        if (nearest.Offset == default) { offset = default; return false; }
        offset = nearest.Offset; return true;
    }

    private static Rectangle Region(int seat) => seat switch {
        0 => new(20, 150, 650, 630), 1 => new(340, 150, 920, 370),
        2 => new(930, 150, 650, 630), _ => new(300, 430, 940, 350)
    };
    private static Rectangle Shift(Rectangle bounds, Vector2 offset) => new(bounds.X + (int)offset.X,
        bounds.Y + (int)offset.Y, bounds.Width, bounds.Height);

    // Maximal empty rectangles preserve both routes around an obstacle. A
    // single guillotine split can discard the only full-size card location.
    private static void Subtract(List<Rectangle> free, Rectangle obstacle)
    {
        for (int index = free.Count - 1; index >= 0; index--) {
            var area = free[index]; var overlap = Rectangle.Intersect(area, obstacle);
            if (overlap.IsEmpty) continue;
            free.RemoveAt(index);
            if (overlap.Left > area.Left) free.Add(new(area.Left, area.Top, overlap.Left - area.Left, area.Height));
            if (overlap.Right < area.Right) free.Add(new(overlap.Right, area.Top, area.Right - overlap.Right, area.Height));
            if (overlap.Top > area.Top) free.Add(new(area.Left, area.Top, area.Width, overlap.Top - area.Top));
            if (overlap.Bottom < area.Bottom) free.Add(new(area.Left, overlap.Bottom, area.Width, area.Bottom - overlap.Bottom));
        }
        for (int index = free.Count - 1; index >= 0; index--)
            if (free.Where((_, other) => other != index).Any(area => area.Contains(free[index]))) free.RemoveAt(index);
    }
}
