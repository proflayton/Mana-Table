using Mana.Contracts;
using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Renderer;

/// <summary>An explicit layout study: independent, seat-facing controller territories.
/// A pending effect or another seat's density never participates in this packing.</summary>
public static class SharedTableStudy
{
    private static readonly string[] Roles = ["Creatures", "Support", "Lands"];
    private const float AttachmentEdge = 36, AttachmentDrop = 8;
    public static float Facing(int seat) => seat switch { 0 => MathF.PI / 2, 1 => MathF.PI, 2 => -MathF.PI / 2, _ => 0 };
    public static Rectangle ActionArea => new(565, 390, 470, 165);
    public static Rectangle Territory(int seat) => seat switch {
        0 => new(195, 160, 350, 470), 1 => new(510, 171, 580, 204),
        2 => new(1055, 160, 350, 370), _ => new(320, 555, 915, 225)
    };
    public static Rectangle Lane(int seat, string role) => (seat, role) switch {
        (0, "Creatures") => new(300, 165, 195, 385),
        (0, "Support") => new(195, 165, 94, 210),
        (0, _) => new(195, 390, 94, 230),
        (1, "Creatures") => new(510, 270, 580, 105),
        (1, "Support") => new(510, 171, 430, 94),
        (1, _) => new(955, 171, 135, 94),
        (2, "Creatures") => new(1105, 165, 190, 355),
        (2, "Support") => new(1310, 165, 94, 218),
        (2, _) => new(1310, 398, 94, 122),
        (_, "Creatures") => new(590, 555, 645, 84),
        (_, "Support") => new(320, 555, 260, 155),
        _ => new(980, 640, 255, 64)
    };

