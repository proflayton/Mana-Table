using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private sealed record BattlefieldReaderChip(string Text, Color Accent);
    private Rectangle battlefieldReaderStateBounds;
    private int battlefieldReaderStateScroll, battlefieldReaderStateScrollMax;
    private string battlefieldReaderStateBinding = "";

    private bool IsStudyBattlefieldReader(PresentedCard entry) => TableStudyEnabled && entry.Zone == "Battlefield"
        && entry.Focused && entry.ChoiceSlot < 0 && !entry.Card.FaceDown && entry.Display.Size.X >= 300
        && CardContext.CanRead(entry);

    private static string BattlefieldReaderBinding(PresentedCard entry) => entry.Id + "\n" + entry.Zone;

    private void PaintStudyBattlefieldReader(Card card, PresentedCard entry)
    {
        PaintCard(card, entry.Display, false, IsCardActionable(card), true);
        var face = entry.Display.Bounds;
        if (CardPresentation.HasCombatStats(card)) {
            // Cover the printed stat box, keeping the complete art and rules.
            // Current P/T is supplied by the engine; counters are not added here.
            var statPose = entry.Display with { Center = entry.Display.Point(.87f, .9195f),
                Size = entry.Display.Size * new Vector2(.176f, .069f) };
            var bounds = statPose.Bounds;
            string stats = CurrentStats(card);
            float size = 1.05f;
            while (size > .4f && canvas.TextWidth(stats, size, true) > bounds.Width - 10) size -= .025f;
            canvas.Surface(null, statPose, new(16, 36, 36));
            canvas.CardFrame(statPose, new(172, 209, 193), 1);
            canvas.CenterText(stats, bounds, Ink, size, true);
        }
        if (CounterMarker(card).Length > 0) {
            int width = CounterMarkerWidth(card, .6f) + 2;
            var badge = new Rectangle(face.Left + 12, face.Bottom - 12, width, 24);
            canvas.Panel(badge, new(16, 36, 36), new(118, 174, 158), 4);
            PaintCounterMarker(card, badge, .6f);
            ExtendCardHit(card, badge);
        }
        PaintBattlefieldReaderState(card, entry);
    }

    private void PaintBattlefieldReaderState(Card card, PresentedCard entry)
    {
        battlefieldReaderStateBounds = Rectangle.Empty;
        battlefieldReaderStateScrollMax = 0;
        string binding = (match?.Id ?? "") + "\n" + BattlefieldReaderBinding(entry);
        if (binding != battlefieldReaderStateBinding) {
            battlefieldReaderStateBinding = binding;
            battlefieldReaderStateScroll = 0;
        }
        var chips = CardPresentation.Counters(card).Select(counter => new BattlefieldReaderChip(
            counter.Value + " " + counter.Key + (counter.Value == 1 ? " counter" : " counters"), new(154, 224, 203))).ToList();
        if (card.Damage > 0) chips.Add(new(card.Damage + " damage marked", Red));
        if (card.PhasedOut) chips.Add(new("Phased out", AbilityColor("phased out")));
        var abilityLabels = CardPresentation.AbilityLabels(card);
        if (card.Tapped && !abilityLabels.Contains("Tapped", StringComparer.OrdinalIgnoreCase))
            chips.Add(new("Tapped", Gold));
        if (card.Sick && CardPresentation.HasCombatStats(card)
            && !abilityLabels.Contains("Summoning sick", StringComparer.OrdinalIgnoreCase))
            chips.Add(new("Summoning sick", Blue));
        chips.AddRange(abilityLabels.Select(label => new BattlefieldReaderChip(label, Blue)));
        if (chips.Count == 0) return;

        var face = entry.Display.Bounds;
        int top = face.Bottom + 16, available = Math.Min(84, 766 - top);
        if (available < 24) return;
        const float size = .62f;
        const int rowPitch = 28, chipHeight = 24;
        int x = 0, row = 0;
        var positions = new List<(BattlefieldReaderChip Chip, int X, int Row, int Width)>();
        foreach (var chip in chips) {
            int width = Math.Min(face.Width, canvas.TextWidth(chip.Text, size, true) + 18);
            if (x > 0 && x + width > face.Width) { x = 0; row++; }
            positions.Add((chip, x, row, width)); x += width + 6;
        }
        int contentHeight = (row + 1) * rowPitch - 4;
        int height = Math.Min(available, contentHeight);
        battlefieldReaderStateBounds = new(face.Left, top, face.Width, height);
        battlefieldReaderStateScrollMax = Math.Max(0, contentHeight - height);
        battlefieldReaderStateScroll = Math.Clamp(battlefieldReaderStateScroll, 0, battlefieldReaderStateScrollMax);
        canvas.Line(new(face.Left + 20, face.Bottom + 2), new(face.Left + 20, top), new Color(118, 174, 158) * .65f, 1.5f);
        foreach (var item in positions) {
            int y = top + item.Row * rowPitch - battlefieldReaderStateScroll;
            if (y < top || y + chipHeight > top + height) continue;
            var bounds = new Rectangle(face.Left + item.X, y, item.Width, chipHeight);
            canvas.Panel(bounds, new(15, 30, 34), item.Chip.Accent * .55f, 4);
            canvas.CenterText(item.Chip.Text, bounds, item.Chip.Accent, size, true);
        }
        if (battlefieldReaderStateScrollMax > 0) {
            int thumbHeight = Math.Max(16, height * height / contentHeight);
            int thumbTop = top + (height - thumbHeight) * battlefieldReaderStateScroll / battlefieldReaderStateScrollMax;
            canvas.Rounded(new(face.Right + 3, top, 3, height), Muted * .18f, 1);
            canvas.Rounded(new(face.Right + 3, thumbTop, 3, thumbHeight), Blue * .7f, 1);
        }
        // Read-only state must not click a different battlefield beneath it.
        hits.Add(new("battlefield-reader-state", Scope, battlefieldReaderStateBounds, () => { }));
    }

    private bool ScrollBattlefieldReaderState(int direction)
    {
        if (cardPresentation.Pinned is not { } entry || !IsStudyBattlefieldReader(entry)
            || battlefieldReaderStateBinding != (match?.Id ?? "") + "\n" + BattlefieldReaderBinding(entry)
            || !battlefieldReaderStateBounds.Contains(pointer)) return false;
        battlefieldReaderStateScroll = Math.Clamp(battlefieldReaderStateScroll - direction * 28, 0, battlefieldReaderStateScrollMax);
        return true;
    }
}
