using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private sealed record DenseStatsPaint(Rectangle Bounds, int MeasuredWidth);
    private readonly Dictionary<string, DenseStatsPaint> denseStatsBounds = [];

    private void PaintDenseBattlefieldLabels(Card card, int owner, SceneCard node, int copies)
    {
        if (BattlefieldFocused) return;
        var area = boardView.Apply(node.DenseLabels);
        if (area.IsEmpty) return;
        float size = node.DenseMiniature ? .3f : .375f;
        int pitch = node.DenseRowHeight, padding = 4;
        var face = cardPresentation.TryGetPose(node.Id, out var displayed) ? displayed : boardView.Apply(node.Pose);
        var tile = Rectangle.Union(face.Bounds, area);
        canvas.Panel(tile, Color.Transparent, new(67, 85, 98), 3);
        canvas.Fill(area, new(14, 24, 31));
        Rectangle Row(int index) => new(area.X, area.Y + index * pitch, area.Width, pitch);
        void Text(string kind, string text, Rectangle bounds, Color color)
        {
            int measured = canvas.TextWidth(text, size, true), available = Math.Max(1, bounds.Width - padding * 2);
            canvas.Text(text, bounds.X + padding, bounds.Y + 1, color, size, available, 1, true);
            RecordBattlefieldLabel(card, owner, kind, text, bounds, measured, available);
            if (kind == "pt") denseStatsBounds[CardViews.Identity(card)] = new(bounds, measured);
        }
        var nameBounds = Row(0);
        if ((card.States?.Length ?? card.CombatKeywords.Length) > 0 || card.PhasedOut || card.Sick) {
            int width = 14;
            var center = new Vector2(nameBounds.Right - width / 2, nameBounds.Center.Y);
            canvas.Ring(center, 4, card.PhasedOut ? AbilityColor("phased out") : Muted, 1.2f);
            nameBounds.Width -= width;
        }
        if (card.Tapped) {
            int width = canvas.TextWidth("TAPPED", size, true) + padding * 2;
            Text("tapped", "TAPPED", new(nameBounds.Right - width, nameBounds.Top, width, pitch), Gold);
            nameBounds.Width -= width;
        }
        if (card.Damage > 0) {
            string damage = "DMG " + card.Damage;
            int width = canvas.TextWidth(damage, size, true) + padding * 2;
            Text("damage", damage, new(nameBounds.Right - width, nameBounds.Top, width, pitch), Red);
            nameBounds.Width -= width;
        }
        Text("name", card.FaceDown ? "Face-down permanent" : card.Name, nameBounds, Ink);
        if (area.Height <= pitch) return;
        var row = Row(1);
        canvas.Fill(row, new(19, 34, 40));
        int cursor = row.X;
        if (CardPresentation.HasCombatStats(card)) {
            string stats = CurrentStats(card);
            int width = canvas.TextWidth(stats, size, true) + padding * 2;
            Text("pt", stats, new(cursor, row.Y, width, row.Height), Ink);
            cursor += width;
        }
        if (copies > 1) {
            string value = "×" + copies;
            int width = canvas.TextWidth(value, size, true) + padding * 2;
            Text("copies", value, new(cursor, row.Y, width, row.Height), Gold);
            cursor += width;
        }
        if (CounterMarker(card).Length > 0) {
            int width = CounterMarkerWidth(card, size);
            PaintCounterMarker(card, new(Math.Max(cursor, row.Right - width), row.Y, width, row.Height), size, owner);
        }
    }
}