    internal static BattlefieldCluster[] Arrange(int seat, int owner, IReadOnlyList<Card> cards,
        Func<Card, bool> isResource, IReadOnlySet<string>? separate,
        Func<string, Rectangle>? lanes = null, float faceScale = 1, bool horizontalRows = false, bool centerRows = false,
        BattlefieldAttachments? attachments = null, bool seatFacing = true, IReadOnlyDictionary<string, string>? combatCohorts = null)
    {
        faceScale = float.IsFinite(faceScale) && faceScale > 0 ? faceScale : 1;
        string Identity(Card card) => CardViews.Identity(card) is { Length: > 0 } id ? id
            : "anonymous:" + Enumerable.Range(0, cards.Count).First(index => ReferenceEquals(cards[index], card));
        string Partition(Card card)
        {
            string id = Identity(card);
            if (separate?.Contains(id) == true) return "separate:" + id;
            if (combatCohorts != null && !combatCohorts.ContainsKey(id)) return "unreported:" + id;
            return "availability:" + card.Selectable + ":" + card.Highlighted + ":" + combatCohorts?.GetValueOrDefault(id);
        }
        // A family joins public names, not mutable characteristics. Hidden and
        // anonymous identities never acquire a family through their printed face.
        var families = cards.GroupBy(card => attachments?.Under(Identity(card)).Count > 0 ? "assembly:" + Identity(card)
                : card.FaceDown || string.IsNullOrWhiteSpace(card.Name)
                ? "occurrence:" + Identity(card) : "name:" + card.Name, StringComparer.Ordinal)
            .Select(group => {
                var members = group.ToArray();
                var cohorts = members.GroupBy(Partition, StringComparer.Ordinal)
                    .SelectMany(partition => BattlefieldLayout.Groups(partition.ToArray(), null, int.MaxValue,
                        includeCombatTargets: combatCohorts == null || partition.Any(card => !combatCohorts.ContainsKey(Identity(card))))).ToArray();
                string role = members.Any(card => card.Type.Contains("Creature", StringComparison.Ordinal)) ? "Creatures"
                    : members.Any(isResource) ? "Lands" : "Support";
                string name = members[0].FaceDown ? "Face-down permanent"
                    : string.IsNullOrWhiteSpace(members[0].Name) ? "Unnamed permanent" : members[0].Name;
                var under = attachments?.Under(Identity(members[0])) ?? [];
                return new Family(owner + ":" + group.Key, name, role, cohorts, under.Count, under.Count == 0 ? 0 : under.Max(member => member.Depth));
            }).ToArray();
        return Roles.Select(kind => {
            var selected = families.Where(family => family.Role == kind).ToArray();
            if (selected.Length == 0) return new BattlefieldCluster(kind, []);
            var lane = lanes?.Invoke(kind) ?? Lane(seat, kind);
            // Pose.Bounds rounds out to integer pixels, including tap rotation.
            lane.Inflate(-2, -2);
            float facing = seatFacing ? Facing(seat) : 0;
            var worldCenter = lane.Center.ToVector2();
            bool sideways = seatFacing && seat is 0 or 2;
            if (sideways) lane = new(0, 0, lane.Height, lane.Width);
            Vector2 Turn(Vector2 point) {
                var local = point - lane.Center.ToVector2();
                // Quarter turns keep exact packing bounds and pointer geometry.
                return worldCenter + (facing == 0 ? local : facing == MathF.PI ? -local
                    : facing > 0 ? new Vector2(-local.Y, local.X) : new Vector2(local.Y, -local.X));
            }
            Pose TurnPose(Pose pose) => pose with { Center = Turn(pose.Center), Angle = pose.Angle + facing };
            Rectangle TurnBounds(Rectangle bounds) => bounds.IsEmpty ? bounds
                : TurnPose(new(new(bounds.X + bounds.Width / 2f, bounds.Y + bounds.Height / 2f), bounds.Size.ToVector2())).Bounds;
            // Every role reserves the card's complete quarter-turn footprint,
            // independent of its current tap state or the current viewpoint.
            var desired = new Vector2(kind == "Creatures" ? 128 : 112) * faceScale;
            var slots = Pack(selected, lane, desired, faceScale, seat, horizontalRows, centerRows);
            var nodes = new List<SceneCard>();
            foreach (var slot in slots) {
                var familyNodes = new List<SceneCard>();
                var cohortPlans = new List<StudyBattlefieldCohort>();
                float factor = slot.Scale, detail = faceScale * factor;
                var size = desired * factor;
                var cell = size;
                float fan = slot.Family.Fan * detail, gap = 6 * detail;
                float header = slot.Header;
                float bodyY = slot.Position.Y + header + (header > 0 ? 4 * detail : 0);
                for (int cohort = 0; cohort < slot.Family.Cohorts.Length; cohort++) {
                    var copies = slot.Family.Cohorts[cohort];
                    var position = new Vector2(slot.Position.X + cohort % slot.Columns * (cell.X + fan + gap),
                        bodyY + cohort / slot.Columns * (cell.Y + fan + gap));
                    var cohortNodes = new List<SceneCard>();
                    for (int copy = 0; copy < copies.Length; copy++) {
                        var card = copies[copy];
                        // Copy count never enlarges a cohort's footprint. All
                        // real occurrences remain, with one current front/count.
                        float advance = copies.Length > 1 ? 6 * detail * copy / (copies.Length - 1) : 0;
                        var center = position + cell / 2 + new Vector2(advance) + new Vector2(slot.Family.Attachments * AttachmentEdge * detail, 0);
                        // Full portrait printing for every permanent. Tapping
                        // turns this same face inside its stable square slot.
                        var face = new Vector2(size.Y * 488 / 680, size.Y);
                        var pose = new Pose(center, face,
                            card.Tapped ? MathF.PI / 2 : 0);
                        int ordinal = CardViews.Identity(card).Length == 0
                            ? Enumerable.Range(0, cards.Count).First(index => ReferenceEquals(cards[index], card)) : 0;
                        var node = SceneCard.Create(card, pose, "Battlefield", owner, ordinal, TableLayout.Hero(seat)) with {
                            ScanFace = true, Compact = true, GroupFront = copy == copies.Length - 1, GroupCount = copies.Length
                        };
                        var under = attachments?.Under(node.Id) ?? [];
                        // Paint deepest cards first; the front host covers their
                        // bodies while every attachment keeps an exposed edge.
                        for (int layer = under.Count - 1; layer >= 0; layer--) {
                            var member = under[layer];
                            float hostWidth = card.Tapped ? face.Y : face.X;
                            float memberWidth = member.Card.Tapped ? face.Y : face.X;
                            // Align the exposed edge to the host's actual turn,
                            // while the attachment keeps its own tap state.
                            var memberCenter = center + new Vector2((memberWidth - hostWidth) / 2
                                - AttachmentEdge * (layer + 1) * detail, AttachmentDrop * member.Depth * detail);
                            var memberPose = new Pose(memberCenter, face, member.Card.Tapped ? MathF.PI / 2 : 0);
                            familyNodes.Add(SceneCard.Create(member.Card, memberPose, "Battlefield", member.Owner, 0, TableLayout.Hero(seat)) with {
                                ScanFace = true, Compact = true,
                                Attachment = new(node.Id, owner, member.HostId, layer + 1, member.Depth, AttachmentEdge * detail / memberWidth, facing)
                            });
                        }
                        cohortNodes.Add(node); familyNodes.Add(node);
                    }
                    cohortPlans.Add(new(cohortNodes[^1].Id, Array.AsReadOnly(cohortNodes.Select(node => node.Id).ToArray()),
                        cohortNodes.Select(node => node.Pose.Bounds).Aggregate(Rectangle.Union)));
                }
                var extent = slot.Size;
                var nameBounds = header > 0 ? Bounds(slot.Position, new(extent.X, header)) : Rectangle.Empty;
                familyNodes = familyNodes.Select(node => node with { Pose = TurnPose(node.Pose) }).ToList();
                var bodyBounds = familyNodes.Select(node => node.Pose.Bounds).Aggregate(Rectangle.Union);
                nameBounds = TurnBounds(nameBounds);
                var family = new StudyBattlefieldFamily(slot.Family.Id, slot.Family.Name, kind,
                    Rectangle.Union(bodyBounds, nameBounds.IsEmpty ? bodyBounds : nameBounds), nameBounds, bodyBounds,
                    Array.AsReadOnly(cohortPlans.Select(cohort => cohort with { Bounds = TurnBounds(cohort.Bounds) }).ToArray()));
                nodes.AddRange(familyNodes.Select(node => node with { StudyFamily = family }));
            }
            return new BattlefieldCluster(kind, nodes.ToArray());
        }).ToArray();
    }

