using Mana.Contracts;
using Mana.Magic;
using Microsoft.Xna.Framework;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private bool SelectingActionTarget => SourceActionsSupported && match?.Decision is {
        Intent: DecisionIntent.Selection, InputType: "InputSelectTargets"
    };

    private void DrawSourceTargeting()
    {
        if (!SelectingActionTarget || session.NeedsRecovery || OverlayOpen || match is not { Decision: { } decision } state) return;
        var source = InteractionCards.Source(state, decision);
        if (source == null) return;
        var origin = cardPresentation.Cards.LastOrDefault(card => card.Zone != "Stack" && card.ChoiceSlot < 0 && SameVisibleCard(card.Card, source));
        if (origin == null) return;
        canvas.CardFrame(origin.Display with { Size = origin.Display.Size + new Vector2(9) }, Blue, 2);
        var hit = HitAt(pointer);
        Vector2? target = null;
        if (hit?.Card is { Selectable: true, Key.Length: > 0 } card) {
            var destination = cardPresentation.Cards.LastOrDefault(entry => entry.ChoiceSlot < 0 && SameVisibleCard(entry.Card, card));
            if (destination != null && destination.Id != origin.Id) target = destination.Display.EdgeToward(origin.Display.Center, 5);
        } else if (hit?.PlayerId is { } player && decision.PlayerChoices.Contains(player) && heroPositions.TryGetValue(player, out var hero)) {
            var direction = origin.Display.Center - hero;
            target = hero + (direction.LengthSquared() > 0 ? Vector2.Normalize(direction) : Vector2.UnitY) * (player == state.ViewerId ? 51 : 40);
        }
        if (target is { } point) canvas.Arrow(origin.Display.EdgeToward(point, 5), point, Blue, 10);
    }
}
