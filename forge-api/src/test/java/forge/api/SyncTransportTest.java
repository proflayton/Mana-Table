package forge.api;

import com.google.gson.*;
import forge.api.sync.SyncTransport;
import org.testng.annotations.Test;
import java.io.*;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import java.util.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.AtomicInteger;
import static org.testng.Assert.*;

public class SyncTransportTest {
    @Test(timeOut = 10000) public void localAndNetworkUseTheSameAuthorityAndResumeTheSameSeat() throws Exception {
        var sequence = new AtomicInteger(); var seats = new AtomicInteger(1);
        try (var server = new SyncTransport.Server((seat, method, args) -> Map.of("seat", seat, "value", method.equals("add") ? sequence.incrementAndGet() : sequence.get()), seats::getAndIncrement)) {
            int port = server.listen();
            try (var local = server.local(0); var remote = new SyncTransport.Remote("127.0.0.1:" + port)) {
                local.request("add", new JsonObject()); remote.request("add", new JsonObject());
                assertEquals(((Map<?, ?>)local.request("read", null)).get("value"), 2L);
                Object before = remote.request("read", null); remote.reconnect();
                assertEquals(remote.request("read", null), before);
                assertEquals(seats.get(), 2, "Reconnect must not allocate another seat");
                server.revoke(0);
                assertThrows(IllegalStateException.class, () -> local.request("add", null));
                assertEquals(sequence.get(), 2);
            }
        }
    }
    @Test(timeOut = 10000) public void aLostReceiptCannotApplyTheSameCommandTwiceOrExposeAnotherSeat() throws Exception {
        var applied = new AtomicInteger();
        var board = new LinkedHashMap<String, Object>(); board.put("value", 0);
        try (var server = new SyncTransport.Server((seat, method, args) -> { board.put("value", applied.incrementAndGet()); return board; }, () -> 1)) {
            int port = server.listen(); String resume;
            var command = JsonParser.parseString("{\"sequence\":1,\"method\":\"add\",\"arguments\":{}}").getAsJsonObject();
            try (var wire = new Wire(port)) {
                var hello = wire.exchange("{\"version\":3,\"resume\":\"\"}"); resume = hello.get("resume").getAsString();
                var first = wire.exchange(command.toString());
                board.put("value", 999); // Cached receipts must not alias mutable engine data.
                assertEquals(wire.exchange(command.toString()), first);
            }
            try (var wire = new Wire(port)) {
                wire.exchange(SyncTransport.JSON.toJson(Map.of("version", 3, "resume", resume)));
                var receipt = wire.exchange(command.toString());
                assertEquals(receipt.getAsJsonObject("result").get("value").getAsInt(), 1);
                assertEquals(applied.get(), 1);
            }
            server.revoke(1);
            try (var wire = new Wire(port)) {
                assertThrows(EOFException.class, () -> wire.exchange(SyncTransport.JSON.toJson(Map.of("version", 3, "resume", resume))));
            }
        }
    }
    @Test(timeOut = 10000) public void mismatchedVersionsAndOutOfOrderCommandsNeverReachTheEngine() throws Exception {
        var applied = new AtomicInteger();
        try (var server = new SyncTransport.Server((seat, method, args) -> applied.incrementAndGet(), () -> 1)) {
            int port = server.listen();
            try (var wire = new Wire(port)) { assertTrue(wire.exchange("{\"version\":1}").has("error")); }
            try (var wire = new Wire(port)) {
                wire.exchange("{\"version\":3}");
                assertThrows(EOFException.class, () -> wire.exchange("{\"sequence\":2,\"method\":\"add\",\"arguments\":{}}"));
            }
            assertEquals(applied.get(), 0);
        }
    }
    @Test(timeOut = 10000) public void pollingSeesOnlyCompletedAuthoritativeSnapshots() throws Exception {
        var entered = new CountDownLatch(1); var release = new CountDownLatch(1);
        var workers = Executors.newFixedThreadPool(2);
        try (var server = new SyncTransport.Server((seat, method, args) -> {
            if (method.equals("update")) { entered.countDown(); if (!release.await(3, TimeUnit.SECONDS)) throw new AssertionError("Test did not release update"); }
            return List.of("complete", "snapshot");
        }, () -> 1); var local = server.local(0)) {
            var update = workers.submit(() -> local.request("update", null)); assertTrue(entered.await(3, TimeUnit.SECONDS));
            var read = workers.submit(() -> local.request("observe", null));
            assertThrows(TimeoutException.class, () -> read.get(100, TimeUnit.MILLISECONDS));
            release.countDown(); assertEquals(update.get(), read.get());
        } finally { release.countDown(); workers.shutdownNow(); }
    }
    private static final class Wire implements AutoCloseable {
        final Socket socket; final BufferedReader input; final BufferedWriter output;
        Wire(int port) throws IOException { socket = new Socket("127.0.0.1", port); socket.setSoTimeout(3000); input = new BufferedReader(new InputStreamReader(socket.getInputStream(), StandardCharsets.UTF_8)); output = new BufferedWriter(new OutputStreamWriter(socket.getOutputStream(), StandardCharsets.UTF_8)); }
        JsonObject exchange(String command) throws IOException { output.write(command); output.newLine(); output.flush(); var line = input.readLine(); if (line == null) throw new EOFException(); return JsonParser.parseString(line).getAsJsonObject(); }
        @Override public void close() throws IOException { socket.close(); }
    }
}
