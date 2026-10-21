using Mana.Contracts;
using Microsoft.Xna.Framework;

namespace Mana.Renderer;

public sealed record BattlefieldCluster(string Kind, SceneCard[] Cards);

// Packs public-state copy groups into shallow fans. Every occurrence has a pose;
// rows overlap only their faces, leaving an individually reachable header edge.
public static class BattlefieldLayout
{
    public static BattlefieldCluster[] Build(int seat, int owner, Card[] cards, Func<Card, bool> isResource)
    {
        return new[] { "Creatures", "Support", "Lands" }.Select(kind => {
            var role = cards.Where(c => Role(c, isResource) == kind).ToArray();
            // Keep public families adjacent, but give differing states their own
            // cells. Only matching public-state copies share a compact fan.
            var groups = role.GroupBy(FamilyKey).SelectMany(f => f.GroupBy(StateKey).SelectMany(g => g.Chunk(8))).ToArray();
            if (groups.Length == 0) return new BattlefieldCluster(kind, []);
            bool own = seat == 3, side = seat is 0 or 2, land = kind == "Lands", support = kind == "Support";
            float width = land ? 54 : support ? 64 : own ? 84 : side ? 66 : 77;
            float span = own ? 540 : side ? 380 : 550;
            float acrossCenter = 0;
            float near = own ? land ? 285 : support ? 230 : 90 : side ? land ? 565 : support ? 465 : 330 : land ? 385 : support ? 300 : 180;
            float far = own ? land ? 310 : support ? 245 : 185 : side ? land ? 610 : support ? 505 : 425 : land ? 425 : support ? 335 : 265;
            var occupied = cards.Select(c => Role(c, isResource)).Distinct().ToArray();
            if (occupied.Length == 1) {
                span = own ? 540 : side ? 380 : 550;
                near = own ? 90 : side ? 330 : 180; far = own ? 310 : side ? 550 : 400;
            } else if (kind == "Creatures" && !occupied.Contains("Support")) far = own ? 220 : side ? 520 : 335;
            const float fanStep = 9;
            var cells = new List<(int Row, float Across)>();
            int rows;
            while (true) {
                cells.Clear(); int rowIndex = 0; float cursor = -span / 2;
                foreach (var group in groups) {
                    float faceAcross = group[0].Tapped ? width * 1.4f : width;
                    float cellWidth = faceAcross + (group.Length - 1) * fanStep;
                    if (cursor + cellWidth > span / 2 + .01f) { rowIndex++; cursor = -span / 2; }
                    cells.Add((rowIndex, acrossCenter + cursor + faceAcross / 2));
                    cursor += cellWidth + 14;
                }
                rows = rowIndex + 1;
                // Borrow empty-role space first. Only genuinely large unique
                // fields need smaller faces; the UI and hover reader keep size.
                if (groups.Length <= 20 && rows <= 4 || rows <= 1 || (rows - 1) * (width * 1.4f + 8) <= far - near || width <= 24) break;
                width = Math.Max(24, width * .9f);
            }
            var size = new Vector2(width, width * 1.4f);
            var nodes = new List<SceneCard>();
            for (int group = 0; group < groups.Length; group++) {
                var (row, across) = cells[group];
                float outward = near + (far - near) * row / Math.Max(1, rows - 1f);
                var copies = groups[group];
                bool matchingState = copies.All(c => StateKey(c) == StateKey(copies[0]));
                for (int copy = 0; copy < copies.Length; copy++) {
                    var card = copies[copy];
                    float advance = card.Attacking || card.Blocking ? 14 : 0;
                    var pose = TableWorld.Card(TableWorld.SeatPoint(seat, across + copy * fanStep, outward - advance),
                        size, TableWorld.SeatAngle(seat) + (card.Tapped ? MathF.PI / 2 : 0));
                    string id = CardViews.Identity(card);
                    if (id.Length == 0) id = $"anonymous:{owner}:Battlefield:{Array.FindIndex(cards, c => ReferenceEquals(c, card))}";
                    nodes.Add(new(id, card, pose, "Battlefield", owner, TableLayout.SeatHand(seat)) {
                        Compact = rows > 1 || copies.Length > 1 || width < 60, GroupFront = !matchingState || copy == copies.Length - 1, GroupCount = matchingState ? copies.Length : 1
                    });
                }
            }
            return new BattlefieldCluster(kind, nodes.ToArray());
        }).ToArray();
    }
    public static string Role(Card card, Func<Card, bool> isResource) => card.Type.Contains("Creature", StringComparison.Ordinal)
        ? "Creatures" : isResource(card) ? "Lands" : "Support";
    // Decision handles, Selectable and Highlighted deliberately do not participate.
    // Never use a hidden printed face or infer a face-down card's characteristics.
    private static string FamilyKey(Card c) => c.FaceDown ? "anonymous:" + CardViews.Identity(c)
        : string.Join("\u001f", c.Name, c.ArtFace, c.Type);
    private static string StateKey(Card c) => string.Join("\u001f", c.Name, c.ArtFace, c.Type, c.ManaCost, c.Text, c.Power, c.Toughness, c.Damage,
        c.Tapped, c.PhasedOut, c.Sick, c.FaceDown, c.Attacking, c.Blocking, c.DefenderId, c.Defender,
        string.Join(',', c.CombatKeywords.Order(StringComparer.Ordinal)), string.Join(',', c.Counters.OrderBy(v => v.Key, StringComparer.Ordinal)),
        c.AttachedTo?.Id, string.Join(',', c.Attachments.Select(a => a.Id).Order(StringComparer.Ordinal)));
}
