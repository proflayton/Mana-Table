using Mana.Contracts;
using Mana.Magic;
using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private bool SelectingActionTarget => (SourceActionsSupported || match?.Decision?.Effect is { Kind: "damage" }) && match?.Decision is {
        Intent: DecisionIntent.Selection, InputType: "InputSelectTargets"
    };

    private void DrawSourceTargeting()
    {
        targetingEffectBounds = Rectangle.Empty;
        pendingCounterReceiptBounds.Clear();
        if (session.NeedsRecovery || OverlayOpen || match is not { } state) return;
        if (!SelectingActionTarget || state.Decision is not { } decision) {
            if (state.Stack.FirstOrDefault()?.Effect is { Kind: "damage" } pending)
                DrawPendingCounterReceipt(pending);
            return;
        }
        var source = InteractionCards.Source(state, decision);
        bool valued = decision.Effect is { Kind: "damage" };
        var origin = source == null ? null : cardPresentation.Cards.LastOrDefault(card => card.Zone != "Stack" && card.ChoiceSlot < 0 && SameVisibleCard(card.Card, source));
        if (valued) DrawTargetingEffect(state, decision, source);
        if (origin == null && !valued) return;
        if (origin != null) canvas.CardFrame(origin.Display with { Size = origin.Display.Size + new Vector2(9) }, valued ? Gold : Blue, 2);
        var originPose = valued ? new Mana.Renderer.Pose(targetingEffectBounds.Center.ToVector2(), targetingEffectBounds.Size.ToVector2()) : origin!.Display;
        var hit = HitAt(pointer);
        Vector2? target = null;
        Rectangle? targetBounds = null;
        if (hit is { Card: { Selectable: true, Key.Length: > 0 } card } && CurrentHit(hit)
            && IsCardActionable(card) && !hit.Id.StartsWith("targeting-", StringComparison.Ordinal)) {
            var destination = cardPresentation.Cards.LastOrDefault(entry => entry.ChoiceSlot < 0 && SameVisibleCard(entry.Card, card));
            if (destination != null && (valued || destination.Id != origin?.Id)) {
                target = destination.Display.EdgeToward(originPose.Center, 5);
                targetBounds = destination.Display.Bounds;
            }
            else if (hit.Zone == "Stack") {
                var effect = new Mana.Renderer.Pose(hit.Bounds.Center.ToVector2(), hit.Bounds.Size.ToVector2());
                target = effect.EdgeToward(originPose.Center, 5);
                targetBounds = hit.Bounds;
            }
        } else if (hit?.PlayerId is { } player && CurrentHit(hit) && decision.PlayerChoices.Contains(player) && heroPositions.TryGetValue(player, out var hero)) {
            var direction = originPose.Center - hero;
            int radius = player == state.ViewerId ? 51 : 40;
            target = hero + (direction.LengthSquared() > 0 ? Vector2.Normalize(direction) : Vector2.UnitY) * radius;
            targetBounds = new((int)hero.X - radius, (int)hero.Y - radius, radius * 2, radius * 2);
        }
        if (target is { } point) {
            var start = originPose.EdgeToward(point, 5);
            canvas.Arrow(start, point, valued ? Red : Blue, 10);
            if (valued && decision.Effect is { Amount: { } amount, Distribution: not "divided" })
                PaintPendingDamageMarker(amount, start, point, targetBounds);
        }
    }
}
