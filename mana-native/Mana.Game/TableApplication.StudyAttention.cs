using Mana.Contracts;
using Mana.Renderer;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private Rectangle studyAttentionArea = SharedTableStudy.ActionArea;
    private IReadOnlyList<Rectangle> studyRequiredObjects = [];
    private bool studyHudConstrained;
    private bool StudyAttentionActive => match is { Stack.Length: > 0 } || ValuedTargetDecision || publicAnnouncementPlan != null;

    // Panel shadows and the heading's phase-stop glyph are painted material too.
    private static Rectangle HudPaint(Rectangle area) => area.IsEmpty ? area
        : new(area.X - 2, area.Y - 4, area.Width + 6, area.Height + 11);

    private Rectangle? TryPlaceStudyHud(Rectangle preferred, IReadOnlyList<Rectangle> reserved, Rectangle viewport)
    {
        _ = StudyHudObstacles;
        var paint = HudPaint(preferred);
        var hard = studyFixedHudObstacles.Concat(studyRequiredObjects).Concat(reserved).Where(bounds => !bounds.IsEmpty).ToArray();
        var soft = StudyAttentionObstacles.Concat(hard).ToArray();
        // First leave every physical card visible. On a busy table, temporary
        // context may cover unrelated cards, but owner facts and current actions
        // remain hard exclusions. Measured text never gets squeezed to fit.
        var placed = DecisionWorkspaceLayout.TranslateComplete([paint], viewport, soft)
            ?? DecisionWorkspaceLayout.TranslateComplete([paint], viewport, hard);
        return placed is { } complete
            ? new Rectangle(preferred.X + complete.X - paint.X, preferred.Y + complete.Y - paint.Y, preferred.Width, preferred.Height) : null;
    }

    private Rectangle PlaceStudyHud(Rectangle preferred, IReadOnlyList<Rectangle> reserved, Rectangle viewport)
    {
        if (TryPlaceStudyHud(preferred, reserved, viewport) is { } clear) return clear;
        // An infeasible frame is an explicit best-effort outcome, not a claim
        // of clear geometry. Keep required actions at full size; their ordinary
        // late paint/input order remains authoritative. Regression journeys
        // assert that the supported arrangements never enter this outcome.
        studyHudConstrained = true;
        var hard = studyFixedHudObstacles.Concat(studyRequiredObjects).Concat(reserved).Where(bounds => !bounds.IsEmpty).ToArray();
        return DecisionWorkspaceLayout.Place(new(preferred.Center.X, preferred.Center.Y, 1, 1), preferred.Width, preferred.Height,
            StudyAttentionObstacles, bounded: true, minimumWidth: preferred.Width, minimumHeight: preferred.Height, protectedObjects: hard);
    }

    private void PrepareStudyAttentionArea()
    {
        portraitPriorityPlan = null; automaticModeArea = Rectangle.Empty;
        combatBarArea = Rectangle.Empty; abilityPassingArea = Rectangle.Empty;
        publicAnnouncementPlan = null;
        studyRequiredObjects = [];
        studyHudConstrained = false;
        var preferred = TwoFieldTable || StudyFocusActive || !boardView.IsIdentity
            ? DuelTableStudy.ActionArea : SharedTableStudy.ActionArea;
        studyAttentionArea = preferred;
        if (match == null || tableScene == null) return;
        if (TableStudyEnabled) studyRequiredObjects = RequiredStudyObjects();
        if (PortraitPriorityCanPaint) {
            var measured = MeasurePortraitPriority();
            portraitPriorityPlan = measured with { Area = PlacePortraitPriorityArea(measured.Area) };
        }
        if (portraitPriorityPlan is { } priority) {
            var body = priority.Area;
            var preferredMode = new Rectangle(body.Right - TurnControlLayout.AutoControlWidth, body.Bottom + 8,
                TurnControlLayout.AutoControlWidth, TurnControlLayout.AutoControlHeight);
            automaticModeArea = PlaceStudyHud(preferredMode, [HudPaint(body)], new(8, 58, 1584, 834));
        }
        if (!TableStudyEnabled) return;
        var reserved = new List<Rectangle> { HudPaint(portraitPriorityPlan?.Area ?? Rectangle.Empty), HudPaint(automaticModeArea) };
        PrepareAbilityPassingArea(reserved);
        if (!abilityPassingArea.IsEmpty) reserved.Add(HudPaint(abilityPassingArea));
        PrepareCombatBar(reserved);
        if (!combatBarArea.IsEmpty) reserved.Add(HudPaint(combatBarArea));
        publicAnnouncementPlan = MeasurePublicAnnouncement(preferred.Width);
        if (!StudyAttentionActive) return;
        int height = 0, announcementHeight = 0;
        Rectangle? fitted = null;
        foreach (int width in new[] { preferred.Width, preferred.Width + 100, preferred.Width + 220 }) {
            var measuredAnnouncement = MeasurePublicAnnouncement(width);
            int stackHeight = match.Stack.Length > 0 ? RepeatedStackWorkload ? 190 : MeasureStudyStackHeight(width)
                : ValuedTargetDecision ? preferred.Height : 0;
            var candidate = new Rectangle(preferred.X, preferred.Y, width, stackHeight + (measuredAnnouncement?.Height ?? 0));
            if (TryPlaceStudyHud(candidate, reserved, new(12, 64, 1576, 696)) is not { } complete) continue;
            fitted = complete; height = stackHeight; announcementHeight = measuredAnnouncement?.Height ?? 0;
            publicAnnouncementPlan = measuredAnnouncement; break;
        }
        if (fitted == null) {
            height = match.Stack.Length > 0 ? RepeatedStackWorkload ? 190 : MeasureStudyStackHeight(preferred.Width)
                : ValuedTargetDecision ? preferred.Height : 0;
            announcementHeight = publicAnnouncementPlan?.Height ?? 0;
        }
        var context = fitted ?? PlaceStudyHud(new(preferred.X, preferred.Y, preferred.Width, height + announcementHeight), reserved, new(12, 64, 1576, 696));
        if (publicAnnouncementPlan is { } announcement)
            publicAnnouncementPlan = announcement with { Area = new(context.X, context.Y, context.Width, announcementHeight) };
        studyAttentionArea = height == 0 ? context : new(context.X, context.Y + announcementHeight, context.Width, height);
    }

    private IReadOnlyList<Rectangle> RequiredStudyObjects()
    {
        var required = new List<Rectangle>();
        if (SelectingActionTarget || decisions.Payload is TargetSelectionPayload or EntitySelectionPayload) {
            foreach (var card in tableScene!.BattlefieldDrawOrder.Where(card => IsCardActionable(card.Card)))
                required.Add(boardView.Apply(card.Pose).Bounds);
            foreach (var seat in tableScene.Seats.Where(seat => IsPlayerTarget(seat.PlayerId)))
                required.Add(new((int)seat.Geometry.Hero.X - 43, (int)seat.Geometry.Hero.Y - 43, 86, 86));
        }
        var source = match?.Decision?.Context.Source?.Card;
        var counter = (decisions.Payload as TargetSelectionPayload)?.Effect?.CounterChange?.Card;
        foreach (var card in tableScene!.BattlefieldDrawOrder.Where(card =>
            source != null && card.Id == CardViews.Identity(source) || counter != null && card.Id == CardViews.Identity(counter)))
            required.Add(boardView.Apply(card.Pose).Bounds);
        return required;
    }
}
