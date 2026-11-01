using Mana.Contracts;
using Mana.Renderer;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private Rectangle studyAttentionArea = SharedTableStudy.ActionArea;
    private bool StudyAttentionActive => match is { Stack.Length: > 0 } || ValuedTargetDecision;

    private void PrepareStudyAttentionArea()
    {
        var preferred = TwoFieldTable || StudyFocusActive || !boardView.IsIdentity
            ? DuelTableStudy.ActionArea : SharedTableStudy.ActionArea;
        studyAttentionArea = preferred;
        if (!TableStudyEnabled || !StudyAttentionActive || tableScene == null) return;

        // The cloth and its rows keep their physical dimensions. Temporary
        // announcements use the actual objects on the current table, rather
        // than imposing a permanent paragraph-sized hole in everyone's mat.
        var occupied = StudyAttentionObstacles.ToList();
        if (PortraitTurnControlsCanPaint) {
            occupied.Add(PlacePortraitPriorityArea(PortraitPriorityArea()));
            var passing = AbilityPassingControlBounds();
            if (!passing.IsEmpty) occupied.Add(passing);
        }
        if (!occupied.Any(bounds => bounds.Intersects(preferred))) return;
        var required = new List<Rectangle>();
        if (SelectingActionTarget || decisions.Payload is TargetSelectionPayload or EntitySelectionPayload) {
            foreach (var card in tableScene.BattlefieldDrawOrder.Where(card => IsCardActionable(card.Card)))
                required.Add(boardView.Apply(card.Pose).Bounds);
            foreach (var seat in tableScene.Seats.Where(seat => IsPlayerTarget(seat.PlayerId)))
                required.Add(new((int)seat.Geometry.Hero.X - 43, (int)seat.Geometry.Hero.Y - 43, 86, 86));
        }
        var source = match?.Decision?.Context.Source?.Card;
        var counter = (decisions.Payload as TargetSelectionPayload)?.Effect?.CounterChange?.Card;
        foreach (var card in tableScene.BattlefieldDrawOrder.Where(card =>
            source != null && card.Id == CardViews.Identity(source) || counter != null && card.Id == CardViews.Identity(counter)))
            required.Add(boardView.Apply(card.Pose).Bounds);
        var anchor = new Rectangle(preferred.Center.X, preferred.Center.Y, 1, 1);
        studyAttentionArea = DecisionWorkspaceLayout.Place(anchor, preferred.Width, preferred.Height, occupied,
            bounded: true, minimumWidth: preferred.Width, minimumHeight: preferred.Height, protectedObjects: required);
    }
}
