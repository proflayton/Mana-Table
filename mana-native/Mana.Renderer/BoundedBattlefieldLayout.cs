using Mana.Contracts;
using Microsoft.Xna.Framework;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Renderer;

// Crowded fields share one packing mode: a neighbor cannot expand into another
// seat's cards. Every source occurrence remains painted and independently hit.
internal static class BoundedBattlefieldLayout
{
    private sealed record Group(string Role, Card[] Cards, bool Plain, bool Ordered, bool Miniature,
        int LabelWidth, int Rows, int RowHeight, CopyFan Fan);
    private static Vector2 FaceSize(Group group, float scale) => new(
        Math.Max(group.Ordered ? 78 : 0, (group.Plain ? 27 : group.Miniature ? 26 : 42) * scale),
        (group.Plain ? 28 : group.Miniature ? 34 : 60) * scale);
    // A two-dimensional fan gives every real copy a four-pixel corner. Fifty
    // identical tokens need one state label, rather than a 200-pixel art strip.
    // The final occurrence stays in front and owns that shared state label.
    private readonly record struct CopyFan(int Columns, int Count)
    {
        internal Point Offset(int copy) => new(copy % Columns * 4, copy / Columns * 4);
        internal Point Front => Offset(Count - 1);
        internal int Width => (Math.Min(Columns, Count) - 1) * 4;
        internal int Height => (Count - 1) / Columns * 4;
        internal static CopyFan Create(int count, bool crowded) => new(crowded
            ? Math.Max(1, (int)MathF.Ceiling(MathF.Sqrt(count))) : count, count);
    }
    internal static Rectangle Territory(int seat) => seat switch {
        0 => new(20, 110, 540, 410), 1 => new(560, 150, 480, 370),
        2 => new(1040, 110, 540, 410), _ => new(300, 520, 940, 260)
    };
    internal static Rectangle[] Areas(int seat) => seat switch {
        0 => [Territory(seat), new(20, 520, 280, 260), new(20, 70, 540, 40)],
        1 => [Territory(seat), new(860, 70, 180, 80)],
        2 => [Territory(seat), new(1240, 520, 340, 260)],
        _ => [Territory(seat)]
    };
    private static List<Rectangle> FreeAreas(int seat) => Areas(seat).Select((area, index) => new Rectangle(
        area.X + 3, area.Y + (index == 0 ? 22 : 3), area.Width - 6, area.Height - (index == 0 ? 25 : 6))).ToList();
    internal static BattlefieldCluster[] Build(int seat, int owner, Card[] cards, IReadOnlyList<string> roleByCard,
        IReadOnlySet<string>? separate, IReadOnlyList<Rectangle> obstacles, Func<Card, int, bool, BattlefieldLabelMetrics>? denseMeasure,
        bool compact = false)
    {
        string[] roles = ["Creatures", "Support", "Lands"];
        // Public-state equality already separates different counters, combat,
        // tapped state and attachments. Do not arbitrarily split identical
        // tokens into repeated labels on a genuinely crowded field.
        bool crowded = compact || cards.Length >= 80;
        int maximumCopies = crowded ? int.MaxValue : 4;
        var allGroups = BattlefieldLayout.Groups(cards, separate, maximumCopies);
        var groups = roles.SelectMany(role => BattlefieldLayout.Groups(cards.Where((_, index) => roleByCard[index] == role).ToArray(), separate, maximumCopies)
            .Select(copies => {
                var card = copies[^1];
                bool plain = !card.Type.Contains("Creature", StringComparison.Ordinal) && card.Power == null && card.Toughness == null
                    && !card.Counters.Any(counter => counter.Value > 0) && card.Damage == 0 && (card.States?.Length ?? card.CombatKeywords.Length) == 0 && !card.PhasedOut;
                bool ordered = separate?.Contains(card.VisualId) == true;
                bool miniature = !ordered && (crowded || allGroups.Length > 25 && !card.Counters.Any(counter => counter.Value > 0)
                    && card.Damage == 0 && (card.States?.Length ?? card.CombatKeywords.Length) == 0 && !card.PhasedOut && !card.Sick);
                int label = plain ? 56 : miniature ? 48 : 96;
                var metrics = denseMeasure?.Invoke(card, copies.Length, miniature) ?? new(label, miniature || plain ? 18 : 20);
                label = Math.Max(label, metrics.Width);
                // The measurement delegate reserves a 32-pixel name fragment
                // beside header states. At the smallest supported viewport a
                // useful name needs 48 logical pixels, without shrinking type.
                if (crowded) label = Math.Max(label, metrics.HeaderWidth + 48);
                if (ordered) label = Math.Max(96, label);
                // The name and one combined P/T, copy and counter-marker row
                // form a single tile. Counter types belong in the reader,
                // rather than extending a separate stack beside the card.
                int rows = 1 + (card.Type.Contains("Creature", StringComparison.Ordinal) || copies.Length > 1
                    || card.Counters.Any(counter => counter.Value > 0) ? 1 : 0);
                return new Group(role, copies, plain, ordered, miniature, label, rows, Math.Max(crowded && miniature ? 16 : 18, metrics.RowHeight),
                    CopyFan.Create(copies.Length, crowded));
            })).ToArray();
        if (groups.Length == 0) return roles.Select(role => new BattlefieldCluster(role, [])).ToArray();
        var territory = Territory(seat);
        Rectangle[] placements = [];
        float scale = 1;
        // Already-crowded fields try a final smaller thumbnail before losing
        // metadata. Measured label width, font and row height never shrink;
        // physical copy fans and the ordering band retain their dimensions.
        int attempts = crowded ? 48 : 32;
        for (int attempt = 0; attempt < attempts; attempt++) {
            scale = attempt < 16 ? 1 : attempt < 32 ? .8f : .5f;
            var free = FreeAreas(seat);
            foreach (var obstacle in obstacles) Subtract(free, obstacle);
            var placed = new Rectangle[groups.Length]; int placedCount = 0;
            var workItems = groups.Select((group, index) => {
                var faceSize = FaceSize(group, scale);
                int faceWidth = (int)MathF.Ceiling(faceSize.X);
                int width = Math.Max(group.Ordered ? 82 : 0,
                    Math.Max(group.Fan.Width + faceWidth, group.Fan.Front.X + faceWidth + 4 + group.LabelWidth) + 4);
                int face = (int)MathF.Ceiling(faceSize.Y);
                int height = Math.Max(group.Fan.Front.Y + group.Rows * group.RowHeight, group.Fan.Height + face)
                    + 4 + (group.Ordered ? 36 : 0);
                return (group, index, width, height);
            });
            var work = attempt % 4 < 2 ? workItems.OrderByDescending(item => item.height).ThenByDescending(item => item.width).ThenBy(item => item.index)
                : workItems.OrderBy(item => item.index);
            foreach (var item in work) {
                var group = item.group;
                int width = item.width, height = item.height;
                var candidates = free.Where(rect => rect.Width >= width && rect.Height >= height);
                var available = attempt % 2 == 0 ? candidates
                    .OrderBy(rect => Math.Min(rect.Width - width, rect.Height - height))
                    .ThenBy(rect => Math.Max(rect.Width - width, rect.Height - height)).ThenBy(rect => rect.Top).ThenBy(rect => rect.Left).FirstOrDefault()
                    : candidates.OrderBy(rect => rect.Top).ThenBy(rect => rect.Left).ThenBy(rect => rect.Width * rect.Height).FirstOrDefault();
                if (available.IsEmpty) break;
                int corner = attempt / 4 % 4;
                var rectangle = new Rectangle(corner % 2 == 1 ? available.Right - width : available.X,
                    corner >= 2 ? available.Bottom - height : available.Y, width, height);
                placed[item.index] = rectangle; placedCount++; Subtract(free, rectangle);
            }
            if (placedCount == groups.Length) { placements = placed; break; }
        }
        if (placements.Length != groups.Length) {
            // Commander cards and state reservations can crowd a smaller field
            // too. Retry the measured compact representation before abandoning
            // every group's readable metadata for individual faces.
            if (!crowded) return Build(seat, owner, cards, roleByCard, separate, obstacles, denseMeasure, compact: true);
            return PackFaces(seat, owner, cards, roleByCard, territory, obstacles);
        }
        var result = roles.ToDictionary(role => role, _ => new List<SceneCard>());
        for (int groupIndex = 0; groupIndex < groups.Length; groupIndex++) {
            var group = groups[groupIndex]; var cell = placements[groupIndex];
            int orderBand = group.Ordered ? 36 : 0;
            var faceSize = FaceSize(group, scale);
            var labels = new Rectangle(cell.X + group.Fan.Front.X + 2 + (int)MathF.Ceiling(faceSize.X) + 4,
                cell.Y + group.Fan.Front.Y + 2 + orderBand,
                group.LabelWidth, group.Rows * group.RowHeight);
            for (int copy = 0; copy < group.Cards.Length; copy++) {
                var card = group.Cards[copy];
                // Earlier rows keep their top corner; earlier columns in the
                // front row keep their left corner. Shared labels sit beside
                // the front art without covering those exposed copy targets.
                var offset = group.Fan.Offset(copy);
                var pose = new Pose(new(cell.X + 2 + offset.X + faceSize.X / 2, cell.Y + 2 + offset.Y + faceSize.Y / 2), faceSize);
                string id = CardViews.Identity(card);
                if (id.Length == 0) id = $"anonymous:{owner}:Battlefield:{Array.FindIndex(cards, candidate => ReferenceEquals(candidate, card))}";
                result[group.Role].Add(new(id, card, pose, "Battlefield", owner, TableLayout.SeatHand(seat)) {
                    Compact = true, Dense = true, DenseLabels = labels, DenseScale = scale,
                    DenseRowHeight = group.RowHeight, DenseMiniature = group.Miniature,
                    GroupFront = copy == group.Cards.Length - 1, GroupCount = group.Cards.Length
                });
            }
        }
        return roles.Select(role => new BattlefieldCluster(role, result[role].ToArray())).ToArray();
    }
    private static BattlefieldCluster[] PackFaces(int seat, int owner, Card[] cards, IReadOnlyList<string> roleByCard, Rectangle area, IReadOnlyList<Rectangle> obstacles)
    {
        // Beyond the readable label budget, retain actual individual faces and
        // their explicit reader instead of covering another seat or a control.
        var positions = new List<Rectangle>();
        List<Rectangle> remaining = [];
        foreach (int width in new[] { 24, 20, 16, 12, 8, 4, 2, 1 }) {
            var free = FreeAreas(seat);
            foreach (var obstacle in obstacles) Subtract(free, obstacle);
            remaining = free.ToList();
            positions.Clear(); int height = Math.Max(1, width * 7 / 5);
            foreach (var card in cards) {
                var slot = free.Where(rectangle => rectangle.Width >= width && rectangle.Height >= height)
                    .OrderBy(rectangle => rectangle.Top).ThenBy(rectangle => rectangle.Left).FirstOrDefault();
                if (slot.IsEmpty) break;
                var bounds = new Rectangle(slot.X, slot.Y, width, height);
                positions.Add(bounds); Subtract(free, bounds);
            }
            if (positions.Count == cards.Length) break;
        }
        // A subpixel board cannot offer independent physical targets. Keep even
        // that extreme input contained instead of indexing a partial placement
        // array or spilling over another seat. Ordinary dense fields stay on
        // the measured, labeled path above.
        if (positions.Count < cards.Length) {
            var safe = remaining.OrderByDescending(rectangle => rectangle.Width * rectangle.Height).FirstOrDefault();
            if (safe.IsEmpty) safe = new(area.X + 3, area.Y + 22, 1, 1);
            while (positions.Count < cards.Length) positions.Add(new(safe.X, safe.Y, 1, 1));
        }
        return new[] { "Creatures", "Support", "Lands" }.Select(role => new BattlefieldCluster(role,
            cards.Select((card, index) => (card, index)).Where(entry => roleByCard[entry.index] == role).Select(entry => {
                var bounds = positions[entry.index];
                var size = entry.card.Tapped ? new Vector2(Math.Max(.5f, bounds.Height - 1), Math.Max(.5f, bounds.Width - 1))
                    : new(Math.Max(.5f, bounds.Width - 1), Math.Max(.5f, bounds.Height - 1));
                string id = CardViews.Identity(entry.card);
                if (id.Length == 0) id = $"anonymous:{owner}:Battlefield:{entry.index}";
                return new SceneCard(id, entry.card, new(bounds.Center.ToVector2(), size, entry.card.Tapped ? MathF.PI / 2 : 0), "Battlefield", owner,
                    TableLayout.SeatHand(seat)) { Compact = true, Dense = true, DenseMiniature = true };
            }).ToArray())).ToArray();
    }
    private static void Subtract(List<Rectangle> free, Rectangle used)
    {
        var next = new List<Rectangle>();
        foreach (var rectangle in free) {
            var overlap = Rectangle.Intersect(rectangle, used);
            if (overlap.IsEmpty) { next.Add(rectangle); continue; }
            Add(new(rectangle.Left, rectangle.Top, rectangle.Width, overlap.Top - rectangle.Top));
            Add(new(rectangle.Left, overlap.Bottom, rectangle.Width, rectangle.Bottom - overlap.Bottom));
            Add(new(rectangle.Left, rectangle.Top, overlap.Left - rectangle.Left, rectangle.Height));
            Add(new(overlap.Right, rectangle.Top, rectangle.Right - overlap.Right, rectangle.Height));
        }
        free.Clear();
        for (int index = 0; index < next.Count; index++)
            if (!next.Where((_, other) => other != index).Any(rectangle => rectangle.Contains(next[index])
                && (rectangle != next[index] || next.FindIndex(candidate => candidate == rectangle) < index))) free.Add(next[index]);
        void Add(Rectangle rectangle) { if (rectangle.Width > 0 && rectangle.Height > 0) next.Add(rectangle); }
    }
}
