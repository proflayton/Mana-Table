using Mana.Contracts;
using Mana.Renderer;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private PresentedCard? PendingConnectionCard(string id)
    {
        if (match == null || id.Length == 0) return null;
        // A stack source can outlive its permanent. Only a currently disclosed,
        // actually displayed occurrence owns a live board endpoint.
        var current = match.Players.SelectMany(player => player.Zones).SelectMany(zone => zone.Cards)
            .FirstOrDefault(card => !card.FaceDown && (card.VisualId == id || card.CombatId == id || CardViews.Identity(card) == id));
        return current == null ? null : cardPresentation.Cards.LastOrDefault(entry =>
            entry.Zone != "Stack" && entry.ChoiceSlot < 0 && !entry.Request.OpensZone && SameVisibleCard(current, entry.Card));
    }

    private void DrawPendingEffectConnections(StackItem item, Rectangle effect, Rectangle occupied)
    {
        Vector2 Port(Pose external, int y) => new(external.Center.X < occupied.Center.X ? effect.Left : effect.Right, y);
        void Connect(Pose external, bool incoming)
        {
            // Separate ports and shoulders keep a source that is also a target
            // from losing one direction beneath the other connection.
            var port = Port(external, effect.Y + (incoming ? 34 : Math.Min(effect.Height - 34, 78)));
            var path = PanelConnection.Route(occupied, port, external, incoming ? 24 : 48);
            if (path.Length < 2) return;
            if (incoming) Array.Reverse(path);
            Color color = incoming ? Gold : Red;
            for (int i = 1; i < path.Length - 1; i++) {
                canvas.Line(path[i - 1], path[i], Color.Black * .75f, 6);
                canvas.Line(path[i - 1], path[i], color * .95f, 3);
                canvas.Circle(path[i], 1.5f, color);
            }
            canvas.Arrow(path[^2], path[^1], color, 6);
            canvas.Circle(port, 4, color);
        }

        if (item.Card is { } source && PendingConnectionCard(CardViews.Identity(source)) is { } sourceCard) {
            canvas.CardFrame(sourceCard.Display with { Size = sourceCard.Display.Size + new Vector2(7) }, Gold * .8f, 2);
            Connect(sourceCard.Display, incoming: true);
        }
        foreach (var target in item.Targets) {
            if (target.Kind == "card" && PendingConnectionCard(target.Id) is { } card) {
                canvas.CardFrame(card.Display with { Size = card.Display.Size + new Vector2(10) }, Red, 3);
                Connect(card.Display, incoming: false);
            } else if (target.Kind == "player" && target.PlayerId is { } player && heroPositions.TryGetValue(player, out var hero)) {
                float radius = player == match!.ViewerId ? 52 : 40;
                Connect(new(hero, new(radius * 2)), incoming: false);
            }
        }
    }
}