    private sealed record Family(string Id, string Name, string Role, Card[][] Cohorts, int Attachments, int AttachmentDepth)
    {
        internal int Fan => Cohorts.Any(cohort => cohort.Length > 1) ? 6 : 0;
        internal int Header => Cohorts.Length > 1 ? 26 : 0;
    }
    private sealed record Slot(Family Family, Vector2 Position, int Columns, float Scale, Vector2 Size, float Header);
    private readonly record struct Space(float X, float Y, float Width, float Height)
    {
        internal float Right => X + Width;
        internal float Bottom => Y + Height;
    }
    private static Rectangle Bounds(Vector2 position, Vector2 size)
    {
        int x = (int)MathF.Floor(position.X), y = (int)MathF.Floor(position.Y);
        return new(x, y, (int)MathF.Ceiling(position.X + size.X) - x, (int)MathF.Ceiling(position.Y + size.Y) - y);
    }
    private static float HeaderHeight(Family family, float detail, float headerFloor) => family.Header > 0
        ? Math.Max(family.Header * detail, headerFloor) : 0;
    private static Vector2 Shape(Family family, int columns, Vector2 desired, float faceScale, float scale, float headerFloor)
    {
        desired *= scale;
        int rows = (family.Cohorts.Length + columns - 1) / columns;
        float detail = faceScale * scale;
        float fan = family.Fan * detail, gap = 6 * detail;
        return new(columns * (desired.X + fan) + (columns - 1) * gap + family.Attachments * AttachmentEdge * detail,
            rows * (desired.Y + fan) + (rows - 1) * gap
                + (family.Header > 0 ? HeaderHeight(family, detail, headerFloor) + 4 * detail : 0) + family.AttachmentDepth * AttachmentDrop * detail);
    }

