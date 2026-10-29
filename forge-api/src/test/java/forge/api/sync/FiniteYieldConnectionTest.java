package forge.api.sync;

import com.google.gson.*;
import org.testng.annotations.Test;
import java.io.*;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import java.util.concurrent.atomic.AtomicInteger;
import static org.testng.Assert.*;

public class FiniteYieldConnectionTest {
    @Test(timeOut = 10000) public void replacedSocketCleanupCannotRevokeTheNewConnectionConsent() throws Exception {
        var boundaries = new AtomicInteger(); var commands = new AtomicInteger();
        try (var server = new SyncTransport.Server((seat, method, args) -> commands.incrementAndGet(), () -> 1,
                seat -> { assertEquals(seat, 1); boundaries.incrementAndGet(); });
             var first = new Peer(server.listen())) {
            var hello = first.exchange("{\"version\":3}");
            assertEquals(boundaries.get(), 1);
            try (var next = new Peer(server.listen())) {
                next.exchange("{\"version\":3,\"resume\":\"" + hello.get("resume").getAsString() + "\"}");
                assertEquals(boundaries.get(), 2, "Reattachment clears old consent before accepting commands");
                first.send("{\"sequence\":1,\"method\":\"old\",\"arguments\":{}}");
                assertNull(first.input.readLine(), "The replaced connection cannot retain command authority");
                assertEquals(boundaries.get(), 2, "Delayed old detach cannot stop the newer connection");
                assertEquals(commands.get(), 0);
                assertEquals(next.exchange("{\"sequence\":1,\"method\":\"new\",\"arguments\":{}}").get("result").getAsInt(), 1);
            }
            long until = System.nanoTime() + 2_000_000_000L;
            while (boundaries.get() < 3 && System.nanoTime() < until) Thread.sleep(2);
            assertEquals(boundaries.get(), 3, "The current connection's actual loss clears its grant");
        }
    }
    @Test public void localGuestClosureAlsoEndsItsSeatConsent() {
        var boundaries = new AtomicInteger();
        try (var server = new SyncTransport.Server((seat, method, args) -> seat, () -> 1, ignored -> boundaries.incrementAndGet())) {
            var local = server.joinLocal(); assertEquals(boundaries.get(), 1);
            local.close(); local.close(); assertEquals(boundaries.get(), 2);
            assertThrows(IllegalStateException.class, () -> local.request("read", new JsonObject()));
        }
    }
    private static final class Peer implements AutoCloseable {
        final Socket socket; final BufferedReader input; final BufferedWriter output;
        Peer(int port) throws IOException {
            socket = new Socket("127.0.0.1", port); socket.setSoTimeout(3000);
            input = new BufferedReader(new InputStreamReader(socket.getInputStream(), StandardCharsets.UTF_8));
            output = new BufferedWriter(new OutputStreamWriter(socket.getOutputStream(), StandardCharsets.UTF_8));
        }
        void send(String json) throws IOException { output.write(json); output.newLine(); output.flush(); }
        JsonObject exchange(String json) throws IOException { send(json); return JsonParser.parseString(input.readLine()).getAsJsonObject(); }
        @Override public void close() throws IOException { socket.close(); }
    }
}
