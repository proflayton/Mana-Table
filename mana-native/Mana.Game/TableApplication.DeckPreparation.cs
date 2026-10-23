using Mana.Magic;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    // Called once by Update before the immutable frame is emitted. Painting
    // consumes these choices and never advances navigation or reader ownership.
    private void PrepareDeckInteraction()
    {
        preparedAnalysisLayout = null; preparedAnalysisEvidence = null;
        if (match != null || !deckEditor || selectedDeckDetails == null) return;
        deckCardsPage = Math.Clamp(deckCardsPage, 0, Math.Max(0, (DeckRowsToShow().Length - 1) / DeckRows));
        deckIssuePage = Math.Clamp(deckIssuePage, 0, Math.Max(0, IssueTextPages(DeckIssues, 90).Length - 1));
        if (DeckSelection is { } card) {
            string selection = card.Id + ":" + deckCardSection;
            if (shownDeckIssueCard != selection) { shownDeckIssueCard = selection; deckCardIssuePage = 0; }
            deckCardIssuePage = Math.Clamp(deckCardIssuePage, 0, Math.Max(0, IssueTextPages(CardIssues(card.Id, deckCardSection), 72).Length - 1));
        }
        if (workshopTab == "test" && deckWorkspace.View.Hand is { } hand)
            workshopPage = Math.Clamp(workshopPage, 0, Math.Max(0, (hand.Hand.Length - 1) / 7));
        else if (workshopTab == "results")
            workshopPage = Math.Clamp(workshopPage, 0, Math.Max(0, (deckWorkspace.View.Tests.Length - 1) / 6));
        PrepareWorkshopDialogPage();
        preparedAnalysisLayout = PrepareAnalysisWorkspace();
        deckInteraction.ReconcileReading(WorkspaceReadingScope);
        if (analysisOpen && (deckInteraction.Detail.Title.Length > 0 || deckInteraction.Advanced))
            preparedAnalysisEvidence = PrepareAnalysisEvidence();
    }

    private void PrepareWorkshopDialogPage()
    {
        int count = 0, perPage = 7;
        switch (workshopDialog) {
            case "versions": count = deckWorkspace.View.Workshop?.Versions.Length ?? 0; break;
            case "version-view":
                count = workshopCompareVersion ? workshopVersionView?.Differences.Length ?? 0 : workshopVersionView?.Entries.Length ?? 0;
                perPage = 8; break;
            case "source-confirm" when workshopSourcePreview is { } source:
                count = DeckExperience.SourceRows(source, workshopSourceChoice, workshopSourceImport || workshopSourceChoice == "copy").Length;
                perPage = 6; break;
            case "swap": count = selectedDeckDetails!.Entries.Count(entry => entry.Section == "Main" && entry.Quantity > 0); break;
            case "source-preview" when workshopSourcePreview is { } preview:
                count = preview.Changes.Length > 0 ? preview.Changes.Length : preview.Cards.Length; break;
            case "source-details" when workshopSourcePreview is { } details:
                count = details.Problems.Select(problem => "Needs attention: " + problem).Concat(details.Warnings.Select(warning => "Note: " + warning))
                    .SelectMany(part => TextPages(part, ReadingPageSize(180))).Count(); break;
            case "saved-hand": count = deckWorkspace.View.Tests.FirstOrDefault(test => test.Id == workshopSelectedTest)?.Hand.Length ?? 0; break;
            case "saved-card" when workshopSavedCard is { } saved:
                inspectPage = Math.Clamp(inspectPage, 0, Math.Max(0, TextPages(DeckCardFace(saved.Card, workshopSavedBack).Text, ReadingPageSize(1150)).Length - 1));
                return;
            case "opponents":
                count = workshopSetup?.Opponents.Count(opponent => (opponent.Name + " " + opponent.Description).Contains(workshopOpponentSearch, StringComparison.OrdinalIgnoreCase)) ?? 0;
                perPage = 6; break;
            default: return;
        }
        WorkshopDialogPage = Math.Clamp(WorkshopDialogPage, 0, Math.Max(0, (count - 1) / perPage));
    }
}
