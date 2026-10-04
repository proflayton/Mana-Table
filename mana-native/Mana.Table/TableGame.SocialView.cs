using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableGame
{
    private long chatAnchor;
    private long? chatSelectedMessage;
    private Rectangle ChatBounds => new(998, 66, 582, DesignHeight - 80);
    private string ChatScope => $"chat:{conversation.TableId}:{chatOpen}:{chatPeople}:{chatPage}:{chatAnchor}:{chatSelectedMessage}:{chatDraft}:{preferences.MuteTableChat}:{string.Join(',', conversation.MutedMembers.Order())}";
    private void DrawSocial()
    {
        if (match == null && !deckEditor && setupModal.Length == 0)
            Button("player-name", preferences.PlayerName.Length == 0 ? "Your display name" : preferences.PlayerName, 1200, 17, 178, OpenPlayerName, loaded && !busy, height: 34);
        if (!SocialAvailable) return;
        Button("chat-toggle", conversation.Unread > 0 ? $"Chat ({conversation.Unread})" : "Table chat", match == null ? 1390 : 1052, match == null ? 17 : 12, match == null ? 164 : 126, ToggleChat, accent: chatOpen || conversation.Unread > 0, height: 35);
        if (!chatOpen && now < chatToastUntil && chatToast.Length > 0 && !conversation.Muted) {
            canvas.Panel(new(28, 66, 515, 48), new(25, 42, 53), Blue, 9);
            canvas.Text(chatToast, 42, 82, Ink, .78f, 485, 1);
            hits.Add(new("chat-toast", ChatScope, new(28, 66, 515, 48), ToggleChat));
        }
        if (chatOpen) {
            var bounds = ChatBounds; int x = bounds.X + 20, bottom = bounds.Bottom;
            // Leave the rest of the table playable; the drawer consumes its own area.
            hits.RemoveAll(h => h.Bounds.Intersects(bounds));
            hits.Add(new("chat-background", ChatScope, bounds, () => chatFocus = false));
            canvas.Panel(bounds, new(17, 26, 37), new(67, 100, 119), 12);
            canvas.Text("TABLE CHAT", x, 83, Gold, .9f, 336, 1, true);
            Button("chat-close", "×", bounds.Right - 61, 77, 42, ToggleChat, height: 32);
            Button("chat-messages", conversation.Unread > 0 ? $"Messages ({conversation.Unread})" : "Messages", x, 116, 188, () => { chatPeople = false; chatPage = 0; chatSelectedMessage = null; conversation.MarkRead(); }, accent: !chatPeople, height: 30);
            Button("chat-people", "Players & mute", x + 200, 116, 188, () => { chatPeople = true; chatFocus = false; }, accent: chatPeople, height: 30);
            if (chatPeople) DrawChatPeople(x);
            else if (conversation.Messages.FirstOrDefault(m => m.Id == chatSelectedMessage) is { } expanded) {
                canvas.Text(expanded.Name + " · Seat " + (expanded.Seat + 1), x + 12, 175, Gold, .84f, 518, 1, true);
                canvas.Text(expanded.Text, x + 12, 218, Ink, .85f, 518, 16);
                Button("chat-message-back", "Back to messages", x, bottom - 332, 250, () => chatSelectedMessage = null, height: 30);
            }
            else {
                chatSelectedMessage = null;
                var messages = conversation.Messages;
                if (chatPage > 0) messages = messages.Where(m => m.Id <= chatAnchor).ToArray();
                chatPage = Math.Clamp(chatPage, 0, Math.Max(0, (messages.Length - 1) / ChatPageSize));
                int end = messages.Length - chatPage * ChatPageSize, start = Math.Max(0, end - ChatPageSize);
                int rowHeight = (bottom - 346 - 156) / ChatPageSize;
                for (int i = start; i < end; i++) {
                    var entry = messages[i]; int y = 159 + (i - start) * rowHeight;
                    bool own = entry.SenderId == conversation.LocalMemberId;
                    canvas.Rounded(new(x, y, 542, rowHeight - 6), own ? new(29, 48, 60) : new(23, 35, 46), 6);
                    canvas.Text(entry.Name + (own ? " · You" : " · Seat " + (entry.Seat + 1)), x + 12, y + 8, entry.Kind == "system" ? Muted : own ? Blue : Gold, .62f, 517, 1, true);
                    canvas.Text(entry.Text, x + 12, y + 31, entry.Kind == "system" ? Muted : Ink, .72f, 515, 3, entry.Kind == "emote");
                    hits.Add(new("chat-message:" + entry.Id, ChatScope, new(x, y, 542, rowHeight - 6), () => { chatSelectedMessage = entry.Id; chatFocus = false; }));
                }
                if (messages.Length == 0) canvas.Text("Say hello to your table. Messages stay here through the game and rematch.", x + 18, 205, Muted, .92f, 490, 4);
                Button("chat-older", "Older", x, bottom - 332, 109, () => { if (chatPage == 0) chatAnchor = conversation.State?.Revision ?? 0; chatPage++; }, (chatPage + 1) * ChatPageSize < messages.Length, height: 30);
                Button("chat-newer", "Newer", x + 120, bottom - 332, 109, () => { chatPage--; if (chatPage == 0) conversation.MarkRead(); }, chatPage > 0, height: 30);
                Button("chat-latest", "Latest", x + 431, bottom - 332, 109, () => { chatPage = 0; conversation.MarkRead(); }, chatPage > 0 || conversation.Unread > 0, height: 30);
            }
            if (chatError.Length > 0) canvas.Text(chatError, x + 4, bottom - 291, Red, .67f, 535, 2);
            else if (!chatPeople) canvas.Text("Click a message to read it in full.", x + 4, bottom - 290, Muted, .61f, 532, 1);
            for (int i = 0; i < Emotes.Length; i++) {
                string emote = Emotes[i];
                Button("chat-emote:" + i, emote, x + i % 3 * 184, bottom - 248 + i / 3 * 40, 174, () => SendChat(emote), !socialSending && now >= nextChatSend, height: 32);
            }
            var input = new Rectangle(x, bottom - 160, 542, 74);
            canvas.Panel(input, new(11, 19, 29), chatFocus ? Blue : LobbyTrim, 6);
            string draftPreview = chatDraft.Length > 180 ? "…" + chatDraft[^180..] : chatDraft;
            canvas.Text(chatDraft.Length == 0 ? "Message your table…" : draftPreview + (chatFocus && (int)(now * 2) % 2 == 0 ? "|" : ""), x + 12, input.Y + 12, chatSelectAll ? Gold : chatDraft.Length == 0 ? Muted : Ink, .72f, 516, 3);
            hits.Add(new("chat-input", ChatScope, input, () => { chatFocus = true; chatSelectAll = false; textFocus = false; }));
            canvas.Text($"{chatDraft.Length}/300  ·  Enter to send  ·  Esc to close", x + 2, bottom - 61, Muted, .6f, 365, 2);
            Button("chat-send", socialSending ? "Sending…" : "Send", x + 391, bottom - 74, 150, () => SendChat(), !socialSending && now >= nextChatSend && !string.IsNullOrWhiteSpace(chatDraft), true, 40);
            canvas.Text("Shared with everyone at this table.", x + 2, bottom - 24, Muted, .59f, 533, 1);
            if (bounds.Contains(pointer)) hovered = null;
        }
        for (int i = 0; i < hits.Count; i++) if (hits[i].Id.StartsWith("chat-")) hits[i] = hits[i] with { Scope = ChatScope };
    }
    private void DrawChatPeople(int x)
    {
        canvas.Text("Mute hides chat and emotes on your screen. Table activity remains visible.", x + 4, 170, Muted, .74f, 530, 3);
        Button("chat-mute-all", preferences.MuteTableChat ? "Unmute table" : "Mute table", x, 234, 540, () => {
            preferences = preferences with { MuteTableChat = !preferences.MuteTableChat }; conversation.Muted = preferences.MuteTableChat; SavePreferences(); chatToast = "";
        }, accent: preferences.MuteTableChat, height: 36);
        var members = conversation.State?.Members ?? [];
        for (int i = 0; i < members.Length; i++) {
            var member = members[i]; int y = 285 + i * 57; bool own = member.Seat == conversation.State?.LocalSeat;
            canvas.Text($"{member.Seat + 1}  ·  {member.Name}" + (own ? " (You)" : ""), x + 7, y + 11, own ? Blue : Ink, .76f, 374, 1);
            if (!own) Button("chat-mute:" + member.Id, conversation.MutedMembers.Contains(member.Id) ? "Unmute" : "Mute", x + 398, y, 143, () => {
                if (!conversation.MutedMembers.Remove(member.Id)) conversation.MutedMembers.Add(member.Id); chatToast = "";
            }, height: 34);
        }
    }
    private void DrawPlayerName()
    {
        canvas.Text("Make yourself at home", 204, 185, Ink, 1.4f, 1030, 1, true);
        canvas.Text("Your friends see this name in the lobby, chat, and game.", 206, 260, Muted, .92f, 1120, 2);
        SetupSearchBox("player-name-input", "Your name", new(230, 377, 1140, 55));
        canvas.Text("Up to 24 characters. Names can be changed between games.", 235, 469, Muted, .78f, 1110, 2);
        Button("player-name-save", "Save name", 1010, 811, 360, SavePlayerName, !busy && nameDraft.Trim().Length is > 0 and <= 24, true, 50);
    }
}
