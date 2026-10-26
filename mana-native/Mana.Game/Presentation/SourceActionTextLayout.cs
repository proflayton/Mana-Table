using System.Globalization;
using System.Text.RegularExpressions;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table.Presentation;

internal sealed record SourceActionTextRun(string Text, string? Symbol, int X, int Width);

/// <summary>Display tokens only. The original offer text and command remain untouched.</summary>
internal sealed record SourceActionTextLayout(string Text, IReadOnlyList<IReadOnlyList<SourceActionTextRun>> Lines,
    int LineHeight, int SymbolSide, int Width)
{
    public int Height => Lines.Count * LineHeight;

    public static SourceActionTextLayout Measure(DrawList canvas, string text, float font, int width, bool bold)
    {
        width = Math.Max(1, width);
        int lineHeight = canvas.TextSize("Ag", font, 2000, 80, bold).Y;
        int symbolSide = Math.Max(12, lineHeight - 4), symbolAdvance = symbolSide + 3;
        int space = Math.Max(1, canvas.TextWidth("M M", font, bold) - canvas.TextWidth("MM", font, bold));
        int Advance(string value) => Math.Max(1, canvas.TextWidth(value, font, bold) - 6);
        var lines = new List<IReadOnlyList<SourceActionTextRun>>();
        var current = new List<SourceActionTextRun>();
        int x = 0, measuredWidth = 0;
        void FinishLine()
        {
            measuredWidth = Math.Max(measuredWidth, x);
            lines.Add(current.AsReadOnly()); current = []; x = 0;
        }
        foreach (Match word in Regex.Matches(text, @"\S+")) {
            var parts = new List<(string Text, string? Symbol)>();
            int start = 0;
            foreach (Match token in Regex.Matches(word.Value, @"\{([^{}]+)\}")) {
                string symbol = token.Groups[1].Value;
                if (SymbolCrop(symbol).IsEmpty) continue;
                if (token.Index > start) parts.Add((word.Value[start..token.Index], null));
                parts.Add((token.Value, symbol)); start = token.Index + token.Length;
            }
            if (start < word.Length) parts.Add((word.Value[start..], null));
            int wordWidth = parts.Sum(part => part.Symbol == null ? Advance(part.Text) : symbolAdvance);
            if (x > 0 && x + space + wordWidth > width) FinishLine();
            else if (x > 0) x += space;
            foreach (var part in parts) {
                if (part.Symbol != null) {
                    if (x > 0 && x + symbolAdvance > width) FinishLine();
                    current.Add(new(part.Text, part.Symbol, x, symbolSide)); x += symbolAdvance;
                    continue;
                }
                string remaining = part.Text;
                while (remaining.Length > 0) {
                    int available = width - x;
                    if (available <= 0) { FinishLine(); available = width; }
                    int length = remaining.Length;
                    if (Advance(remaining) > available) {
                        // Long prose can wrap; a recognized symbol is always one atom.
                        var boundaries = StringInfo.ParseCombiningCharacters(remaining);
                        int lo = 1, hi = boundaries.Length;
                        while (lo < hi) {
                            int mid = (lo + hi + 1) / 2;
                            int end = mid == boundaries.Length ? remaining.Length : boundaries[mid];
                            if (Advance(remaining[..end]) <= available) lo = mid; else hi = mid - 1;
                        }
                        length = lo == boundaries.Length ? remaining.Length : boundaries[lo];
                    }
                    string fragment = remaining[..length];
                    int advance = Advance(fragment);
                    current.Add(new(fragment, null, x, advance + 6)); x += advance;
                    remaining = remaining[length..];
                    if (remaining.Length > 0) FinishLine();
                }
            }
        }
        if (current.Count > 0 || lines.Count == 0) FinishLine();
        return new(text, lines.AsReadOnly(), lineHeight, symbolSide, measuredWidth);
    }

    // Exact sprite_manaicons.png / Forge FSkinProp coordinates. Unknown tokens
    // remain literal prose rather than acquiring a guessed resource meaning.
    public static Rectangle SymbolCrop(string symbol)
    {
        var crop = symbol switch {
            "W" => new Rectangle(412, 84, 80, 80), "U" => new(330, 84, 80, 80),
            "B" => new(166, 2, 80, 80), "R" => new(330, 2, 80, 80),
            "G" => new(166, 84, 80, 80), "C" => new(248, 2, 80, 80),
            "T" => new(166, 412, 80, 80), "Q" => new(248, 412, 80, 80),
            "S" => new(412, 2, 80, 80), "X" => new(248, 576, 80, 80),
            "Y" => new(330, 576, 80, 80), "Z" => new(412, 576, 80, 80),
            "B/G" => new(166, 166, 80, 80), "B/R" => new(248, 166, 80, 80),
            "G/U" => new(166, 248, 80, 80), "G/W" => new(248, 248, 80, 80),
            "R/G" => new(166, 330, 80, 80), "R/W" => new(248, 330, 80, 80),
            "U/B" => new(330, 330, 80, 80), "U/R" => new(412, 330, 80, 80),
            "W/B" => new(330, 412, 80, 80), "W/U" => new(412, 412, 80, 80),
            "2/B" => new(166, 494, 80, 80), "2/G" => new(248, 494, 80, 80),
            "2/R" => new(330, 494, 80, 80), "2/U" => new(412, 494, 80, 80), "2/W" => new(166, 576, 80, 80),
            "W/P" => new(412, 248, 80, 80), "U/P" => new(330, 248, 80, 80),
            "B/P" => new(248, 84, 80, 80), "R/P" => new(412, 166, 80, 80), "G/P" => new(330, 166, 80, 80),
            _ => Rectangle.Empty
        };
        if (!crop.IsEmpty) return crop;
        if (!int.TryParse(symbol, NumberStyles.None, CultureInfo.InvariantCulture, out int number)
            || number is < 0 or > 20 || number.ToString(CultureInfo.InvariantCulture) != symbol) return Rectangle.Empty;
        return number <= 15 ? new(2 + number % 2 * 82, 2 + number / 2 * 82, 80, 80)
            : new(2 + (number - 16) * 82, 658, 80, 80);
    }
}
