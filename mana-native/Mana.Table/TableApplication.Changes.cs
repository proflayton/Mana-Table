using Mana.Contracts;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private readonly PublicChangeFeedback publicChanges = new();
    private string? selectedChangeId;
    private int changePage;
    private Rectangle? changeCalloutBounds;
    private readonly HashSet<string> highlightedChangeTargets = [];
    private string? highlightedChangeSource;
    private static readonly Rectangle ChangeBridgeBounds = new(1254, 550, 320, 104);
    private static readonly Rectangle ChangeRecipientsBounds = new(1464, 554, 106, 96);
    private string? changeReadPreviewGroup;
    private Rectangle? changeReadPreviewBounds;
    private sealed record ChangeIncrement(string Property, long Amount, int Recipients);
    private EffectChangeGroup? CurrentChange => selectedChangeId == null ? publicChanges.Latest
        : publicChanges.Groups.FirstOrDefault(group => group.Id == selectedChangeId) ?? publicChanges.Latest;
    private void OpenChanges(string? id = null)
    {
        selectedChangeId = id; changePage = 0; overlay = "changes";
    }
    private void DrawChangesControl()
    {
        if (publicChanges.Groups.Count > 0)
            Button("recent-changes", "Recent changes", 1004, 12, 176, () => OpenChanges(), height: 35);
    }
    private bool DrawChangeCallout()
    {
        if (publicChanges.Latest is not { } group || ChangeSourcePreview(group) is not { } source) {
            changeReadPreviewGroup = null; changeReadPreviewBounds = null;
            return false;
        }
        var recipients = CurrentChangeRecipients(group);
        if (recipients.Length == 0) return false;
        changeCalloutBounds = ChangeBridgeBounds;
        var accent = new Color(103, 228, 200);
        // A current offscreen source is the only reason to repeat a face here.
        // The battlefield itself already carries the result when that source is visible.
        PaintInteractionCard(source.Card, source.Pose, false, false);
        bool sourceOver = source.Pose.Bounds.Contains(pointer);
        canvas.CardFrame(source.Pose, sourceOver ? HoverAccent : Gold * .8f, sourceOver ? 3 : 2);
        hits.Add(new("change-source:" + CardViews.Identity(source.Card), Scope, source.Pose.Bounds,
            () => { }, source.Card, "ChangeReference"));
        canvas.Arrow(new(1330, 602), new(1349, 602), Gold, 3);
        DrawChangeIncrements(group, recipients.Select(CardViews.Identity).ToHashSet(StringComparer.Ordinal), accent);
        canvas.Arrow(new(1444, 602), new(1460, 602), accent, 3);
        var fan = recipients.Take(3).Reverse().ToArray();
        for (int index = 0; index < fan.Length; index++) {
            bool front = index == fan.Length - 1;
            // Rear faces only suggest the recipient group. The readable front
            // uses authoritative current state, with its count below the face.
            var pose = front ? new Pose(new(1532, 590), new(54, 76))
                : new Pose(new(1486 + index * 15, 580 + index * 4), new(38, 53), (index - 1) * .06f);
            if (front) PaintInteractionCard(fan[index], pose, false, false);
            else PaintCard(fan[index], pose, false, false);
            canvas.CardFrame(pose, ChangeRecipientsBounds.Contains(pointer) ? HoverAccent : accent * .8f, 2);
        }
        int total = group.Changes.Select(change => change.TargetId).Distinct(StringComparer.Ordinal).Count();
        string count = recipients.Length == total ? "×" + total : recipients.Length + "/" + total;
        canvas.CenterText(count, new(1464, 630, 106, 23), accent, .72f, true);
        // The fan is an observation/navigation gesture, never a game-card action.
        hits.Add(new("change-locate", Scope, new(1352, 554, 218, 96), () => FocusAffectedChange(group)));
        if (new Rectangle(1352, 554, 218, 96).Contains(pointer)) DrawChangeRecipientFocus(group);
        DrawChangeSourceReading(group, source.Card, sourceOver);
        return true;
    }
    private void DrawChangeIncrements(EffectChangeGroup group, IReadOnlySet<string> recipients, Color accent)
    {
        var increments = group.Changes.Where(change => recipients.Contains(change.TargetId))
            .GroupBy(change => (change.TargetId, change.Change.Property))
            .Select(changes => (changes.Key.Property, Amount: changes.Sum(change => (long)change.Change.After - change.Change.Before)))
            .Where(change => change.Amount != 0).GroupBy(change => (change.Property, change.Amount))
            .Select(changes => new ChangeIncrement(changes.Key.Property, changes.Key.Amount, changes.Count())).ToArray();
        string Label(ChangeIncrement increment) => (increment.Amount > 0 ? "+" : "") + increment.Amount
            + (increment.Recipients == recipients.Count ? "" : " ·" + increment.Recipients + "/" + recipients.Count);
        int rowHeight = increments.Length == 1 ? 96 : 46;
        var heights = increments.Select(increment => (
            Amount: canvas.TextSize(Label(increment), .72f, 70, 80, true).Y,
            Property: increment.Property == "+1/+1" ? 0 : canvas.TextSize(increment.Property, .55f, 88, 80).Y)).ToArray();
        bool simple = increments.Length is > 0 and <= 2 && heights.All(height =>
            height.Amount + (height.Property > 0 ? height.Property + 2 : 0) <= rowHeight);
        if (!simple) {
            // Different properties/amounts are never collapsed into a false total.
            PaintCounterGlyph(new(1384, 568, 17, 17), accent);
            canvas.Text("Mixed\ncounters", 1354, 590, accent, .6f, 88, 80, true);
            return;
        }
        for (int index = 0; index < increments.Length; index++) {
            var increment = increments[index];
            int height = heights[index].Amount + (heights[index].Property > 0 ? heights[index].Property + 2 : 0);
            int y = 554 + index * 48 + (rowHeight - height) / 2;
            PaintCounterGlyph(new(1354, y + Math.Max(0, (heights[index].Amount - 14) / 2), 14, 14), accent);
            canvas.Text(Label(increment), 1374, y, accent, .72f, 70, 80, true);
            if (increment.Property != "+1/+1") canvas.Text(increment.Property, 1354, y + heights[index].Amount + 2, accent, .55f, 88, 80);
        }
    }
    private Card[] CurrentChangeRecipients(EffectChangeGroup group)
    {
        var ids = group.Changes.Select(change => change.TargetId).ToHashSet(StringComparer.Ordinal);
        return match?.Players.SelectMany(player => player.Zone("Battlefield").Cards)
            .Where(card => !card.FaceDown && ids.Contains(CardViews.Identity(card)))
            .DistinctBy(CardViews.Identity).ToArray() ?? [];
    }
    private void DrawChangeRecipientFocus(EffectChangeGroup group)
    {
        foreach (string id in CurrentChangeRecipients(group).Select(CardViews.Identity)) {
            if (DisplayedChangeCardPose(id) is not { } pose) continue;
            highlightedChangeTargets.Add(id);
            canvas.CardFrame(pose with { Size = pose.Size + new Vector2(6) }, HoverAccent, 3);
        }
    }
    private void DrawChangeSourceReading(EffectChangeGroup group, Card source, bool hoveredSource)
    {
        bool held = changeReadPreviewGroup == group.Id && changeReadPreviewBounds is { } previous && previous.Contains(pointer);
        if (!hoveredSource && !held) { changeReadPreviewGroup = null; changeReadPreviewBounds = null; return; }
        var bounds = new Rectangle(824, 100, 410, 574);
        if (bounds.Intersects(decisionPanelBounds) || !targetingEffectBounds.IsEmpty && bounds.Intersects(targetingEffectBounds)) return;
        changeReadPreviewGroup = group.Id; changeReadPreviewBounds = bounds;
        PaintReferencePreview(source, new(bounds.Center.ToVector2(), bounds.Size.ToVector2()));
        hits.Add(new("change-card-preview", Scope, bounds, () => { }));
    }
    private void FocusAffectedChange(EffectChangeGroup effect)
    {
        if (match == null) return;
        var affected = effect.Changes.Select(change => change.TargetId).ToHashSet(StringComparer.Ordinal);
        var owners = match.Players.Where(player => player.Zone("Battlefield").Cards.Any(card => affected.Contains(CardViews.Identity(card)))).ToArray();
        if (owners.Length == 0) return;
        if (owners.Length > 1) {
            CollapseCardReading(); zoneName = null;
            if (TableStudyEnabled) SetStudyFocus(null);
            return;
        }
        var owner = owners[0];
        CollapseCardReading(); OpenZone(owner.Id, "Battlefield");
        if (TableStudyEnabled) return;
        var groups = FocusedBattlefieldGroups(owner);
        var plan = FocusedBattlefieldLayout.Arrange(groups, BattlefieldFocusViewport, 0,
            (group, width) => MeasureBattlefieldFocusDetails(group.Front, width).Height);
        var first = plan.Tiles.FirstOrDefault(tile => tile.Group.Cards.Any(card => affected.Contains(CardViews.Identity(card))));
        battlefieldFocusGame = match.Id; battlefieldFocusOwner = owner.Id;
        battlefieldFocusScroll = first == null ? 0 : Math.Clamp(first.Bounds.Top - BattlefieldFocusViewport.Top, 0, plan.MaximumScroll);
    }
    private (Card Card, Pose Pose)? ChangeSourcePreview(EffectChangeGroup group)
    {
        if (match == null || now - group.Updated >= 4 || readingId != null || match.Stack.Length > 0 || OverlayOpen && !BattlefieldFocused
            || ChangeBridgeBounds.Intersects(decisionPanelBounds) || !targetingEffectBounds.IsEmpty && ChangeBridgeBounds.Intersects(targetingEffectBounds)
            || group.Cause?.CardId is not { Length: > 0 } sourceId || DisplayedChangeCardPose(sourceId) != null) return null;
        var card = CardViews.Located(match).Where(item => item.Zone is "Battlefield" or "Command" or "Graveyard" or "Exile" or "Stack")
            .Select(item => item.Card).FirstOrDefault(card => CardViews.Identity(card) == sourceId && !card.FaceDown);
        if (card == null || CurrentChangeRecipients(group).Length == 0) return null;
        return (card, new(new(1292, 602), new(64, 90)));
    }
    private Pose? DisplayedChangeCardPose(string id)
    {
        if (BattlefieldFocused && battlefieldFocusPlan is { } focus) {
            var tile = focus.Tiles.FirstOrDefault(tile => CardViews.Identity(tile.Group.Front) == id && focus.Viewport.Contains(tile.Art));
            if (tile != null) return new(tile.Art.Center.ToVector2(), tile.Art.Size.ToVector2());
        }
        var entry = cardPresentation.Cards.FirstOrDefault(card => card.Id == id
            && card.Zone is "Battlefield" or "Command" or "Graveyard" or "Exile" or "Stack");
        if (entry == null || BattlefieldFocused && entry.Display.Bounds.Intersects(BattlefieldFocusBounds)
            || entry.Display.Bounds.Intersects(decisionPanelBounds)) return null;
        if (entry.Zone == "Battlefield" && tableScene?.Seats.SelectMany(seat => seat.Battlefield).SelectMany(cluster => cluster.Cards)
            .Any(card => card.Id == id && !card.GroupFront) == true) return null;
        return entry.Display;
    }
    private void DrawChangeHighlights()
    {
        if (OverlayOpen || publicChanges.Latest is not { } group || now - group.Updated > 8) return;
        var current = cardPresentation.Cards.Where(entry => entry.Zone == "Battlefield").Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        string? focus = HitAt(pointer)?.Card is { } card ? CardViews.Identity(card) : null;
        Pose? source = group.Cause?.CardId is { } sourceId && current.Contains(sourceId)
            && cardPresentation.TryGetPose(sourceId, out var sourcePose) ? sourcePose : null;
        if (source is { } origin) {
            canvas.CardFrame(origin with { Size = origin.Size + new Vector2(6) }, Gold, 3);
            highlightedChangeSource = group.Cause?.CardId;
        }
        foreach (var target in group.Changes.GroupBy(change => change.TargetId)) {
            if (!current.Contains(target.Key) || !cardPresentation.TryGetPose(target.Key, out var pose)) continue;
            highlightedChangeTargets.Add(target.Key);
            canvas.CardFrame(pose with { Size = pose.Size + new Vector2(4) }, new(103, 228, 200), 2);
            if (focus != target.Key) continue;
            if (source is { } start && Vector2.Distance(start.Center, pose.Center) > 1)
                canvas.Arrow(start.EdgeToward(pose.Center, 4), pose.EdgeToward(start.Center, 4), Gold * .85f, 5);
        }
    }
    private void DrawChangeDetails()
    {
        var group = CurrentChange;
        ModalFrame("What changed", () => overlay = "");
        if (group == null) { canvas.Text("These permanents are no longer visible. Public history still records the completed action.", 208, 211, Ink, .9f, 1150, 4); return; }
        var groups = publicChanges.Groups.Reverse().ToArray();
        int selected = Array.FindIndex(groups, item => item.Id == group.Id);
        canvas.Text(group.Title, 208, 191, Gold, 1.04f, 1160, 2, true);
        string cause = group.Cause == null ? "The engine did not report the source of this change."
            : (group.Cause.ControllerId is { } controller ? match!.Players.FirstOrDefault(player => player.Id == controller)?.Name + " · " : "")
                + (group.Cause.Kind == "trigger" ? "Triggered ability" : "Effect") + "\n" + group.Cause.Ability;
        string changes = string.Join("\n", group.Changes.Select(change => change.TargetName + " · " + change.Change.Property
            + " counters " + change.Change.Before + " → " + change.Change.After));
        var pages = canvas.TextPages(cause + "\n\n" + group.Summary + "\n\n" + changes, .84f, 1150, 460, 24);
        changePage = Math.Clamp(changePage, 0, pages.Length - 1);
        canvas.Text(pages[changePage], 208, 254, Ink, .84f, 1150, 24);
        Button("changes-page-prev", "Previous page", 208, 734, 175, () => changePage--, changePage > 0);
        Button("changes-page-next", "More results", 395, 734, 175, () => changePage++, changePage + 1 < pages.Length);
        Button("changes-source-prev", "Earlier effect", 797, 734, 180, () => { selectedChangeId = groups[selected + 1].Id; changePage = 0; }, selected + 1 < groups.Length);
        Button("changes-source-next", "Newer effect", 989, 734, 180, () => { selectedChangeId = groups[selected - 1].Id; changePage = 0; }, selected > 0);
        canvas.Text($"Effect {selected + 1} / {groups.Length}", 1190, 748, Muted, .6f, 200, 1);
    }
}
