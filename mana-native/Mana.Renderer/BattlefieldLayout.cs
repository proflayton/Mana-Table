using Mana.Contracts;
using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Renderer;

public sealed record BattlefieldCluster(string Kind, SceneCard[] Cards);
public sealed record BattlefieldLabelMetrics(int Width, int RowHeight);

// Packs public-state copy groups into separate role lanes. Overlap may hide art,
// but ordinary rows reserve the inward edge used by identity and state labels.
public static class BattlefieldLayout
{
    public static Rectangle DenseTerritory(int seat) => BoundedBattlefieldLayout.Territory(seat);
    public static IReadOnlyList<Rectangle> DenseAreas(int seat) => BoundedBattlefieldLayout.Areas(seat);
    public static BattlefieldCluster[] Build(int seat, int owner, Card[] cards, Func<Card, bool> isResource, IReadOnlySet<string>? separateOccurrences = null, GameSnapshot? state = null,
        Func<Card, int, bool, BattlefieldLabelMetrics>? denseMeasure = null)
    {
        if (state != null && state.Players.Any(player => Groups(player.Zone("Battlefield").Cards, separateOccurrences, 8).Length > 7))
            return BoundedBattlefieldLayout.Build(seat, owner, cards, isResource, separateOccurrences, state, denseMeasure);
        var occupied = cards.Select(c => Role(c, isResource)).ToHashSet(StringComparer.Ordinal);
        return new[] { "Creatures", "Support", "Lands" }.Select(kind => {
            var role = cards.Where(c => Role(c, isResource) == kind).ToArray();
            // Keep public families adjacent, but give differing states their own
            // cells. Only matching public-state copies share a compact fan.
            // Ordering needs every candidate's own visible position badge.
            // Split the complete candidate set once, independently of selection.
            var groups = Groups(role, separateOccurrences, 8);
            if (groups.Length == 0) return new BattlefieldCluster(kind, []);
            bool own = seat == 3, side = seat is 0 or 2, land = kind == "Lands", support = kind == "Support";
            float width = land ? 54 : support ? 64 : own ? 84 : side ? 66 : 77;
            // Your field ends before the right-hand decision controls. The far
            // field leaves the commanders on its right; side fields leave their
            // command cards above the uppermost role labels.
            float span = own ? 880 : side ? 500 : 720;
            float acrossCenter = own ? -80 : side ? 20 : 70;
            if (own && groups.Length <= 7) { span = 540; acrossCenter = 0; }
            float near = own ? land ? 285 : support ? 260 : 65 : side ? land ? 650 : support ? 570 : 280 : land ? 400 : support ? 365 : 100;
            float far = own ? land ? 310 : support ? 275 : 215 : side ? land ? 660 : support ? 585 : 500 : land ? 410 : support ? 380 : 300;
            float fullNear = own ? 65 : side ? 280 : 100, fullFar = own ? 325 : side ? 660 : 410;
            if (occupied.Count == 1) { near = fullNear; far = fullFar; }
            else if (kind == "Creatures" && !occupied.Contains("Support")) far = own ? 265 : side ? 575 : 375;
            else if (kind == "Support" && !occupied.Contains("Creatures")) near = fullNear;
            if (kind != "Lands" && !occupied.Contains("Lands")) far = fullFar;
            // A far/rotated sliver needs the original wider spacing to retain
            // a genuine two-pixel target after the viewport's physical scaling.
            float fanStep = own ? 5 : 9;
            const float gap = 12;
            // Labels remain upright in screen space, so side seats reserve the
            // label's height along the rotated across axis instead of its width.
            float labelAcross = side ? 62 / TableWorld.Pitch : own ? 94 : 108;
            float rowPitch = side ? 112 : own ? 75 : 110;
            var cells = new List<(int Row, float Across)>();
            int rows;
            while (true) {
                cells.Clear(); int rowIndex = 0; float cursor = -span / 2;
                foreach (var group in groups) {
                    float faceAcross = group[0].Tapped ? width * 1.4f : width;
                    // The position badge is wider than a small land face.
                    // Reserve a whole cell for it, including distant seats.
                    float reservedAcross = Math.Max(faceAcross, labelAcross);
                    if (separateOccurrences?.Contains(group[0].VisualId) == true) reservedAcross = Math.Max(84, reservedAcross);
                    // The front copy owns the metadata. Do not add its entire
                    // label reservation again for every sliver of a shared fan.
                    float cellWidth = Math.Max(reservedAcross, faceAcross + (group.Length - 1) * fanStep);
                    if (cursor + cellWidth > span / 2 + .01f) {
                        rowIndex++;
                        // Rear own rows clear the separate command cards at the
                        // left rim without shifting the whole field into the dock.
                        cursor = -span / 2 + (own ? 60 : 0);
                    }
                    cells.Add((rowIndex, acrossCenter + cursor + faceAcross / 2));
                    cursor += cellWidth + gap;
                }
                rows = rowIndex + 1;
                // A small group count is not evidence that its rows fit. The
                // former four-row shortcut buried names, P/T and counters on
                // otherwise ordinary fifteen-creature boards.
                bool canPackMoreTightly = groups.Any(group => (group[0].Tapped ? width * 1.4f : width) > labelAcross + .1f);
                if (rows <= 1 || (rows - 1) * rowPitch <= far - near || width <= 24 || !canPackMoreTightly) break;
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
    internal static Card[][] Groups(Card[] cards, IReadOnlySet<string>? separate, int maximumCopies) => cards
        .GroupBy(card => separate?.Contains(card.VisualId) == true ? "ordered:" + card.VisualId : "family:" + FamilyKey(card))
        .SelectMany(family => family.GroupBy(StateKey).SelectMany(group => group.Chunk(maximumCopies))).ToArray();
    // Decision handles, Selectable and Highlighted deliberately do not participate.
    // Never use a hidden printed face or infer a face-down card's characteristics.
    private static string FamilyKey(Card c) => c.FaceDown ? "anonymous:" + CardViews.Identity(c)
        : string.Join("\u001f", c.Name, c.ArtFace, c.Type);
    private static string StateKey(Card c) => string.Join("\u001f", c.Name, c.ArtFace, c.Type, c.ManaCost, c.Text, c.Power, c.Toughness, c.Damage,
        c.Tapped, c.PhasedOut, c.Sick, c.FaceDown, c.Attacking, c.Blocking, c.DefenderId, c.Defender,
        string.Join(',', c.CombatKeywords.Order(StringComparer.Ordinal)), string.Join(',', c.Counters.OrderBy(v => v.Key, StringComparer.Ordinal)),
        c.AttachedTo?.Id, string.Join(',', c.Attachments.Select(a => a.Id).Order(StringComparer.Ordinal)));
}
