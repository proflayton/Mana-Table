using Mana.Contracts;
using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Renderer;

/// <summary>An explicit layout study: independent, upright controller territories.
/// A pending effect or another seat's density never participates in this packing.</summary>
public static class SharedTableStudy
{
    private static readonly string[] Roles = ["Creatures", "Support", "Lands"];
    public static Rectangle ActionArea => new(565, 390, 470, 165);
    public static Rectangle Territory(int seat) => seat switch {
        0 => new(195, 160, 350, 470), 1 => new(510, 210, 580, 165),
        2 => new(1055, 160, 350, 370), _ => new(320, 565, 915, 170)
    };
    public static Rectangle Lane(int seat, string role) => (seat, role) switch {
        (0, "Creatures") => new(300, 165, 195, 385),
        (0, "Support") => new(195, 165, 94, 210),
        (0, _) => new(195, 390, 94, 230),
        (1, "Creatures") => new(510, 270, 580, 105),
        (1, "Support") => new(510, 210, 235, 52),
        (1, _) => new(760, 210, 330, 52),
        (2, "Creatures") => new(1105, 165, 190, 355),
        (2, "Support") => new(1310, 165, 94, 165),
        (2, _) => new(1310, 345, 94, 175),
        (_, "Creatures") => new(320, 565, 915, 104),
        (_, "Support") => new(320, 682, 360, 53),
        _ => new(885, 682, 350, 53)
    };

    internal static BattlefieldCluster[] Arrange(int seat, int owner, Card[] cards,
        Func<Card, bool> isResource, IReadOnlySet<string>? separate)
    {
        var role = cards.Select(card => BattlefieldLayout.Role(card, isResource)).ToArray();
        return Roles.Select(kind => {
            var selected = cards.Where((_, index) => role[index] == kind).ToArray();
            var groups = BattlefieldLayout.Groups(selected, separate, int.MaxValue);
            var lane = Lane(seat, kind);
            // Leave room for the face outline and floating-point tap rotation
            // before Pose.Bounds rounds out to physical hit rectangles.
            lane.Inflate(-2, -2);
            var nodes = new List<SceneCard>();
            if (groups.Length == 0) return new BattlefieldCluster(kind, []);
            var desired = kind == "Creatures" ? new Vector2(112, 104) : new Vector2(88, 62);
            var fans = groups.Select(group => Fan(group.Length)).ToArray();
            int fanWidth = fans.Max(fan => fan.Width), fanHeight = fans.Max(fan => fan.Height);
            float gap = Math.Min(9, MathF.Sqrt(lane.Width * lane.Height / (float)groups.Length) / 4);
            int columns = 1, rows = groups.Length;
            float best = -1;
            for (int candidate = 1; candidate <= groups.Length; candidate++) {
                int count = (groups.Length + candidate - 1) / candidate;
                float scale = Math.Min(1, Math.Min(
                    (lane.Width - (candidate - 1) * gap) / (candidate * (desired.X + fanWidth)),
                    (lane.Height - (count - 1) * gap) / (count * (desired.Y + fanHeight))));
                if (scale > best) { best = scale; columns = candidate; rows = count; }
            }
            float factor = Math.Max(float.Epsilon, best);
            float width = desired.X * factor, height = desired.Y * factor;
            float stepX = width + fanWidth * factor + gap, stepY = height + fanHeight * factor + gap;
            float totalWidth = columns * stepX - gap, totalHeight = rows * stepY - gap;
            float left = seat == 0 ? lane.Right - totalWidth : seat == 2 ? lane.Left : lane.Center.X - totalWidth / 2;
            float top = seat == 1 ? lane.Bottom - totalHeight : lane.Top;
            for (int index = 0; index < groups.Length; index++) {
                var copies = groups[index]; var fan = fans[index];
                float x = left + index % columns * stepX, y = top + index / columns * stepY;
                for (int copy = 0; copy < copies.Length; copy++) {
                    var card = copies[copy];
                    var offset = new Vector2(copy % fan.Columns * fan.Step, copy / fan.Columns * fan.Step) * factor;
                    // Tap rotation stays inside the same cell. Painting and input
                    // consume this exact pose; no detached display-only geometry.
                    var size = card.Tapped ? new Vector2(height, width) : new Vector2(width, height);
                    var pose = new Pose(new Vector2(x + width / 2, y + height / 2) + offset,
                        size, card.Tapped ? MathF.PI / 2 : 0);
                    int ordinal = CardViews.Identity(card).Length == 0
                        ? Array.FindIndex(cards, candidate => ReferenceEquals(candidate, card)) : 0;
                    var node = SceneCard.Create(card, pose, "Battlefield", owner, ordinal, TableLayout.Hero(seat));
                    nodes.Add(node with { ScanFace = true, Compact = true,
                        GroupFront = copy == copies.Length - 1, GroupCount = copies.Length });
                }
            }
            return new BattlefieldCluster(kind, nodes.ToArray());
        }).ToArray();
    }
    private readonly record struct CopyFan(int Columns, int Width, int Height, int Step);
    private static CopyFan Fan(int count)
    {
        int columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(count))), step = count > 1 ? 3 : 0;
        return new(columns, (Math.Min(columns, count) - 1) * step, (count - 1) / columns * step, step);
    }
}
