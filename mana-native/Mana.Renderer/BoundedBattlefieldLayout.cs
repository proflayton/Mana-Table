using Mana.Contracts;
using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Renderer;

// Crowded fields share one packing mode: a neighbor cannot expand into another
// seat's cards. Every source occurrence remains painted and independently hit.
internal static class BoundedBattlefieldLayout
{
    private sealed record Group(string Role, Card[] Cards, bool Plain, bool Ordered, bool Miniature, int LabelWidth, int Rows, int RowHeight);
    internal static Rectangle Territory(int seat) => seat switch {
        0 => new(80, 130, 480, 440), 1 => new(560, 150, 480, 420),
        2 => new(1040, 130, 480, 440), _ => new(330, 570, 900, 210)
    };
    internal static Rectangle[] Areas(int seat) => seat switch {
        0 => [Territory(seat), new(80, 570, 250, 210)],
        2 => [Territory(seat), new(1230, 570, 290, 210)],
        _ => [Territory(seat)]
    };
    private static List<Rectangle> FreeAreas(int seat) => Areas(seat).Select((area, index) => new Rectangle(
        area.X + 3, area.Y + (index == 0 ? 22 : 3), area.Width - 6, area.Height - (index == 0 ? 25 : 6))).ToList();
    internal static BattlefieldCluster[] Build(int seat, int owner, Card[] cards, IReadOnlyList<string> roleByCard,
        IReadOnlySet<string>? separate, IReadOnlyList<Rectangle> obstacles, Func<Card, int, bool, BattlefieldLabelMetrics>? denseMeasure)
    {
        string[] roles = ["Creatures", "Support", "Lands"];
        var allGroups = BattlefieldLayout.Groups(cards, separate, 4);
        var groups = roles.SelectMany(role => BattlefieldLayout.Groups(cards.Where((_, index) => roleByCard[index] == role).ToArray(), separate, 4)
            .Select(copies => {
                var card = copies[0];
                bool plain = !card.Type.Contains("Creature", StringComparison.Ordinal) && card.Power == null && card.Toughness == null
                    && !card.Counters.Any(counter => counter.Value > 0) && card.Damage == 0 && (card.States?.Length ?? card.CombatKeywords.Length) == 0 && !card.PhasedOut;
                bool ordered = separate?.Contains(card.VisualId) == true;
                bool miniature = allGroups.Length > 25 && !ordered && !card.Counters.Any(counter => counter.Value > 0)
                    && card.Damage == 0 && (card.States?.Length ?? card.CombatKeywords.Length) == 0 && !card.PhasedOut && !card.Sick;
                int label = plain ? 56 : miniature ? 48 : 96;
                var metrics = denseMeasure?.Invoke(card, copies.Length, miniature) ?? new(label, miniature || plain ? 18 : 20);
                label = Math.Max(label, metrics.Width);
                if (ordered) label = Math.Max(96, label);
                int rows = 1 + (card.Power != null && card.Toughness != null || copies.Length > 1 ? 1 : 0)
                    + (card.Counters.Any(counter => counter.Value > 0) ? 1 : 0) + (card.Counters.Count(counter => counter.Value > 0) > 1 ? 1 : 0);
                return new Group(role, copies, plain, ordered, miniature, label, rows, Math.Max(18, metrics.RowHeight));
            })).ToArray();
        if (groups.Length == 0) return roles.Select(role => new BattlefieldCluster(role, [])).ToArray();
        var territory = Territory(seat);
        Rectangle[] placements = [];
        float scale = 1;
        for (int attempt = 0; attempt < 32; attempt++) {
            scale = attempt < 16 ? 1 : .8f;
            var free = FreeAreas(seat);
            foreach (var obstacle in obstacles) Subtract(free, obstacle);
            var placed = new Rectangle[groups.Length]; int placedCount = 0;
            var workItems = groups.Select((group, index) => {
                int fan = (group.Cards.Length - 1) * 4;
                int width = Math.Max(group.Ordered ? 82 : 0, fan + group.LabelWidth + 2);
                int face = (int)MathF.Ceiling((group.Plain ? 28 : group.Miniature ? 34 : 60) * scale);
                int height = Math.Max(group.Rows * group.RowHeight, face) + 4 + (group.Ordered ? 36 : 0);
                return (group, index, width, height);
            });
            var work = attempt % 4 < 2 ? workItems.OrderByDescending(item => item.height).ThenByDescending(item => item.width).ThenBy(item => item.index)
                : workItems.OrderBy(item => item.index);
            foreach (var item in work) {
                var group = item.group;
                int fan = (group.Cards.Length - 1) * 4;
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
            return PackFaces(seat, owner, cards, roleByCard, territory, obstacles);
        }
        var result = roles.ToDictionary(role => role, _ => new List<SceneCard>());
        for (int groupIndex = 0; groupIndex < groups.Length; groupIndex++) {
            var group = groups[groupIndex]; var cell = placements[groupIndex];
            int fan = (group.Cards.Length - 1) * 4;
            int orderBand = group.Ordered ? 36 : 0;
            var labels = new Rectangle(cell.X + fan + 2, cell.Y + 2 + orderBand, group.LabelWidth, Math.Max(1, cell.Height - 4 - orderBand));
            for (int copy = 0; copy < group.Cards.Length; copy++) {
                var card = group.Cards[copy];
                float width = (group.Plain ? 27 : group.Miniature ? 26 : 42) * scale, height = (group.Plain ? 28 : group.Miniature ? 34 : 60) * scale;
                if (group.Ordered) width = Math.Max(78, width);
                // The free left strip belongs to each real back copy; metadata
                // begins at the front occurrence and cannot steal that strip.
                var pose = new Pose(new(cell.X + 2 + copy * 4 + width / 2, cell.Y + 2 + height / 2), new(width, height));
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
