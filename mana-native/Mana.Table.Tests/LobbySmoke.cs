using Mana.Contracts;
using Mana.Forge;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using System.Text.Json;
using Point = Microsoft.Xna.Framework.Point;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Keys = Microsoft.Xna.Framework.Input.Keys;

namespace Mana.Table;

// Real native controls, real persisted deck and four independent Forge processes.
internal sealed class LobbySmoke(TableGame.Probe view, ForgePaths paths) : ITableAutomation, IDisposable
{
    private readonly List<string> checks = [];
    private readonly List<ForgeEngine> guests = [];
    private readonly DateTime deadline = DateTime.UtcNow.AddMinutes(7);
    private Task? background;
    private int stage, next, mousePhase;
    private Point mouse;
    private string[] choices = [];
    private string joinTo = "";
    private string otherDeckId = "";
    private string opponentId = "", editCardId = "";
    private long reviewRevision;
    private KeyboardState keyboard;
    private string gameChatScope = "";
    private TableConversation[] peerConversations = [];
    private bool staleChatRejected;
    public KeyboardState? ReadKeyboard() => keyboard;
    private Lobby? peerLobby;
    private double nextProgress, previewAfter;
    public string? ExpectedClick { get; private set; }
    public bool ClickAccepted { get; set; }
    private DeckDetails Deck => view.DeckDetails!;
    private Seat? Local => view.Lobby?.Seats.FirstOrDefault(s => s.Local);
    public MouseState ReadMouse()
    {
        bool down = mousePhase == 1;
        if (mousePhase is 1 or 2) mousePhase++;
        return new(mouse.X, mouse.Y, 0, down ? ButtonState.Pressed : ButtonState.Released,
            ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);
    }
    private void Check(bool valid, string text)
    {
        if (!valid) { Capture("failure"); throw new InvalidOperationException($"Lobby stage {stage}: {text}"); }
        checks.Add(text); File.AppendAllText(Path.Combine(view.Profile, "lobby-progress.log"), text + "\n");
    }
    private void Click(string id, int target)
    {
        var hit = view.Hits.LastOrDefault(h => h.Id == id) ?? throw new InvalidOperationException($"Missing {id} at lobby stage {stage}");
        var p = hit.Bounds.Center; var viewport = view.GraphicsDevice.Viewport;
        float scale = Math.Min(viewport.Width / 1600f, viewport.Height / (float)view.DesignHeight);
        mouse = new((int)((viewport.Width - 1600 * scale) / 2 + p.X * scale), (int)((viewport.Height - view.DesignHeight * scale) / 2 + p.Y * scale));
        ExpectedClick = id; ClickAccepted = false; next = target; mousePhase = 1;
    }
    private void Capture(string name)
    {
        using var file = File.Create(Path.Combine(view.Profile, name + ".png")); view.Surface.SaveAsPng(file, view.Surface.Width, view.Surface.Height);
        File.WriteAllText(Path.Combine(view.Profile, name + ".json"), JsonSerializer.Serialize(new { view.DeckDetails, view.DeckConfirmed, view.SetupModal, view.Lobby, view.SoloSetup, view.Match }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private string[] Opponents() => view.SoloChoices.Select(i => view.SoloSetup!.Opponents[i].Id).ToArray();
    private static string LocalAddress(Lobby lobby) => lobby.Addresses.First(a => a.Url.StartsWith("127.0.0.1:")).Url;
    private async Task JoinGuests()
    {
        string address = LocalAddress(view.Lobby!);
        for (int i = 0; i < 3; i++) guests.Add(new ForgeEngine(paths with { Profile = Path.Combine(paths.Profile, "peer-" + i) }));
        await Task.WhenAll(guests.Select(async (guest, index) => {
            await guest.InitializeAsync();
            var deck = await guest.ImportAsync("Guest Commander deck", "Commander\n1 Rhys the Redeemed\nDeck\n99 Forest");
            var joined = await guest.JoinAsync(address);
            await guest.SetPlayerNameAsync(joined.TableId, index == 0 ? "Jebb" : "Friend " + (index + 1));
            await guest.SelectDeckAsync(deck.Id); await guest.SetReadyAsync(true);
        }));
    }
    private async Task FinishOpening()
    {
        // Complete each seat's real starting-player and keep-hand decisions before
        // ending the game. Concession during Forge's pregame setup is not a stable
        // lifecycle boundary for this lobby regression.
        var openingDeadline = DateTime.UtcNow.AddSeconds(45);
        ICardEngine[] seats = [view.Engine, .. guests];
        while (DateTime.UtcNow < openingDeadline) {
            foreach (var seat in seats) {
                var state = await seat.ObserveAsync();
                if (state?.PhaseKey == "MAIN1") return;
                if (state?.Decision is not { } decision) continue;
                DecisionReply? reply = decision.Kind switch {
                    "choice" => new(state.Id, decision.Id, ReplyAction.Choose, Choices: decision.Choices.Take(Math.Max(decision.Min, Math.Min(1, decision.Max))).Select(c => c.Index).ToArray()),
                    "reveal" => new(state.Id, decision.Id, ReplyAction.Choose, Choices: []),
                    _ when decision.PlayerChoices.Length > 0 => new(state.Id, decision.Id, ReplyAction.SelectPlayer, Player: decision.PlayerChoices[0]),
                    _ when decision.OkEnabled => new(state.Id, decision.Id, ReplyAction.Confirm),
                    _ => null
                };
                if (reply != null) try { await seat.ReplyAsync(reply); }
                    catch (InvalidOperationException ex) when (ex.Message.Contains("choice has changed")) { }
            }
            await Task.Delay(30);
        }
        throw new TimeoutException("Four-seat opening decisions did not reach the first main phase");
    }
    public void Frame(Texture2D surface)
    {
        if (view.Now >= nextProgress) {
            nextProgress = view.Now + 2;
            File.WriteAllText(Path.Combine(view.Profile, "lobby-latest.json"), JsonSerializer.Serialize(new { stage, view.Busy, view.Polling, view.Match, view.Lobby }));
        }
        if (DateTime.UtcNow > deadline) { Capture("timeout"); throw new TimeoutException("Lobby UI timed out at " + stage); }
        if (view.Error.Length > 0 && stage is not (36 or 37)) { Capture("failure"); throw new InvalidOperationException(view.Error); }
        if (view.ChatError.Length > 0) { Capture("failure"); throw new InvalidOperationException(view.ChatError); }
        if (mousePhase is 1 or 2) return;
        if (mousePhase == 3) {
            if (!ClickAccepted) { Capture("failure"); throw new InvalidOperationException("Rejected lobby click: " + ExpectedClick); }
            mousePhase = 0; ExpectedClick = null; stage = next;
        }
        if (background != null) {
            if (!background.IsCompleted) return;
            background.GetAwaiter().GetResult(); background = null;
        }
        // Observation can run continuously between pregame/finished snapshots.
        // Normal input is allowed during polls; ClientSession discards stale results.
        if (!view.Loaded || view.Busy) return;
        switch (stage) {
            case 0:
                Check(!view.DeckConfirmed && !view.Hits.Any(h => h.Id == "start-solo"), "An unconfirmed deck cannot start a solo table");
                Click("player-name", 400); break;
            case 400: view.TypeText("Table Host"); stage++; break;
            case 401: Click("player-name-save", 402); break;
            case 402:
                Check(view.PlayerName == "Table Host" && view.SetupModal.Length == 0, "A display name is saved through the native profile field");
                Click("change-deck", 1); break;
            case 1:
                Check(view.SetupModal == "decks" && !view.Hits.Any(h => h.Id == "friends-tab"), "Deck picker isolates input from the lobby behind it");
                Click("picker-presets", 2); break;
            case 2: Click("picker-search", 200); break;
            case 200: view.TypeSetupText("no deck has this name"); stage++; break;
            case 201:
                Check(!view.Hits.Any(h => h.Id.StartsWith("preset:") || h.Id == "decks-next"), "An empty deck search has no stale deck or paging actions");
                Click("picker-search-clear", 202); break;
            case 202: view.TypeSetupText(view.Presets[0].Name.ToUpperInvariant()); stage++; break;
            case 203:
                Check(view.Hits.Count(h => h.Id.StartsWith("preset:")) == 1, "Deck search filters names without case sensitivity");
                Capture("01-deck-picker"); Click("preset:" + view.Presets[0].Id, 3); break;
            case 3:
                if (previewAfter == 0) { previewAfter = view.Now + 1; break; }
                if (view.Now < previewAfter) break;
                Check(view.SetupModal == "confirm" && !view.DeckConfirmed && Deck.Problem.Length == 0, "Selecting a precon opens a review without implicitly confirming it");
                Capture("02-deck-confirmation"); Click("setup-close", 4); break;
            case 4:
                Check(!view.DeckConfirmed && !view.Hits.Any(h => h.Id == "start-solo"), "Dismissing the review does not confirm the deck");
                Click("review-deck", 5); break;
            case 5: Click("confirm-deck", 6); break;
            case 6:
                Check(view.DeckConfirmed && view.SoloSetup != null && view.Hits.Any(h => h.Id == "start-solo"), "Explicit confirmation prepares a playable four-seat AI lobby");
                Click("ai-next:0", 7); break;
            case 7: choices = Opponents(); Click("choose-ai:1", 210); break;
            case 210:
                Check(!view.Hits.Any(h => h.Id is "import" or "picker-saved"), "AI deck selection exposes only supported opponent decks");
                Click("picker-search", 211); break;
            case 211:
                var opponent = view.SoloSetup!.Opponents.First(o => o.Id != choices[1]); opponentId = opponent.Id;
                view.TypeSetupText(opponent.Name); stage++; break;
            case 212: Capture("08-ai-deck-picker"); Click("opponent:" + opponentId, 213); break;
            case 213:
                Check(view.DeckConfirmed && Opponents()[1] == opponentId && Opponents()[0] == choices[0] && Opponents()[2] == choices[2], "Choosing an AI deck changes only that seat and preserves the confirmed player deck");
                choices = Opponents(); Capture("03-solo-lobby"); Click("edit-deck", 8); break;
            case 8:
                Check(view.DeckEditor && !view.DeckConfirmed, "Opening the builder unlocks the confirmed deck");
                view.RenameDeck("My merfolk table"); stage++; break;
            case 9: otherDeckId = Deck.Id; view.DuplicateDeck("My merfolk table copy"); stage = 109; break;
            case 109: Click("deck-done", 10); break;
            case 10:
                Check(view.SetupModal == "confirm" && Deck.Name == "My merfolk table copy" && !view.DeckConfirmed, "Returning from the builder reviews the saved changes before confirming again");
                Click("confirm-deck", 11); break;
            case 11:
                Check(view.DeckConfirmed && Opponents().SequenceEqual(choices), "Reconfirming an edited deck preserves all three AI choices");
                Click("friends-tab", 12); break;
            case 12:
                Check(!view.DeckConfirmed, "Switching between AI and friend tables requires a fresh confirmation");
                Click("connection-options", 13); break;
            case 13: Click("forward", 14); break;
            case 14: Click("setup-close", 15); break;
            case 15: Click("host", 16); break;
            case 16:
                Check(view.Lobby is { Mode: "hosting", Seats.Length: 4, CanStart: false, PortMapping: "disabled" } && Local?.Ready == false, "Hosting opens four synchronized seats with no implicit readiness or router mapping");
                Click("review-deck", 17); break;
            case 17: Click("confirm-deck", 18); break;
            case 18:
                Check(view.DeckConfirmed && Local is { Ready: false } && Local.Deck == Deck.Name, "Confirming submits the exact saved deck but leaves readiness explicit");
                Click("ready", 19); break;
            case 19:
                Check(Local?.Ready == true && view.Lobby?.CanStart == false, "A ready host still waits for the other three seats");
                Click("review-deck", 119); break;
            case 119: reviewRevision = Deck.Revision; Click("review-list", 230); break;
            case 230:
                Check(view.ReviewedCard?.Section == "Commander" && view.Hits.Any(h => h.Id == "review-next"), "Full deck review starts with the commander and paginates the complete saved list");
                Click("review-next", 231); break;
            case 231:
                Check(!view.Hits.Any(h => h.Id.StartsWith("review-card:Commander:")), "Paging shows different card rows without duplicating the command zone");
                Click("review-section:command", 232); break;
            case 232:
                Check(view.Hits.Count(h => h.Id.StartsWith("review-card:")) == 1 && !view.Hits.Any(h => h.Id == "review-prev"), "Changing sections resets review pagination and isolates the commander");
                Click("review-search", 233); break;
            case 233: view.TypeSetupText("Sol Ring"); stage++; break;
            case 234:
                Check(!view.Hits.Any(h => h.Id.StartsWith("review-card:")), "Card search respects the selected deck section");
                Click("review-search-clear", 235); break;
            case 235: Click("review-section:side", 236); break;
            case 236:
                Check(!view.Hits.Any(h => h.Id.StartsWith("review-card:") || h.Id == "review-next"), "An empty sideboard stays browsable without stale cards or pagination");
                Click("review-section:all", 237); break;
            case 237: Click("review-search", 238); break;
            case 238: view.TypeSetupText("sOl RiNg"); stage++; break;
            case 239: Click("review-card:Main:" + Deck.Entries.Single(e => e.Card.Name == "Sol Ring").Id, 240); break;
            case 240:
                Check(view.ReviewedCard?.Card.Name == "Sol Ring" && view.Hits.Count(h => h.Id.StartsWith("review-card:")) == 1, "Selecting a searched card opens its large preview from the actual saved deck");
                Check(Deck.Revision == reviewRevision && view.DeckConfirmed && Local?.Ready == true, "Searching, paging and inspecting cards preserve the document and synchronized readiness");
                previewAfter = view.Now + .7; Click("review-search-clear", 241); break;
            case 241:
                if (view.Now < previewAfter) break;
                Capture("09-full-deck-review"); Click("confirm-deck", 120); break;
            case 120:
                Check(view.DeckConfirmed && Local?.Ready == true, "Reviewing an unchanged confirmed deck preserves readiness");
                Click("change-deck", 20); break;
            case 20:
                Check(Local?.Ready == true, "Browsing the deck picker alone does not withdraw readiness");
                Click("setup-close", 21); break;
            case 21:
                Check(view.DeckConfirmed && Local?.Ready == true, "Canceling deck browsing preserves the confirmed seat");
                Click("change-deck", 121); break;
            case 121: Click("deck:" + Deck.Id, 221); break;
            case 221:
                Check(view.SetupModal == "confirm" && view.DeckConfirmed && Local?.Ready == true, "Selecting the current saved deck is read-only and keeps its seat ready");
                Click("setup-close", 222); break;
            case 222: Click("change-deck", 223); break;
            case 223: Click("deck:" + otherDeckId, 122); break;
            case 122:
                Check(Deck.Id == otherDeckId && view.SetupModal == "confirm" && !view.DeckConfirmed && Local?.Ready == false, "Choosing another saved deck clears confirmation and readiness before its review");
                Click("confirm-deck", 123); break;
            case 123: Click("ready", 124); break;
            case 124: Click("review-deck", 250); break;
            case 250: Click("review-list", 251); break;
            case 251: Click("review-search", 252); break;
            case 252: view.TypeSetupText("Forest"); stage++; break;
            case 253:
                editCardId = Deck.Entries.First(e => e.Card.Name == "Forest").Id;
                Click("review-card:Main:" + editCardId, 254); break;
            case 254: Click("confirm-edit", 22); break;
            case 22:
                Check(view.DeckEditor && !view.DeckConfirmed && Local?.Ready == false, "Entering the builder clears synchronized readiness before any editing begins");
                Check(view.DeckSelection?.Id == editCardId && view.Hits.Any(h => h.Id == "deck-card:Main:" + editCardId), "Opening the builder from review selects and reveals the exact card printing for editing");
                Click("deck-done", 23); break;
            case 23: Click("confirm-deck", 24); break;
            case 24: Click("ready", 25); break;
            case 25: background = JoinGuests(); stage++; break;
            case 26:
                if (view.Lobby?.CanStart != true) break;
                Check(view.Lobby.Seats.All(s => s.Ready) && view.Hits.Any(h => h.Id == "start"), "Three independent guests populate the roster and unlock Start only when everyone is ready");
                Capture("04-friends-lobby"); stage = 300; break;
            case 300:
                if (view.Conversation?.Members.Length != 4) break;
                Check(view.Lobby!.Seats.Any(s => s.Name == "Jebb") && view.Conversation.Members.Any(m => m.Name == "Table Host"), "Saved and guest display names synchronize to the roster and chat");
                Click("chat-toggle", 301); break;
            case 301:
                view.TypeText("Hello friends, ready for Commander?"); keyboard = new(Keys.F, Keys.C, Keys.Space); stage++; break;
            case 302:
                Check(!view.Automatic && view.ChatDraft == "Hello friends, ready for Commander?", "Chat typing consumes gameplay shortcut keys without changing Auto");
                keyboard = new(Keys.Enter); stage++; break;
            case 303:
                keyboard = default; if (view.SocialSending) break;
                Check(view.ChatDraft.Length == 0 && view.Conversation!.Messages.Count(m => m.Text == "Hello friends, ready for Commander?") == 1 && Local?.Ready == true, "Enter sends one message without consuming readiness");
                background = Task.Run(async () => {
                    try { await guests[0].SendMessageAsync("previous-table", "chat", "A stale message"); }
                    catch (InvalidOperationException ex) when (ex.Message.Contains("table has changed")) { staleChatRejected = true; }
                    peerConversations = await Task.WhenAll(guests.Select(g => g.ConversationAsync(view.Lobby!.TableId)));
                }); stage++; break;
            case 304:
                Check(peerConversations.All(c => c.Messages.Any(m => m.Name == "Table Host" && m.Text == "Hello friends, ready for Commander?")), "All three guests receive the authoritative sender and message");
                Check(staleChatRejected && peerConversations.All(c => !c.Messages.Any(m => m.Text == "A stale message")), "A delayed command for an old table cannot send into the current conversation");
                Click("chat-close", 305); break;
            case 305: background = guests[0].SendMessageAsync(view.Lobby!.TableId, "chat", "Ready when you are."); stage++; break;
            case 306:
                if (!view.Conversation!.Messages.Any(m => m.Text == "Ready when you are.")) break;
                Check(view.ChatUnread == 1, "An incoming message increments the closed drawer's unread badge");
                Click("chat-toggle", 307); break;
            case 307:
                Check(view.ChatUnread == 0, "Opening the latest messages marks them read");
                Click("chat-people", 308); break;
            case 308: Click("chat-mute:" + view.Conversation!.Members.Single(m => m.Name == "Jebb").Id, 309); break;
            case 309:
                background = Task.Run(async () => { await Task.Delay(800); await guests[0].SendMessageAsync(view.Lobby!.TableId, "emote", "Nice play!"); }); stage++; break;
            case 310:
                if (view.Conversation!.Messages.LastOrDefault(m => m.Text == "Nice play!") is not { } muted) break;
                Check(!view.ChatVisible(muted) && view.ChatUnread == 0, "Muting a player hides their emotes and unread alerts locally");
                Click("chat-mute:" + muted.SenderId, 311); break;
            case 311:
                Check(view.ChatVisible(view.Conversation!.Messages.Last(m => m.Text == "Nice play!")), "Unmuting restores the player's conversation history");
                Click("chat-mute-all", 316); break;
            case 316:
                Check(PlayPreferences.Load(view.Profile).MuteTableChat && !view.ChatVisible(view.Conversation!.Messages.Last(m => m.Text == "Nice play!")), "Mute table hides remote emotes and persists the preference");
                Click("chat-mute-all", 317); break;
            case 317:
                Check(!PlayPreferences.Load(view.Profile).MuteTableChat && view.ChatVisible(view.Conversation!.Messages.Last(m => m.Text == "Nice play!")), "Unmute table restores remote chat and saves the updated preference");
                Click("chat-messages", 312); break;
            case 312: Click("chat-emote:0", 313); break;
            case 313:
                if (view.SocialSending) break;
                Check(view.Conversation!.Messages.Any(m => m.Kind == "emote" && m.Text == "Hello!" && m.Name == "Table Host"), "Quick emotes use the same authoritative table transcript");
                Capture("10-lobby-chat"); stage = 321; break;
            case 321: background = guests[1].SendMessageAsync(view.Lobby!.TableId, "chat", new string('W', 300)); stage++; break;
            case 322:
                if (view.Conversation!.Messages.LastOrDefault(m => m.Text == new string('W', 300)) is not { } longMessage) break;
                Click("chat-message:" + longMessage.Id, 323); break;
            case 323:
                Check(view.Hits.Any(h => h.Id == "chat-message-back"), "A maximum-length message opens a complete readable view");
                Capture("12-long-chat-message"); Click("chat-message-back", 324); break;
            case 324: Click("chat-toggle", 325); break;
            case 325:
                Check(!view.ChatOpen, "The header chat button closes the drawer using its independent input scope");
                Click("unready", 27); break;
            case 27:
                Check(Local?.Ready == false && view.Lobby?.CanStart == false && !view.Hits.Any(h => h.Id == "start"), "Withdrawing readiness immediately disables the host start action");
                Click("ready", 28); break;
            case 28:
                if (view.Lobby?.CanStart != true) break;
                Click("start", 29); break;
            case 29:
                if (view.Match is not { Players.Length: 4 }) break;
                Check(view.Match.Viewer!.Zone("Command").Cards.Any(c => c.Name == "Hakbal of the Surging Soul"), "Starting the synchronized table uses the confirmed command zone");
                Capture("05-started-table");
                background = FinishOpening(); stage = 330; break;
            case 330:
                if (view.Match?.PhaseKey != "MAIN1") break;
                gameChatScope = view.Scope; Click("chat-toggle", 331); break;
            case 331: view.TypeText("Does anyone have removal?"); keyboard = new(Keys.F, Keys.C, Keys.Space); stage++; break;
            case 332:
                Check(view.Scope == gameChatScope && !view.Automatic && view.ChatDraft == "Does anyone have removal?", "Typing during a real game neither passes priority nor changes the pending decision");
                keyboard = new(Keys.Enter); stage++; break;
            case 333:
                keyboard = default; if (view.SocialSending) break;
                Check(view.Scope == gameChatScope && view.ChatDraft.Length == 0, "Sending in-game chat leaves the game decision valid");
                background = Task.Run(async () => peerConversations = await Task.WhenAll(guests.Select(g => g.ConversationAsync(view.Lobby!.TableId)))); stage++; break;
            case 334:
                Check(peerConversations.All(c => c.Messages.Any(m => m.Text == "Does anyone have removal?")) && view.Match!.Players.Any(p => p.Name == "Jebb"), "In-game chat reaches all four seats and the game uses lobby display names");
                Capture("11-game-chat"); Click("chat-close", 335); break;
            case 335:
                background = Task.Run(async () => { foreach (var guest in guests) { var match = await guest.ObserveAsync(); await guest.ConcedeAsync(match!.Id); } }); stage = 30; break;
            case 30:
                if (view.Match?.Status != "finished") break;
                Click("return", 31); break;
            case 31:
                Check(view.Match == null && view.Lobby?.MatchActive == false && Local?.Ready == false, "Returning from a finished game restores the lobby with readiness cleared");
                Check(view.Conversation!.Messages.Any(m => m.Text == "Does anyone have removal?"), "Table conversation survives the game and return to lobby");
                Click("leave", 32); break;
            case 32:
                Check(view.Lobby == null && !view.DeckConfirmed, "Leaving clears the old table's deck confirmation");
                Check(view.Conversation == null && !view.ChatOpen && view.ChatDraft.Length == 0, "Leaving clears the old conversation, draft and drawer");
                background = Task.Run(async () => { var host = await guests[0].HostAsync(4, false); joinTo = LocalAddress(host); }); stage++; break;
            case 33: Click("join", 34); break;
            case 34: view.TypeSetupText(new string('\b', 220) + "MT1-invalid"); stage++; break;
            case 35: Click("join-confirm", 36); break;
            case 36:
                Check(view.SetupModal == "join" && view.Error.Length > 0 && view.Lobby == null, "An invalid invite stays in the native join dialog without losing the selected deck");
                Click("dismiss", 37); break;
            case 37: view.TypeSetupText(new string('\b', 220) + joinTo); stage++; break;
            case 38: Click("join-confirm", 39); break;
            case 39:
                Check(view.SetupModal.Length == 0 && view.Lobby?.Mode == "joined" && !view.DeckConfirmed, "Native invite entry joins the remote host and asks for deck confirmation");
                Click("review-deck", 40); break;
            case 40: Click("confirm-deck", 41); break;
            case 41: Click("ready", 42); break;
            case 42:
                Check(Local?.Ready == true && !view.Hits.Any(h => h.Id == "start"), "A guest can ready its confirmed deck but cannot start the host's table");
                Capture("06-guest-lobby"); Click("edit-deck", 43); break;
            case 43:
                Check(view.DeckEditor && !view.DeckConfirmed && Local?.Ready == false, "Guest deck editing also clears the shared readiness state");
                background = Task.Run(async () => peerLobby = await guests[0].LobbyAsync()); stage++; break;
            case 44:
                Check(peerLobby!.Seats.Any(s => s.Type == "REMOTE" && !s.Ready && s.Deck == Deck.Name), "The host observes the guest becoming unready through ordinary synchronization");
                Click("deck-done", 45); break;
            case 45: Click("confirm-deck", 46); break;
            case 46:
                Check(view.DeckConfirmed && Local?.Ready == false, "Reconfirmation does not silently ready the guest"); Click("ready", 47); break;
            case 47: Click("leave", 48); break;
            case 48:
                Check(view.Lobby == null && !view.DeckConfirmed, "Leaving a joined table clears its seat and confirmation");
                // An incomplete draft must remain buildable without being confirmable.
                Click("new-deck", 49); break;
            case 49: Click("deck-done", 50); break;
            case 50:
                Check(view.SetupModal == "confirm" && !view.Hits.Any(h => h.Id == "confirm-deck") && view.Hits.Any(h => h.Id == "confirm-edit"), "Incomplete drafts offer a return to the builder and cannot be confirmed");
                Capture("07-incomplete-deck");
                File.WriteAllText(Path.Combine(view.Profile, "lobby-results.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
                view.Exit(); stage++; break;
        }
    }
    public void Dispose()
    {
        try { background?.GetAwaiter().GetResult(); } catch { }
        foreach (var guest in guests) guest.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
