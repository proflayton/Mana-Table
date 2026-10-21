using Mana.Contracts;

namespace Mana.Magic;

public sealed record DeckSourceOutcome(string Card, string Section, int Before, int After);
public static class DeckExperience
{
    public static DeckSourceOutcome[] SourceRows(DeckSourcePreview preview, string choice, bool copy) => copy
        ? preview.Cards.GroupBy(c => (c.Card.Card.Name, c.Section)).Select(g => new DeckSourceOutcome(g.Key.Name, g.Key.Section,
            preview.Changes.FirstOrDefault(c => c.Card == g.Key.Name && c.Section == g.Key.Section)?.Local ?? g.Sum(c => c.Quantity),
            g.Sum(c => c.Quantity))).ToArray()
        : preview.Changes.Select(c => new DeckSourceOutcome(c.Card, c.Section, c.Local, SourceQuantity(c, choice))).ToArray();
    public static string EligibilityLabel(DeckDetails deck) => deck.Eligibility switch {
        "bundled-rules" => "Bundled eligibility checked", "failed" => "Bundled eligibility needs attention",
        _ => "Format eligibility unverified"
    };
    public static string ResultLabel(DeckTest test) => test.Status switch {
        "conceded" => "Ended early — conceded", "interrupted" => "Ended early — interrupted",
        "error" => "Ended early — error", "running" => "In progress",
        _ => test.Result ?? (test.Kind == "hand" ? "Sample recorded" : "Completed")
    };
    public static AnalysisHistory? LatestDecision(DeckAnalysisState state, string recommendationId) =>
        state.History.LastOrDefault(h => h.Fingerprint == state.Report.Fingerprint && h.RecommendationId == recommendationId);
    public static bool Reviewed(DeckAnalysisState state, string recommendationId) =>
        LatestDecision(state, recommendationId)?.Decision is "keep" or "dismiss" or "tested";
    public static int SourceQuantity(DeckSourceChange row, string choice) => choice switch {
        "local" => row.Local, "remote" or "copy" => row.Remote,
        "merge" when !row.Conflict => row.Remote == row.Baseline ? row.Local : row.Remote,
        _ => throw new InvalidOperationException("Review conflicting cards before merging.")
    };
    public static string SourceEffect(string choice) => choice switch {
        "local" => "Keep every local playable card, Considering card and format. Accept this source snapshot as the new baseline; these source changes will not be offered again unless the source changes.",
        "copy" => "Create a separate linked deck with the source playable list, Considering list and format. The original deck, notes and source baseline stay unchanged.",
        "merge" => "Apply source-changed quantities and retain independent local changes, including Considering. Keep the local format and favorite markers. Accept this source snapshot as the new baseline.",
        _ => "Replace the entire playable list and Considering list with the source quantities and format. Local-only cards are removed. Keep local notes, favorites and earlier versions; accept this source snapshot as the new baseline."
    };
}
