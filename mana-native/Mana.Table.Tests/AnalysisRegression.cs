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
        IDeckWorkshop builder = engine;
        var workshop = await builder.WorkshopAsync(deck.Id, deck.Revision);
        Check(initial.Report.Fingerprint == workshop.VersionId && initial.Report.VersionId == workshop.VersionId, "Analysis uses the same playable version as tests and restores");
        var saved = await analysis.SaveAnalysisProfileAsync(deck.Id, deck.Revision, initial.MetadataRevision, initial.Report.Fingerprint,
            initial.Report.Profile with { Strategy = "artifact-recursion", Pace = "fast", Plan = "Recurring artifacts into a sacrifice finish." });
        Check(saved.Report.Profile.Pace == "fast", "Native profile serialization saves goals with the shared API");
        var swap = saved.Report.Recommendations.First(r => r.CutName == "Lifecrafter's Bestiary");
        var considered = await analysis.ConsiderAnalysisAsync(deck.Id, deck.Revision, saved.MetadataRevision, saved.Report.Fingerprint, swap.Id, "Test the enters/death draw option.");
        var reviewed = considered.Analysis;
        Check(reviewed.History.Length == 1 && reviewed.History[0].Fingerprint == reviewed.Report.Fingerprint, "Review decisions retain their exact deck version");
        Check(considered.Workshop.Considering.Any(c => c.Card.Card.Name == swap.Add.Name && c.Quantity == 1), "Consider saves the actual candidate outside the playable list");
        Check(considered.Workshop.MetadataRevision == reviewed.MetadataRevision && considered.Workshop.Plan == saved.Report.Profile.Plan,
            "Goals and candidate decisions share the workshop metadata revision");
        var protectedView = await analysis.SaveAnalysisProfileAsync(deck.Id, deck.Revision, reviewed.MetadataRevision, reviewed.Report.Fingerprint,
            reviewed.Report.Profile with { ProtectedCards = ["Lifecrafter's Bestiary"] });
        Check(protectedView.Report.Recommendations.All(r => r.CutName != "Lifecrafter's Bestiary"), "Preserved cards are excluded from proposed cuts");
        var pinnedWorkshop = await builder.WorkshopAsync(deck.Id, deck.Revision);
        string favoriteId = deck.Entries.Single(e => e.Card.Name == "Lifecrafter's Bestiary").Id;
        Check(pinnedWorkshop.Roles.Any(r => r.CardId == favoriteId && r.Pinned), "Analysis favorites are the same favorites shown in Build");
        Check(pinnedWorkshop.VersionId == workshop.VersionId, "Preferences and Considering preserve the playable version");
        var builderEdit = await builder.UpdateWorkshopAsync(deck.Id, deck.Revision, new(Plan: "Build's shared plan", MetadataRevision: pinnedWorkshop.MetadataRevision));
        var fromBuilder = await analysis.AnalyzeDeckAsync(deck.Id, deck.Revision);
        Check(fromBuilder.Report.Profile.Plan == builderEdit.Plan && fromBuilder.MetadataRevision == builderEdit.MetadataRevision,
            "Builder edits update the analysis plan without a competing journal");
        bool staleRejected = false;
        try { await analysis.SaveAnalysisProfileAsync(deck.Id, deck.Revision, protectedView.MetadataRevision, protectedView.Report.Fingerprint, protectedView.Report.Profile); }
        catch (Exception) { staleRejected = true; }
        Check(staleRejected, "An old analysis profile cannot overwrite newer workshop metadata");
        var reopened = await engine.OpenDeckAsync(deck.Id);
        var persisted = await analysis.AnalyzeDeckAsync(reopened.Id, reopened.Revision);
        Check(persisted.History.Length == 1 && persisted.Report.Profile.Plan == builderEdit.Plan, "Goals and reviews survive reopening");
        Check(reopened.Entries.Sum(e => e.Quantity) == 100 && reopened.Entries.All(e => e.Card.Name != "Ichor Wellspring"), "Analysis never mutates playable deck contents");
        var forest = reopened.Entries.Single(e => e.Card.Name == "Forest");
        var changed = await engine.EditDeckAsync(reopened.Id, reopened.Revision, [new("Main", forest.Id, 90)]);
        var changedWorkshop = await builder.WorkshopAsync(changed.Id, changed.Revision);
        staleRejected = false;
        try { await analysis.SaveAnalysisProfileAsync(changed.Id, changed.Revision, changedWorkshop.MetadataRevision, persisted.Report.Fingerprint, persisted.Report.Profile); }
        catch (Exception) { staleRejected = true; }
        Check(staleRejected, "A content fingerprint rejects profiles from an earlier playable version");
        var current = await analysis.AnalyzeDeckAsync(changed.Id, changed.Revision);
        Check(current.Report.Fingerprint == changedWorkshop.VersionId && current.Report.Fingerprint != persisted.Report.Fingerprint,
            "Analysis follows content edits while preserving previous decisions");
        await File.WriteAllTextAsync(Path.Combine(paths.Profile, "analysis-adapter-results.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
    }
}
