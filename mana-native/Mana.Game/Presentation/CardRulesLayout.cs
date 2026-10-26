using Mana.Contracts;
using Mana.Renderer;

namespace Mana.Table.Presentation;

// Readable text is measured before requesting reader geometry. Full rules and
// complete names never acquire ellipses, pages or a smaller font to fit a print.
internal sealed record CardRulesLayout(int Width, int Height, SourceActionTextLayout Name,
    SourceActionTextLayout Mana, SourceActionTextLayout Type, IReadOnlyList<SourceActionTextLayout> Paragraphs)
{
    internal const float NameFont = .96f, ManaFont = .78f, TypeFont = .70f, RulesFont = .82f;
    internal static CardRulesLayout Measure(DrawList canvas, Card card)
    {
        CardRulesLayout At(int width) {
            int inner = width - 40;
            var name = SourceActionTextLayout.Measure(canvas, card.Name, NameFont, inner, true);
            var mana = SourceActionTextLayout.Measure(canvas, card.ManaCost, ManaFont, inner, false);
            var type = SourceActionTextLayout.Measure(canvas, card.Type, TypeFont, inner, true);
            var paragraphs = card.Text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(text => SourceActionTextLayout.Measure(canvas, text, RulesFont, inner, false)).ToArray();
            int height = 28 + name.Height + (card.ManaCost.Length == 0 ? 0 : mana.Height + 6)
                + type.Height + 26 + paragraphs.Sum(paragraph => paragraph.Height + 12) + 60;
            return new(width, Math.Max(574, height), name, mana, type, Array.AsReadOnly(paragraphs));
        }
        var layout = At(410);
        while (layout.Height > 690 && layout.Width < 1130) layout = At(layout.Width + 90);
        return layout;
    }
}
