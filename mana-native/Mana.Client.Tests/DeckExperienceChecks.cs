using Mana.Contracts;
using Mana.Magic;
using System.Text.Json;

internal static class DeckExperienceChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var test = new DeckTest("test", "deck", "version", "game", "First three land drops?", 1, 2,
            "conceded", "Defeat", [], new() { ["landsPlayed"] = 3, ["ownTurns"] = 7 }, "");
        check(DeckExperience.ResultLabel(test) == "Ended early — conceded", "A conceded experiment remains visibly early-ended even when the engine result is Defeat");
        check(DeckExperience.ResultLabel(test with { Status = "interrupted", Result = "Victory" }).Contains("interrupted"), "An interrupted test never presents the engine outcome as a completed experiment");
        var deck = new DeckDetails("deck", "Deck", 0, [], [], "", "");
        check(DeckExperience.EligibilityLabel(deck).Contains("unverified") && DeckExperience.EligibilityLabel(deck with { Eligibility = "bundled-rules" }).Contains("Bundled"), "Missing eligibility stays explicit while a bundled check does not claim online legality");
        var independent = new DeckSourceChange("Main\nForest", "Forest", "Main", 4, 5, 4, false);
        var sourceChanged = independent with { Baseline = 4, Local = 4, Remote = 3 };
        check(DeckExperience.SourceQuantity(independent, "merge") == 5 && DeckExperience.SourceQuantity(sourceChanged, "merge") == 3, "The confirmation preview preserves independent local changes and applies source-changed quantities");
        check(DeckExperience.SourceQuantity(independent, "remote") == 4 && DeckExperience.SourceQuantity(sourceChanged, "local") == 4, "Whole-list choices preview exact source or retained-local quantities");
        bool refused = false;
        try { DeckExperience.SourceQuantity(independent with { Conflict = true, Remote = 2 }, "merge"); } catch (InvalidOperationException) { refused = true; }
        check(refused && DeckExperience.SourceEffect("local").Contains("new baseline") && DeckExperience.SourceEffect("copy").Contains("unchanged"), "Conflicts cannot masquerade as a merge and source-baseline effects are explicit before confirmation");
        var source = new DeckSourcePreview("preview", "https://archidekt.com/decks/123", "Source", "Commander", [
            new("forest", new("forest", new() { Name = "Forest" }, 0, 16, "Main", "TST"), 5, "Main", [], true),
            new("option", new("option", new() { Name = "An option" }, 2, 16, "Main", "TST"), 1, "Considering", [], false)
        ], [], [], [independent], true);
        var copyRows = DeckExperience.SourceRows(source, "copy", true);
        check(copyRows.Length == 2 && copyRows.Single(r => r.Section == "Considering").After == 1,
            "Source-copy confirmation includes the whole remote list and unchanged Considering rows, rather than just the change subset");
        var state = JsonSerializer.Deserialize<DeckAnalysisState>("""
            {"MetadataRevision":3,"Report":{"Fingerprint":"current"},"History":[
             {"Id":"1","Fingerprint":"earlier","RecommendationId":"swap","Decision":"dismiss","Note":"","Created":1},
             {"Id":"2","Fingerprint":"current","RecommendationId":"swap","Decision":"keep","Note":"","Created":2},
             {"Id":"3","Fingerprint":"current","RecommendationId":"swap","Decision":"reopen","Note":"","Created":3}]}
            """)!;
        check(!DeckExperience.Reviewed(state, "swap") && DeckExperience.LatestDecision(state, "swap")?.Decision == "reopen", "Reopening supersedes Keep without erasing the version-scoped history");
        check(DeckExperience.LatestDecision(state with { History = [state.History[0]] }, "swap") == null, "A decision from an earlier playable fingerprint does not decide the current recommendation");
    }
}
