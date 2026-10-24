using Mana.Contracts;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private void DispatchDeckIntent(DeckIntent intent)
    {
        switch (intent) {
            case DeckIntent.NewDeck: ClearExperienceInput(); NewDeck(); break;
            case DeckIntent.ImportArchidekt: ImportArchidekt(); break;
            case DeckIntent.RestoreSavedDeck restore: RestoreSavedDeck(restore.DeckId, restore.RestoreToken); break;
            case DeckIntent.Rename:
                deckMenu = "";
                if (selectedDeckDetails is { } rename) { var name = Ask("Rename deck", "Deck name", rename.Name); if (name != null) RenameDeck(name); }
                break;
            case DeckIntent.Duplicate:
                deckMenu = "";
                if (selectedDeckDetails is { } duplicate) { var name = Ask("Copy deck", "Name this separate copy", duplicate.Name + " copy"); if (name != null) DuplicateDeck(name); }
                break;
            case DeckIntent.Export: ExportDeck(); break;
            case DeckIntent.OpenDeckOptions: deckMenu = deckMenu == "deck-actions" ? "" : "deck-actions"; break;
            case DeckIntent.ChooseCommander choose: ChooseDeckCommander(choose.Partner); break;
            case DeckIntent.CloseEditor: CloseDeckEditor(); break;
            case DeckIntent.Undo: EditDeck(d => catalog.UndoDeckAsync(d.Id, d.Revision), "Edit undone."); break;
            case DeckIntent.Redo: EditDeck(d => catalog.RedoDeckAsync(d.Id, d.Revision), "Edit restored."); break;
            case DeckIntent.Save: EditDeck(d => catalog.SaveDeckAsync(d.Id, d.Revision), "Deck saved."); break;
            case DeckIntent.Delete remove: DeleteSavedDeck(remove.DeckId, remove.LibraryToken, remove.Generation); break;
            case DeckIntent.FocusText focus:
                FocusDeckText(focus.Field.ToString().ToLowerInvariant()); if (focus.Clear) SetDeckText(""); break;
            case DeckIntent.OpenCatalogMenu menu: deckMenu = menu.Menu.ToString().ToLowerInvariant(); break;
            case DeckIntent.CloseCatalogMenu: deckMenu = ""; break;
            case DeckIntent.SelectCatalogFilter filter:
                if (filter.Menu == CatalogFilterMenu.Type) catalogType = Math.Clamp(filter.Index, 0, CatalogTypes.Length - 1);
                else if (filter.Menu == CatalogFilterMenu.Mana) catalogMana = Math.Clamp(filter.Index, 0, 9);
                else catalogManaSort = filter.Index == 1;
                deckMenu = ""; ChangeCatalog(); break;
            case DeckIntent.ResetCatalog: ResetCatalog(); break;
            case DeckIntent.ToggleCommanderColors: catalogCommanderColors = !catalogCommanderColors; ChangeCatalog(); break;
            case DeckIntent.ToggleCatalogColor color:
                if (color.Color is < 0 or > 6) return;
                catalogCommanderColors = false;
                catalogColors = color.Color == 0 ? null : color.Color == 6 ? 0 : catalogColors.GetValueOrDefault() ^ (1 << (color.Color - 1));
                ChangeCatalog(); break;
            case DeckIntent.SelectCatalogCard select:
                if (catalogRead.View.Value?.Cards.Any(card => card.Id == select.Card.Id) != true) return;
                librarySelection = select.Card; deckCardId = deckBackId = null; deckCardSection = "Main";
                if (select.AddCopy) DeckQuantity(DeckQuantity() + 1); break;
            case DeckIntent.PageCatalog page: catalogOffset = Math.Max(0, catalogOffset + page.Direction * 6); ChangeCatalog(true); break;
            case DeckIntent.ClearRoleFilter: workshopRoleFilter = ""; deckCardsPage = 0; break;
            case DeckIntent.ClearManaFilter: workshopManaFilter = null; deckCardsPage = 0; break;
            case DeckIntent.ClearRelatedCards: deckInteraction.ClearRelated(); deckCardsPage = 0; break;
            case DeckIntent.ToggleIssues: ToggleDeckIssues(); break;
            case DeckIntent.FilterList filter: FilterDeck(filter.Section, filter.Leaders); break;
            case DeckIntent.SelectRow select: SelectDeckRow(select.Entry); break;
            case DeckIntent.PageDeck page: deckCardsPage = Math.Max(0, deckCardsPage + page.Direction); break;
            case DeckIntent.OpenPrintings: OpenPrintings(); break;
            case DeckIntent.ChangeQuantity quantity: DeckQuantity(DeckQuantity() + quantity.Delta); break;
            case DeckIntent.EnterQuantity:
                if (DeckSelection is { } card) {
                    var value = Ask("Card quantity", card.Card.Name + " in " + deckCardSection + " (0 removes it)", DeckQuantity().ToString());
                    if (value != null) { if (int.TryParse(value, out int count)) DeckQuantity(count); else error = "Enter a whole-number quantity."; }
                }
                break;
            case DeckIntent.MoveCard: if (deckCardSection == "Considering") MoveConsideringCard(); else MoveDeckCard(); break;
            case DeckIntent.FlipCard: if (DeckSelection is { } flip) deckBackId = deckBackId == flip.Id ? null : flip.Id; break;
            case DeckIntent.SetCommanders commanders: SaveCommanders(commanders.CardIds); break;
            case DeckIntent.ShowIssue issue: ShowDeckIssue(issue.Issue); break;
            case DeckIntent.PageIssues page:
                if (page.SelectedCard) deckCardIssuePage = Math.Max(0, deckCardIssuePage + page.Direction);
                else deckIssuePage = Math.Max(0, deckIssuePage + page.Direction); break;
            case DeckIntent.CyclePrintingFinish: printingFinish = (printingFinish + 1) % 3; LoadPrintings(0); break;
            case DeckIntent.ChoosePrinting printing: ChoosePrinting(printing.Card); break;
            case DeckIntent.LoadPrintings printing: LoadPrintings(printing.Offset); break;
            case DeckIntent.SelectToken token: SelectToken(token.Id); break;
            case DeckIntent.ChooseTokenPrinting token: SetTokenPrinting(token.Id); break;
            case DeckIntent.PageTokens page: tokenListPage = Math.Max(0, tokenListPage + page.Direction); break;
            case DeckIntent.PageTokenPrintings page: tokenPrintingOffset = Math.Max(0, tokenPrintingOffset + page.Direction * 4); break;
            case DeckIntent.RefreshTokens: deckTokensRead.Clear(); tokenPrintingsRead.Clear(); break;
            case DeckIntent.ShowWorkshopTab tab: ShowWorkshopTab(tab.Tab.ToString().ToLowerInvariant()); break;
            case DeckIntent.OpenAnalysis: OpenDeckAnalysis(); break;
            case DeckIntent.EditWorkshopPlan plan: EditWorkshopPlan(plan.Question); break;
            case DeckIntent.OpenSource:
                if (deckWorkspace.View.Workshop?.Source == null) ImportArchidekt(); else { workshopDialog = "source"; WorkshopDialogPage = 0; } break;
            case DeckIntent.OpenVersions: workshopDialog = "versions"; WorkshopDialogPage = 0; break;
            case DeckIntent.RefreshWorkshop: RequestWorkshopRefresh(); break;
            case DeckIntent.FilterRole filter:
                workshopRoleFilter = filter.Toggle && workshopRoleFilter == filter.Role ? "" : filter.Role;
                workshopManaFilter = null; deckInteraction.ClearRelated();
                if (!filter.Toggle) { ShowWorkshopTab("build"); deckFilter = ""; }
                commanderCandidates = partnerCandidates = false; deckSection = "Main"; deckCardsPage = 0; deckIssuesOnly = false; break;
            case DeckIntent.FilterMana filter:
                workshopManaFilter = filter.Value; workshopRoleFilter = ""; deckInteraction.ClearRelated();
                ShowWorkshopTab("build"); commanderCandidates = partnerCandidates = false; deckSection = "Main"; deckFilter = ""; deckCardsPage = 0; deckIssuesOnly = false; break;
            case DeckIntent.ReadCard read: ReadWorkspaceCard(read.Card, read.Identity); break;
            case DeckIntent.CloseReader: DismissDeckWorkspaceReading(); break;
            case DeckIntent.FlipReader readerFlip: deckInteraction.FlipReading(readerFlip.Identity); break;
            case DeckIntent.PageWorkshop page: workshopPage = Math.Max(0, workshopPage + page.Direction); break;
            case DeckIntent.PageWorkshopDialog page: WorkshopDialogPage += page.Direction; break;
            case DeckIntent.Practice practice: PracticeWorkshop(practice.Action switch { PracticeAction.New => "new", PracticeAction.Draw => "draw", PracticeAction.Redraw => "mulligan", PracticeAction.Keep => "keep", _ => throw new ArgumentOutOfRangeException() }); break;
            case DeckIntent.SetPlayers players: workshopPlayers = players.Count; break;
            case DeckIntent.ChooseOpponentSeat opponent:
                if (opponent.Seat < 0 || opponent.Seat >= workshopOpponents.Length) return;
                workshopOpponentSeat = opponent.Seat; workshopOpponentSearch = ""; workshopDialog = "opponents"; WorkshopDialogPage = 0; break;
            case DeckIntent.CycleOpponent opponent:
                if (workshopSetup is not { Opponents.Length: > 0 } setup || opponent.Seat < 0 || opponent.Seat >= workshopOpponents.Length) return;
                workshopOpponents[opponent.Seat] = (workshopOpponents[opponent.Seat] + opponent.Direction + setup.Opponents.Length) % setup.Opponents.Length; break;
            case DeckIntent.PrepareTable: PrepareWorkshopTable(); break;
            case DeckIntent.StartTable: StartWorkshopTable(); break;
            case DeckIntent.SelectTest test:
                if (deckWorkspace.View.Tests.Any(item => item.Id == test.TestId)) workshopSelectedTest = test.TestId; break;
            case DeckIntent.OpenSavedHand: workshopDialog = "saved-hand"; WorkshopDialogPage = 0; break;
            case DeckIntent.SaveTestNote note:
                if (deckWorkspace.View.Tests.FirstOrDefault(test => test.Id == note.TestId) is { } record) SaveWorkshopTestNote(record); break;
            case DeckIntent.InspectVersion version: InspectWorkshopVersion(version.TestId, version.VersionId); break;
            case DeckIntent.BackFromDialog:
                if (workshopDialog == "printings") { ClosePrintings(); break; }
                workshopDialog = workshopDialog switch { "saved-card" => workshopSavedReturn, "version-view" => workshopVersionReturn, "source-confirm" or "source-details" => "source-preview", "swap" => "card", _ => "" }; break;
            case DeckIntent.RestoreVersion version: RestoreWorkshopVersion(version.Version); break;
            case DeckIntent.OpenCardTools: workshopDialog = "card"; break;
            case DeckIntent.ToggleRole role: ToggleWorkshopRole(role.Role); break;
            case DeckIntent.EditRoles: EditWorkshopRoles(); break;
            case DeckIntent.TogglePin: ToggleWorkshopPin(); break;
            case DeckIntent.MoveConsidering:
                workshopDialog = "";
                if (DeckSelection is { } considering) {
                    if (deckCardSection == "Considering" || selectedDeckDetails!.Entries.Any(entry => entry.Section == "Main" && entry.Id == considering.Id)) MoveConsideringCard();
                    else ConsideringQuantity(considering, 1);
                }
                break;
            case DeckIntent.SaveConsidering saved: ConsideringQuantity(saved.Card, 1); break;
            case DeckIntent.OpenSwap: workshopDialog = "swap"; WorkshopDialogPage = 0; break;
            case DeckIntent.SwapConsidering swap:
                if (swap.Pinned && MessageBox.Show("You marked " + swap.Cut.Card.Name + " as a favorite to keep. Swap it out for this test?", "Swap a favorite", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                MoveConsideringCard(swap.Cut.Id); break;
            case DeckIntent.RefreshSource: RefreshArchidekt(); break;
            case DeckIntent.OpenSourcePage source: if (source.Preview) OpenArchidektPreview(); else OpenArchidekt(); break;
            case DeckIntent.OpenSourceDetails: workshopDialog = "source-details"; WorkshopDialogPage = 0; break;
            case DeckIntent.PreviewSourceChoice choice: ApplyArchidekt(choice.Choice.ToString().ToLowerInvariant()); break;
            case DeckIntent.ShowSourcePreview: workshopDialog = "source-preview"; break;
            case DeckIntent.CompareVersion comparison: workshopCompareVersion = comparison.Compare; WorkshopDialogPage = 0; break;
            case DeckIntent.ReadSavedCard read:
                workshopSavedCard = read.Card; workshopSavedBack = false; workshopSavedReturn = read.ReturnDialog; inspectPage = 0; workshopDialog = "saved-card"; break;
            case DeckIntent.ConfirmSource: ConfirmArchidekt(); break;
            case DeckIntent.FlipSavedCard: workshopSavedBack = !workshopSavedBack; inspectPage = 0; break;
            case DeckIntent.PageSavedCard page: inspectPage = Math.Max(0, inspectPage + page.Direction); break;
            case DeckIntent.SearchOpponents:
                var query = Ask("Find an AI deck", "Search deck names and descriptions", workshopOpponentSearch);
                if (query != null) { workshopOpponentSearch = query.Trim(); WorkshopDialogPage = 0; } break;
            case DeckIntent.SelectOpponent opponent:
                if (workshopSetup == null || opponent.Index < 0 || opponent.Index >= workshopSetup.Opponents.Length) return;
                workshopOpponents[workshopOpponentSeat] = opponent.Index; workshopDialog = ""; break;
            case DeckIntent.SelectAnalysisTab tab: deckInteraction.SelectTab(tab.Tab); break;
            case DeckIntent.PageAnalysis page: deckInteraction.PageAnalysis(page.Direction); break;
            case DeckIntent.ToggleReviewed: deckInteraction.ToggleReviewed(); break;
            case DeckIntent.ShowAnalysisModel: ShowAnalysisModel(); break;
            case DeckIntent.OpenAnalysisPreferences: workshopDialog = "analysis-preferences"; break;
            case DeckIntent.SaveAnalysisProfile profile: SaveAnalysisProfile(profile.Profile); break;
            case DeckIntent.EditAnalysisText edit: EditAnalysisText(edit.Field switch { AnalysisTextField.ProtectedCards => "protected", AnalysisTextField.Backup => "backup", AnalysisTextField.Pool => "pool", _ => "turn" }); break;
            case DeckIntent.SelectAnalysisEntry selection:
                if (preparedAnalysisLayout is { } layout && layout.Visible.Contains(selection.Index)) SelectAnalysisEntry(layout.Entries[selection.Index], selection.Index); break;
            case DeckIntent.CloseAdvancedEvidence: deckInteraction.CloseAdvanced(); break;
            case DeckIntent.ConsiderRecommendation recommendation:
                if (CurrentRecommendation(recommendation.RecommendationId) is { } offered) {
                    if (deckWorkspace.View.Workshop?.Considering.Any(card => card.Card.Card.Name.Equals(offered.Add.Name, StringComparison.OrdinalIgnoreCase)) == true) ShowAnalysisOption(offered);
                    else ConsiderAnalysis(offered);
                }
                break;
            case DeckIntent.ReviewRecommendation recommendation:
                if (CurrentRecommendation(recommendation.RecommendationId) != null) RecordAnalysis(recommendation.RecommendationId, recommendation.Decision.ToString().ToLowerInvariant(), ""); break;
            case DeckIntent.TestFinding finding: TestAnalysisFinding(finding.Question); break;
            case DeckIntent.ShowRelatedCards: ShowAnalysisCards(); break;
            default: throw new ArgumentOutOfRangeException(nameof(intent));
        }
    }
    private AnalysisRecommendation? CurrentRecommendation(string id) => AnalysisCurrent
        ? deckWorkspace.View.Analysis?.Report.Recommendations.FirstOrDefault(recommendation => recommendation.Id == id) : null;
}
