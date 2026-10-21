namespace Mana.Renderer;

/// <summary>Pages complete text against the same measured width, height and painted line limit.</summary>
public static class TextPagination
{
    public static string[] Pages(string text, Func<string, bool> fits)
    {
        var pages = new List<string>();
        int offset = 0;
        while (offset < text.Length) {
            string rest = text[offset..];
            if (fits(rest)) { pages.Add(rest); break; }
            int low = 1, high = rest.Length - 1, fit = 0;
            while (low <= high) {
                int length = low + (high - low) / 2;
                if (fits(rest[..length])) { fit = length; low = length + 1; }
                else high = length - 1;
            }
            if (fit == 0) throw new InvalidOperationException("The text region cannot contain a character at this font size.");
            // Keep result rows together when a complete row fits; otherwise
            // preserve every character of a long paragraph over several pages.
            int line = rest.LastIndexOf('\n', fit - 1, fit);
            int boundary = line > 0 ? line + 1 : fit;
            if (line <= 0) {
                while (boundary > 0 && !char.IsWhiteSpace(rest[boundary - 1])) boundary--;
                if (boundary == 0) boundary = fit;
            }
            if (boundary < rest.Length && char.IsHighSurrogate(rest[boundary - 1]) && char.IsLowSurrogate(rest[boundary])) boundary--;
            if (boundary == 0) boundary = fit;
            pages.Add(rest[..boundary]); offset += boundary;
        }
        return pages.Count == 0 ? [""] : pages.ToArray();
    }
}
