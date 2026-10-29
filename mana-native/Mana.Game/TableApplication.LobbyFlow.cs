using System.Collections.Immutable;
using Mana.Contracts;
using Microsoft.Xna.Framework.Input;
using Keys = Microsoft.Xna.Framework.Input.Keys;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private string setupModal = "", joinAddress = "";
    private string? preparedSoloDeckId;
    private bool pickerPresets, setupSelectAll;
    private int connectionPage, homeDeckPage;
    private int friendPlayerCount = 4;
    private void HandleLobbyIntent(ShellIntent.Lobby intent)
    {
        switch (intent) {
            case ShellIntent.ChoosePlayMode choice: ChoosePlayMode(choice.Solo); break;
            case ShellIntent.OpenPlayHome: OpenPlayHome(); break;
            case ShellIntent.OpenDeckLibrary: OpenDeckLibrary(); break;
            case ShellIntent.EditDeck edit:
                if (edit.ClearInput) ClearExperienceInput();
                if (edit.AtReviewCard) OpenDeckEditorAt(reviewList ? reviewCard : null); else OpenDeckEditor();
                break;
            case ShellIntent.ReviewDeck reviewRequest:
                if (reviewRequest.ClearInput) ClearExperienceInput();
                ReviewDeck(); break;
            case ShellIntent.SelectHomeDeck select:
                if (decks.FirstOrDefault(deck => deck.Id == select.DeckId) is { } selectedHomeDeck) { ClearExperienceInput(); ChooseDeck(selectedHomeDeck); }
                break;
            case ShellIntent.Page page: ChangeShellPage(page.Target, page.Delta); break;
            case ShellIntent.OpenOpponentPicker opponent: OpenOpponentPicker(opponent.Seat); break;
            case ShellIntent.SetFriendCount count when !busy && count.Players is >= 2 and <= 4: friendPlayerCount = count.Players; break;
            case ShellIntent.Connect connect: ConnectLobby(connect.Hosting); break;
            case ShellIntent.OpenJoin: ClearExperienceInput(); OpenJoin(); break;
            case ShellIntent.OpenConnection connection:
                if (connection.ClearInput) ClearExperienceInput();
                setupModal = "connection"; connectionPage = 0; break;
            case ShellIntent.LeaveLobby: LeaveLobby(); break;
            case ShellIntent.SetReady ready: SetLobbyReady(ready.Ready); break;
            case ShellIntent.StartSolo: StartSolo(); break;
            case ShellIntent.StartFriends: StartFriends(); break;
            case ShellIntent.CloseSetup: CloseSetupModal(); break;
            case ShellIntent.ShowReview reviewMode: reviewList = reviewMode.List; textFocus = false; break;
            case ShellIntent.FocusSetupInput focus:
                if (focus.Clear) SetSetupInput("");
                textFocus = true;
                if (focus.ResetSelection) setupSelectAll = false;
                break;
            case ShellIntent.TogglePortForwarding: forward = !forward; break;
            case ShellIntent.CopyInternetInvite:
                if (lobby?.InternetInvite is { Length: > 0 } invite) { Clipboard.SetText(invite); message = "Internet invite copied."; }
                break;
            case ShellIntent.CopyAddress copy:
                if (lobby?.Addresses.FirstOrDefault(address => address.Url == copy.Url) is { } currentAddress) { Clipboard.SetText(currentAddress.Url); message = currentAddress.Label + " address copied."; }
                break;
            case ShellIntent.SelectDeckCollection collection:
                pickerPresets = collection.Collection == DeckCollection.Presets; pickerDeleted = collection.Collection == DeckCollection.Deleted; deckPage = 0; break;
            case ShellIntent.ChoosePickerDeck pickerChoice: ChoosePickerDeck(pickerChoice.DeckId); break;
            case ShellIntent.FlipReviewCard: reviewBack = !reviewBack; break;
            case ShellIntent.SelectReviewSection section: reviewSection = section.Section; reviewPage = 0; break;
            case ShellIntent.SelectReviewCard selectedCard:
                if (ReviewRows().FirstOrDefault(row => row.Id == selectedCard.CardId && row.Section == selectedCard.Section) is { } reviewRow) { reviewCard = reviewRow; reviewBack = false; textFocus = false; }
                break;
            case ShellIntent.ResizeLobby resize: ResizeLobby(resize.Players); break;
            case ShellIntent.SetTempo tempo: SetLobbyTempo(tempo.Preset, tempo.Expiry); break;
            case ShellIntent.OpenLobbyAi openAi: OpenLobbyAiPicker(openAi.Seat); break;
            case ShellIntent.SelectLobbyAi selectedAi: SetLobbyAi(selectedAi.OpponentId); break;
            case ShellIntent.RemoveLobbyAi removedAi: RemoveLobbyAi(removedAi.Seat); break;
            case ShellIntent.ImportFile: ImportFile(); break;
            case ShellIntent.ImportClipboard: ImportClipboard(); break;
            case ShellIntent.EditPlayerName: ClearExperienceInput(); OpenPlayerName(); break;
            case ShellIntent.SavePlayerName: SavePlayerName(); break;
        }
    }
    private void ChangeShellPage(ShellPage target, int delta)
    {
        if (delta is not (-1 or 1)) return;
        switch (target) {
            case ShellPage.HomeDecks: homeDeckPage += delta; break;
            case ShellPage.DeckPicker: deckPage += delta; break;
            case ShellPage.Review: reviewPage += delta; break;
            case ShellPage.Connection: connectionPage += delta; break;
            case ShellPage.LobbyAi: lobbyAiPage += delta; break;
            case ShellPage.LabGallery: labGalleryPage += delta; break;
        }
    }
    private void PrepareLobbyPresentation()
    {
        if (!Connected && !deckEditor) homeDeckPage = Math.Clamp(homeDeckPage, 0, Math.Max(0, (decks.Length - 1) / 4));
        if (setupModal == "decks") deckPage = Math.Clamp(deckPage, 0, Math.Max(0, (PickerChoices().Length - 1) / PickerPageSize));
        if (setupModal == "confirm" && reviewList) reviewPage = Math.Clamp(reviewPage, 0, Math.Max(0, (ReviewRows().Length - 1) / ReviewRowsPerPage));
        if (setupModal == "connection" && lobby is { } current) connectionPage = Math.Clamp(connectionPage, 0, Math.Max(0, (current.Addresses.Length - 1) / 5));
        if (setupModal == "lobby-ai" && lobby is { } ai) lobbyAiPage = Math.Clamp(lobbyAiPage, 0, Math.Max(0, (ai.AiOpponents.Length - 1) / 6));
    }
    private bool Connected => lobby is { Mode: not "idle" };
    private Seat? LocalSeat => lobby?.Seats.FirstOrDefault(s => s.Local);
    private bool DeckConfirmed => DeckReady && !deckEditor && (Connected
        ? lobbyDeck.View.Document == DesiredLobbyDeck() && lobbyDeck.Matches(lobby!)
        : preparedSoloDeckId == selectedDeckDetails?.Id);
    private string LobbyScope => $"{DeckScope}:{DeckLibraryScope}:{setupModal}:{soloTab}:{pickerPresets}:{deckPage}:{pickerSearch}:{pickerSeat}:{reviewList}:{reviewSearch}:{reviewSection}:{reviewPage}:{reviewCard?.Section}:{reviewCard?.Id}:{connectionPage}:{preparedSoloDeckId}:{lobbyDeck.View.Generation}:{lobbyDeck.View.Status}:{friendPlayerCount}:{lobby?.TableId}:{lobby?.Revision}:{lobby?.Mode}:{LocalSeat?.Ready}:{lobby?.CanStart}:{string.Join(',', soloChoices)}:{lobbyAiSeat}:{lobbyAiPage}:{string.Join(';', lobby?.Seats.Select(s => $"{s.Index}:{s.Type}:{s.OpponentId}:{s.Ready}") ?? [])}";
    private void InvalidateDeckConfirmation() { preparedSoloDeckId = null; }
    private void SetupMode(bool solo) { if (soloTab != solo) InvalidateDeckConfirmation(); soloTab = solo; setupModal = ""; }
    private void OpenDeckPicker() { pickerPresets = decks.Length == 0; pickerDeleted = false; pickerSeat = -1; pickerSearch = ""; deckPage = 0; setupModal = "decks"; textFocus = false; RefreshDeletedDecks(); }
    private void ReviewDeck() {
        if (selectedDeckDetails == null) return;
        reviewList = false; reviewSearch = reviewSection = ""; reviewPage = 0; reviewBack = false;
        reviewCard = selectedDeckDetails.Entries.FirstOrDefault(e => e.Section == "Commander") ?? selectedDeckDetails.Entries.FirstOrDefault();
        setupModal = "confirm"; textFocus = false;
    }
    private void ChooseDeck(DeckSummary deck) {
        // Inspecting the document already at this table is read-only. Reopening it
        // in the engine would unnecessarily withdraw the player's ready seat.
        if (selectedDeckDetails?.Id == deck.Id) { setupModal = ""; textFocus = false; return; }
        Work(async () => { await UnreadyForDeckEdit(); await ReadSelectedDeck(deck); }, "Opening " + deck.Name, "Getting your commander and deck ready.");
    }
    private void ChoosePreset(string id) => ImportDeck(() => catalog.ImportPresetAsync(id), false,
        "Preparing " + (presets.FirstOrDefault(p => p.Id == id)?.Name ?? "your precon"), "Making a saved copy for you to review and play.");
    private sealed record SoloPreparation(SoloSetup Setup, int[] Choices);
    private string[] CurrentSoloOpponents() => soloSetup is not { } setup ? []
        : soloChoices.Select(index => index >= 0 && index < setup.Opponents.Length ? setup.Opponents[index].Id : "").ToArray();
    private async Task<SoloPreparation> PrepareSoloDeck(string deckId, string[] previous)
    {
        var setup = await tables.PrepareSoloAsync(deckId);
        if (setup.DeckId != deckId) throw new InvalidOperationException("The prepared table belongs to another deck. Try Play again.");
        if (setup.Opponents.Length == 0 || setup.MaxPlayers < 2) throw new InvalidOperationException("This engine has no compatible AI opponents available.");
        var defaults = setup.Opponents.Select((opponent, index) => (opponent, index))
            .Where(option => option.opponent.Name != setup.DeckName).Select(option => option.index).ToArray();
        int[] choices = Enumerable.Range(0, soloChoices.Length).Select(index => {
            int kept = index < previous.Length ? setup.Opponents.IndexOf(setup.Opponents.FirstOrDefault(opponent => opponent.Id == previous[index])!) : -1;
            return kept >= 0 ? kept : defaults.Length > 0 ? defaults[index % defaults.Length] : 0;
        }).ToArray();
        return new(setup, choices);
    }
    private void ApplySoloPreparation(SoloPreparation prepared)
    {
        soloSetup = prepared.Setup;
        Array.Copy(prepared.Choices, soloChoices, soloChoices.Length);
    }
    private void SetLobbyReady(bool ready)
    {
        if (busy || ActivityOpen || !Connected || match != null || ready && !DeckReady) return;
        var document = DesiredLobbyDeck();
        if (ready && document is not { DeckId.Length: > 0 }) return;
        lobbyDeck.Observe(document);
        if (ready) lobbyDeck.Retry();
        var selection = ready && !lobbyDeck.Matches(lobby!) ? lobbyDeck.TryBegin() : null;
        if (ready && !lobbyDeck.Matches(lobby!) && selection == null) return;
        var timing = lobby?.Tempo;
        string tableId = lobby?.TableId ?? "";
        int generation = deckSelectionGeneration;
        Work(async () => {
            if (selection != null) await PublishLobbyDeck(selection);
            if (ready && generation != Volatile.Read(ref deckSelectionGeneration))
                throw new InvalidOperationException("Your selected deck changed. Ready again with the current list.");
            if (services.Tempo is { SupportsTempo: true } tempo && timing != null)
                await tempo.SetReadyAsync(tableId, ready, timing.Revision);
            else await tables.SetReadyAsync(ready);
            var state = await tables.LobbyAsync();
            updates.Enqueue(() => { session.AcceptLobby(state);
                message = ready ? "You are ready. Waiting for the table." : "You are no longer ready."; });
        }, ready ? "Taking your seat" : "Updating your seat", ready ? "Letting the table know you are ready." : "Letting the table know you need a moment.");
    }
    private void StartSolo()
    {
        if (Connected || match != null || !DeckReady || selectedDeckDetails is not { } deck || selectedDeck?.Id != deck.Id) return;
        int generation = deckSelectionGeneration;
        var existing = DeckConfirmed && soloSetup is { } setup && setup.DeckId == deck.Id
            ? new SoloPreparation(setup, soloChoices.ToArray()) : null;
        string[] previous = CurrentSoloOpponents();
        Work(async () => {
            var prepared = existing ?? await PrepareSoloDeck(deck.Id, previous);
            if (generation != Volatile.Read(ref deckSelectionGeneration))
                throw new InvalidOperationException("Your selected deck changed. Play again with the current list.");
            var opponents = prepared.Choices.Take(Math.Min(3, prepared.Setup.MaxPlayers - 1)).Select(index => prepared.Setup.Opponents[index].Id).ToImmutableArray();
            await tables.StartSoloAsync(deck.Id, opponents); var state = await matchConnection.ObserveAsync();
            updates.Enqueue(() => {
                if (generation == deckSelectionGeneration) { ApplySoloPreparation(prepared); preparedSoloDeckId = deck.Id; }
                setupModal = ""; ResetTableView(); session.BindLobby(null); localMatch = true; Accept(state); message = "Starting your AI table...";
            });
        }, "Gathering your AI table", "Preparing your deck, AI opponents, and the opening hand.", scene: true, changesMatch: true);
    }
    private void StartFriends()
    {
        if (!DeckConfirmed || LocalSeat?.Ready != true || lobby is not { Mode: "hosting", CanStart: true }) return;
        Work(async () => { await tables.StartAsync(); var state = await matchConnection.ObserveAsync(); updates.Enqueue(() => { setupModal = ""; Accept(state); }); }, "Starting Commander", $"Bringing all {lobby.Seats.Length} players to the table.", scene: true, changesMatch: true);
    }
    private void ConnectLobby(bool hosting)
    {
        if (Connected || match != null) return;
        string address = joinAddress.Trim();
        if (!hosting && address.Length == 0) return;
        Work(async () => {
            var state = hosting ? await tables.HostAsync(friendPlayerCount, forward) : await tables.JoinAsync(address);
            // Publish a successful connection even if submitting its deck later fails.
            updates.Enqueue(() => { session.BindLobby(state); soloTab = false; setupModal = ""; textFocus = false; InvalidateDeckConfirmation(); });
            if (!string.IsNullOrWhiteSpace(preferences.PlayerName) && services.Social is { } social) {
                await social.SetPlayerNameAsync(state.TableId, preferences.PlayerName);
                var named = await tables.LobbyAsync(); updates.Enqueue(() => session.AcceptLobby(named));
            }
            updates.Enqueue(() => message = hosting ? "Table open. Invite your friends." : "You joined the table. Ready when you are.");
        }, hosting ? "Opening your table" : "Joining your friends", hosting ? $"Preparing {friendPlayerCount} seats and your table connection." : "Connecting to the table from your invite.", scene: true, changesMatch: true);
    }
    private void LeaveLobby() => Work(async () => {
        await tables.LeaveAsync(); updates.Enqueue(() => { session.BindLobby(null); InvalidateDeckConfirmation(); setupModal = ""; message = "You left the table."; });
    }, "Leaving the table", "Returning to your own setup.", changesMatch: true);
    private void OpenJoin()
    {
        string value = "", clipboardError = "";
        try { value = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : ""; }
        catch (System.Runtime.InteropServices.ExternalException) { clipboardError = "The clipboard is unavailable. Enter your invite manually or try Paste again."; }
        joinAddress = value.Length <= 220 && !value.Contains('\n') && (value.StartsWith("MT1-") || value.Contains(':')) ? value : "";
        setupModal = "join"; textFocus = true; setupSelectAll = joinAddress.Length > 0; error = clipboardError;
    }
    private void SetupText(char character)
    {
        if (!SetupHasInput || !textFocus || busy) return;
        string value = setupSelectAll ? "" : SetupInput;
        if (character == '\b') value = value.Length == 0 ? "" : value[..^1];
        else if (!char.IsControl(character)) value += character;
        else return;
        SetSetupInput(value); setupSelectAll = false;
    }
    private void SetupKeys(KeyboardState keys)
    {
        if (!SetupHasInput || !textFocus || busy || match != null || deckEditor) return;
        bool control = keys.IsKeyDown(Keys.LeftControl) || keys.IsKeyDown(Keys.RightControl);
        if (control && keys.IsKeyDown(Keys.A) && previousKeys.IsKeyUp(Keys.A)) setupSelectAll = true;
        if (control && keys.IsKeyDown(Keys.V) && previousKeys.IsKeyUp(Keys.V)) {
            try {
                if (Clipboard.ContainsText()) {
                    var value = (setupSelectAll ? "" : SetupInput) + Clipboard.GetText().Replace("\r", "").Replace("\n", "");
                    SetSetupInput(value); setupSelectAll = false;
                }
            } catch (System.Runtime.InteropServices.ExternalException) { error = "Could not read the clipboard. Try Paste again or enter the invite manually."; }
        }
        if (setupModal == "join" && keys.IsKeyDown(Keys.Enter) && previousKeys.IsKeyUp(Keys.Enter)) ConnectLobby(false);
        if (setupModal == "profile" && keys.IsKeyDown(Keys.Enter) && previousKeys.IsKeyUp(Keys.Enter)) SavePlayerName();
    }
    private void CloseSetupModal() { if (busy) return; setupModal = ""; textFocus = false; }
}
