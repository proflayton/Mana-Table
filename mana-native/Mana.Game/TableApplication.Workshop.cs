using Mana.Contracts;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private WorkshopState? workshop;
    private DeckTest[] workshopTests = [];
    private HandPractice? workshopHand;
    private CatalogCard? workshopSavedCard;
    private bool workshopSavedBack;
    private string workshopSavedReturn = "saved-hand";
    private DeckVersionView? workshopVersionView;
    private string workshopVersionReturn = "";
    private bool workshopCompareVersion;
    private string workshopSourceChoice = "";
    private DeckSourcePreview? workshopSourcePreview;
    private string workshopTab = "build", workshopDialog = "", workshopRoleFilter = "";
    private string workshopError = "", workshopSelectedTest = "";
    private string? workshopMatchDeckId, workshopMatchTestId;
    private int workshopGeneration, workshopPage, workshopPlayers = 4;
    // Background tabs still render beneath a modal. Their page clamps must
    // never alter a modal's page, including when returning from a detail view.
    private readonly Dictionary<string, int> workshopDialogPages = new(StringComparer.Ordinal);
    private int WorkshopDialogPage {
        get => workshopDialogPages.GetValueOrDefault(workshopDialog);
        set => workshopDialogPages[workshopDialog] = Math.Max(0, value);
    }
    private int? workshopManaFilter;
    private int workshopOpponentSeat;
    private string workshopOpponentSearch = "";
    private readonly int[] workshopOpponents = [0, 1, 2];
    private SoloSetup? workshopSetup;
    private long workshopSetupRevision = -1;
    private bool WorkshopSetupCurrent => workshopSetup is { } setup && selectedDeckDetails is { } deck && setup.DeckId == deck.Id && workshopSetupRevision == deck.Revision;
    private string[] CurrentWorkshopOpponents() => !WorkshopSetupCurrent ? [] : workshopOpponents
        .Select(index => index >= 0 && index < workshopSetup!.Opponents.Length ? workshopSetup.Opponents[index].Id : "").ToArray();
    private void ApplyWorkshopPreparation(SoloPreparation prepared, long revision)
    {
        workshopSetup = prepared.Setup; workshopSetupRevision = revision;
        workshopPlayers = Math.Min(workshopPlayers, Math.Min(4, prepared.Setup.MaxPlayers));
        Array.Copy(prepared.Choices, workshopOpponents, workshopOpponents.Length);
    }
    private bool workshopPending, workshopInFlight, workshopSourceImport;
    private IDeckWorkshop? WorkshopEngine => services.Workshop;
    private bool WorkshopCurrent => workshop != null && workshop.DeckId == selectedDeckDetails?.Id && workshop.Revision == selectedDeckDetails?.Revision && !workshopPending && !workshopInFlight;
    private string WorkshopScope => $"{workshopTab}:{workshopDialog}:{printingGeneration}:{WorkshopDialogPage}:{workshopCompareVersion}:{workshopVersionView?.VersionId}:{workshopSourceChoice}:{workshopRoleFilter}:{workshopManaFilter}:{workshopGeneration}:{workshopPage}:{workshopPlayers}:{workshopSelectedTest}:{workshopOpponentSeat}:{workshopOpponentSearch}:{workshopSourcePreview?.Token}:{workshopHand?.TestId}:{workshopHand?.Draws}:{workshopHand?.Redraws}:{workspaceCardReading?.Identity}:{workspaceCardReading?.OtherFace}:{string.Join(',', workshopOpponents)}";
    private bool IsWorkshopMatch => match != null && workshopMatchTestId != null;

    private void RequestWorkshopRefresh(bool changed = false)
    {
        if (WorkshopEngine == null) return;
        workshopGeneration++; workshopPending = true; workshopError = "";
        if (changed) {
            DismissDeckWorkspaceReading();
            workshopDialogPages.Clear();
            workshop = null; workshopTests = []; workshopHand = null; workshopSetup = null;
            workshopManaFilter = null;
            workshopTab = "build"; workshopDialog = workshopRoleFilter = workshopSelectedTest = ""; workshopPage = 0;
        }
    }
    private void UpdateWorkshopData()
    {
        if (WorkshopEngine is not { } service || selectedDeckDetails is not { } deck || !deckEditor || match != null
            || !loaded || busy || !workshopPending || workshopInFlight) return;
        int generation = workshopGeneration;
        workshopPending = false; workshopInFlight = true;
        _ = Task.Run(async () => {
            try {
                var state = await service.WorkshopAsync(deck.Id, deck.Revision);
                var tests = await service.GetTestsAsync(deck.Id);
                updates.Enqueue(() => {
                    if (generation != workshopGeneration || selectedDeckDetails?.Id != deck.Id || selectedDeckDetails?.Revision != deck.Revision) return;
                    workshop = state; workshopTests = tests;
                    if (workshopHand?.VersionId != state.VersionId) workshopHand = null;
                    if (workshopSelectedTest.Length == 0) workshopSelectedTest = tests.FirstOrDefault()?.Id ?? "";
                });
            } catch (Exception ex) { updates.Enqueue(() => { if (generation == workshopGeneration) workshopError = ex.Message; }); }
            finally { updates.Enqueue(() => workshopInFlight = false); }
        });
    }
    private void ShowWorkshopTab(string tab)
    {
        workshopDialogPages.Clear();
        DismissDeckWorkspaceReading();
        workshopTab = tab; workshopDialog = ""; workshopPage = 0;
        deckTextFocus = ""; textFocus = false;
        if (tab == "results") RequestWorkshopRefresh();
        if (!CommanderDeck) workshopPlayers = 2;
    }
    private void SaveWorkshopMetadata(WorkshopMetadataEdit edit, string success)
    {
        if (WorkshopEngine is not { } service || selectedDeckDetails is not { } deck || !WorkshopCurrent) return;
        Work(async () => {
            var saved = await service.UpdateWorkshopAsync(deck.Id, deck.Revision, edit with { MetadataRevision = workshop!.MetadataRevision });
            updates.Enqueue(() => { workshop = saved; workshopGeneration++; message = success; });
        }, "Saving deck preferences", "Keeping your plan and card roles with this deck.", false);
    }
    private void EditWorkshopPlan(bool question)
    {
        if (!WorkshopCurrent) return;
        string? value = Ask(question ? "A question for your next test" : "Your deck's plan", question
            ? "What would you like to learn? This stays with each test." : "What is this deck trying to do? A short reminder for your future edits.", question ? workshop!.Question : workshop!.Plan);
        if (value != null) SaveWorkshopMetadata(question ? new(Question: value) : new(Plan: value), question ? "Test question saved." : "Deck plan saved.");
    }
    private void ToggleWorkshopRole(string role)
    {
        if (!WorkshopCurrent || DeckSelection is not { } card) return;
        var previous = workshop!.Roles.FirstOrDefault(r => r.CardId == card.Id);
        var roles = previous?.Roles.ToList() ?? [];
        if (roles.RemoveAll(value => value.Equals(role, StringComparison.OrdinalIgnoreCase)) == 0) roles.Add(role);
        var updated = new WorkshopRole(card.Id, roles.ToArray(), previous?.Pinned ?? false, "local");
        SaveWorkshopMetadata(new(Roles: workshop.Roles.Where(r => r.CardId != card.Id && r.Origin != "estimated").Append(updated).ToArray()), "Card roles saved. Roles can overlap.");
    }
    private void EditWorkshopRoles()
    {
        if (!WorkshopCurrent || DeckSelection is not { } card) return;
        var previous = workshop!.Roles.FirstOrDefault(r => r.CardId == card.Id);
        string? text = Ask("Card categories", "Comma-separated categories for " + card.Card.Name, string.Join(", ", previous?.Roles ?? []));
        if (text == null) return;
        string[] roles = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var updated = new WorkshopRole(card.Id, roles, previous?.Pinned ?? false, "local");
        SaveWorkshopMetadata(new(Roles: workshop.Roles.Where(r => r.CardId != card.Id && r.Origin != "estimated").Append(updated).ToArray()), "Categories saved.");
    }
    private void ToggleWorkshopPin()
    {
        if (!WorkshopCurrent || DeckSelection is not { } card) return;
        var previous = workshop!.Roles.FirstOrDefault(r => r.CardId == card.Id);
        var updated = new WorkshopRole(card.Id, previous?.Roles ?? [], !(previous?.Pinned ?? false), "local");
        SaveWorkshopMetadata(new(Roles: workshop.Roles.Where(r => r.CardId != card.Id && r.Origin != "estimated").Append(updated).ToArray()), updated.Pinned ? "Card marked as a favorite to keep." : "Keep marker removed.");
    }
    private void ConsideringQuantity(CatalogCard card, int quantity)
    {
        if (!WorkshopCurrent || quantity is < 0 or > 1000) return;
        var previous = workshop!.Considering.FirstOrDefault(c => c.CardId == card.Id);
        var entries = workshop.Considering.Where(c => c.CardId != card.Id).Select(c => new ConsideringEdit(c.CardId, c.Quantity, c.Categories)).ToList();
        if (quantity > 0) entries.Add(new(card.Id, quantity, previous?.Categories ?? []));
        SaveWorkshopMetadata(new(Considering: entries.ToArray()), quantity > 0 ? "Saved to Considering. It is excluded from games." : "Removed from Considering.");
    }
    private void MoveConsideringCard(string? cutCardId = null)
    {
        if (!WorkshopCurrent || WorkshopEngine is not { } service || selectedDeckDetails is not { } deck || DeckSelection is not { } card) return;
        bool fromConsidering = deckCardSection == "Considering";
        if (!fromConsidering && deckCardSection != "Main") return;
        long metadataRevision = workshop!.MetadataRevision;
        Work(async () => {
            await UnreadyForDeckEdit();
            var updated = await service.MoveWorkshopCardAsync(deck.Id, deck.Revision, metadataRevision, card.Id, fromConsidering ? "Main" : "Considering", cutCardId);
            updates.Enqueue(() => { deckCardSection = fromConsidering ? "Main" : "Considering"; ApplyDeck(updated); workshopDialog = ""; message = cutCardId == null ? "Card moved. Considering stays outside your playable list." : "Paired swap saved as one edit. Undo restores the playable list."; });
        }, "Moving your card", "Keeping the playable list separate from your options.");
    }
    private void RestoreWorkshopVersion(WorkshopVersion version)
    {
        if (WorkshopEngine is not { } service || selectedDeckDetails is not { } deck) return;
        Work(async () => {
            await UnreadyForDeckEdit();
            var restored = await service.RestoreWorkshopVersionAsync(deck.Id, deck.Revision, version.Id);
            updates.Enqueue(() => { ApplyDeck(restored); workshopDialog = ""; workshopTab = "build"; message = "Version restored. Your previous versions and test records are preserved."; });
        }, "Restoring this version", "Restoring its playable cards while keeping your test history.");
    }
    private void PrepareWorkshopTable()
    {
        if (selectedDeckDetails is not { } deck || !DeckReady || busy || Connected || match != null) return;
        int generation = deckSelectionGeneration;
        string[] previous = CurrentWorkshopOpponents();
        Work(async () => {
            var prepared = await PrepareSoloDeck(deck.Id, previous);
            updates.Enqueue(() => {
                if (generation != deckSelectionGeneration || selectedDeckDetails?.Revision != deck.Revision || Connected || match != null) return;
                ApplyWorkshopPreparation(prepared, deck.Revision);
            });
        }, "Preparing your test table", "Finding compatible AI opponents for this deck.");
    }
    private void StartWorkshopTable()
    {
        if (WorkshopEngine is not { } service || selectedDeckDetails is not { } deck || !DeckReady || !WorkshopCurrent || Connected || match != null) return;
        int generation = deckSelectionGeneration, requestedPlayers = workshopPlayers;
        long metadataRevision = workshop!.MetadataRevision;
        string question = workshop.Question, version = workshop.VersionId;
        string[] previous = CurrentWorkshopOpponents();
        var existing = WorkshopSetupCurrent && workshopSetup is { Opponents.Length: > 0 } setup
            && workshopOpponents.All(index => index >= 0 && index < setup.Opponents.Length)
            ? new SoloPreparation(setup, workshopOpponents.ToArray()) : null;
        bool Current() => generation == Volatile.Read(ref deckSelectionGeneration) && selectedDeckDetails is { } currentDeck && currentDeck.Id == deck.Id
            && currentDeck.Revision == deck.Revision && workshop is { } currentWorkshop && currentWorkshop.VersionId == version && currentWorkshop.MetadataRevision == metadataRevision
            && currentWorkshop.Question == question && requestedPlayers == workshopPlayers && previous.SequenceEqual(CurrentWorkshopOpponents())
            && !Connected && match == null;
        Work(async () => {
            var prepared = existing ?? await PrepareSoloDeck(deck.Id, previous);
            if (!Current()) throw new InvalidOperationException("Your deck or test setup changed. Start again with the current list.");
            int players = Math.Min(requestedPlayers, Math.Min(4, prepared.Setup.MaxPlayers));
            string[] opponents = prepared.Choices.Take(players - 1).Select(index => prepared.Setup.Opponents[index].Id).ToArray();
            await UnreadyForDeckEdit();
            if (!Current()) throw new InvalidOperationException("Your deck or test setup changed. Start again with the current list.");
            var test = await service.StartDeckTestAsync(deck.Id, deck.Revision, opponents, question);
            var state = await matchConnection.ObserveAsync();
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (state != null && (state.Status == "starting" || state.Players.Length == 0) && state.Status != "error" && DateTime.UtcNow < deadline) {
                await Task.Delay(100);
                state = await matchConnection.ObserveAsync();
            }
            updates.Enqueue(() => {
                ApplyWorkshopPreparation(prepared, deck.Revision);
                workshopMatchDeckId = deck.Id; workshopMatchTestId = test.Id; workshopSelectedTest = test.Id;
                setupModal = workshopDialog = ""; ResetTableView(); lobby = null; localMatch = true; Accept(state);
                message = "Testing your saved deck version.";
            });
            if (state?.Status == "error") throw new InvalidOperationException(state.Error ?? "The test game could not start.");
        }, "Gathering your test table", "Preparing your exact deck version, AI opponents and opening hand.", scene: true);
    }
    private void PracticeWorkshop(string action, int? index = null)
    {
        if (WorkshopEngine is not { } service || selectedDeckDetails is not { } deck || !WorkshopCurrent) return;
        Work(async () => {
            var hand = await service.PracticeAsync(deck.Id, deck.Revision, action, index, workshop!.Question);
            var tests = await service.GetTestsAsync(deck.Id);
            updates.Enqueue(() => { workshopHand = hand; workshopTests = tests; workshopSelectedTest = hand.TestId; workshopGeneration++; message = action == "keep" ? "Hand decision recorded for this version." : "Sample updated."; });
        }, action == "new" ? "Drawing a sample hand" : "Updating the sample", "Using the actual cards in this saved library.", false);
    }
    private void SaveWorkshopTestNote(DeckTest test, string? suppliedNote = null)
    {
        if (WorkshopEngine is not { } service || selectedDeckDetails is not { } deck) return;
        string? note = suppliedNote ?? Ask("Test notes", "What happened, and what would you change? These are your observations.", test.Note);
        if (note == null) return;
        Work(async () => {
            var saved = await service.SaveTestNoteAsync(deck.Id, test.Id, note);
            updates.Enqueue(() => { workshopTests = workshopTests.Select(t => t.Id == saved.Id ? saved : t).ToArray(); workshopGeneration++; message = "Test note saved."; });
        }, "Saving your test notes", "Keeping this note with the version you tested.", false);
    }
    private void ReturnToWorkshop(bool testAgain = false)
    {
        if (workshopMatchDeckId is not { } deckId) return;
        Work(async () => {
            // Returning early ends this local game explicitly and records it as conceded.
            if (match is { Status: not "finished" and not "error" } state) await matchConnection.ConcedeAsync(state.Id);
            var deck = await catalog.OpenDeckAsync(deckId);
            updates.Enqueue(() => {
                localMatch = false; session.Reset(); ResetTableView(); ApplyDeck(deck, true);
                workshopMatchDeckId = workshopMatchTestId = null;
                workshopTab = testAgain ? "test" : "results"; workshopDialog = "";
                message = "Back at your deck. This test keeps the version that was played.";
            });
        }, "Returning to your deck", "Saving the test record and reopening your deck workspace.", true);
    }

    private void ImportArchidekt(string? suppliedUrl = null)
    {
        if (WorkshopEngine is not { } service) return;
        string initial = "";
        if (suppliedUrl == null) {
            try { initial = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : ""; }
            catch (System.Runtime.InteropServices.ExternalException) { message = "The clipboard is unavailable. Enter the public Archidekt URL manually."; }
        }
        if (!initial.Contains("archidekt.com", StringComparison.OrdinalIgnoreCase)) initial = "";
        string? url = suppliedUrl ?? Ask("Link an Archidekt deck", "Paste the URL of a public Archidekt deck. Review the cards before importing.", initial);
        if (string.IsNullOrWhiteSpace(url)) return;
        Work(async () => {
            var preview = await service.PreviewDeckSourceAsync(url.Trim());
            updates.Enqueue(() => { workshopSourcePreview = preview; workshopSourceImport = true; workshopDialog = "source-preview"; WorkshopDialogPage = 0; setupModal = ""; });
        }, "Reading your Archidekt deck", "Fetching its playable cards, categories, and Considering list.");
    }
    private void RefreshArchidekt()
    {
        if (WorkshopEngine is not { } service || selectedDeckDetails is not { } deck) return;
        Work(async () => {
            var preview = await service.RefreshDeckSourceAsync(deck.Id, deck.Revision);
            updates.Enqueue(() => { workshopSourcePreview = preview; workshopSourceImport = false; workshopDialog = "source-preview"; WorkshopDialogPage = 0; });
        }, "Checking Archidekt", "Comparing the source with your last accepted list and local edits.");
    }
    private void ApplyArchidekt(string choice)
    {
        if (workshopSourcePreview == null) return;
        workshopSourceChoice = choice; workshopDialog = "source-confirm"; WorkshopDialogPage = 0;
    }
    private void ConfirmArchidekt()
    {
        if (WorkshopEngine is not { } service || workshopSourcePreview is not { } preview) return;
        string choice = workshopSourceChoice;
        bool importing = workshopSourceImport;
        string? deckId = importing ? null : selectedDeckDetails?.Id;
        long? revision = importing ? null : selectedDeckDetails?.Revision;
        Work(async () => {
            await UnreadyForDeckEdit();
            var deck = await service.ApplyDeckSourceAsync(preview.Token, deckId, revision, choice);
            var saved = await catalog.DecksAsync();
            updates.Enqueue(() => { decks = saved; ApplyDeck(deck, true); workshopDialog = ""; workshopSourcePreview = null; workshopTab = "build"; message = choice == "local" ? "Kept your playable list and accepted the source baseline." : "Archidekt deck saved. Earlier tests keep their original versions."; });
        }, choice == "copy" ? "Saving a source copy" : "Saving your linked deck", "Applying the reviewed changes and keeping version history.");
    }
    private void InspectWorkshopVersion(string? testId, string versionId)
    {
        if (services.Versions is not { } history || selectedDeckDetails is not { } deck) return;
        Work(async () => {
            var result = testId != null ? await history.InspectTestAsync(deck.Id, deck.Revision, testId)
                : await history.InspectVersionAsync(deck.Id, deck.Revision, versionId);
            updates.Enqueue(() => {
                if (selectedDeckDetails?.Id != deck.Id || selectedDeckDetails.Revision != deck.Revision) return;
                workshopVersionView = result; workshopVersionReturn = testId == null ? "versions" : "";
                workshopCompareVersion = false; workshopDialog = "version-view"; WorkshopDialogPage = 0;
            });
        }, "Opening the saved list", "Reading this version without changing your current deck.", false);
    }
    private void OpenArchidekt()
    {
        string? url = workshop?.Source?.Url;
        if (url == null) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { error = "Could not open Archidekt: " + ex.Message; }
    }
    private void OpenArchidektPreview()
    {
        if (workshopSourcePreview?.Url is not { } url) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { error = "Could not open Archidekt: " + ex.Message; }
    }
}
