using Mana.Contracts;

namespace Mana.Client;

// Independent from SessionReplica: chat must not invalidate a pending game decision.
public sealed class ConversationReplica
{
    public string TableId { get; private set; } = "";
    public TableConversation? State { get; private set; }
    public HashSet<string> MutedMembers { get; } = [];
    public bool Muted { get; set; }
    private long readThrough;
    public void Bind(string tableId)
    {
        if (TableId == tableId) return;
        TableId = tableId; State = null; readThrough = 0; MutedMembers.Clear();
    }
    public bool Accept(TableConversation state)
    {
        if (state.TableId != TableId || State != null && state.Revision < State.Revision) return false;
        if (State == null) readThrough = state.Revision;
        State = state; return true;
    }
    public string? LocalMemberId => State?.Members.FirstOrDefault(m => m.Seat == State.LocalSeat)?.Id;
    public bool Visible(TableMessage message) => message.Kind == "system" || message.SenderId == LocalMemberId || !Muted && !MutedMembers.Contains(message.SenderId);
    public TableMessage[] Messages => State?.Messages.Where(Visible).ToArray() ?? [];
    public int Unread => Messages.Count(m => m.Id > readThrough && m.SenderId != LocalMemberId && m.Kind != "system");
    public void MarkRead() { readThrough = State?.Revision ?? 0; }
}
