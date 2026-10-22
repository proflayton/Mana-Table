namespace Mana.Table;

internal sealed partial class TableApplication
{
    private string? helpTextCache;
    private float helpTextScale;
    private string[] helpTextPages = [];
    private string helpPageText = "";

    private string[] MeasuredHelpPages(string text)
    {
        if (helpTextCache == text && helpTextScale == preferences.TextScale) return helpTextPages;
        var pages = new List<string>();
        int offset = 0;
        while (offset < text.Length) {
            string rest = text[offset..];
            if (canvas.TextSize(rest, .93f, 1155, 80).Y <= 500) { pages.Add(rest); break; }
            int remaining = text.Length - offset, low = 1, high = remaining - 1, fit = 0;
            while (low <= high) {
                int length = low + (high - low) / 2;
                if (canvas.TextSize(text.Substring(offset, length), .93f, 1155, 80).Y <= 500) {
                    fit = length; low = length + 1;
                } else high = length - 1;
            }
            // A single character fits this fixed font and reading area. Keep a
            // progress guard so malformed text can never stall pagination.
            fit = Math.Max(1, fit);
            if (fit < remaining) {
                int boundary = fit;
                while (boundary > 0 && !char.IsWhiteSpace(text[offset + boundary - 1])) boundary--;
                if (boundary > 0) fit = boundary;
                // Retain every character, including whitespace, without splitting
                // a UTF-16 pair or a CRLF line ending between adjacent pages.
                if (char.IsHighSurrogate(text[offset + fit - 1]) && char.IsLowSurrogate(text[offset + fit])) fit = fit > 1 ? fit - 1 : 2;
                if (fit < remaining && text[offset + fit - 1] == '\r' && text[offset + fit] == '\n') fit = fit > 1 ? fit - 1 : 2;
            }
            pages.Add(text.Substring(offset, fit)); offset += fit;
        }
        if (pages.Count == 0) pages.Add("");
        helpTextCache = text; helpTextScale = preferences.TextScale; helpTextPages = pages.ToArray();
        return helpTextPages;
    }
}
