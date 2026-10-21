namespace Mana.Contracts;

// Optional capability keeps analysis independent of match setup and playable deck mutations.
public interface IDeckAnalysis
{
    Task<DeckAnalysisState> AnalyzeDeckAsync(string deckId, long revision);
    Task<DeckAnalysisState> SaveAnalysisProfileAsync(string deckId, long revision, long metadataRevision, string fingerprint, AnalysisProfile profile);
    Task<AnalysisConsiderResult> ConsiderAnalysisAsync(string deckId, long revision, long metadataRevision, string fingerprint,
        string recommendationId, string note);
    Task<DeckAnalysisState> ReviewAnalysisAsync(string deckId, long revision, long metadataRevision, string fingerprint,
        string recommendationId, string decision, string note);
}
public sealed record AnalysisProfile(string RulesFormat, string Strategy, string Plan, string Backup, string Pace,
    int TargetTurn, string[] ProtectedCards, string[] Pool);
public sealed record AnalysisFormat(string Name, int MinimumMain, bool ExactSize, int CopyLimit, int SideboardLimit,
    bool CommanderIdentity, bool PoolRequired);
public sealed record CardFunction(string Role, string Event, string Provides, string[] Requires, string Confidence);
public sealed record AnalysisCard(string CardId, string Name, int Quantity, string Section, int ManaValue,
    CardFunction[] Functions, string[] EngineIds, bool ProtectedCard);
public sealed record AnalysisEngine(string Id, string Name, string[] Cards, string[] Payoffs, string[] Missing, string[] Steps,
    string[] Outputs, string[] Requirements, string Confidence);
public sealed record AnalysisFinding(string Id, string Dimension, string Severity, string Title, string Evidence,
    string[] Cards, string Confidence);
public sealed record AnalysisAddition(string Id, string Name, int ManaValue, string Type, string OracleText);
public sealed record AnalysisRecommendation(string Id, string FindingId, string CutCardId, string CutName, AnalysisAddition Add,
    string Benefit, string Tradeoff, Dictionary<string, int> RoleDelta, int ManaValueDelta, string Eligibility, string Confidence);
public sealed record AnalysisMetrics(int MainCards, int Lands, int Creatures, int ExpensiveSpells, int EarlyRamp,
    int UnconditionalDraw, int ConditionalDraw, int Interaction, int[] Curve, int CardsSeen, int LandGoal,
    double? LandProbability, string ProbabilityAssumptions);
public sealed record AnalysisReport(string DeckId, long Revision, string Fingerprint, AnalysisProfile Profile, AnalysisFormat Format,
    AnalysisMetrics Metrics, AnalysisCard[] Cards, AnalysisEngine[] Engines, AnalysisFinding[] Findings,
    AnalysisRecommendation[] Recommendations, string[] Limitations)
{
    public string VersionId { get; init; } = "";
}
public sealed record AnalysisHistory(string Id, string Fingerprint, string RecommendationId, string Decision, string Note, long Created);
public sealed record DeckAnalysisState(long MetadataRevision, AnalysisReport Report, AnalysisHistory[] History)
{
    public AnalysisHistory[] Decisions { get; init; } = [];
}
public sealed record AnalysisConsiderResult(DeckAnalysisState Analysis, WorkshopState Workshop);
