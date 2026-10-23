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
        return current == null ? null : matchPresentation.Cards.Cards.LastOrDefault(entry =>
            entry.Zone != "Stack" && entry.ChoiceSlot < 0 && !entry.Request.OpensZone && SameVisibleCard(current, entry.Card));
    }

    private void DrawPendingEffectConnections(StackItem item, Rectangle effect, Rectangle occupied)
    {
        var connections = new List<(Pose External, bool Incoming)>();
        void Connect(Pose external, bool incoming, Vector2 port, float lowest, float highest)
        {
            var path = PanelConnection.Route(occupied, port, external, incoming ? 24 : 48);
            if (path.Length < 2) {
                // An adjacent card can occupy the original side port's height.
                // Move the port clear of its bounds without reversing the order
                // of neighboring connections or weakening the routing margin.
                foreach (float y in new[] { external.Bounds.Top - 12f, external.Bounds.Bottom + 12f }
                    .Where(y => y >= lowest && y <= highest).OrderBy(y => Math.Abs(y - port.Y))) {
                    var alternative = new Vector2(port.X, y);
                    path = PanelConnection.Route(occupied, alternative, external, incoming ? 24 : 48);
                    if (path.Length >= 2) { port = alternative; break; }
                }
            }
            if (path.Length < 2) {
                var alternative = new Vector2(port.X == effect.Left ? effect.Right : effect.Left, port.Y);
                path = PanelConnection.Route(occupied, alternative, external, incoming ? 24 : 48);
                if (path.Length >= 2) port = alternative;
            }
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
            connections.Add((sourceCard.Display, true));
        }
        foreach (var target in item.Targets) {
            if (target.Kind == "card" && PendingConnectionCard(target.Id) is { } card) {
                canvas.CardFrame(card.Display with { Size = card.Display.Size + new Vector2(10) }, Red, 3);
                connections.Add((card.Display, false));
            } else if (target.Kind == "player" && target.PlayerId is { } player && heroPositions.TryGetValue(player, out var hero)) {
                float radius = player == match!.ViewerId ? 52 : 40;
                connections.Add((new(hero, new(radius * 2)), false));
            }
        }
        // Preserve the spatial order of endpoints on each side. Connecting a
        // lower card to an upper port crosses the neighboring upper card's link.
        foreach (var side in connections.GroupBy(connection => connection.External.Center.X < occupied.Center.X)) {
            var ordered = side.OrderBy(connection => connection.External.Center.Y).ToArray();
            for (int index = 0; index < ordered.Length; index++) {
                var connection = ordered[index];
                int offset = ordered.Length == 1 ? connection.Incoming ? 34 : Math.Min(effect.Height - 34, 78)
                    : 34 + (int)((effect.Height - 68) * index / (float)(ordered.Length - 1));
                float halfGap = ordered.Length == 1 ? effect.Height : (effect.Height - 68f) / (ordered.Length - 1) / 2;
                float lowest = index == 0 ? effect.Top + 18 : effect.Y + offset - halfGap + 3;
                float highest = index == ordered.Length - 1 ? effect.Bottom - 18 : effect.Y + offset + halfGap - 3;
                Connect(connection.External, connection.Incoming, new(side.Key ? effect.Left : effect.Right, effect.Y + offset), lowest, highest);
            }
        }
    }
}
