using Mana.Client;
using Mana.Contracts;
using System.Text.Json;

namespace Mana.Table;

internal static class SocialRegression
{
    public static void Run(string profile)
    {
        var checks = new List<string>();
        void Check(bool passed, string text) { if (!passed) throw new InvalidOperationException(text); checks.Add(text); }
        TableMessage Message(long id, string sender = "guest", string kind = "chat") => new(id, sender, sender == "host" ? 0 : 1, sender, kind, "Message " + id);
        TableConversation Snapshot(long revision, params TableMessage[] messages) => new("table-a", revision, 0, [new("host", 0, "Host"), new("guest", 1, "Guest")], messages);
        var replica = new ConversationReplica(); replica.Bind("table-a");
        Check(replica.Accept(Snapshot(1, Message(1))) && replica.Unread == 0, "Joining a table loads its history without treating old messages as unread");
        replica.Accept(Snapshot(3, Message(1), Message(2), Message(3, "host")));
        Check(replica.Unread == 1, "Only new remote chat is unread; local echoes are excluded");
        replica.Accept(Snapshot(3, Message(1), Message(2), Message(3, "host")));
        Check(replica.Unread == 1, "Repeated receipts and polls cannot double-count unread chat");
        Check(!replica.Accept(Snapshot(2, Message(1))) && replica.State!.Revision == 3, "A late snapshot cannot roll back the transcript");
        replica.MutedMembers.Add("guest");
        Check(replica.Unread == 0 && replica.Messages.Length == 1, "Per-player mute hides remote chat while retaining the local echo");
        replica.Accept(Snapshot(4, Message(2), Message(3, "host"), Message(4, kind: "system")));
        Check(replica.Messages.Any(m => m.Kind == "system"), "Table activity remains visible when its player is muted");
        replica.Accept(Snapshot(5, Message(5, "new-guest")));
        Check(replica.Messages.Length == 1 && replica.Unread == 1, "A new person in a previously muted seat inherits no mute");
        replica.Muted = true;
        Check(replica.Unread == 0 && replica.Messages.Length == 0, "Mute table hides incoming messages and their unread indicators");
        replica.Muted = false; replica.MarkRead();
        Check(replica.Unread == 0, "Reading the latest transcript clears unread messages");
        replica.Bind("table-b");
        Check(replica.State == null && replica.MutedMembers.Count == 0, "Changing tables clears old history and per-table mutes");
        Check(!replica.Accept(Snapshot(99, Message(99))), "A delayed result from a previous table is ignored even at a higher revision");
        replica.Accept(new("table-b", 1, 0, [new("host", 0, "Host")], [Message(1, "host")]));
        Check(replica.State!.TableId == "table-b" && replica.Unread == 0, "The next table begins with its own authoritative transcript");
        replica.Muted = true;
        replica.Accept(new("table-b", 2, 0, [new("host", 0, "Host")], [new(2, "previous-occupant", 0, "Previous occupant", "chat", "Earlier message")]));
        Check(replica.Messages.Length == 0, "Historical messages from a prior occupant of your seat are not attributed to you");
        File.WriteAllText(Path.Combine(profile, "social-results.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
    }
}