    private static Slot[] Pack(Family[] families, Rectangle lane, Vector2 desired, float faceScale, int seat, bool horizontalRows, bool centerRows)
    {
        // A single-column plan is always available before trying roomier shapes.
        // This fallback preserves every cohort even beyond the readable budget.
        var preferred = families.Select(family => Shape(family, 1, desired, faceScale, 1, 0)).ToArray();
        float gap = 9 * faceScale;
        float low = Math.Min(1, Math.Min(lane.Width / preferred.Max(size => size.X),
            lane.Height / (preferred.Sum(size => size.Y) + gap * (families.Length - 1)))) * .995f;
        // A normal family keeps a real caption band while its state subfaces
        // shrink independently. Extremely dense fields can
        // still use the source-attached hover name when even these bands fail.
        float headerFloor = families.Count(family => family.Header > 0) * 23 < lane.Height ? 23 : 0;
        Vector2[] Narrow(float scale) => families.Select(family => Shape(family, 1, desired, faceScale, scale, headerFloor)).ToArray();
        var narrow = Narrow(low);
        while (narrow.Sum(size => size.Y) + gap * low * (families.Length - 1) > lane.Height) {
            low *= .85f; narrow = Narrow(low);
        }
        float top = lane.Top;
        var result = families.Select((family, index) => {
            float x = seat == 0 ? lane.Right - narrow[index].X : lane.Left;
            var slot = new Slot(family, new(x, top), 1, low, narrow[index], HeaderHeight(family, faceScale * low, headerFloor));
            top += narrow[index].Y + gap * low;
            return slot;
        }).ToArray();
        if (TryPack(1, out var full)) return full;
        float high = 1;
        for (int iteration = 0; iteration < 12; iteration++) {
            float scale = (low + high) / 2;
            if (TryPack(scale, out var candidate)) { low = scale; result = candidate; }
            else high = scale;
        }
        return result;

        bool TryPack(float scale, out Slot[] slots)
        {
            if (horizontalRows) return TryRows(scale, out slots);
            var free = new List<Space> { new(lane.X, lane.Y, lane.Width, lane.Height) };
            var placed = new List<Slot>();
            foreach (var family in families) {
                int chosenArea = -1, chosenColumns = 1;
                Vector2 chosenSize = default;
                (float ShortSide, float Waste, float Y, float X, int Columns) best = (float.MaxValue, float.MaxValue, 0, 0, 0);
                for (int areaIndex = 0; areaIndex < free.Count; areaIndex++) {
                    var area = free[areaIndex];
                    for (int columns = 1; columns <= family.Cohorts.Length; columns++) {
                        var extent = Shape(family, columns, desired, faceScale, scale, headerFloor);
                        if (extent.X > area.Width || extent.Y > area.Height) continue;
                        var score = (Math.Min(area.Width - extent.X, area.Height - extent.Y),
                            area.Width * area.Height - extent.X * extent.Y, area.Y, area.X, columns);
                        if (score.CompareTo(best) >= 0) continue;
                        best = score; chosenArea = areaIndex; chosenColumns = columns; chosenSize = extent;
                    }
                }
                if (chosenArea < 0) { slots = []; return false; }
                var available = free[chosenArea]; free.RemoveAt(chosenArea);
                bool right = seat == 0, bottom = seat == 1;
                float x = right ? available.X + available.Width - chosenSize.X : available.X;
                float y = bottom ? available.Y + available.Height - chosenSize.Y : available.Y;
                placed.Add(new(family, new(x, y), chosenColumns, scale, chosenSize, HeaderHeight(family, faceScale * scale, headerFloor)));
                float space = gap * scale;
                float across = available.Width - chosenSize.X - space;
                float down = available.Height - chosenSize.Y - space;
                if (across > 0) free.Add(new(right ? available.X : x + chosenSize.X + space, available.Y, across, available.Height));
                if (down > 0) free.Add(new(x, bottom ? available.Y : y + chosenSize.Y + space, chosenSize.X, down));
            }
            slots = placed.ToArray(); return true;
        }

        bool TryRows(float scale, out Slot[] slots)
        {
            // An explicit focused field has room for horizontal rows. Place
            // multi-state families first, then fill both beside and below their
            // blocks. A guillotine's fixed side strip otherwise strands space
            // and lets the first tall family shrink every unrelated creature.
            foreach (bool flat in new[] { true, false }) {
                var free = new List<Space> { new(0, 0, lane.Width, lane.Height) };
                var placed = new Dictionary<string, Slot>(StringComparer.Ordinal);
                foreach (var family in families.OrderByDescending(family => family.Cohorts.Length)) {
                    int targetColumns = flat ? family.Cohorts.Length : (int)MathF.Ceiling(MathF.Sqrt(family.Cohorts.Length));
                    int chosenColumns = 0;
                    Space chosenArea = default;
                    Vector2 chosenSize = default;
                    (int Shape, float Y, float X, float Waste) best = (int.MaxValue, float.MaxValue, float.MaxValue, float.MaxValue);
                    foreach (var area in free) {
                        for (int columns = 1; columns <= family.Cohorts.Length; columns++) {
                            var extent = Shape(family, columns, desired, faceScale, scale, headerFloor);
                            if (extent.X > area.Width || extent.Y > area.Height) continue;
                            var score = (Math.Abs(columns - targetColumns), area.Y, area.X,
                                area.Width * area.Height - extent.X * extent.Y);
                            if (score.CompareTo(best) >= 0) continue;
                            best = score; chosenArea = area; chosenColumns = columns; chosenSize = extent;
                        }
                    }
                    if (chosenColumns == 0) break;
                    float x = lane.Left + chosenArea.X;
                    float y = seat == 1 ? lane.Bottom - chosenArea.Y - chosenSize.Y : lane.Top + chosenArea.Y;
                    placed.Add(family.Id, new(family, new(x, y), chosenColumns, scale, chosenSize,
                        HeaderHeight(family, faceScale * scale, headerFloor)));
                    float padding = gap * scale;
                    Occupy(free, new(chosenArea.X, chosenArea.Y, chosenSize.X + padding, chosenSize.Y + padding));
                }
                if (placed.Count == families.Length) {
                    slots = families.Select(family => placed[family.Id]).ToArray();
                    if (centerRows) {
                        float left = slots.Min(slot => slot.Position.X), right = slots.Max(slot => slot.Position.X + slot.Size.X);
                        float shift = lane.Left + (lane.Width - (right - left)) / 2 - left;
                        slots = slots.Select(slot => slot with { Position = slot.Position + new Vector2(shift, 0) }).ToArray();
                    }
                    return true;
                }
            }
            slots = []; return false;
        }
    }

