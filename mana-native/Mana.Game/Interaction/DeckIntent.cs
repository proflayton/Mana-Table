using Mana.Contracts;
using Mana.Magic;

namespace Mana.Table.Interaction;

internal enum DeckTextField { Library, Deck, Printing }
internal enum CatalogFilterMenu { Type, Mana, Sort }
internal enum WorkshopTab { Build, Test, Results }
internal enum PracticeAction { New, Draw, Redraw, Keep }
internal enum SourceChoice { Local, Remote, Copy, Merge }
internal enum AnalysisTextField { Turn, ProtectedCards, Backup, Pool }
internal enum AnalysisReviewDecision { Reopen, Keep, Dismiss }

// These are player intents with their semantic payloads. Display/debug IDs do
// not choose behavior, and an intent never retains an executable callback.
internal abstract record DeckIntent : UiIntent
{
    internal sealed override InteractionScopeKind ScopeKind => InteractionScopeKind.Current;
    internal sealed record Rename : DeckIntent;
    internal sealed record NewDeck : DeckIntent;
    internal sealed record ImportArchidekt : DeckIntent;
    internal sealed record RestoreSavedDeck(string DeckId, string RestoreToken) : DeckIntent;
    internal sealed record Duplicate : DeckIntent;
    internal sealed record Export : DeckIntent;
    internal sealed record CloseEditor : DeckIntent;
    internal sealed record Undo : DeckIntent;
    internal sealed record Redo : DeckIntent;
    internal sealed record Save : DeckIntent;
    internal sealed record Delete(string DeckId, string LibraryToken, int Generation) : DeckIntent;
    internal sealed record FocusText(DeckTextField Field, bool Clear = false) : DeckIntent;
    internal sealed record OpenCatalogMenu(CatalogFilterMenu Menu) : DeckIntent;
    internal sealed record CloseCatalogMenu : DeckIntent;
    internal sealed record SelectCatalogFilter(CatalogFilterMenu Menu, int Index) : DeckIntent;
    internal sealed record ResetCatalog : DeckIntent;
    internal sealed record ToggleCommanderColors : DeckIntent;
    internal sealed record ToggleCatalogColor(int Color) : DeckIntent;
    internal sealed record SelectCatalogCard(CatalogCard Card, bool AddCopy = false) : DeckIntent;
    internal sealed record PageCatalog(int Direction) : DeckIntent;
    internal sealed record ClearRoleFilter : DeckIntent;
    internal sealed record ClearManaFilter : DeckIntent;
    internal sealed record ClearRelatedCards : DeckIntent;
    internal sealed record ToggleIssues : DeckIntent;
    internal sealed record FilterList(string Section, bool Leaders) : DeckIntent;
    internal sealed record SelectRow(DeckEntry Entry) : DeckIntent;
    internal sealed record PageDeck(int Direction) : DeckIntent;
    internal sealed record OpenPrintings : DeckIntent;
    internal sealed record ChangeQuantity(int Delta) : DeckIntent;
    internal sealed record EnterQuantity : DeckIntent;
    internal sealed record MoveCard : DeckIntent;
    internal sealed record FlipCard : DeckIntent;
    internal sealed record SetCommanders(string[] CardIds) : DeckIntent;
    internal sealed record ShowIssue(DeckIssue Issue) : DeckIntent;
    internal sealed record PageIssues(int Direction, bool SelectedCard = false) : DeckIntent;
    internal sealed record CyclePrintingFinish : DeckIntent;
    internal sealed record ChoosePrinting(CatalogCard Card) : DeckIntent;
    internal sealed record LoadPrintings(int Offset) : DeckIntent;
    internal sealed record ShowWorkshopTab(WorkshopTab Tab) : DeckIntent;
    internal sealed record OpenAnalysis : DeckIntent;
    internal sealed record EditWorkshopPlan(bool Question) : DeckIntent;
    internal sealed record OpenSource : DeckIntent;
    internal sealed record OpenVersions : DeckIntent;
    internal sealed record RefreshWorkshop : DeckIntent;
    internal sealed record FilterRole(string Role, bool Toggle = false) : DeckIntent;
    internal sealed record FilterMana(int Value) : DeckIntent;
    internal sealed record ReadCard(Card Card, string Identity) : DeckIntent;
    internal sealed record CloseReader : DeckIntent;
    internal sealed record FlipReader(string Identity) : DeckIntent;
    internal sealed record PageWorkshop(int Direction) : DeckIntent;
    internal sealed record PageWorkshopDialog(int Direction) : DeckIntent;
    internal sealed record Practice(PracticeAction Action) : DeckIntent;
    internal sealed record SetPlayers(int Count) : DeckIntent;
    internal sealed record ChooseOpponentSeat(int Seat) : DeckIntent;
    internal sealed record CycleOpponent(int Seat, int Direction) : DeckIntent;
    internal sealed record PrepareTable : DeckIntent;
    internal sealed record StartTable : DeckIntent;
    internal sealed record SelectTest(string TestId) : DeckIntent;
    internal sealed record OpenSavedHand : DeckIntent;
    internal sealed record SaveTestNote(string TestId) : DeckIntent;
    internal sealed record InspectVersion(string? TestId, string VersionId) : DeckIntent;
    internal sealed record BackFromDialog : DeckIntent;
    internal sealed record RestoreVersion(WorkshopVersion Version) : DeckIntent;
    internal sealed record OpenCardTools : DeckIntent;
    internal sealed record ToggleRole(string Role) : DeckIntent;
    internal sealed record EditRoles : DeckIntent;
    internal sealed record TogglePin : DeckIntent;
    internal sealed record MoveConsidering : DeckIntent;
    internal sealed record SaveConsidering(CatalogCard Card) : DeckIntent;
    internal sealed record OpenSwap : DeckIntent;
    internal sealed record SwapConsidering(DeckEntry Cut, bool Pinned) : DeckIntent;
    internal sealed record RefreshSource : DeckIntent;
    internal sealed record OpenSourcePage(bool Preview = false) : DeckIntent;
    internal sealed record OpenSourceDetails : DeckIntent;
    internal sealed record PreviewSourceChoice(SourceChoice Choice) : DeckIntent;
    internal sealed record ShowSourcePreview : DeckIntent;
    internal sealed record CompareVersion(bool Compare) : DeckIntent;
    internal sealed record ReadSavedCard(CatalogCard Card, string ReturnDialog) : DeckIntent;
    internal sealed record ConfirmSource : DeckIntent;
    internal sealed record FlipSavedCard : DeckIntent;
    internal sealed record PageSavedCard(int Direction) : DeckIntent;
    internal sealed record SearchOpponents : DeckIntent;
    internal sealed record SelectOpponent(int Index) : DeckIntent;
    internal sealed record SelectAnalysisTab(int Tab) : DeckIntent;
    internal sealed record PageAnalysis(int Direction) : DeckIntent;
    internal sealed record ToggleReviewed : DeckIntent;
    internal sealed record ShowAnalysisModel : DeckIntent;
    internal sealed record OpenAnalysisPreferences : DeckIntent;
    internal sealed record SaveAnalysisProfile(AnalysisProfile Profile) : DeckIntent;
    internal sealed record EditAnalysisText(AnalysisTextField Field) : DeckIntent;
    internal sealed record SelectAnalysisEntry(int Index) : DeckIntent;
    internal sealed record CloseAdvancedEvidence : DeckIntent;
    internal sealed record ConsiderRecommendation(string RecommendationId) : DeckIntent;
    internal sealed record ReviewRecommendation(string RecommendationId, AnalysisReviewDecision Decision) : DeckIntent;
    internal sealed record TestFinding(TuningQuestion Question) : DeckIntent;
    internal sealed record ShowRelatedCards : DeckIntent;
}
