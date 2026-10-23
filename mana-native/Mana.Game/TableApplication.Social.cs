using Mana.Client;
using Mana.Contracts;
using Microsoft.Xna.Framework.Input;
using Keys = Microsoft.Xna.Framework.Input.Keys;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private readonly ConversationReplica conversation = new();
    private bool chatOpen, chatFocus, chatSelectAll, socialPolling, socialSending, chatPeople;
    private string chatDraft = "", chatError = "", chatToast = "", nameDraft = "";
    private int chatPage;
    private long chatAnchor;
    private long? chatSelectedMessage;
    private double nextSocialPoll, nextChatSend, chatToastUntil;
    private const int ChatPageSize = 4;
    private static readonly string[] Emotes = ["Hello!", "Good luck!", "Nice play!", "Thinking…", "Thanks!", "Good game!"];
    private bool SocialAvailable => Connected && services.Social != null && !string.IsNullOrEmpty(lobby?.TableId);
    private string ChatScope => $"chat:{conversation.TableId}:{chatOpen}:{chatPeople}:{chatPage}:{chatAnchor}:{chatSelectedMessage}:{chatDraft}:{preferences.MuteTableChat}:{string.Join(',', conversation.MutedMembers.Order())}";
    private void HandleSocialIntent(ShellIntent.Social intent)
    {
        switch (intent) {
            case ShellIntent.ToggleChat: ToggleChat(); break;
            case ShellIntent.BlurChat: chatFocus = false; break;
            case ShellIntent.ShowMessages:
                chatPeople = false; chatPage = 0; chatSelectedMessage = null; conversation.MarkRead(); break;
            case ShellIntent.ShowPeople: chatPeople = true; chatFocus = false; break;
            case ShellIntent.ReadMessage read:
                if (conversation.Messages.Any(message => message.Id == read.MessageId)) { chatSelectedMessage = read.MessageId; chatFocus = false; }
                break;
            case ShellIntent.CloseMessage: chatSelectedMessage = null; break;
            case ShellIntent.OlderMessages:
                if (chatPage == 0) chatAnchor = conversation.State?.Revision ?? 0;
                chatPage++; break;
            case ShellIntent.NewerMessages:
                chatPage = Math.Max(0, chatPage - 1);
                if (chatPage == 0) conversation.MarkRead();
                break;
            case ShellIntent.LatestMessages: chatPage = 0; conversation.MarkRead(); break;
            case ShellIntent.SendMessage: SendChat(); break;
            case ShellIntent.SendEmote emote when Emotes.Contains(emote.Text): SendChat(emote.Text); break;
            case ShellIntent.FocusChat: chatFocus = true; chatSelectAll = false; textFocus = false; break;
            case ShellIntent.ToggleTableMute:
                preferences = preferences with { MuteTableChat = !preferences.MuteTableChat };
                conversation.Muted = preferences.MuteTableChat; SavePreferences(); chatToast = ""; break;
            case ShellIntent.ToggleMemberMute mute:
                if (conversation.State?.Members.Any(member => member.Id == mute.MemberId && member.Seat != conversation.State.LocalSeat) == true) {
                    if (!conversation.MutedMembers.Remove(mute.MemberId)) conversation.MutedMembers.Add(mute.MemberId);
                    chatToast = "";
                }
                break;
        }
    }
    private void PrepareSocialPresentation()
    {
        if (!SocialAvailable || !chatOpen || chatPeople) return;
        if (chatSelectedMessage != null && conversation.Messages.Any(message => message.Id == chatSelectedMessage)) return;
        chatSelectedMessage = null;
        var messages = chatPage > 0 ? conversation.Messages.Where(message => message.Id <= chatAnchor).ToArray() : conversation.Messages;
        chatPage = Math.Clamp(chatPage, 0, Math.Max(0, (messages.Length - 1) / ChatPageSize));
    }
    private void UpdateSocial()
    {
        string tableId = SocialAvailable ? lobby!.TableId : "";
        if (conversation.TableId != tableId) {
            conversation.Bind(tableId); chatDraft = chatError = chatToast = ""; chatPage = 0;
            chatOpen = chatFocus = false; nextSocialPoll = 0;
        }
        conversation.Muted = preferences.MuteTableChat;
        if (tableId.Length == 0 || socialPolling || now < nextSocialPoll || closing) return;
        socialPolling = true; nextSocialPoll = now + .6;
        _ = Task.Run(async () => {
            try {
                var state = await services.Social!.ConversationAsync(tableId);
                updates.Enqueue(() => AcceptConversation(state));
            } catch (Exception ex) { updates.Enqueue(() => { if (conversation.TableId == tableId) chatError = ex.Message; }); }
            finally { updates.Enqueue(() => socialPolling = false); }
        });
    }
    private void AcceptConversation(TableConversation state)
    {
        var before = conversation.State;
        if (!conversation.Accept(state)) return;
        if (before != null && state.Messages.LastOrDefault(m => m.Id > before.Revision && m.Kind == "emote" && conversation.Visible(m)) is { } emote) {
            chatToast = emote.Name + ": " + emote.Text; chatToastUntil = now + 4;
        }
        // Keep an older page anchored when new messages arrive.
        if (chatOpen && chatPage == 0 && !chatPeople && chatSelectedMessage == null) conversation.MarkRead();
    }
    private void ToggleChat()
    {
        chatOpen = !chatOpen; chatFocus = chatOpen; chatSelectAll = false;
        textFocus = false; deckTextFocus = ""; pressed = null;
        if (chatOpen) { chatPeople = false; chatPage = 0; chatSelectedMessage = null; conversation.MarkRead(); }
    }
    private void SendChat(string? emote = null)
    {
        string text = emote ?? chatDraft.Trim();
        if (!SocialAvailable || socialSending || now < nextChatSend || text.Length == 0) return;
        string tableId = conversation.TableId, originalDraft = chatDraft;
        socialSending = true; nextChatSend = now + .8; chatError = "";
        _ = Task.Run(async () => {
            try {
                var state = await services.Social!.SendMessageAsync(tableId, emote == null ? "chat" : "emote", text);
                updates.Enqueue(() => {
                    if (conversation.TableId != tableId) return;
                    AcceptConversation(state);
                    if (emote == null && chatDraft == originalDraft) chatDraft = "";
                    chatPage = 0; conversation.MarkRead();
                });
            } catch (Exception ex) { updates.Enqueue(() => { if (conversation.TableId == tableId) chatError = ex.Message; }); }
            finally { updates.Enqueue(() => socialSending = false); }
        });
    }
    private void ChatText(char value)
    {
        if (!chatOpen || !chatFocus) return;
        if (value == '\b') chatDraft = chatSelectAll ? "" : chatDraft.Length == 0 ? "" : chatDraft[..^1];
        else if (!char.IsControl(value)) chatDraft = ((chatSelectAll ? "" : chatDraft) + value)[..Math.Min((chatSelectAll ? 0 : chatDraft.Length) + 1, 300)];
        else return;
        chatSelectAll = false;
    }
    private bool ChatKeys(KeyboardState keys)
    {
        bool Fresh(Keys key) => keys.IsKeyDown(key) && previousKeys.IsKeyUp(key);
        if (chatOpen && Fresh(Keys.Escape)) { ToggleChat(); return true; }
        if (!chatOpen) {
            if (SocialAvailable && Fresh(Keys.Enter) && !textFocus && !deckEditor && setupModal.Length == 0 && !OverlayOpen) { ToggleChat(); return true; }
            return false;
        }
        if (!chatFocus) return false;
        bool control = keys.IsKeyDown(Keys.LeftControl) || keys.IsKeyDown(Keys.RightControl);
        if (control && Fresh(Keys.A)) chatSelectAll = true;
        if (control && Fresh(Keys.V) && Clipboard.ContainsText()) {
            var text = new string(Clipboard.GetText().Where(c => !char.IsControl(c) && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format).ToArray());
            var value = (chatSelectAll ? "" : chatDraft) + text; chatDraft = value[..Math.Min(300, value.Length)]; chatSelectAll = false;
        }
        if (Fresh(Keys.Enter)) SendChat();
        return true;
    }
    private void OpenPlayerName()
    {
        nameDraft = preferences.PlayerName ?? ""; setupModal = "profile"; textFocus = true; setupSelectAll = true;
    }
    private void SavePlayerName()
    {
        string name = nameDraft.Trim(); if (name.Length is < 1 or > 24 || busy) return;
        Work(async () => {
            if (SocialAvailable) {
                var state = await services.Social!.SetPlayerNameAsync(lobby!.TableId, name);
                var nextLobby = await tables.LobbyAsync(); updates.Enqueue(() => { lobby = nextLobby; AcceptConversation(state); });
            }
            updates.Enqueue(() => { preferences = preferences with { PlayerName = name }; SavePreferences(); setupModal = ""; textFocus = false; message = "Display name saved."; });
        }, "Saving your display name", "Updating the name your table will see.");
    }
}
