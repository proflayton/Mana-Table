using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private BattlefieldLabelMetrics MeasureDenseBattlefieldLabel(Card card, int copies, bool miniature)
    {
        float size = miniature ? .3f : .375f;
        const int padding = 8;
        var counters = CardPresentation.Counters(card);
        bool plain = !CardPresentation.HasCombatStats(card) && card.Power == null && card.Toughness == null
            && counters.Length == 0 && card.Damage == 0 && (card.States?.Length ?? card.CombatKeywords.Length) == 0 && !card.PhasedOut;
        int width = miniature ? 48 : plain ? 56 : 96;
        int CopyWidth() => copies > 1 ? canvas.TextWidth(copies + " copies", size, true) + padding : 0;
        if (CardPresentation.HasCombatStats(card))
            width = Math.Max(width, canvas.TextWidth($"{card.Power}/{card.Toughness}", size, true) + padding + CopyWidth());
        else width = Math.Max(width, CopyWidth());
        if (counters.Length > 0)
            width = Math.Max(width, canvas.TextWidth(CounterLabel(card, counters[0].Key, counters[0].Value), size, true) + padding);
        if (counters.Length > 1)
            width = Math.Max(width, canvas.TextWidth("+" + (counters.Length - 1) + " counter type" + (counters.Length > 2 ? "s" : ""), size, true) + padding);
        int header = (card.States?.Length ?? card.CombatKeywords.Length) > 0 || card.PhasedOut || card.Sick && CardPresentation.HasCombatStats(card) ? 24 : 0;
        if (card.Tapped) header += canvas.TextWidth("TAPPED", size, true) + padding;
        if (card.Damage > 0) header += canvas.TextWidth("DMG " + card.Damage, size, true) + padding;
        // State indicators share the identity row; reserve both their complete
        // values and a useful portion of the name when several apply together.
        if (header > 0) width = Math.Max(width, header + 32);
        int rowHeight = Math.Max(miniature ? 16 : 18, canvas.TextSize("Ag 0123456789+/", size, 2000, 1, true).Y + 2);
        return new(width, rowHeight, header);
    }
}
