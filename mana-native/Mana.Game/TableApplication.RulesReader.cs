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
    private string? rulesReaderId, rulesReaderGame;
    private bool rulesReaderText;
    private double rulesReaderTurnedAt = double.NegativeInfinity;
    private const double RulesFlipSeconds = .20;
    private (string Name, string Cost, string Type, string Text, float Scale)? rulesLayoutKey;
    private CardRulesLayout? rulesLayout;

    private void ClearRulesReader()
    {
        rulesReaderId = rulesReaderGame = null;
        rulesReaderText = false; rulesReaderTurnedAt = double.NegativeInfinity;
        rulesLayoutKey = null; rulesLayout = null;
    }

    private CardRulesLayout RulesLayout(Card card)
    {
        var key = (card.Name, card.ManaCost, card.Type, card.Text, canvas.TextScale);
        if (rulesLayoutKey != key) { rulesLayout = CardRulesLayout.Measure(canvas, card); rulesLayoutKey = key; }
        return rulesLayout!;
    }

    private Vector2? RulesReaderSize()
    {
        if (!rulesReaderText || rulesReaderGame != match?.Id || rulesReaderId != readingId || rulesLayout is not { } layout) return null;
        return new(layout.Width, layout.Height);
    }

    private void FlipReaderText(string id)
    {
        if (matchPresentation.Cards.Pinned is not { } entry || entry.Id != id || entry.Card.FaceDown || !CardContext.CanRead(entry)) return;
        if (rulesReaderId != id || rulesReaderGame != match?.Id) ClearRulesReader();
        rulesReaderId = id; rulesReaderGame = match?.Id;
        rulesReaderText = !rulesReaderText; rulesReaderTurnedAt = now;
        if (rulesReaderText) RulesLayout(entry.Card);
    }

    private void DrawRulesReaderControl(PresentedCard entry, Rectangle face)
    {
        if (!entry.Focused || entry.Card.FaceDown || !CardContext.CanRead(entry)) return;
        string label = rulesReaderId == entry.Id && rulesReaderText ? "Artwork" : "Rules";
        Button("read-card-rules", label, face.Right - 112, face.Bottom + 6, 108,
            new MatchIntent.FlipReaderText(entry.Id), height: 30);
    }

    private bool DrawRulesReader(PresentedCard entry)
    {
        if (!entry.Focused || rulesReaderId != entry.Id || rulesReaderGame != match?.Id
            || entry.Card.FaceDown || !CardContext.CanRetainPinned(entry) || entry.Display.Size.X < 300) return false;
        float progress = preferences.ReducedMotion ? 1 : Math.Clamp((float)((now - rulesReaderTurnedAt) / RulesFlipSeconds), 0, 1);
        if (!rulesReaderText && progress >= 1) return false;
        bool text = progress >= .5f ? rulesReaderText : !rulesReaderText;
        float width = Math.Max(.025f, Math.Abs(MathF.Cos(progress * MathF.PI)));
        var pose = entry.Display with { Size = entry.Display.Size * new Vector2(width, 1) };
        if (width < .35f) {
            canvas.Surface(null, pose, Gold);
        } else if (text) PaintRulesFace(entry.Card, pose);
        else PaintCard(entry.Card, pose, false, false, true);
        if (IsStudyBattlefieldReader(entry)) PaintBattlefieldReaderState(entry.Card, entry);
        return true;
    }

    private void PaintRulesFace(Card card, Pose pose, CardRulesLayout? prepared = null, Card? illustration = null)
    {
        var face = pose.Bounds;
        var layout = prepared ?? RulesLayout(card);
        var ink = new Color(30, 35, 35);
        canvas.Surface(null, pose, new(224, 220, 200));
        canvas.CardFrame(pose, Gold, 2);
        // The narrow phase of the turn shows the card edge. Text appears on the
        // settled face; it is never squeezed sideways through the flip.
        if (face.Width < layout.Width - 4) return;
        int x = face.Left + 20, y = face.Top + 16;
        void Paint(SourceActionTextLayout text, float font, bool bold) {
            for (int line = 0; line < text.Lines.Count; line++)
                PaintSourceActionLine(text, line, x, y + line * text.LineHeight, ink, font, bold);
            y += text.Height;
        }
        Paint(layout.Name, CardRulesLayout.NameFont, true);
        if (layout.IllustrationHeight > 0) {
            y += 12;
            var art = new Rectangle(x, y, face.Width - 40, layout.IllustrationHeight);
            if (illustration != null && resources.Card(illustration) is { } image) {
                var crop = new Rectangle((int)(image.Width * .12f), (int)(image.Height * .14f),
                    (int)(image.Width * .76f), (int)(image.Height * .38f));
                canvas.Sprite(image, art.Center.ToVector2(), art.Size.ToVector2(), 0, Color.White, crop);
            } else canvas.Fill(art, new(43, 58, 64));
            y += layout.IllustrationHeight;
        }
        if (card.ManaCost.Length > 0) { y += 6; Paint(layout.Mana, CardRulesLayout.ManaFont, false); }
        y += 8; Paint(layout.Type, CardRulesLayout.TypeFont, true);
        y += 8; canvas.Line(new(x, y), new(face.Right - 20, y), new(114, 115, 99), 1); y += 12;
        foreach (var paragraph in layout.Paragraphs) { Paint(paragraph, CardRulesLayout.RulesFont, false); y += 12; }
        var statsCard = illustration ?? card;
        if (CardPresentation.HasCombatStats(statsCard)) {
            var stats = new Rectangle(face.Right - 112, face.Bottom - 45, 92, 32);
            canvas.Rounded(stats, new(24, 47, 45), 4);
            canvas.CenterText(CurrentStats(statsCard), stats, Ink, .94f, true);
        }
    }
}
