package forge.api.sync;

import com.google.gson.*;
import java.util.*;
import java.util.concurrent.atomic.AtomicLong;
import org.testng.annotations.Test;
import static org.testng.Assert.*;

public class TableConversationTest {
    @Test public void transcriptIsBoundedOrderedAndSnapshotsDoNotChange() {
        var clock = new AtomicLong(); var chat = new TableConversation(clock::get); chat.join(0, "Host");
        var before = chat.snapshot(0);
        for (int i = 0; i < 205; i++) { clock.addAndGet(750_000_000); chat.send(0, "chat", "Message " + i); }
        var after = chat.snapshot(0);
        assertEquals(before.messages().size(), 1); assertEquals(after.messages().size(), 200);
        assertEquals(after.messages().get(0).text(), "Message 5");
        assertEquals(after.messages().get(199).id(), after.revision());
        assertEquals(after.messages().stream().map(TableConversation.Message::id).distinct().count(), 200L);
    }
    @Test public void validationAndThrottlingDoNotAppendOrConsumeValidMessages() {
        var clock = new AtomicLong(); var chat = new TableConversation(clock::get); chat.join(0, "Host");
        for (String invalid : List.of("", "\n", "hidden\u202Ename", "x".repeat(301)))
            assertThrows(IllegalArgumentException.class, () -> chat.send(0, "chat", invalid));
        assertThrows(IllegalArgumentException.class, () -> chat.send(0, "system", "fake activity"));
        assertThrows(IllegalArgumentException.class, () -> chat.send(0, "emote", "not an emote"));
        assertEquals(chat.snapshot(0).messages().size(), 1);
        chat.send(0, "chat", " Hello ");
        assertEquals(chat.snapshot(0).messages().get(1).text(), "Hello");
        assertThrows(IllegalStateException.class, () -> chat.send(0, "emote", "Good game!"));
        clock.addAndGet(750_000_000); chat.send(0, "emote", "Good game!");
        assertEquals(chat.snapshot(0).messages().size(), 3);
    }
    @Test public void namesAndReusedSeatsKeepDistinctIdentities() {
        var chat = new TableConversation(); chat.join(0, "Host"); chat.join(1, "Guest");
        String first = chat.snapshot(0).members().get(1).id(); chat.rename(1, "Jebb");
        assertEquals(chat.snapshot(0).members().get(1).id(), first);
        assertThrows(IllegalArgumentException.class, () -> chat.rename(1, "host"));
        assertThrows(IllegalArgumentException.class, () -> chat.rename(1, "x".repeat(25)));
        chat.leave(1); assertThrows(IllegalStateException.class, () -> chat.send(1, "chat", "Still here"));
        chat.join(1, "New guest");
        assertNotEquals(chat.snapshot(0).members().get(1).id(), first);
        assertEquals(chat.snapshot(0).messages().get(1).name(), "Guest", "Historical messages keep their original author name");
    }
    @Test(timeOut = 10000) public void localAndRemoteSeeTheSameTranscriptAndCannotSpoofASender() throws Exception {
        var chat = new TableConversation(); chat.join(0, "Host");
        try (var server = new SyncTransport.Server((seat, method, args) -> {
            if (method.equals("send")) chat.send(seat, args.get("kind").getAsString(), args.get("text").getAsString());
            return chat.snapshot(seat);
        }, () -> { chat.join(1, "Guest"); return 1; }); var local = server.local(0)) {
            try (var remote = new SyncTransport.Remote("127.0.0.1:" + server.listen())) {
                var args = JsonParser.parseString("{\"kind\":\"chat\",\"text\":\"Hello\",\"seat\":0,\"name\":\"Host\"}").getAsJsonObject();
                var received = SyncTransport.JSON.toJsonTree(remote.request("send", args)).getAsJsonObject();
                var message = received.getAsJsonArray("messages").get(2).getAsJsonObject();
                assertEquals(message.get("name").getAsString(), "Guest"); assertEquals(message.get("seat").getAsInt(), 1);
                var host = SyncTransport.JSON.toJsonTree(local.request("read", null)).getAsJsonObject();
                assertEquals(host.get("messages"), received.get("messages"));
                remote.reconnect(); assertEquals(remote.request("read", null), SyncTransport.JSON.fromJson(received, Object.class));
            }
        }
    }
}
