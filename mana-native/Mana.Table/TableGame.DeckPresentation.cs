using Mana.Contracts;

namespace Mana.Table;

internal sealed partial class TableGame
{
    private static Card DeckCardFace(Card card, bool otherFace) => otherFace && card.OtherFace is { } back ? card with {
        Name = back.Name, Type = back.Type, Text = back.OracleText, ManaCost = back.ManaCost,
        Power = back.Power, Toughness = back.Toughness, ArtName = back.ArtName, ArtFace = back.ArtFace
    } : card;

    // One presentation of main-deck composition for both the builder and lobby.
    private void DrawDeckComposition(DeckDetails deck, int x, int y, int width)
    {
        var main = deck.Entries.Where(e => e.Section == "Main").ToArray();
        var spells = main.Where(e => !e.Card.Type.Contains("Land")).ToArray();
        int lands = main.Where(e => e.Card.Type.Contains("Land")).Sum(e => e.Quantity);
        int creatures = main.Where(e => e.Card.Type.Contains("Creature")).Sum(e => e.Quantity);
        double average = spells.Sum(e => e.ManaValue * e.Quantity) / (double)Math.Max(1, spells.Sum(e => e.Quantity));
        canvas.Text($"{lands} lands · {creatures} creatures · Avg. MV {average:0.0}", x, y, Ink, .65f, width, 1);
        var curve = Enumerable.Range(0, 7).Select(i => spells.Where(e => Math.Min(6, e.ManaValue) == i).Sum(e => e.Quantity)).ToArray();
        int step = width / 7;
        for (int i = 0; i < curve.Length; i++) {
            int left = x + 4 + i * step, height = (int)(36f * curve[i] / Math.Max(1, curve.Max()));
            canvas.Fill(new(left, y + 65 - height, step - 18, height), new(95, 160, 185));
            canvas.Text(curve[i].ToString(), left + (step - 36) / 2, y + 48 - height, Muted, .47f, 35, 1);
            canvas.Text(i == 6 ? "6+" : i.ToString(), left + (step - 30) / 2, y + 69, Muted, .51f, 36, 1);
        }
    }
}
