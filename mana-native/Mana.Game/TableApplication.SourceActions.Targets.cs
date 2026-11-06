using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private bool SelectingActionTarget => decisions.Presentation?.Payload is TargetSelectionPayload;

    private void DrawSourceTargeting()
    {
        targetingEffectBounds = Rectangle.Empty;
        pendingCounterReceiptBounds.Clear();
        if (session.NeedsRecovery || OverlayOpen || match is not { } state) return;
        if (decisions.Presentation?.Payload is not TargetSelectionPayload selection || state.Decision is not { } decision) {
            if (state.Stack.FirstOrDefault()?.Effect is { Kind: "damage" } pending)
                DrawPendingCounterReceipt(pending);
            return;
        }
        if (selection.Search != null) return;
        var source = CardViews.Source(state, decision);
        bool valued = selection.Effect is { Kind: "damage" };
        var origin = source == null ? null : matchPresentation.Cards.Cards.LastOrDefault(card => card.Zone != "Stack" && card.ChoiceSlot < 0 && SameVisibleCard(card.Card, source));
        if (valued) DrawTargetingEffect(state, decision, selection, source);
        if (origin == null && !valued) return;
        if (origin != null) canvas.CardFrame(origin.Display with { Size = origin.Display.Size + new Vector2(9) }, valued ? Gold : Blue, 2);
        var originPose = valued ? new Mana.Renderer.Pose(targetingEffectBounds.Center.ToVector2(), targetingEffectBounds.Size.ToVector2()) : origin!.Display;
        if (CurrentSourceTargetAim(state, selection, originPose, valued, origin?.Id) is { } aim) {
            var start = originPose.EdgeToward(aim.Point, 5);
            canvas.Arrow(start, aim.Point, valued ? Red : Blue, 10);
            if (valued && selection.Effect is { Amount: { } amount, Distribution: not "divided" })
                PaintPendingDamageMarker(amount, start, aim.Point, aim.Bounds);
        }
    }

    private readonly record struct SourceTargetAim(Vector2 Point, Rectangle Bounds, TableChoiceTarget? PublicTarget);

    // The local arrow and shared consideration resolve the same actual hit.
    // A readable private target can still draw locally, but has no public endpoint.
    private SourceTargetAim? CurrentSourceTargetAim(GameSnapshot state, TargetSelectionPayload selection,
        Pose originPose, bool allowSelf, string? originId)
    {
        var hit = HitAt(pointer);
        if (hit is { Card: { Selectable: true, Key.Length: > 0 } card } && CurrentHit(hit)
            && IsCardActionable(card) && !hit.Id.StartsWith("targeting-", StringComparison.Ordinal)) {
            var destination = matchPresentation.Cards.Cards.LastOrDefault(entry => entry.ChoiceSlot < 0 && SameVisibleCard(entry.Card, card));
            if (destination != null && (allowSelf || destination.Id != originId)) {
                return new(destination.Display.EdgeToward(originPose.Center, 5), destination.Display.Bounds, PublicChoiceTarget(hit));
            }
            else if (hit.Zone == "Stack") {
                var effect = new Mana.Renderer.Pose(hit.Bounds.Center.ToVector2(), hit.Bounds.Size.ToVector2());
                return new(effect.EdgeToward(originPose.Center, 5), hit.Bounds, PublicChoiceTarget(hit));
            }
        } else if (hit?.PlayerId is { } player && CurrentHit(hit) && selection.Players.Contains(player) && heroPositions.TryGetValue(player, out var hero)) {
            var direction = originPose.Center - hero;
            int radius = player == state.ViewerId ? 51 : 40;
            return new(hero + (direction.LengthSquared() > 0 ? Vector2.Normalize(direction) : Vector2.UnitY) * radius,
                new((int)hero.X - radius, (int)hero.Y - radius, radius * 2, radius * 2),
                new(TableChoiceTargetKind.Player, player.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        return null;
    }
}
