using Mana.Contracts;
using Microsoft.Xna.Framework.Input;
using Keys = Microsoft.Xna.Framework.Input.Keys;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private string setupModal = "", joinAddress = "";
    private string? confirmedDeckId;
    private bool pickerPresets, setupSelectAll;
    private int connectionPage;
    private int friendPlayerCount = 4;
    private bool Connected => lobby is { Mode: not "idle" };
    private Seat? LocalSeat => lobby?.Seats.FirstOrDefault(s => s.Local);
    private bool DeckConfirmed => DeckReady && confirmedDeckId == selectedDeckDetails?.Id;
    private string LobbyScope => $"{DeckScope}:{DeckLibraryScope}:{setupModal}:{soloTab}:{pickerPresets}:{deckPage}:{pickerSearch}:{pickerSeat}:{reviewList}:{reviewSearch}:{reviewSection}:{reviewPage}:{reviewCard?.Section}:{reviewCard?.Id}:{connectionPage}:{confirmedDeckId}:{friendPlayerCount}:{lobby?.TableId}:{lobby?.Mode}:{LocalSeat?.Ready}:{lobby?.CanStart}:{string.Join(',', soloChoices)}:{lobbyAiSeat}:{lobbyAiPage}:{string.Join(';', lobby?.Seats.Select(s => $"{s.Index}:{s.Type}:{s.OpponentId}:{s.Ready}") ?? [])}";
    private void InvalidateDeckConfirmation() { confirmedDeckId = null; }
    private void SetupMode(bool solo) { if (soloTab != solo) InvalidateDeckConfirmation(); soloTab = solo; setupModal = ""; }
    private void OpenDeckPicker() { pickerPresets = decks.Count == 0; pickerDeleted = false; pickerSeat = -1; pickerSearch = ""; deckPage = 0; setupModal = "decks"; textFocus = false; RefreshDeletedDecks(); }
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
            int kept = index < previous.Length ? Array.FindIndex(setup.Opponents, opponent => opponent.Id == previous[index]) : -1;
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
        if (!Connected || match != null || ready && !DeckReady) return;
        string? deckId = ready ? selectedDeckDetails?.Id : null;
        if (ready && deckId == null) return;
        bool select = ready && !DeckConfirmed;
        int generation = deckSelectionGeneration;
        Work(async () => {
            if (select) await tables.SelectDeckAsync(deckId!);
            if (ready && generation != Volatile.Read(ref deckSelectionGeneration))
                throw new InvalidOperationException("Your selected deck changed. Ready again with the current list.");
            await tables.SetReadyAsync(ready); var state = await tables.LobbyAsync();
            updates.Enqueue(() => { lobby = state;
                if (ready && generation == deckSelectionGeneration) confirmedDeckId = deckId;
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
            var opponents = prepared.Choices.Take(Math.Min(3, prepared.Setup.MaxPlayers - 1)).Select(index => prepared.Setup.Opponents[index].Id).ToArray();
            await tables.StartSoloAsync(deck.Id, opponents); var state = await matchConnection.ObserveAsync();
            updates.Enqueue(() => {
                if (generation == deckSelectionGeneration) { ApplySoloPreparation(prepared); confirmedDeckId = deck.Id; }
                setupModal = ""; ResetTableView(); lobby = null; localMatch = true; Accept(state); message = "Starting your AI table...";
            });
        }, "Gathering your AI table", "Preparing your deck, AI opponents, and the opening hand.", scene: true);
    }
    private void StartFriends()
    {
        if (!DeckConfirmed || LocalSeat?.Ready != true || lobby is not { Mode: "hosting", CanStart: true }) return;
        Work(async () => { await tables.StartAsync(); var state = await matchConnection.ObserveAsync(); updates.Enqueue(() => { setupModal = ""; Accept(state); }); }, "Starting Commander", $"Bringing all {lobby.Seats.Length} players to the table.", scene: true);
    }
    private void ConnectLobby(bool hosting)
    {
        if (Connected || match != null) return;
        string address = joinAddress.Trim();
        if (!hosting && address.Length == 0) return;
        string? deckId = DeckReady ? selectedDeckDetails?.Id : null;
        int generation = deckSelectionGeneration;
        Work(async () => {
            var state = hosting ? await tables.HostAsync(friendPlayerCount, forward) : await tables.JoinAsync(address);
            // Publish a successful connection even if submitting its deck later fails.
            updates.Enqueue(() => { lobby = state; soloTab = false; setupModal = ""; textFocus = false; InvalidateDeckConfirmation(); });
            if (!string.IsNullOrWhiteSpace(preferences.PlayerName) && services.Social is { } social) {
                await social.SetPlayerNameAsync(state.TableId, preferences.PlayerName);
                var named = await tables.LobbyAsync(); updates.Enqueue(() => lobby = named);
            }
            if (deckId != null && generation == Volatile.Read(ref deckSelectionGeneration)) {
                await tables.SelectDeckAsync(deckId); var selected = await tables.LobbyAsync();
                updates.Enqueue(() => { lobby = selected; if (generation == deckSelectionGeneration) confirmedDeckId = deckId; });
            }
            updates.Enqueue(() => message = hosting ? "Table open. Invite your friends." : "You joined the table. Ready when you are.");
        }, hosting ? "Opening your table" : "Joining your friends", hosting ? $"Preparing {friendPlayerCount} seats and your table connection." : "Connecting to the table from your invite.", scene: true);
    }
    private void LeaveLobby() => Work(async () => {
        await tables.LeaveAsync(); updates.Enqueue(() => { lobby = null; InvalidateDeckConfirmation(); setupModal = ""; message = "You left the table."; });
    }, "Leaving the table", "Returning to your own setup.");
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
