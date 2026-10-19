using Mana.Contracts;

namespace Mana.Forge;

public sealed partial class ForgeEngine : IDeckAnalysis
{
    public async Task<DeckAnalysisState> AnalyzeDeckAsync(string deckId, long revision) =>
        Read<DeckAnalysisState>(await Request("deckAnalysis", new { deckId, revision }));
    public async Task<DeckAnalysisState> SaveAnalysisProfileAsync(string deckId, long revision, long metadataRevision, string fingerprint, AnalysisProfile profile) =>
        Read<DeckAnalysisState>(await Request("analysisProfile", new { deckId, revision, metadataRevision, fingerprint, profile = new {
            rulesFormat = profile.RulesFormat, strategy = profile.Strategy, plan = profile.Plan, backup = profile.Backup,
            pace = profile.Pace, targetTurn = profile.TargetTurn, protectedCards = profile.ProtectedCards, pool = profile.Pool
        } }));
    public async Task<AnalysisConsiderResult> ConsiderAnalysisAsync(string deckId, long revision, long metadataRevision, string fingerprint,
        string recommendationId, string note)
    {
        var result = await Request("analysisConsider", new { deckId, revision, metadataRevision, fingerprint, recommendationId, note });
        return new(Read<DeckAnalysisState>(result.GetProperty("analysis")), MapWorkshop(result.GetProperty("workshop")));
    }
    public async Task<DeckAnalysisState> ReviewAnalysisAsync(string deckId, long revision, long metadataRevision, string fingerprint,
        string recommendationId, string decision, string note) => Read<DeckAnalysisState>(await Request("analysisReview",
            new { deckId, revision, metadataRevision, fingerprint, recommendationId, decision, note }));
}
