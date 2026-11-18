using System.Collections.Immutable;
using Mana.Magic;
using Mana.Renderer;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private bool tableStudy;
    private bool allowLiveTableStudy;
    private bool TableStudyAvailable => allowLiveTableStudy || ExperienceLabOpen || Automated;
    private bool TableStudyEnabled => TableStudyAvailable && match?.Players.Length is >= 2 and <= 6
        && (tableStudy || match.Players.Length > 4);
    internal void EnableLiveTableStudy()
    {
        allowLiveTableStudy = true;
        SetTableStudy(true);
    }
    internal void SetTableStudy(bool enabled)
    {
        if (!TableStudyAvailable || dragging) return;
        tableStudy = enabled;
        manualBoardView = null; boardPanning = false;
        studyFocusOwner = null;
        ClearCardReading();
        SetTurnFollow(false);
        boardViewMotion.Reset();
        pressed = null;
    }

    private static string StudyEffectText(Mana.Contracts.StackItem entry) => entry.Text.Length > 0 ? entry.Text
        : !entry.Ability && entry.Card is { Text.Length: > 0 } spell ? spell.Text : "Effect text not reported.";

    private int MeasureStudyStackHeight(int width)
    {
        var entry = match!.Stack[0];
        string owner = entry.ControllerId is { } controller ? StateClarity.PlayerName(match, controller) : "Controller not reported";
        int source = Math.Max(entry.Ability ? 51 : 75, 14 + canvas.TextSize(entry.Name, .67f, width - 78, 80, true).Y
            + canvas.TextSize(owner, .48f, width - 78, 80).Y);
        int effect = entry.Effect is { Kind: "damage" } damage ? 62 + PendingDamageCounterHeight(damage, width - 24)
            : canvas.TextSize(StudyEffectText(entry), .66f, width - 24, 80, true).Y;
        // A long rules passage keeps its normal type and full hover reader;
        // ordinary triggers expand instead of shrinking into a fixed tile.
        return StudyStackHeaderHeight + 2 + source + Math.Min(340, effect) + (entry.Targets.Length > 0 ? 28 : 0) + 12;
    }

    private int StudyStackHeaderHeight => RepeatedStackWorkload
        ? Math.Max(23, MeasureStackWorkloadCount(CurrentStackWorkloads[0]).Y) : 23;

    private void DrawStudyStackFlow()
    {
        var area = StudyActionArea;
        if (RepeatedStackWorkload) { DrawStackWorkloads(area); return; }
        DrawStudyStackEffect(area);
    }

    private void DrawStudyStackEffect(Rectangle area)
    {
        var state = match!;
        var entry = state.Stack[0];
        var workload = RepeatedStackWorkload ? CurrentStackWorkloads[0] : null;
        string owner = entry.ControllerId is { } controller ? StateClarity.PlayerName(state, controller) : "Controller not reported";
        string status = workload != null ? "Next to resolve" : StackHeading(entry);
        var header = new Rectangle(area.X, area.Y, area.Width, StudyStackHeaderHeight);
        canvas.Rounded(header, new(11, 22, 28), 4);
        var count = workload != null ? new Rectangle(header.Right - MeasureStackWorkloadCount(workload).X - 6,
                header.Y, MeasureStackWorkloadCount(workload).X, header.Height)
            : state.Stack.Length > 1 ? new Rectangle(header.Right - 125, header.Y, 119, 23) : Rectangle.Empty;
        int titleWidth = count.IsEmpty ? header.Width - 20 : count.X - header.X - 20;
        int titleHeight = canvas.TextSize(status, .52f, titleWidth, 1, true).Y;
        var title = new Rectangle(header.X + 10, workload != null ? header.Y + (header.Height - titleHeight) / 2 : header.Y + 3,
            titleWidth, titleHeight);
        canvas.Text(status, title.X, title.Y, Gold, .52f, title.Width, 1, true);
        if (workload != null) {
            canvas.Rounded(count, new(13, 25, 29), 4);
            canvas.CenterText("× " + workload.Members.Length, count, Gold, .82f, true);
            hits.Add(new("stack-workload:" + entry.Id, Scope, count, new MatchIntent.OpenStackWorkload(workload.Key)));
        } else if (state.Stack.Length > 1) {
            canvas.CenterText(state.Stack.Length + " pending", count, Muted, .5f, true);
            hits.Add(new("study-stack-list", Scope, count, new MatchIntent.OpenStack(0)));
        }
        var body = new Rectangle(area.X, header.Bottom + 2, area.Width, area.Height - header.Height - 2);
        canvas.Panel(body, new(17, 29, 36), Gold * .55f, 4);
        PaintStackArrival(entry, body);
        var art = new Rectangle(body.X + 10, body.Y + 8, 42, 37);
        if (entry.Ability && entry.Card is { FaceDown: false } source && resources.Card(source) is { } image) {
            var crop = new Rectangle((int)(image.Width * .12f), (int)(image.Height * .14f),
                (int)(image.Width * .76f), (int)(image.Height * .38f));
            canvas.Sprite(image, art.Center.ToVector2(), art.Size.ToVector2(), 0, Color.White, crop);
        } else if (entry.Ability) canvas.Fill(art, new(43, 58, 64));
        int nameHeight = canvas.TextSize(entry.Name, .67f, body.Width - 78, 80, true).Y;
        int ownerHeight = canvas.TextSize(owner, .48f, body.Width - 78, 80).Y;
        int sourceHeight = Math.Max(entry.Ability ? 51 : 75, 14 + nameHeight + ownerHeight);
        var name = new Rectangle(art.Right + 10, body.Y + 7, body.Width - 78, nameHeight);
        var controllerName = new Rectangle(name.X, body.Y + 10 + nameHeight, name.Width, ownerHeight);
        canvas.Text(entry.Name, name.X, name.Y, Ink, .67f, name.Width, 80, true);
        canvas.Text(owner, controllerName.X, controllerName.Y, Gold, .48f, controllerName.Width, 80);
        AddPendingSourceReference(entry, new(art.X, art.Y, body.Width - 20, sourceHeight - 8));
        var effectArea = new Rectangle(body.X + 12, body.Y + sourceHeight, body.Width - 24,
            Math.Max(25, body.Height - sourceHeight - (entry.Targets.Length > 0 ? 28 : 0) - 8));
        void RecordWorkload(string preview)
        {
            if (workload == null) return;
            stackWorkloadPaint.Add(new(workload.Key, area, workload.Members.Select(member => member.Item.Id).ToImmutableArray(),
                workload.Members.Select(member => member.Position).ToImmutableArray(), true) {
                NameBounds = name, ControllerBounds = controllerName, EffectBounds = effectArea, CountBounds = count,
                PositionBounds = title, Name = entry.Name, Controller = owner, EffectPreview = preview
            });
        }
        if (entry.Effect is { Kind: "damage" } value) {
            int targetHeight = entry.Targets.Length > 0 ? 28 : 0;
            PaintPendingDamageValue(value, new(body.X + 12, body.Y + sourceHeight, body.Width - 24,
                Math.Max(1, body.Height - sourceHeight - 8 - targetHeight)));
            if (entry.Targets.Length > 0)
                DrawPendingTargetReferences(entry, new(body.X + 12, body.Bottom - 28, body.Width - 24, 24));
            RecordWorkload(StudyEffectText(entry));
            stackRows.Add(new(entry.Id, 1, body));
            return;
        }
        string effect = StudyEffectText(entry);
        const float effectSize = .66f;
        string[] effectPages = canvas.TextPages(effect, effectSize, effectArea.Width, effectArea.Height, 80, true);
        string preview = effectPages[0];
        if (effectPages.Length > 1) {
            while (preview.Length > 0 && canvas.TextSize(preview + " …", effectSize, effectArea.Width, 80, true).Y > effectArea.Height) {
                int space = preview.LastIndexOf(' ');
                preview = space > 0 ? preview[..space] : "";
            }
            preview += " …";
        }
        canvas.Text(preview, effectArea.X, effectArea.Y, Ink,
            effectSize, effectArea.Width, 80, true);
        RecordWorkload(preview);
        hits.Add(new("study-stack-effect:" + entry.Id, Scope, effectArea, new MatchIntent.InspectPendingEffect(entry.Id)));
        if (entry.Targets.Length > 0)
            DrawPendingTargetReferences(entry, new(body.X + 12, body.Bottom - 28, body.Width - 24, 24));
        stackRows.Add(new(entry.Id, 1, body));
        // Source/target references retain their own hover and inspection.
        // Keep the normal table free of always-on cross-seat connector webs.
    }
}
