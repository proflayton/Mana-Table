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
    private readonly List<StackWorkloadPaint> stackWorkloadPaint = [];
    internal sealed record StackWorkloadPaint(string Key, Rectangle Bounds, ImmutableArray<string> MemberIds,
        ImmutableArray<int> Positions, bool IsNext)
    {
        public Rectangle NameBounds { get; init; }
        public Rectangle ControllerBounds { get; init; }
        public Rectangle EffectBounds { get; init; }
        public Rectangle CountBounds { get; init; }
        public Rectangle PositionBounds { get; init; }
        public string Name { get; init; } = "";
        public string Controller { get; init; } = "";
        public string EffectPreview { get; init; } = "";
    }

    private sealed record StackWorkloadRow(int Height, int NameHeight, int ControllerHeight,
        int EffectHeight, int PositionHeight, int CountWidth, string Controller, string EffectPreview);

    private ImmutableArray<StackWorkload> CurrentStackWorkloads
    {
        get {
            if (!ReferenceEquals(workloadSnapshot, match)) {
                workloadSnapshot = match;
                // This is a census of current members. Its order follows each
                // group's first CURRENT position, never an earlier snapshot.
                cachedWorkloads = match == null ? [] : StackWorkloads.From(match.Stack)
                    .OrderBy(group => group.First.Position).ToImmutableArray();
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
        int primaryHeight = MeasureStudyStackHeight(area.Width);
        // Repeated work retains exactly the same current effect surface as a
        // single item. Source rows below it are references, not a rules queue.
        DrawStudyStackEffect(new(area.X, area.Y, area.Width, primaryHeight));
        int headingY = area.Y + primaryHeight + 12;
        canvas.Text(state.Stack.Length + " pending · by source", area.X, headingY, Muted, .55f, area.Width - 110, 1);
        if (groups.Length > shown)
            Button("stack-all-sources", "+" + (groups.Length - shown) + " sources", area.Right - 106, headingY, 106,
                new MatchIntent.OpenStack(), height: 25);
        int y = headingY + StackWorkloadHeadingHeight(area.Width);
        foreach (var group in visible) {
            var row = MeasureStackWorkloadRow(group, area.Width);
            var first = group.First;
            var bounds = new Rectangle(area.X, y, area.Width, row.Height);
            canvas.Panel(bounds, new(17, 29, 36), Muted * .55f, 3);
            var art = new Rectangle(bounds.X + 10, bounds.Y + 10, 42, 37);
            PaintWorkloadArt(first.Item, art);
            var count = new Rectangle(bounds.Right - row.CountWidth - 10, bounds.Y + 10, row.CountWidth, 30);
            canvas.Rounded(count, new(13, 25, 29), 6);
            canvas.CenterText("× " + group.Members.Length, count, Ink, .82f, true);
            var name = new Rectangle(art.Right + 10, bounds.Y + 10,
                count.X - art.Right - 20, row.NameHeight);
            var controller = new Rectangle(name.X, name.Bottom + 3, name.Width, row.ControllerHeight);
            var effect = new Rectangle(bounds.X + 10, Math.Max(art.Bottom, controller.Bottom) + 6,
                bounds.Width - 20, row.EffectHeight);
            var position = new Rectangle(effect.X, effect.Bottom + 4, effect.Width, row.PositionHeight);
            canvas.Text(first.Item.Name, name.X, name.Y, Ink, .67f, name.Width, 80, true);
            canvas.Text(row.Controller, controller.X, controller.Y, Muted, .48f, controller.Width, 80);
            canvas.Text(row.EffectPreview, effect.X, effect.Y, Muted, .52f, effect.Width, 80);
            canvas.Text("First at #" + first.Position + " · " + WorkloadKind(first.Item), position.X, position.Y,
                Muted, .48f, position.Width, 80);
            // The group gesture opens references. Hover still resolves the
            // exact current first member through the existing ability reader.
            hits.Add(new("stack-workload:" + first.Item.Id, Scope, bounds, new MatchIntent.OpenStackWorkload(group.Key)));
            // Keep the primary body's geometry authoritative for stack[0].
            if (!group.IsNext) stackRows.Add(new(first.Item.Id, first.Position, bounds));
            stackWorkloadPaint.Add(new(group.Key, bounds, group.Members.Select(member => member.Item.Id).ToImmutableArray(),
                group.Members.Select(member => member.Position).ToImmutableArray(), group.IsNext) {
                NameBounds = name, ControllerBounds = controller, EffectBounds = effect, CountBounds = count,
                PositionBounds = position, Name = first.Item.Name, Controller = row.Controller, EffectPreview = row.EffectPreview
            });
            if (!group.IsNext) PaintStackArrival(first.Item, bounds);
            y = bounds.Bottom + 8;
        }
    }

    private int MeasureStackWorkloadsHeight(int width) => MeasureStudyStackHeight(width) + 12 + StackWorkloadHeadingHeight(width)
        + CurrentStackWorkloads.Take(3).Sum(group => MeasureStackWorkloadRow(group, width).Height + 8) - 8;

    private int StackWorkloadHeadingHeight(int width) => Math.Max(25,
        canvas.TextSize(match!.Stack.Length + " pending · by source", .55f, width - 110, 1).Y + 5);

    private static string WorkloadKind(StackItem item) => item.Kind == StackActionKind.TriggeredAbility ? "triggers"
        : item.Ability ? "ability" : "spell";

    private StackWorkloadRow MeasureStackWorkloadRow(StackWorkload group, int width)
    {
        var first = group.First;
        int countWidth = Math.Max(54, canvas.TextWidth("× " + group.Members.Length, .82f, true) + 16);
        int nameWidth = width - 82 - countWidth;
        int nameHeight = canvas.TextSize(first.Item.Name, .67f, nameWidth, 80, true).Y;
        string controller = StateClarity.StackController(match!, first.Item);
        int controllerHeight = canvas.TextSize(controller, .48f, nameWidth, 80).Y;
        int previewHeight = canvas.TextSize("Reference\nReference", .52f, width - 20, 80).Y;
        string effect = StudyEffectText(first.Item);
        string[] pages = canvas.TextPages(effect, .52f, width - 20, previewHeight, 80);
        string preview = pages[0];
        if (pages.Length > 1) {
            while (preview.Length > 0 && canvas.TextSize(preview + " …", .52f, width - 20, 80).Y > previewHeight) {
                int space = preview.LastIndexOf(' ');
                preview = space > 0 ? preview[..space] : "";
            }
            preview += " …";
        }
        int effectHeight = canvas.TextSize(preview, .52f, width - 20, 80).Y;
        int positionHeight = canvas.TextSize("First at #" + first.Position + " · " + WorkloadKind(first.Item), .48f, width - 20, 80).Y;
        int height = 10 + Math.Max(37, nameHeight + 3 + controllerHeight) + 6 + effectHeight + 4 + positionHeight + 10;
        return new(height, nameHeight, controllerHeight, effectHeight, positionHeight, countWidth, controller, preview);
    }

    private void PaintWorkloadArt(StackItem item, Rectangle art)
    {
        if (item.Card is { FaceDown: false } source && resources.Card(source) is { } image) {
            var crop = new Rectangle((int)(image.Width * .12f), (int)(image.Height * .14f),
                (int)(image.Width * .76f), (int)(image.Height * .38f));
            canvas.Sprite(image, art.Center.ToVector2(), art.Size.ToVector2(), 0, Color.White, crop);
        } else canvas.Gradient(art, new(43, 58, 64), new(28, 40, 48));
    }

    private void PaintWorkloadReference(StackItem item, Rectangle bounds, bool next, int position)
    {
        canvas.Panel(bounds, new(17, 29, 36), next ? Gold * .8f : Muted * .55f, 3);
        PaintWorkloadArt(item, new(bounds.X + 6, bounds.Y + 6, bounds.Width - 12, Math.Max(30, bounds.Height - 12)));
        var badge = new Rectangle(bounds.X + 2, bounds.Y + 3, 33, 22);
        canvas.Rounded(badge, new(13, 25, 29), 3);
        canvas.CenterText(position.ToString(), badge, next ? Gold : Ink, .55f, true);
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
        string heading = group.First.Item.Name + " · " + group.Members.Length + " current " + WorkloadKind(group.First.Item);
        int headingHeight = canvas.TextSize(heading, .8f, 790, 80, true).Y;
        int instructionY = 91 + headingHeight + 6;
        int instructionHeight = canvas.TextSize("Numbers are actual stack positions", .53f, 690, 1).Y;
        var area = new Rectangle(190, instructionY + instructionHeight + 8,
            Math.Max(380, width + stride * (columns - 1)), 24 + height + rowStride * (rows - 1));
        canvas.Text(heading, area.X, 91, Gold, .8f, 790, 80, true);
        canvas.Text("Numbers are actual stack positions", area.X, instructionY, Muted, .53f, 690, 1);
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
