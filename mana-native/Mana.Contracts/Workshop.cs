using System.Collections.Immutable;

namespace Mana.Contracts;

// Workshop data belongs to a saved deck; immutable versions identify playable
// content independently of the editor's optimistic concurrency revision.
public interface IDeckWorkshop
{
    Task<WorkshopState> WorkshopAsync(string deckId, long revision);
    Task<WorkshopState> UpdateWorkshopAsync(string deckId, long revision, WorkshopMetadataEdit edit);
    Task<DeckDetails> RestoreWorkshopVersionAsync(string deckId, long revision, string versionId);
    Task<DeckDetails> MoveWorkshopCardAsync(string deckId, long revision, long metadataRevision, string cardId, string to, string? cutCardId = null);
    Task<DeckSourcePreview> PreviewDeckSourceAsync(string url);
    Task<DeckSourcePreview> RefreshDeckSourceAsync(string deckId, long revision);
    Task<DeckDetails> ApplyDeckSourceAsync(string token, string? deckId, long? revision, string choice);
    Task<ImmutableArray<DeckTest>> GetTestsAsync(string deckId);
    Task<HandPractice> PracticeAsync(string deckId, long revision, string action, int? index = null, string? question = null);
    Task<DeckTest> StartDeckTestAsync(string deckId, long revision, ImmutableArray<string> opponents, string question);
    Task<DeckTest> SaveTestNoteAsync(string deckId, string testId, string note);
}

public sealed record WorkshopRole(string CardId, ImmutableArray<string> Roles, bool Pinned, string Origin);
public sealed record ConsideringCard(string CardId, CatalogCard Card, int Quantity, ImmutableArray<string> Categories);
public sealed record ConsideringEdit(string CardId, int Quantity, ImmutableArray<string> Categories);
public sealed record WorkshopVersion(string Id, string Label, long Created, int MainCount, int CommanderCount);
public sealed record WorkshopSource(string Url, string RemoteId, string Name, string UpdatedAt, long LastChecked, string Status);
public sealed record WorkshopInsights(ImmutableDictionary<string, int> RoleCounts, ImmutableArray<int> Curve, ImmutableDictionary<string, int> ColoredSources, string Note);
public sealed record WorkshopState(string DeckId, long Revision, long MetadataRevision, string VersionId,
    string Plan, string Question, ImmutableArray<WorkshopRole> Roles, ImmutableArray<ConsideringCard> Considering,
    ImmutableArray<WorkshopVersion> Versions, WorkshopSource? Source, WorkshopInsights Insights);
public sealed record WorkshopMetadataEdit(long? MetadataRevision = null, string? Plan = null, string? Question = null,
    ImmutableArray<WorkshopRole>? Roles = null, ImmutableArray<ConsideringEdit>? Considering = null);
public sealed record DeckSourceCard(string CardId, CatalogCard Card, int Quantity, string Section, ImmutableArray<string> Categories, bool Included);
public sealed record DeckSourceChange(string Key, string Card, string Section, int Baseline, int Local, int Remote, bool Conflict);
public sealed record DeckSourcePreview(string Token, string Url, string Name, string Format, ImmutableArray<DeckSourceCard> Cards,
    ImmutableArray<string> Problems, ImmutableArray<string> Warnings, ImmutableArray<DeckSourceChange> Changes, bool HasChanges);
public sealed record DeckTest(string Id, string DeckId, string VersionId, string Kind, string Question,
    long Started, long? Ended, string Status, string? Result, ImmutableArray<string> Opponents, ImmutableDictionary<string, int> Observations, string Note)
{
    public ImmutableArray<CatalogCard> Hand { get; init; } = [];
}
public sealed record HandPractice(string TestId, string VersionId, ImmutableArray<CatalogCard> Hand, int LibraryCount, int Draws, int Redraws, string Status);

public interface IDeckVersionHistory
{
    Task<DeckVersionView> InspectTestAsync(string deckId, long revision, string testId);
    Task<DeckVersionView> InspectVersionAsync(string deckId, long revision, string versionId);
}
public sealed record DeckVersionDifference(string Section, string Card, int Saved, int Current);
public sealed record DeckVersionView(string VersionId, string Name, string Format, string CurrentFormat,
    ImmutableArray<DeckEntry> Entries, ImmutableArray<DeckVersionDifference> Differences);