    private static void Occupy(List<Space> free, Space used)
    {
        for (int index = free.Count - 1; index >= 0; index--) {
            var area = free[index];
            float left = Math.Max(area.X, used.X), top = Math.Max(area.Y, used.Y);
            float right = Math.Min(area.Right, used.Right), bottom = Math.Min(area.Bottom, used.Bottom);
            if (right <= left || bottom <= top) continue;
            free.RemoveAt(index);
            if (left > area.X) free.Add(new(area.X, area.Y, left - area.X, area.Height));
            if (right < area.Right) free.Add(new(right, area.Y, area.Right - right, area.Height));
            if (top > area.Y) free.Add(new(area.X, area.Y, area.Width, top - area.Y));
            if (bottom < area.Bottom) free.Add(new(area.X, bottom, area.Width, area.Bottom - bottom));
        }
        // Maximal free rectangles can overlap, but no retained rectangle may
        // contain a placed family. Remove redundant nested rectangles only.
        for (int index = free.Count - 1; index >= 0; index--) {
            var area = free[index];
            for (int other = 0; other < free.Count; other++) {
                var containing = free[other];
                if (other == index || containing.X > area.X || containing.Y > area.Y
                    || containing.Right < area.Right || containing.Bottom < area.Bottom
                    || containing == area && other > index) continue;
                free.RemoveAt(index); break;
            }
        }
    }
}
