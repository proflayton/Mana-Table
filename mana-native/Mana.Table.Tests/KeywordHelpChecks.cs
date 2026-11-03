using System.Globalization;
using Mana.Contracts;
using Mana.Renderer;
using Mana.Table.Presentation;

namespace Mana.Table;

internal static class KeywordHelpChecks
{
    public static void Run(Action<bool, string> check)
    {
        static PublicState Ability(string id, string label, string detail = "Engine-supplied reminder.") =>
            new(id, label, "ability", null, null, detail);
        var firstStrike = Ability("first", "First strike");
        var ward = Ability("ward-blue", "Ward {2}{U}");
        const string text = "\U0001F30A A\u0301  Protection \tfrom blue; Ward {2}{U}. Ward {3}. FIRST\nSTRIKE.";
        var protection = Ability("blue", "Protection from blue");
        var matches = KeywordTextHelp.Find(text, [firstStrike, ward, protection]);
        check(matches.Count == 3 && matches.Select(match => match.Description.Id).SequenceEqual(["blue", "ward-blue", "first"])
            && matches[0].Start == text.IndexOf("Protection", StringComparison.Ordinal)
            && text.Substring(matches[0].Start, matches[0].Length) == "Protection \tfrom blue"
            && text.Substring(matches[2].Start, matches[2].Length) == "FIRST\nSTRIKE",
            "Keyword help retains original UTF16 offsets and full case-insensitive phrases across whitespace, including a supplementary-character prefix");
        check(matches.Single(match => match.Description == ward) is { Length: 11 }
            && KeywordTextHelp.Find("Ward {2}{U}; Ward {3}.", [Ability("ward-two", "Ward {2}")]).Count == 0,
            "Parameterized keyword help keeps the exact supplied cost and never substitutes a partial or different mana qualification");

        var flying = Ability("flying", "Flying");
        const string boundaries = "Flyingness unflying flying_2 \u03B1Flying Flying\u0301 \U00010400Flying Flying2 (FLYING), Flying.";
        var wholeWords = KeywordTextHelp.Find(boundaries, [flying]);
        check(wholeWords.Count == 2 && wholeWords[0].Start == boundaries.IndexOf("(FLYING)", StringComparison.Ordinal) + 1
            && wholeWords[1].Start == boundaries.LastIndexOf("Flying", StringComparison.Ordinal),
            "Keyword help excludes substrings, Unicode letter and combining-mark continuations, numeric suffixes and identifiers");
        var repeated = flying with { Id = "second-flying" };
        var otherReminder = flying with { Detail = "A separately supplied reminder." };
        var instances = KeywordTextHelp.Find("Flying, flying.", [flying, flying with { }, repeated, otherReminder]);
        check(instances.Count == 6 && instances.Take(3).Select(match => match.Description).SequenceEqual([flying, repeated, otherReminder])
            && instances.Skip(3).All(match => match.Start == 8),
            "Exact duplicate descriptions collapse while separate engine instances and distinct details survive on every occurrence");
        var hexproof = Ability("hexproof", "Hexproof");
        var fromRed = Ability("hexproof-red", "Hexproof from red");
        var anotherFromRed = fromRed with { Id = "another-red", Detail = "Another qualified instance." };
        const string qualifiedText = "Hexproof from red, then Hexproof.";
        var qualified = KeywordTextHelp.Find(qualifiedText, [hexproof, fromRed, anotherFromRed, Ability("inner", "from red")]);
        check(qualified.Count == 3 && qualified.Take(2).Select(match => match.Description).SequenceEqual([fromRed, anotherFromRed])
            && qualified.Take(2).All(match => match.Start == 0 && match.Length == fromRed.Label.Length)
            && qualified[2].Description == hexproof && qualified[2].Start == qualifiedText.LastIndexOf("Hexproof", StringComparison.Ordinal),
            "Full engine-supplied qualifiers suppress strictly contained labels, preserve distinct same-range instances and leave a later unqualified occurrence readable");
        var unfamiliar = Ability("future", "Quantum weave {C}", "The exact future engine explanation.");
        var future = KeywordTextHelp.Find("Flying. Quantum weave {C}.", [unfamiliar]);
        check(future.Count == 1 && ReferenceEquals(future[0].Description, unfamiliar)
            && KeywordTextHelp.Find("Flying. Quantum weave {C}.", []).Count == 0,
            "Unfamiliar supplied abilities work without a glossary while unprojected words acquire no invented explanation");
        check(KeywordTextHelp.Find("Flying", [flying with { Detail = " \t" }, flying with { Label = " " },
                flying with { Kind = "counter" }, flying with { Kind = "status" }]).Count == 0
            && KeywordTextHelp.Find("", [flying]).Count == 0,
            "Only engine ability labels with real details can produce help; blank labels, missing reminders and other state kinds stay plain");

        using var drawing = new DrawList();
        var inline = SourceActionTextLayout.Measure(drawing, "{unknown}{U}: Add {C}.", .68f, 1000, false);
        check(inline.Lines.Count == 1 && inline.Lines[0].Select(run => (run.Text, run.Symbol, run.SourceStart)).SequenceEqual([
                ("{unknown}", null, 0), ("{U}", "U", 9), (":", null, 12), ("Add", null, 14), ("{C}", "C", 18), (".", null, 21)]),
            "Inline rules preserve punctuation and unknown literal prefixes beside known symbols without changing the existing unwrapped run structure");
        const string laidOut = "  \U0001F30A A\u0301 Protection \tfrom blue; Ward {2}{U}, {unfamiliar}First strike.  ";
        var layout = SourceActionTextLayout.Measure(drawing, laidOut, .68f, 42, false);
        var runs = layout.Lines.SelectMany(line => line).ToArray();
        var boundariesUtf16 = StringInfo.ParseCombiningCharacters(laidOut).Append(laidOut.Length).ToHashSet();
        check(layout.Text == laidOut && runs.Length > 10 && layout.Lines.Count > 3
            && runs.All(run => run.SourceStart >= 0 && laidOut.Substring(run.SourceStart, run.Text.Length) == run.Text)
            && runs.Select(run => run.SourceStart).SequenceEqual(runs.Select(run => run.SourceStart).Order())
            && runs.All(run => boundariesUtf16.Contains(run.SourceStart) && boundariesUtf16.Contains(run.SourceStart + run.Text.Length)),
            "Wrapped rules runs retain exact monotonic source ranges without splitting supplementary characters or combining sequences");
        check(runs.Where(run => run.Symbol != null).Select(run => (run.Text, run.SourceStart, run.Width)).SequenceEqual([
                ("{2}", laidOut.IndexOf("{2}", StringComparison.Ordinal), layout.SymbolSide),
                ("{U}", laidOut.IndexOf("{U}", StringComparison.Ordinal), layout.SymbolSide)])
            && runs.Where(run => run.SourceStart >= laidOut.IndexOf("{unfamiliar}", StringComparison.Ordinal)).All(run => run.Symbol == null)
            && string.Concat(runs.Select(run => run.Text)) == string.Concat(laidOut.Where(character => !char.IsWhiteSpace(character))),
            "Recognized mana atoms keep original offsets and sprite sizes while unfamiliar tokens remain complete literal text through wrapping");
        var wrappedMatches = KeywordTextHelp.Find(laidOut, [protection, ward, firstStrike]);
        check(wrappedMatches.Count == 3 && wrappedMatches.All(match => layout.Lines.Count(line => line.Any(run =>
                run.SourceStart < match.Start + match.Length && run.SourceStart + run.Text.Length > match.Start)) > 1)
            && new SourceActionTextRun("Legacy", null, 0, 40).SourceStart == -1,
            "Multiword and mana-qualified keyword ranges span every wrapped run while legacy text-run construction remains compatible");
    }
}
