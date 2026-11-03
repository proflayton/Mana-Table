using System.Globalization;
using System.Text;
using Mana.Contracts;

namespace Mana.Table.Presentation;

/// <summary>A range in the original UTF-16 text, retaining its exact engine description.</summary>
internal sealed record KeywordTextMatch(PublicState Description, int Start, int Length);

/// <summary>Finds supplied public ability labels; it neither infers abilities nor supplies rules.</summary>
internal static class KeywordTextHelp
{
    public static IReadOnlyList<KeywordTextMatch> Find(string text, IEnumerable<PublicState> descriptions)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(descriptions);
        var matches = new List<KeywordTextMatch>();
        foreach (var description in descriptions.Distinct()) {
            if (description.Kind != "ability" || string.IsNullOrWhiteSpace(description.Label)
                || string.IsNullOrWhiteSpace(description.Detail)) continue;
            string[] words = description.Label.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            int search = 0;
            while (search < text.Length) {
                int start = text.IndexOf(words[0], search, StringComparison.OrdinalIgnoreCase);
                if (start < 0) break;
                search = start + words[0].Length;
                if (WordBefore(text, start)) continue;
                int end = search;
                bool complete = true;
                foreach (string word in words.Skip(1)) {
                    int whitespace = end;
                    while (end < text.Length && char.IsWhiteSpace(text[end])) end++;
                    if (whitespace == end || !text.AsSpan(end).StartsWith(word, StringComparison.OrdinalIgnoreCase)) {
                        complete = false; break;
                    }
                    end += word.Length;
                }
                if (!complete || WordAt(text, end)) continue;
                // Adjacent parameter atoms belong to one qualified label. A
                // shorter supplied cost must not explain part of a longer one.
                if (end < text.Length && text[end - 1] == '}' && text[end] == '{') continue;
                matches.Add(new(description, start, end - start));
            }
        }
        // Prefer the full supplied qualification over a label inside it. The
        // stable order still retains every distinct instance on an exact range.
        var specific = new List<KeywordTextMatch>();
        int coveringStart = -1, coveringEnd = -1;
        foreach (var match in matches.OrderBy(match => match.Start).ThenByDescending(match => match.Length)) {
            int end = match.Start + match.Length;
            if (end <= coveringEnd && (match.Start != coveringStart || end != coveringEnd)) continue;
            specific.Add(match);
            if (end > coveringEnd) { coveringStart = match.Start; coveringEnd = end; }
        }
        return specific.AsReadOnly();
    }

    private static bool WordBefore(string text, int start)
    {
        if (start == 0) return false;
        int previous = start - 1;
        if (char.IsLowSurrogate(text[previous]) && previous > 0 && char.IsHighSurrogate(text[previous - 1])) previous--;
        return WordAt(text, previous);
    }

    private static bool WordAt(string text, int index) => index < text.Length
        && Rune.TryGetRuneAt(text, index, out var rune) && Rune.GetUnicodeCategory(rune) is
            UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.LetterNumber
            or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.OtherNumber or UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark or UnicodeCategory.ConnectorPunctuation;
}
