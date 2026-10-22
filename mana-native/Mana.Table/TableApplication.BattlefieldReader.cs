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
        int x = face.Right + CardPresentationController.StudyDetailsGap;
        int width = CardPresentationController.StudyDetailsWidth;
        int top = face.Top + 16, bottom = Math.Min(face.Bottom, x >= 1240 ? 538 : 760);
        int available = bottom - top;
        if (available < 44) return;
        const float size = .86f;
        const int inset = 14, gap = 5;
        int textWidth = width - inset * 2;
        int lineHeight = canvas.TextSize("Ag", size, textWidth, 80, true).Y;
        int contentHeight = 0;
        var positions = new List<(BattlefieldReaderChip Chip, string[] Lines, int Y, int Height)>();
        foreach (var chip in chips) {
            var lines = canvas.TextPages(chip.Text, size, textWidth, lineHeight, 80, true);
            int rowHeight = Math.Max(48, lines.Length * lineHeight + inset * 2);
            positions.Add((chip, lines, contentHeight, rowHeight));
            contentHeight += rowHeight + gap;
        }
        contentHeight -= gap;
        int height = Math.Min(available, contentHeight);
        battlefieldReaderStateBounds = new(x, top, width, height);
        battlefieldReaderStateScrollMax = Math.Max(0, contentHeight - height);
        battlefieldReaderStateScroll = Math.Clamp(battlefieldReaderStateScroll, 0, battlefieldReaderStateScrollMax);
        foreach (var item in positions) {
            int y = top + item.Y - battlefieldReaderStateScroll;
            var bounds = Rectangle.Intersect(new(x, y, width, item.Height), battlefieldReaderStateBounds);
            if (bounds.IsEmpty) continue;
            canvas.Rounded(bounds, new(13, 25, 31), 5);
            if (bounds.Height > 16) canvas.Fill(new(bounds.Left, bounds.Top + 8, 3, bounds.Height - 16), item.Chip.Accent * .75f);
            for (int line = 0; line < item.Lines.Length; line++) {
                int lineY = y + inset + line * lineHeight;
                if (lineY >= top && lineY + lineHeight <= top + height)
                    canvas.Text(item.Lines[line], x + inset, lineY, item.Chip.Accent, size, textWidth, 1, true);
            }
        }
        if (battlefieldReaderStateScrollMax > 0) {
            int thumbHeight = Math.Max(16, height * height / contentHeight);
            int thumbTop = top + (height - thumbHeight) * battlefieldReaderStateScroll / battlefieldReaderStateScrollMax;
            canvas.Rounded(new(x + width + 3, top, 3, height), Muted * .18f, 1);
            canvas.Rounded(new(x + width + 3, thumbTop, 3, thumbHeight), Blue * .7f, 1);
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
