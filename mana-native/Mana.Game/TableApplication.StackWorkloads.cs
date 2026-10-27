using System.Collections.Immutable;
using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private string expandedStackWorkload = "";
    private GameSnapshot? workloadSnapshot;
    private ImmutableArray<StackWorkload> cachedWorkloads = [];
    private (string Game, int Viewer)? workloadOrderScope;
    private readonly Dictionary<string, int> workloadOrder = new(StringComparer.Ordinal);
    private readonly List<StackWorkloadPaint> stackWorkloadPaint = [];
    internal sealed record StackWorkloadPaint(string Key, Rectangle Bounds, ImmutableArray<string> MemberIds,
        ImmutableArray<int> Positions, bool IsNext);

    private ImmutableArray<StackWorkload> CurrentStackWorkloads
    {
        get {
            if (!ReferenceEquals(workloadSnapshot, match)) {
                workloadSnapshot = match;
                var scope = match == null ? ((string, int)?)null : (match.Id, match.ViewerId);
                if (workloadOrderScope != scope) { workloadOrder.Clear(); workloadOrderScope = scope; }
                var groups = match == null ? [] : StackWorkloads.From(match.Stack);
                foreach (var group in groups) if (!workloadOrder.ContainsKey(group.Key)) workloadOrder.Add(group.Key, workloadOrder.Count);
                cachedWorkloads = groups.OrderBy(group => workloadOrder[group.Key]).ToImmutableArray();
            }
            return cachedWorkloads;
        }
    }
    private bool RepeatedStackWorkload => CurrentStackWorkloads.Any(group => group.Members.Length > 1);

    private void OpenStackWorkload(string key)
    {
        if (match == null || !CurrentStackWorkloads.Any(group => group.Key == key)) return;
        expandedStackWorkload = key;
    }

    private void DrawStackWorkloads(Rectangle area)
    {
        var state = match!;
        var groups = CurrentStackWorkloads;
        if (state.Decision is { Payload: not PriorityPayload }) expandedStackWorkload = "";
        var expanded = groups.FirstOrDefault(group => group.Key == expandedStackWorkload);
        if (expanded != null) { DrawStackWorkloadFan(expanded); return; }
        expandedStackWorkload = "";
        int shown = Math.Min(3, groups.Length);
        var visible = groups.Take(shown).ToArray();
        var next = groups.FirstOrDefault(group => group.IsNext);
        if (next != null && !visible.Any(group => group.IsNext)) visible[^1] = next;
        const int gap = 14;
        int width = Math.Min(146, (area.Width - gap * (shown - 1)) / shown);
        int left = area.X;
        canvas.Text(state.Stack.Length + " pending · by source", area.X, area.Y - 25, Muted, .55f, area.Width - 110, 1);
        if (groups.Length > shown)
            Button("stack-all-sources", "+" + (groups.Length - shown) + " sources", area.Right - 106, area.Y - 28, 106,
                new MatchIntent.OpenStack(), height: 25);
        for (int index = 0; index < shown; index++) {
            var group = visible[index]; var first = group.First;
            var bounds = new Rectangle(left + index * (width + gap), area.Y, width, Math.Min(151, area.Height));
            for (int layer = Math.Min(3, group.Members.Length - 1); layer > 0; layer--)
                canvas.CardFrame(new(bounds.Center.ToVector2() + new Vector2(layer * 3, -layer * 3),
                    new Vector2(bounds.Width - 12, bounds.Height - 16)), Muted * .6f, 1);
            PaintWorkloadReference(first.Item, bounds, group.IsNext);
            var count = new Rectangle(bounds.Right - 48, bounds.Y + 8, 42, 27);
            canvas.Rounded(count, new(13, 25, 29), 6);
            canvas.CenterText(group.Members.Length.ToString(), count, group.IsNext ? Gold : Ink, .82f, true);
            string type = first.Item.Kind == StackActionKind.TriggeredAbility ? "triggers" : first.Item.Ability ? "ability" : "spell";
            canvas.Text(group.IsNext ? "NEXT · " + type : type, bounds.X + 7, bounds.Bottom - 22,
                group.IsNext ? Gold : Muted, .48f, bounds.Width - 14, 1, true);
            // The group gesture opens references. Hover still resolves the
            // exact current first member through the existing ability reader.
            hits.Add(new("stack-workload:" + first.Item.Id, Scope, bounds, new MatchIntent.OpenStackWorkload(group.Key)));
            stackRows.Add(new(first.Item.Id, first.Position, bounds));
            stackWorkloadPaint.Add(new(group.Key, bounds, group.Members.Select(member => member.Item.Id).ToImmutableArray(),
                group.Members.Select(member => member.Position).ToImmutableArray(), group.IsNext));
            PaintStackArrival(first.Item, bounds);
        }
    }

    private void PaintWorkloadReference(StackItem item, Rectangle bounds, bool next, int? position = null)
    {
        canvas.Panel(bounds, new(17, 29, 36), next ? Gold * .8f : Muted * .55f, 3);
        var art = new Rectangle(bounds.X + 6, bounds.Y + 6, bounds.Width - 12, Math.Max(30, bounds.Height - (position == null ? 78 : 12)));
        if (item.Card is { FaceDown: false } source && resources.Card(source) is { } image) {
            var crop = new Rectangle((int)(image.Width * .12f), (int)(image.Height * .14f),
                (int)(image.Width * .76f), (int)(image.Height * .38f));
            canvas.Sprite(image, art.Center.ToVector2(), art.Size.ToVector2(), 0, Color.White, crop);
        } else canvas.Gradient(art, new(43, 58, 64), new(28, 40, 48));
        if (position == null) {
            canvas.Text(item.Name, bounds.X + 7, art.Bottom + 4, Ink, .56f, bounds.Width - 14, 3, true);
            canvas.Text(StateClarity.StackController(match!, item), bounds.X + 7, bounds.Bottom - 36, Muted, .45f, bounds.Width - 14, 1);
        }
        if (position is { } order) {
            var badge = new Rectangle(bounds.X + 2, bounds.Y + 3, 33, 22);
            canvas.Rounded(badge, new(13, 25, 29), 3);
            canvas.CenterText(order.ToString(), badge, next ? Gold : Ink, .55f, true);
        }
    }

    private void DrawStackWorkloadFan(StackWorkload group)
    {
        // Like opening a physical pile: every member remains a separate
        // reference carrying its real absolute stack position. No page/scroll
        // changes the engine order or chooses a gameplay target.
        var members = group.Members;
        int columns = Math.Min(Math.Max(18, (int)Math.Ceiling(Math.Sqrt(members.Length * 2d))), members.Length);
        int rows = (members.Length + columns - 1) / columns;
        int width = 112, height = 150;
        int stride = columns == 1 ? 0 : Math.Min(86, (790 - width) / (columns - 1));
        int rowStride = rows == 1 ? 0 : Math.Min(170, Math.Max(1, (420 - height) / (rows - 1)));
        var area = new Rectangle(190, 126, Math.Max(380, width + stride * (columns - 1)), 24 + height + rowStride * (rows - 1));
        canvas.Text(group.First.Item.Name + " · " + group.Members.Length + " current triggers", area.X, 91, Gold, .8f, 790, 2, true);
        canvas.Text("Numbers are actual stack positions", area.X, 116, Muted, .53f, 690, 1);
        Button("stack-workload-close", "Back to table", area.Right - 150, area.Bottom + 12, 150,
            new MatchIntent.CloseStackWorkload(), height: 30);
        foreach (var (member, index) in members.Select((member, index) => (member, index))) {
            var bounds = new Rectangle(area.X + index % columns * stride, area.Y + 24 + index / columns * rowStride, width, height);
            PaintWorkloadReference(member.Item, bounds, member.Position == 1, member.Position);
            int activeWidth = index % columns == columns - 1 || index == members.Length - 1 ? width : stride;
            int activeHeight = index / columns == rows - 1 ? height : rowStride;
            hits.Add(new("stack-workload-member:" + member.Item.Id, Scope, new(bounds.X, bounds.Y, activeWidth, activeHeight),
                new MatchIntent.InspectPendingEffect(member.Item.Id)));
            stackRows.Add(new(member.Item.Id, member.Position, bounds));
        }
    }
}
