using Mana.Contracts;
using Microsoft.Xna.Framework;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Renderer;

internal static class HandLayout
{
    internal static HandPlan Build(GameSnapshot state, Point pointer,
        string? previouslyRaised, string? dragged, bool allowHover)
    {
        var hand = state.Viewer?.Zone("Hand").Cards ?? [];
        var visible = hand.ToArray();
        int raised = -1;
        if (allowHover) {
            for (int index = 0; index < visible.Length; index++)
                if (TableLayout.Hand(index, visible.Length).Contains(pointer)) raised = index;
            int old = Array.FindIndex(visible, card => CardViews.Identity(card) == previouslyRaised);
            if (raised < 0 && old >= 0 && TableLayout.Hand(old, visible.Length, true).Contains(pointer)) raised = old;
        }
        Vector2 entrance = TableWorld.Pile(3, 0).Center;
        var cards = visible.Select((card, index) => SceneCard.Create(card,
                TableLayout.Hand(index, visible.Length, index == raised), "Hand", state.ViewerId, index, entrance)
                with { EntranceDelay = Math.Min(index, 10) * .035f })
            .Where(node => CardViews.Identity(node.Card) != dragged)
            .OrderBy(node => node.Card == visible.ElementAtOrDefault(raised) ? 1 : 0).ToArray();
        return new(hand.Length, raised < 0 ? null : CardViews.Identity(visible[raised]), cards,
            hand.Length == 0 ? Rectangle.Empty : new(263, 864, 373, 31));
    }
}
