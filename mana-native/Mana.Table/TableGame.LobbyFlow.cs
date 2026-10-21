using Mana.Contracts;
using Microsoft.Xna.Framework.Input;
using Keys = Microsoft.Xna.Framework.Input.Keys;

namespace Mana.Table;

internal sealed partial class TableGame
{
    private string setupModal = "", joinAddress = "";
    private string? confirmedDeckId;
    private bool pickerPresets, setupSelectAll;
    private int connectionPage;
    private int friendPlayerCount = 4;
    private bool Connected => lobby is { Mode: not "idle" };
    private Seat? LocalSeat => lobby?.Seats.FirstOrDefault(s => s.Local);
    private bool DeckConfirmed => DeckReady && confirmedDeckId == selectedDeckDetails?.Id;
    private string LobbyScope => $"{DeckScope}:{setupModal}:{soloTab}:{pickerPresets}:{deckPage}:{pickerSearch}:{pickerSeat}:{reviewList}:{reviewSearch}:{reviewSection}:{reviewPage}:{reviewCard?.Section}:{reviewCard?.Id}:{connectionPage}:{confirmedDeckId}:{friendPlayerCount}:{lobby?.TableId}:{lobby?.Mode}:{LocalSeat?.Ready}:{lobby?.CanStart}:{string.Join(',', soloChoices)}:{lobbyAiSeat}:{lobbyAiPage}:{string.Join(';', lobby?.Seats.Select(s => $"{s.Index}:{s.Type}:{s.OpponentId}:{s.Ready}") ?? [])}";
    private void InvalidateDeckConfirmation() { confirmedDeckId = null; }
    private void SetupMode(bool solo) { if (soloTab != solo) InvalidateDeckConfirmation(); soloTab = solo; setupModal = ""; }
    private void OpenDeckPicker() { pickerPresets = decks.Count == 0; pickerSeat = -1; pickerSearch = ""; deckPage = 0; setupModal = "decks"; textFocus = false; }
    private void ReviewDeck() {
        if (selectedDeckDetails == null) return;
        reviewList = false; reviewSearch = reviewSection = ""; reviewPage = 0; reviewBack = false;
        reviewCard = selectedDeckDetails.Entries.FirstOrDefault(e => e.Section == "Commander") ?? selectedDeckDetails.Entries.FirstOrDefault();
        setupModal = "confirm"; textFocus = false;
    }
    private void ChooseDeck(DeckSummary deck) {
        // Inspecting the document already at this table is read-only. Reopening it
        // in the engine would unnecessarily withdraw the player's ready seat.
        if (selectedDeckDetails?.Id == deck.Id) { ReviewDeck(); return; }
        Work(async () => { await UnreadyForDeckEdit(); await ReadSelectedDeck(deck); updates.Enqueue(ReviewDeck); }, "Opening " + deck.Name, "Getting your commander and deck ready to review.");
    }
    private void ChoosePreset(string id) => ImportDeck(() => engine.ImportPresetAsync(id), false,
        "Preparing " + (presets.FirstOrDefault(p => p.Id == id)?.Name ?? "your precon"), "Making a saved copy for you to review and play.");
    private void ConfirmDeck()
    {
        if (!DeckReady || selectedDeck is not { } deck || busy) return;
        if (DeckConfirmed) { setupModal = ""; message = "Your deck is still confirmed."; return; }
        Work(async () => {
            if (Connected) {
                await engine.SelectDeckAsync(deck.Id);
                var state = await engine.LobbyAsync(); updates.Enqueue(() => lobby = state);
            } else if (soloTab) {
                var setup = await engine.PrepareSoloAsync(deck.Id);
                if (setup.Opponents.Length == 0) throw new InvalidOperationException("This engine has no AI opponents available.");
                updates.Enqueue(() => {
                    var previous = soloSetup == null ? [] : soloChoices.Select(i => soloSetup.Opponents[i].Id).ToArray();
                    var defaults = setup.Opponents.Select((p, i) => (p, i)).Where(o => o.p.Name != setup.DeckName).Select(o => o.i).ToArray();
                    for (int i = 0; i < 3; i++) {
                        int kept = i < previous.Length ? Array.FindIndex(setup.Opponents, o => o.Id == previous[i]) : -1;
                        soloChoices[i] = kept >= 0 ? kept : defaults.Length > 0 ? defaults[i % defaults.Length] : 0;
                    }
                    soloSetup = setup;
                });
            }
            updates.Enqueue(() => { confirmedDeckId = deck.Id; setupModal = ""; message = Connected ? "Deck confirmed. Ready up when you are." : "Deck confirmed for this table."; });
        }, "Confirming your deck", soloTab && !Connected ? "Preparing your list and three AI opponents." : "Setting this deck at your seat.");
    }
    private void SetLobbyReady(bool ready)
    {
        if (ready && !DeckConfirmed) return;
        Work(async () => {
            await engine.SetReadyAsync(ready); var state = await engine.LobbyAsync();
            updates.Enqueue(() => { lobby = state; message = ready ? "You are ready. Waiting for the table." : "You are no longer ready."; });
        }, ready ? "Taking your seat" : "Updating your seat", ready ? "Letting the table know you are ready." : "Letting the table know you need a moment.");
    }
    private void StartSolo()
    {
        if (!DeckConfirmed || soloSetup is not { } setup || setup.DeckId != selectedDeck?.Id) return;
        var opponents = soloChoices.Take(Math.Min(3, setup.MaxPlayers - 1)).Select(i => setup.Opponents[i].Id).ToArray();
        Work(async () => {
            await engine.StartSoloAsync(setup.DeckId, opponents); var state = await engine.ObserveAsync();
            updates.Enqueue(() => { setupModal = ""; ResetTableView(); lobby = null; localMatch = true; Accept(state); message = "Starting your AI table..."; });
        }, "Gathering your AI table", $"Preparing your deck, {opponents.Length} AI opponent{(opponents.Length == 1 ? "" : "s")}, and the opening hand.", scene: true);
    }
    private void StartFriends()
    {
        if (!DeckConfirmed || LocalSeat?.Ready != true || lobby?.CanStart != true) return;
        Work(async () => { await engine.StartAsync(); var state = await engine.ObserveAsync(); updates.Enqueue(() => { setupModal = ""; Accept(state); }); }, "Starting Commander", $"Bringing all {lobby.Seats.Length} players to the table.", scene: true);
    }
    private void ConnectLobby(bool hosting)
    {
        string address = joinAddress.Trim();
        if (!hosting && address.Length == 0) return;
        string? deckId = DeckConfirmed ? selectedDeck?.Id : null;
        Work(async () => {
            var state = hosting ? await engine.HostAsync(friendPlayerCount, forward) : await engine.JoinAsync(address);
            // Publish a successful connection even if submitting its deck later fails.
            updates.Enqueue(() => { lobby = state; soloTab = false; setupModal = ""; textFocus = false; InvalidateDeckConfirmation(); });
            if (!string.IsNullOrWhiteSpace(preferences.PlayerName) && engine is ITableSocial social) {
                await social.SetPlayerNameAsync(state.TableId, preferences.PlayerName);
                var named = await engine.LobbyAsync(); updates.Enqueue(() => lobby = named);
            }
            if (deckId != null) {
                await engine.SelectDeckAsync(deckId); var selected = await engine.LobbyAsync();
                updates.Enqueue(() => { lobby = selected; confirmedDeckId = deckId; });
            }
            updates.Enqueue(() => message = hosting ? "Table open. Invite your friends." : "You joined the table. Confirm your deck and ready up.");
        }, hosting ? "Opening your table" : "Joining your friends", hosting ? $"Preparing {friendPlayerCount} seats and your table connection." : "Connecting to the table from your invite.", scene: true);
    }
    private void LeaveLobby() => Work(async () => {
        await engine.LeaveAsync(); updates.Enqueue(() => { lobby = null; InvalidateDeckConfirmation(); setupModal = ""; message = "You left the table."; });
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
