using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private string StackHeading(StackItem item)
    {
        if (matchPresentation.StackArrivalStrength(item.Id, now) <= 0) return "Next to resolve";
        var actor = item.ControllerId is { } controller ? StateClarity.PlayerName(match!, controller) : "A player";
        bool own = item.ControllerId == match!.ViewerId;
        return item.Kind switch {
            StackActionKind.Spell => actor + (own ? " cast" : " casts"),
            StackActionKind.ActivatedAbility => actor + (own ? " activate" : " activates"),
            StackActionKind.TriggeredAbility => "Triggered · " + actor,
            _ => "Ability · " + actor
        };
    }

    private void PaintStackArrival(StackItem item, Rectangle bounds)
    {
        float strength = matchPresentation.StackArrivalStrength(item.Id, now);
        if (strength <= 0) return;
        float pulse = preferences.ReducedMotion ? 1 : .75f + .25f * MathF.Sin((float)now * 6);
        canvas.CardFrame(new(bounds.Center.ToVector2(), bounds.Size.ToVector2()),
            Gold * ((.35f + .65f * strength) * pulse), 3);
        if (item.Card is not { FaceDown: false } source) return;
        var physical = matchPresentation.Cards.Cards.LastOrDefault(card => card.Zone == "Battlefield"
            && CardViews.Same(card.Card, source));
        if (physical != null) canvas.CardFocus(physical.Display, Gold * ((.4f + .6f * strength) * pulse));
    }
}
