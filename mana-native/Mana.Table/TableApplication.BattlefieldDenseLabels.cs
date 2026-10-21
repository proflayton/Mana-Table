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
        var area = node.DenseLabels;
        if (area.IsEmpty) return;
        float scale = node.DenseScale;
        float fontSize = node.DenseMiniature ? .3f : .375f;
        int pitch = node.DenseRowHeight;
        int padding = Math.Max(1, (int)(4 * scale));
        string name = card.FaceDown ? "Face-down permanent" : string.IsNullOrWhiteSpace(card.Name) ? "Permanent" : card.Name;
        Rectangle Row(int row) => new(area.X, area.Y + row * pitch, area.Width, Math.Max(1, pitch - 1));
        void Text(string kind, string text, Rectangle bounds, Color accent, bool allowShortName = false)
        {
            bool attachedStats = kind == "pt" && (card.AttachedTo != null || card.Attachments.Length > 0);
            canvas.Panel(bounds, attachedStats ? new Color(48, 39, 22) : new(14, 24, 31), accent, 2);
            float size = fontSize;
            int available = Math.Max(1, bounds.Width - padding * 2);
            int measured = canvas.TextWidth(text, size, true);
            canvas.Text(text, bounds.X + padding, bounds.Y + Math.Max(0, (int)scale), Ink, size, available, 1, true);
            RecordBattlefieldLabel(card, owner, kind, text, bounds, measured, available);
            if (kind == "pt") denseStatsBounds[CardViews.Identity(card)] = new(bounds, measured);
        }
        var states = CardPresentation.Abilities(card).ToList();
        if (card.States != null && (!card.FaceDown || card.Type.Length > 0))
            states.AddRange(card.States.Where(state => state.Kind != "ability").Select(state => state.Id));
        if (card.Sick && CardPresentation.HasCombatStats(card)) states.Insert(0, "summoning sick");
        if (card.PhasedOut) states.Insert(0, "phased out");
        var nameBounds = Row(0);
        if (states.Count > 0) {
            int reserve = Math.Min(24, Math.Max(12, (int)(24 * scale)));
            nameBounds.Width -= reserve;
            var stateBounds = new Rectangle(area.Right - reserve, area.Y, reserve, pitch);
            canvas.Panel(stateBounds, new(14, 24, 31), AbilityColor(states[0]), 2);
            // The same established rules icon stays visible without making every
            // crowded permanent grow another complete text row.
            if (scale >= .8f && pitch >= 22) PaintAbilityIcon(states[0], new(stateBounds.Center.X, stateBounds.Center.Y));
            else canvas.CenterText("+" + states.Count, stateBounds, AbilityColor(states[0]), .32f * scale, true);
            var descriptor = card.States?.FirstOrDefault(state => state.Id == states[0]);
            string stateLabel = descriptor == null ? CardPresentation.AbilityLabel(card, states[0]) : PublicStatePresentation.Caption(descriptor);
            RecordBattlefieldLabel(card, owner, "state-icon", stateLabel + (states.Count > 1 ? " +" + (states.Count - 1) : ""), stateBounds, 0, stateBounds.Width);
        }
        if (card.Tapped) {
            string tapped = "TAPPED";
            int width = Math.Min(nameBounds.Width, canvas.TextWidth(tapped, fontSize, true) + padding * 2);
            var bounds = new Rectangle(nameBounds.Right - width, nameBounds.Top, width, nameBounds.Height);
            nameBounds.Width -= width;
            Text("tapped", tapped, bounds, Gold);
        }
        if (card.Damage > 0) {
            string damage = "DMG " + card.Damage;
            int width = Math.Min(nameBounds.Width, canvas.TextWidth(damage, fontSize, true) + padding * 2);
            var bounds = new Rectangle(nameBounds.Right - width, nameBounds.Top, width, nameBounds.Height);
            nameBounds.Width -= width;
            Text("damage", damage, bounds, Red);
        }
        Text("name", name, nameBounds, new(67, 85, 98), allowShortName: true);
        int next = 1;
        if (CardPresentation.HasCombatStats(card)) {
            string pt = $"{card.Power}/{card.Toughness}", copyText = copies > 1 ? copies + " copies" : "";
            var line = Row(next++);
            float size = fontSize;
            int ptWidth = canvas.TextWidth(pt, size, true) + padding * 2;
            int copyWidth = copyText.Length > 0 ? canvas.TextWidth(copyText, size, true) + padding * 2 : 0;
            // Width is supplied by the real Canvas measurement delegate, never
            // reduced below the font's actual minimum during dense packing.
            var ptBounds = new Rectangle(line.X, line.Y, ptWidth, line.Height);
            Text("pt", pt, ptBounds, card.AttachedTo != null || card.Attachments.Length > 0 ? Gold : new(140, 155, 162));
            if (copies > 1) Text("copies", copyText, new(line.X + ptWidth, line.Y, line.Width - ptWidth, line.Height), Gold);
        } else if (copies > 1) Text("copies", copies + " copies", Row(next++), Gold);
        var counters = CardPresentation.Counters(card);
        if (counters.Length > 0) {
            Text("counter", CounterLabel(card, counters[0].Key, counters[0].Value), Row(next++), new(168, 207, 200));
            if (counters.Length > 1) Text("counter-types", "+" + (counters.Length - 1) + " counter type" + (counters.Length > 2 ? "s" : ""), Row(next), new(168, 207, 200));
        }
    }
}
