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
    @Test(timeOut = 10000) public void tablePresenceUsesTheActualHostCapabilityInsteadOfTheLocalAdaptersFeatureList() throws Exception {
        for (boolean supported : List.of(false, true)) {
            var calls = new AtomicInteger();
            try (var server = new SyncTransport.Server((seat, method, args) -> {
                calls.incrementAndGet(); return Map.of("seat", seat);
            }, () -> 1, ignored -> { }, supported ? Set.of("table-presence") : Set.of());
                 var remote = new SeatConnection(new SyncTransport.Remote("127.0.0.1:" + server.listen()))) {
                var frame = remote.presence(new JsonObject());
                assertEquals(frame != null, supported);
                assertEquals(calls.get(), supported ? 1 : 0, "An older host receives no unsupported presence method");
            }
        }
    }
    @Test(timeOut = 10000) public void privateFaceProjectionIsExplicitAndForwardedOnlyToAnAdvertisingHost() throws Exception {
        for (boolean supported : List.of(false, true)) {
            Set<String> capabilities = supported ? Set.of("owned-decision-observation", "private-action-faces")
                    : Set.of("owned-decision-observation");
            try (var server = new SyncTransport.Server((seat, method, args) -> Map.of("opted", MatchEndpoint.privateActionFaces(args)),
                    () -> 1, ignored -> { }, capabilities);
                 var remote = new SeatConnection(new SyncTransport.Remote("127.0.0.1:" + server.listen()))) {
                assertEquals(remote.state().get("opted"), false);
                assertEquals(remote.decisionObservation().get("opted"), false);
                assertEquals(remote.state(true).get("opted"), supported);
                assertEquals(remote.decisionObservation(true).get("opted"), supported);
            }
        }
        assertFalse(MatchEndpoint.privateActionFaces(new JsonObject()));
        var invalid = new JsonObject(); invalid.addProperty("privateActionFaces", "true");
        assertThrows(IllegalArgumentException.class, () -> MatchEndpoint.privateActionFaces(invalid));
    }

    @Test(timeOut = 10000) public void codedFailuresKeepTheirIdentityAcrossTcpAndDoNotAuthorizeReplay() throws Exception {
        var applied = new AtomicInteger();
        try (var server = new SyncTransport.Server((seat, method, args) -> {
            applied.incrementAndGet();
            if (method.equals("coded")) throw new SyncTransport.CodedFailure("observation-superseded", "Changed owned input");
            throw new IllegalArgumentException("Legacy failure");
        }, () -> 1); var remote = new SyncTransport.Remote("127.0.0.1:" + server.listen())) {
            try { remote.request("coded", new JsonObject()); fail("The coded failure must be returned"); }
            catch (SyncTransport.CodedFailure rejected) { assertEquals(rejected.code(), "observation-superseded"); }
            assertEquals(applied.get(), 1, "The transport never retries a coded failure");
            try { remote.request("legacy", new JsonObject()); fail("The ordinary failure must be returned"); }
            catch (IllegalArgumentException rejected) {
                assertFalse(rejected instanceof SyncTransport.CodedFailure);
                assertEquals(rejected.getMessage(), "Legacy failure");
            }
            assertEquals(applied.get(), 2);
        }
        assertThrows(IllegalArgumentException.class, () -> new SyncTransport.CodedFailure("Invalid code", "No"));
    }

    @Test(timeOut = 10000) public void optionalCapabilitiesAreNegotiatedWithoutChangingVersionOrCommandAuthority() throws Exception {
        try (var server = new SyncTransport.Server((seat, method, args) -> Map.of("seat", seat), () -> 1,
                ignored -> { }, Set.of("match-history")); var local = server.local(0);
             var remote = new SyncTransport.Remote("127.0.0.1:" + server.listen())) {
            assertEquals(SyncTransport.VERSION, 4);
            assertEquals(local.capabilities(), Set.of("match-history"));
            assertEquals(remote.capabilities(), local.capabilities());
            remote.reconnect(); remote.request("observe", null);
            assertEquals(remote.capabilities(), Set.of("match-history"));
            assertThrows(UnsupportedOperationException.class, () -> local.capabilities().add("another"));
        }
    }
    @Test(timeOut = 10000) public void legacyHandshakeWithoutCapabilitiesIsAnExplicitEmptyFeatureSet() throws Exception {
        try (var listener = new java.net.ServerSocket(0)) {
            var done = CompletableFuture.runAsync(() -> {
                try (var socket = listener.accept(); var input = new BufferedReader(new InputStreamReader(socket.getInputStream(), StandardCharsets.UTF_8));
                     var output = new BufferedWriter(new OutputStreamWriter(socket.getOutputStream(), StandardCharsets.UTF_8))) {
                    input.readLine(); output.write("{\"version\":4,\"resume\":\"legacy\",\"seat\":1}\n"); output.flush();
                    while (input.readLine() != null) { }
                } catch (IOException failure) { throw new CompletionException(failure); }
            });
            try (var remote = new SyncTransport.Remote("127.0.0.1:" + listener.getLocalPort())) { assertTrue(remote.capabilities().isEmpty()); }
            done.get(3, TimeUnit.SECONDS);
        }
    }
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
        var nested = new LinkedHashMap<String, Object>(); nested.put("value", 1);
        var values = new ArrayList<Object>(); values.add(nested); board.put("nested", values);
        var raw = JsonParser.parseString("{\"values\":[{\"value\":1}],\"nullable\":null}").getAsJsonObject();
        board.put("raw", raw);
        try (var server = new SyncTransport.Server((seat, method, args) -> { board.put("value", applied.incrementAndGet()); return board; }, () -> 1)) {
            int port = server.listen(); String resume;
            var command = JsonParser.parseString("{\"sequence\":1,\"method\":\"add\",\"arguments\":{}}").getAsJsonObject();
            try (var wire = new Wire(port)) {
                var hello = wire.exchange("{\"version\":4,\"resume\":\"\"}"); resume = hello.get("resume").getAsString();
                var first = wire.exchange(command.toString());
                var expected = first.deepCopy();
                board.put("value", 999); nested.put("value", 999); values.add("later");
                raw.getAsJsonArray("values").get(0).getAsJsonObject().addProperty("value", 999);
                raw.addProperty("nullable", "later");
                first.getAsJsonObject("result").addProperty("caller-only", true);
                assertEquals(wire.exchange(command.toString()), expected,
                        "Nested handler containers, raw Gson trees and the caller's parsed receipt cannot mutate the retained receipt");
            }
            try (var wire = new Wire(port)) {
                wire.exchange(SyncTransport.JSON.toJson(Map.of("version", 4, "resume", resume)));
                var receipt = wire.exchange(command.toString());
                assertEquals(receipt.getAsJsonObject("result").get("value").getAsInt(), 1);
                assertEquals(receipt.getAsJsonObject("result").getAsJsonArray("nested").size(), 1);
                assertEquals(receipt.getAsJsonObject("result").getAsJsonArray("nested").get(0).getAsJsonObject().get("value").getAsInt(), 1);
                assertEquals(receipt.getAsJsonObject("result").getAsJsonObject("raw").getAsJsonArray("values")
                        .get(0).getAsJsonObject().get("value").getAsInt(), 1);
                assertTrue(receipt.getAsJsonObject("result").getAsJsonObject("raw").get("nullable").isJsonNull());
                assertFalse(receipt.getAsJsonObject("result").has("caller-only"));
                assertEquals(applied.get(), 1);
            }
            server.revoke(1);
            try (var wire = new Wire(port)) {
                assertThrows(EOFException.class, () -> wire.exchange(SyncTransport.JSON.toJson(Map.of("version", 4, "resume", resume))));
            }
        }
    }

    @Test(timeOut = 10000) @SuppressWarnings("unchecked") public void localResultsNeverExposeTheRetainedReceiptTreeOrHandlerContainers() {
        var nested = new LinkedHashMap<String, Object>(); nested.put("value", 1);
        var values = new ArrayList<Object>(); values.add(nested);
        var raw = JsonParser.parseString("{\"values\":[1]}").getAsJsonObject();
        var board = new LinkedHashMap<String, Object>(); board.put("nested", values); board.put("raw", raw);
        try (var server = new SyncTransport.Server((seat, method, args) -> board, () -> 1); var local = server.local(0)) {
            var first = (Map<String, Object>)local.request("read", null);
            var firstValues = (List<Object>)first.get("nested");
            ((Map<String, Object>)firstValues.get(0)).put("value", 999);
            firstValues.add("caller-only"); first.put("injected", true);
            ((List<Object>)((Map<String, Object>)first.get("raw")).get("values")).add("caller-only");
            assertEquals(nested.get("value"), 1);
            assertEquals(values.size(), 1);
            assertEquals(raw.getAsJsonArray("values").size(), 1);
            var next = (Map<String, Object>)local.request("read", null);
            var nextValues = (List<Object>)next.get("nested");
            assertNotSame(next, first); assertNotSame(nextValues, firstValues);
            assertEquals(((Map<String, Object>)nextValues.get(0)).get("value"), 1L);
            assertEquals(nextValues.size(), 1); assertFalse(next.containsKey("injected"));
            assertEquals(((List<Object>)((Map<String, Object>)next.get("raw")).get("values")).size(), 1);
        }
    }

    @Test(timeOut = 10000) public void retainedCodedFailuresReplayExactlyAndChangedRequestsRemainRejected() throws Exception {
        var applied = new AtomicInteger();
        try (var server = new SyncTransport.Server((seat, method, args) -> {
            applied.incrementAndGet(); throw new SyncTransport.CodedFailure("observation-superseded", "Changed owned input");
        }, () -> 1)) {
            int port = server.listen(); String resume;
            String command = "{\"sequence\":1,\"method\":\"read\",\"arguments\":{\"value\":1}}";
            try (var wire = new Wire(port)) {
                resume = wire.exchange("{\"version\":4}").get("resume").getAsString();
                var first = wire.exchange(command);
                assertEquals(first.get("errorCode").getAsString(), "observation-superseded");
                assertEquals(first.get("error").getAsString(), "Changed owned input");
                first.addProperty("errorCode", "caller-only");
                assertEquals(wire.exchange(command).get("errorCode").getAsString(), "observation-superseded");
                assertThrows(EOFException.class, () -> wire.exchange("{\"sequence\":1,\"method\":\"read\",\"arguments\":{\"value\":2}}"));
                assertEquals(applied.get(), 1, "Changed arguments at the cached sequence cannot become another command");
            }
            try (var wire = new Wire(port)) {
                wire.exchange(SyncTransport.JSON.toJson(Map.of("version", 4, "resume", resume)));
                var replayed = wire.exchange(command);
                assertEquals(replayed.get("errorCode").getAsString(), "observation-superseded");
                assertEquals(replayed.get("error").getAsString(), "Changed owned input");
                assertEquals(applied.get(), 1, "Reconnect replays the original failure, not its rules operation");
            }
        }
    }

    @Test(timeOut = 10000) public void anOlderConnectionCannotReplayOrDetachTheResumedGeneration() throws Exception {
        var applied = new AtomicInteger();
        try (var server = new SyncTransport.Server((seat, method, args) -> applied.incrementAndGet(), () -> 1)) {
            int port = server.listen();
            try (var old = new Wire(port); var current = new Wire(port)) {
                String resume = old.exchange("{\"version\":4}").get("resume").getAsString();
                String first = "{\"sequence\":1,\"method\":\"add\",\"arguments\":{}}";
                assertEquals(old.exchange(first).get("result").getAsInt(), 1);
                current.exchange(SyncTransport.JSON.toJson(Map.of("version", 4, "resume", resume)));
                assertEquals(current.exchange(first).get("result").getAsInt(), 1);
                assertThrows(EOFException.class, () -> old.exchange(first));
                assertEquals(current.exchange("{\"sequence\":2,\"method\":\"add\",\"arguments\":{}}").get("result").getAsInt(), 2);
                assertEquals(applied.get(), 2);
                server.revoke(1);
                assertThrows(EOFException.class, () -> current.exchange("{\"sequence\":2,\"method\":\"add\",\"arguments\":{}}"));
            }
        }
    }

    @Test(timeOut = 10000) public void aSerializationFailureIsOneCompleteCachedFailureRatherThanAPartialReceipt() throws Exception {
        var applied = new AtomicInteger();
        try (var server = new SyncTransport.Server((seat, method, args) -> {
            int count = applied.incrementAndGet();
            var values = new LinkedHashMap<String, Object>(); values.put("first", count);
            values.put("last", count == 1 ? Double.NaN : 1.0); return values;
        }, () -> 1); var wire = new Wire(server.listen())) {
            wire.exchange("{\"version\":4}");
            String command = "{\"sequence\":1,\"method\":\"read\",\"arguments\":{}}";
            var first = wire.exchange(command);
            assertTrue(first.has("error")); assertFalse(first.has("result"));
            assertFalse(first.has("errorCode"));
            assertEquals(wire.exchange(command), first);
            assertEquals(applied.get(), 1, "Failed result serialization still consumes its original sequence exactly once");
            var next = wire.exchange("{\"sequence\":2,\"method\":\"read\",\"arguments\":{}}");
            assertEquals(next.getAsJsonObject("result").get("first").getAsInt(), 2);
        }
    }

    @Test(timeOut = 15000) public void cachedOversizedNetworkReceiptsKeepTheExistingFrameLimitWithoutReapplying() throws Exception {
        var applied = new AtomicInteger();
        String oversized = "x".repeat(8 * 1024 * 1024);
        try (var server = new SyncTransport.Server((seat, method, args) -> { applied.incrementAndGet(); return oversized; }, () -> 1)) {
            int port = server.listen(); String resume;
            String command = "{\"sequence\":1,\"method\":\"read\",\"arguments\":{}}";
            try (var wire = new Wire(port)) {
                resume = wire.exchange("{\"version\":4}").get("resume").getAsString();
                assertThrows(EOFException.class, () -> wire.exchange(command));
                assertEquals(applied.get(), 1);
            }
            try (var wire = new Wire(port)) {
                wire.exchange(SyncTransport.JSON.toJson(Map.of("version", 4, "resume", resume)));
                assertThrows(EOFException.class, () -> wire.exchange(command));
                assertEquals(applied.get(), 1, "The encoded-frame failure occurs after receipt capture and cannot repeat its handler");
            }
        }
        try (var server = new SyncTransport.Server((seat, method, args) -> oversized.substring(128), () -> 1);
             var wire = new Wire(server.listen())) {
            wire.exchange("{\"version\":4}");
            assertEquals(wire.exchange("{\"sequence\":1,\"method\":\"read\",\"arguments\":{}}").get("result").getAsString().length(),
                    8 * 1024 * 1024 - 128, "A fitting near-limit receipt remains supported");
        }
    }
    @Test(timeOut = 10000) public void mismatchedVersionsAndOutOfOrderCommandsNeverReachTheEngine() throws Exception {
        var applied = new AtomicInteger(); var joined = new AtomicInteger();
        try (var server = new SyncTransport.Server((seat, method, args) -> applied.incrementAndGet(), () -> { joined.incrementAndGet(); return 1; })) {
            int port = server.listen();
            for (int version : new int[] { 1, 3, 5 }) {
                try (var wire = new Wire(port)) {
                    assertTrue(wire.exchange(SyncTransport.JSON.toJson(Map.of("version", version))).has("error"));
                }
                assertEquals(joined.get(), 0, "Incompatible peers cannot acquire a seat before rejection");
            }
            try (var wire = new Wire(port)) {
                wire.exchange("{\"version\":4}");
                assertThrows(EOFException.class, () -> wire.exchange("{\"sequence\":2,\"method\":\"add\",\"arguments\":{}}"));
            }
            assertEquals(applied.get(), 0);
        }
    }
    @Test(timeOut = 10000) public void remoteRejectsOldAndFutureServersBeforeSendingCommands() throws Exception {
        for (int version : new int[] { 3, 5 }) {
            try (var listener = new java.net.ServerSocket(0)) {
                var done = CompletableFuture.runAsync(() -> {
                    try (var socket = listener.accept(); var input = new BufferedReader(new InputStreamReader(socket.getInputStream(), StandardCharsets.UTF_8));
                         var output = new BufferedWriter(new OutputStreamWriter(socket.getOutputStream(), StandardCharsets.UTF_8))) {
                        socket.setSoTimeout(3000);
                        var hello = JsonParser.parseString(input.readLine()).getAsJsonObject();
                        assertEquals(hello.get("version").getAsInt(), 4);
                        output.write(SyncTransport.JSON.toJson(Map.of("version", version, "resume", "mismatch", "seat", 1)) + "\n"); output.flush();
                        assertNull(input.readLine(), "The rejected server cannot receive any command after its incompatible handshake");
                    } catch (IOException failure) { throw new CompletionException(failure); }
                });
                assertThrows(IllegalStateException.class, () -> {
                    try (var remote = new SyncTransport.Remote("127.0.0.1:" + listener.getLocalPort())) {
                        fail("An incompatible server was accepted");
                    }
                });
                done.get(3, TimeUnit.SECONDS);
            }
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
