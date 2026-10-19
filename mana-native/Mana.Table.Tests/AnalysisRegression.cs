using Mana.Contracts;
using Mana.Forge;
using System.Text.Json;

namespace Mana.Table;

internal static class AnalysisRegression
{
    internal const string List = "Commander\n1 Toph, the First Metalbender\nDeck\n1 Ashaya, Soul of the Wild\n1 Earthbending Student\n1 Phyrexian Altar\n1 Impact Tremors\n1 Lifecrafter's Bestiary\n1 Cloudstone Curio\n1 Mycosynth Lattice\n1 Panharmonicon\n91 Forest";
    internal static async Task RunAsync(ForgePaths paths)
    {
        await using var engine = new ForgeEngine(paths); await engine.InitializeAsync();
        var summary = await engine.ImportAsync("Analysis regression", List);
        var deck = await engine.OpenDeckAsync(summary.Id);
        IDeckAnalysis analysis = engine;
        var initial = await analysis.AnalyzeDeckAsync(deck.Id, deck.Revision);
        var checks = new List<string>();
        void Check(bool condition, string text) { if (!condition) throw new InvalidOperationException(text); checks.Add(text); }
        Check(initial.Report.Engines.Any(e => e.Missing.Length == 0 && e.Cards.Contains("Earthbending Student")), "Actual card scripts produce a complete self-renewing earthbend line");
        Check(initial.Report.Findings.Any(f => f.Id == "curio-lattice"), "Dependency conflicts cross the native adapter");
        Check(initial.Report.Cards.Single(c => c.Name == "Lifecrafter's Bestiary").Functions.Any(f => f.Role == "Draw" && f.Event == "cast"), "Conditional draw events retain their meaning");
        var saved = await analysis.SaveAnalysisProfileAsync(deck.Id, deck.Revision, initial.MetadataRevision,
            initial.Report.Profile with { Strategy = "artifact-recursion", Pace = "fast", Plan = "Recurring artifacts into a sacrifice finish." });
        Check(saved.Report.Profile.Pace == "fast", "Native profile serialization saves goals with the shared API");
        var swap = saved.Report.Recommendations.First(r => r.CutName == "Lifecrafter's Bestiary");
        var reviewed = await analysis.ReviewAnalysisAsync(deck.Id, deck.Revision, saved.MetadataRevision, saved.Report.Fingerprint, swap.Id, "consider", "Test the enters/death draw option.");
        Check(reviewed.History.Length == 1 && reviewed.History[0].Fingerprint == reviewed.Report.Fingerprint, "Review decisions retain their exact deck version");
        var protectedView = await analysis.SaveAnalysisProfileAsync(deck.Id, deck.Revision, reviewed.MetadataRevision,
            reviewed.Report.Profile with { ProtectedCards = ["Lifecrafter's Bestiary"] });
        Check(protectedView.Report.Recommendations.All(r => r.CutName != "Lifecrafter's Bestiary"), "Preserved cards are excluded from proposed cuts");
        var reopened = await engine.OpenDeckAsync(deck.Id);
        var persisted = await analysis.AnalyzeDeckAsync(reopened.Id, reopened.Revision);
        Check(persisted.History.Length == 1 && persisted.Report.Profile.Plan == saved.Report.Profile.Plan, "Goals and reviews survive reopening");
        Check(reopened.Entries.Sum(e => e.Quantity) == 100 && reopened.Entries.All(e => e.Card.Name != "Ichor Wellspring"), "Analysis never mutates playable deck contents");
        await File.WriteAllTextAsync(Path.Combine(paths.Profile, "analysis-adapter-results.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
    }
}
