using System.Collections.Immutable;
using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    // StackWorkloads emits JSON object keys; this local navigation key cannot
    // identify a reported cohort or grant any engine action.
    private const string AllWorkloadsKey = "all-current-stack-workloads";
    private string expandedStackWorkload = "";
    private GameSnapshot? workloadSnapshot;
    private ImmutableArray<StackWorkload> cachedWorkloads = [];
    private readonly List<StackWorkloadPaint> stackWorkloadPaint = [];
    private StackWorkloadOverviewLayout? stackWorkloadOverviewLayout;
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
        int EffectHeight, int PositionHeight, int CountWidth, int CountHeight, string Controller, string EffectPreview);
    private sealed record StackWorkloadOverviewRow(StackWorkload Group, Rectangle Bounds, StackWorkloadRow Layout);
    private sealed record StackWorkloadOverviewLayout(Rectangle Surface, string Summary, int HeadingHeight,
        ImmutableArray<StackWorkloadOverviewRow> Rows);

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
        if (match == null || CurrentStackWorkloads.IsEmpty
            || key != AllWorkloadsKey && !CurrentStackWorkloads.Any(group => group.Key == key)) return;
        if (key == AllWorkloadsKey && stackWorkloadOverviewLayout == null) {
            OpenStack();
            expandedStackWorkload = "";
            return;
        }
        expandedStackWorkload = key;
    }

    private void DrawStackWorkloads(Rectangle area)
    {
        var groups = CurrentStackWorkloads;
        if (expandedStackWorkload == AllWorkloadsKey && stackWorkloadOverviewLayout is { } overview) { DrawStackWorkloadOverview(overview); return; }
        var expanded = groups.FirstOrDefault(group => group.Key == expandedStackWorkload);
        if (expanded != null) { DrawStackWorkloadFan(expanded); return; }
        int primaryHeight = MeasureStudyStackHeight(area.Width);
        DrawStudyStackEffect(new(area.X, area.Y, area.Width, primaryHeight));
        var bar = new Rectangle(area.X, area.Y + primaryHeight + 12, area.Width, MeasureStackWorkloadBarHeight(area.Width));
        PaintControlSurface("stack-workload-overview", bar, new(17, 29, 36), Muted * .55f);
        string label = StackWorkloadSummary + (stackWorkloadOverviewLayout == null ? " · Open full stack" : "");
        canvas.Text(label, bar.X + 12, bar.Y + 6, Ink, .55f, bar.Width - 24, 80, true);
        UiIntent action = stackWorkloadOverviewLayout == null ? new MatchIntent.OpenStack() : new MatchIntent.OpenStackWorkload(AllWorkloadsKey);
        hits.Add(new("stack-workload-overview", Scope, bar, action));
    }

    private string StackWorkloadSummary => match!.Stack.Length + " pending · " + CurrentStackWorkloads.Length + " effects";

    private int MeasureStackWorkloadBarHeight(int width) => Math.Max(30,
        canvas.TextSize(StackWorkloadSummary + " · Open full stack", .55f, width - 24, 80, true).Y + 12);

    private int MeasureStackWorkloadsHeight(int width) => MeasureStudyStackHeight(width) + 12 + MeasureStackWorkloadBarHeight(width);

    private StackWorkloadOverviewLayout? MeasureStackWorkloadOverview()
    {
        var groups = CurrentStackWorkloads;
        var visible = groups.Take(3).ToArray();
        int shown = visible.Length, columns = shown;
        // Three sources use the available play space in one row, keeping each
        // source's full reading width rather than growing over player controls.
        int width = shown == 3 ? 1180 : 790;
        const int padding = 14, gap = 12;
        int rowWidth = (width - padding * 2 - gap * (columns - 1)) / columns;
        var measured = visible.Select(group => MeasureStackWorkloadRow(group, rowWidth)).ToArray();
        int headingHeight = canvas.TextSize(StackWorkloadSummary, .8f, width - padding * 2, 80, true).Y;
        const string instruction = "Open a source group to read its complete effect";
        int instructionHeight = canvas.TextSize(instruction, .53f, width - padding * 2, 80).Y;
        int contentY = 126 + padding + headingHeight + 6 + instructionHeight + 12;
        int contentHeight = 0;
        for (int row = 0; row < shown; row += columns)
            contentHeight += measured.Skip(row).Take(columns).Max(item => item.Height) + gap;
        var preferred = new Rectangle(190, 126, width, contentY - 126 + contentHeight + 42 + padding);
        var protectedObjects = ReaderControlReservations.Concat(studyRequiredObjects)
            .Concat(seatReadoutPlans.Values.SelectMany(plan => plan.Pieces))
            .Append(HudPaint(combatBarArea)).Append(HudPaint(portraitPriorityPlan?.Area ?? Rectangle.Empty))
            .Append(HudPaint(automaticModeArea)).Append(HudPaint(abilityPassingArea)).Where(area => !area.IsEmpty).ToArray();
        var paint = HudPaint(preferred);
        if (DecisionWorkspaceLayout.TranslateComplete([paint], new(12, 64, 1576, 696), protectedObjects) is not { } fitted) return null;
        var surface = new Rectangle(preferred.X + fitted.X - paint.X, preferred.Y + fitted.Y - paint.Y, width, preferred.Height);
        int y = contentY + surface.Y - preferred.Y;
        var rows = ImmutableArray.CreateBuilder<StackWorkloadOverviewRow>();
        for (int index = 0; index < shown; index++) {
            rows.Add(new(visible[index], new(surface.X + padding + index % columns * (rowWidth + gap), y, rowWidth, measured[index].Height), measured[index]));
            if (index % columns == columns - 1) y += measured.Skip(index - columns + 1).Take(columns).Max(item => item.Height) + gap;
        }
        return new(surface, StackWorkloadSummary, headingHeight, rows.ToImmutable());
    }

    private void DrawStackWorkloadOverview(StackWorkloadOverviewLayout overview)
    {
        const int padding = 14;
        var surface = overview.Surface;
        canvas.Panel(surface, new(12, 23, 31), Gold * .55f, 6);
        hits.Add(new("stack-workload-overview-surface", Scope, surface, new UiIntent.Block(InteractionScopeKind.Current), ShowPointer: false));
        canvas.Text(overview.Summary, surface.X + padding, surface.Y + padding, Gold, .8f, surface.Width - padding * 2, 80, true);
        canvas.Text("Open a source group to read its complete effect", surface.X + padding, surface.Y + padding + overview.HeadingHeight + 6,
            Muted, .53f, surface.Width - padding * 2, 80);
        if (CurrentStackWorkloads.Length > overview.Rows.Length)
            Button("stack-all-sources", "+" + (CurrentStackWorkloads.Length - overview.Rows.Length) + " effects", surface.X + padding,
                surface.Bottom - padding - 42, 150, new MatchIntent.OpenStack(), height: 42);
        Button("stack-workload-close", "Back to table", surface.Right - padding - 150, surface.Bottom - padding - 42,
            150, new MatchIntent.CloseStackWorkload(), height: 42);
        foreach (var planned in overview.Rows) {
            var group = planned.Group;
            var row = planned.Layout;
            var first = group.First;
            var bounds = planned.Bounds;
            canvas.Panel(bounds, new(17, 29, 36), Muted * .55f, 3);
            var art = new Rectangle(bounds.X + 10, bounds.Y + 10, 42, first.Item.Ability ? 37 : 59);
            PaintWorkloadArt(first.Item, art);
            var count = new Rectangle(bounds.Right - row.CountWidth - 10, bounds.Y + 10, row.CountWidth, row.CountHeight);
            canvas.Rounded(count, new(13, 25, 29), 6);
            canvas.CenterText("× " + group.Members.Length, count, Ink, .82f, true);
            var name = new Rectangle(art.Right + 10, bounds.Y + 10,
                count.X - art.Right - 20, row.NameHeight);
            var controller = new Rectangle(name.X, name.Bottom + 3, name.Width, row.ControllerHeight);
            var effect = new Rectangle(bounds.X + 10, Math.Max(Math.Max(art.Bottom, count.Bottom), controller.Bottom) + 6,
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
            stackRows.Add(new(first.Item.Id, first.Position, bounds));
            stackWorkloadPaint.Add(new(group.Key, bounds, group.Members.Select(member => member.Item.Id).ToImmutableArray(),
                group.Members.Select(member => member.Position).ToImmutableArray(), group.IsNext) {
                NameBounds = name, ControllerBounds = controller, EffectBounds = effect, CountBounds = count,
                PositionBounds = position, Name = first.Item.Name, Controller = row.Controller, EffectPreview = row.EffectPreview
            });
            PaintStackArrival(first.Item, bounds);
        }
    }

    private Point MeasureStackWorkloadCount(StackWorkload group)
    {
        string count = "× " + group.Members.Length;
        int width = Math.Max(54, canvas.TextWidth(count, .82f, true) + 16);
        return new(width, Math.Max(30, canvas.TextSize(count, .82f, width, 80, true).Y + 8));
    }

    private static string WorkloadKind(StackItem item) => item.Kind == StackActionKind.TriggeredAbility ? "triggers"
        : item.Ability ? "ability" : "spell";

    private StackWorkloadRow MeasureStackWorkloadRow(StackWorkload group, int width)
    {
        var first = group.First;
        var count = MeasureStackWorkloadCount(group);
        int countWidth = count.X, countHeight = count.Y;
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
        int height = 10 + Math.Max(Math.Max(first.Item.Ability ? 37 : 59, countHeight), nameHeight + 3 + controllerHeight)
            + 6 + effectHeight + 4 + positionHeight + 10;
        return new(height, nameHeight, controllerHeight, effectHeight, positionHeight, countWidth, countHeight, controller, preview);
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
