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
        if (OverlayOpen && !BattlefieldFocused || readingId != null || match!.Stack.Length > 0 || publicChanges.Latest is not { } group || now - group.Updated > 8) return false;
        // This area is already reserved from battlefield packing. Measured
        // instructions retain their full space above the decision controls.
        int top = Math.Min(676, (QuietPriorityControlVisible ? QuietPriorityControlTop : decisionPanelBounds.Top) - 36);
        if (top < 550) return false;
        changeCalloutBounds = new(1492, top, 70, 28);
        int bottom = Math.Min(710, (QuietPriorityControlVisible ? QuietPriorityControlTop : decisionPanelBounds.Top) - 8);
        if (now - group.Updated < 4 && bottom - 550 >= 110) {
            var preview = ChangeSourcePreview(group);
            var bounds = new Rectangle(1254, 550, 320, bottom - 550);
            canvas.Panel(bounds, new(14, 27, 32), Gold * .7f, 7);
            if (preview is { } source) {
                PaintCard(source.Card, source.Pose, false, false);
                if (effectTransitionFrame.Sources.FirstOrDefault(pulse => pulse.CardId == CardViews.Identity(source.Card)
                        && Vector2.DistanceSquared(pulse.Pose.Center, source.Pose.Center) < 1) is { } pulse) {
                    canvas.CardFrame(source.Pose with { Size = source.Pose.Size + new Vector2(7) }, Gold * pulse.Strength, 3);
                    PaintEffectSpark(source.Pose.Point(1, 0), 7, Gold);
                }
                hits.Add(new("change-source:" + CardViews.Identity(source.Card), Scope, source.Pose.Bounds, () => Inspect(source.Card), source.Card));
            } else PaintEffectSpark(new(1301, 592), 19, Gold);
            canvas.Text("LAST CHANGE", 1355, 554, Muted, .43f, 204, 1, true);
            canvas.Text(group.Title, 1355, 573, Ink, .67f, 204, (int)Math.Ceiling(2 * preferences.TextScale), true);
            var deltas = group.Changes.Select(change => (long)change.Change.After - change.Change.Before).Distinct().ToArray();
            string amount = deltas.Length == 1 ? (deltas[0] > 0 ? "+" : "") + deltas[0] : "Δ";
            int counterY = bottom - 57;
            PaintEffectSpark(new(1372, counterY + 7), 11, Gold);
            canvas.Text(amount, 1390, counterY - 9, new(103, 228, 200), 1.0f, 70, 1, true);
            canvas.Text(group.Changes.Select(change => change.TargetId).Distinct().Count() + " affected", 1443, counterY + 2, Muted, .48f, 119, 1);
            Button("change-locate", "Show cards", 1355, bottom - 28, 132, () => FocusAffectedChange(group), height: 25);
            changeCalloutBounds = bounds;
        }
        Button("change-details", "Why?", 1492, top, 70, () => OpenChanges(group.Id), height: 28);
        return true;
    }
    private void FocusAffectedChange(EffectChangeGroup effect)
    {
        if (match == null) return;
        var affected = effect.Changes.Select(change => change.TargetId).ToHashSet(StringComparer.Ordinal);
        var owner = match.Players.FirstOrDefault(player => player.Zone("Battlefield").Cards.Any(card => affected.Contains(CardViews.Identity(card))));
        if (owner == null) { OpenChanges(effect.Id); return; }
        CollapseCardReading(); OpenZone(owner.Id, "Battlefield");
        var groups = FocusedBattlefieldGroups(owner);
        var plan = FocusedBattlefieldLayout.Arrange(groups, BattlefieldFocusViewport, 0,
            (group, width) => MeasureBattlefieldFocusDetails(group.Front, width).Height);
        var first = plan.Tiles.FirstOrDefault(tile => tile.Group.Cards.Any(card => affected.Contains(CardViews.Identity(card))));
        battlefieldFocusGame = match.Id; battlefieldFocusOwner = owner.Id;
        battlefieldFocusScroll = first == null ? 0 : Math.Clamp(first.Bounds.Top - BattlefieldFocusViewport.Top, 0, plan.MaximumScroll);
    }
    private (Card Card, Pose Pose)? ChangeSourcePreview(EffectChangeGroup group)
    {
        if (match == null || now - group.Updated >= 4 || readingId != null || match.Stack.Length > 0 || OverlayOpen && !BattlefieldFocused) return null;
        int bottom = Math.Min(710, (QuietPriorityControlVisible ? QuietPriorityControlTop : decisionPanelBounds.Top) - 8);
        if (bottom - 550 < 110) return null;
        var card = CardViews.Visible(match).FirstOrDefault(card => CardViews.Identity(card) == group.Cause?.CardId && !card.FaceDown);
        if (card == null) return null;
        int height = Math.Min(114, bottom - 550 - 16), width = (int)(height / 1.4f);
        return (card, new(new(1266 + width / 2, 558 + height / 2), new(width, height)));
    }
    private string CounterLabel(Card card, string type, int value)
    {
        string label = type + " ×" + value;
        if (publicChanges.Latest is not { } group || now - group.Updated > 8) return label;
        long delta = group.Changes.Where(change => change.TargetId == CardViews.Identity(card) && change.Change.Property == type)
            .Sum(change => (long)change.Change.After - change.Change.Before);
        return label + (delta == 0 ? "" : " (" + (delta > 0 ? "+" : "") + delta + ")");
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
