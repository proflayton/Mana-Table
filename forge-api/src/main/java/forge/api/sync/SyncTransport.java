package forge.api.sync;

import com.google.gson.*;
import java.io.*;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.util.*;
import java.util.concurrent.*;

/** Ordered request/receipt transport. No cards, rules, decisions, or engine types belong here. */
public final class SyncTransport {
    public static final int VERSION = 3;
    public static final Gson JSON = new GsonBuilder().serializeNulls()
            .setObjectToNumberStrategy(ToNumberPolicy.LONG_OR_DOUBLE).create();
    private static final int MAX_FRAME = 8 * 1024 * 1024;
    public interface Handler { Object apply(int seat, String method, JsonObject arguments) throws Exception; }
    public interface Connection extends AutoCloseable {
        Object request(String method, JsonObject arguments);
        @Override void close();
    }
    private SyncTransport() { }

    public static final class Server implements AutoCloseable {
        private final Handler handler;
        private final java.util.function.IntSupplier join;
        private final Map<String, SeatChannel> channels = new ConcurrentHashMap<>();
        private final Set<SeatChannel> seatChannels = ConcurrentHashMap.newKeySet();
        private final Set<Socket> sockets = ConcurrentHashMap.newKeySet();
        private final ExecutorService workers = Executors.newCachedThreadPool(r -> { var t = new Thread(r, "Mana sync"); t.setDaemon(true); return t; });
        private ServerSocket listener;
        private volatile boolean closed;
        public Server(Handler handler, java.util.function.IntSupplier join) { this.handler = handler; this.join = join; }
        public synchronized int listen() throws IOException {
            if (listener != null) return listener.getLocalPort();
            listener = new ServerSocket(0);
            workers.execute(() -> {
                while (!closed) try {
                    var socket = listener.accept();
                    socket.setSoTimeout(45000); socket.setTcpNoDelay(true); sockets.add(socket);
                    workers.execute(() -> serve(socket));
                } catch (IOException error) { if (!closed) close(); }
            });
            return listener.getLocalPort();
        }
        public Connection local(int seat) { return new Local(new SeatChannel(seat)); }
        public Connection joinLocal() { return local(join.getAsInt()); }
        public void revoke(int seat) {
            for (var channel : seatChannels) if (channel.seat == seat) channel.revoked = true;
            channels.entrySet().removeIf(entry -> entry.getValue().seat == seat);
        }
        public Connection remoteHost() throws IOException {
            String token = UUID.randomUUID().toString() + UUID.randomUUID();
            channels.put(token, new SeatChannel(0));
            return new Remote("127.0.0.1:" + listen(), token);
        }
        private final class SeatChannel {
            final int seat;
            long lastSequence;
            volatile boolean revoked;
            JsonObject lastRequest, lastReceipt;
            SeatChannel(int seat) { this.seat = seat; seatChannels.add(this); }
            synchronized JsonObject apply(JsonObject request) {
                if (revoked || closed) throw new IllegalStateException("This seat is no longer active");
                long sequence = request.get("sequence").getAsLong();
                if (sequence == lastSequence && lastRequest != null && lastRequest.equals(request)) return lastReceipt.deepCopy();
                if (sequence != lastSequence + 1) throw new IllegalArgumentException("Synchronization sequence changed; reconnect and resnapshot");
                var receipt = new JsonObject(); receipt.addProperty("sequence", sequence);
                try { receipt.add("result", JSON.toJsonTree(handler.apply(seat, request.get("method").getAsString(), request.getAsJsonObject("arguments")))); }
                catch (Exception failure) { receipt.addProperty("error", Objects.requireNonNullElse(failure.getMessage(), failure.getClass().getSimpleName())); }
                lastSequence = sequence; lastRequest = request.deepCopy(); lastReceipt = receipt.deepCopy();
                return receipt;
            }
        }
        private void serve(Socket socket) {
            try (socket; var input = reader(socket); var output = writer(socket)) {
                var hello = read(input);
                if (!hello.has("version") || hello.get("version").getAsInt() != VERSION) {
                    write(output, JSON.toJsonTree(Map.of("error", "Incompatible sync protocol. All players need the same build."))); return;
                }
                SeatChannel channel;
                String token = hello.has("resume") ? hello.get("resume").getAsString() : "";
                {
                    if (!token.isEmpty()) {
                        channel = channels.get(token);
                        if (channel == null) throw new IllegalArgumentException("This seat can no longer be resumed");
                    } else {
                        channel = new SeatChannel(join.getAsInt());
                        token = UUID.randomUUID().toString() + UUID.randomUUID(); channels.put(token, channel);
                    }
                }
                write(output, JSON.toJsonTree(Map.of("version", VERSION, "resume", token, "seat", channel.seat)));
                while (!closed) write(output, channel.apply(read(input)));
            } catch (Exception failure) {
                // A dropped connection retains its seat and receipt. Reconnection never replays a mutation.
            } finally { sockets.remove(socket); }
        }
        private final class Local implements Connection {
            private final SeatChannel channel;
            private long sequence;
            Local(SeatChannel channel) { this.channel = channel; }
            @Override public synchronized Object request(String method, JsonObject args) { return result(channel.apply(envelope(++sequence, method, args)), sequence); }
            @Override public void close() { }
        }
        @Override public synchronized void close() {
            if (closed) return;
            closed = true;
            if (listener != null) try { listener.close(); } catch (IOException ignored) { }
            for (var socket : sockets) try { socket.close(); } catch (IOException ignored) { }
            channels.clear(); seatChannels.clear(); workers.shutdownNow();
        }
    }

    public static final class Remote implements Connection {
        private final InetSocketAddress address;
        private Socket socket;
        private BufferedReader input;
        private BufferedWriter output;
        private String token = "";
        private long sequence;
        private JsonObject unresolved;
        public Remote(String endpoint) { this(endpoint, ""); }
        private Remote(String endpoint, String resume) {
            token = resume;
            var uri = URI.create("tcp://" + endpoint);
            if (uri.getHost() == null || uri.getPort() < 1 || uri.getPort() > 65535 || !uri.getPath().isEmpty()) throw new IllegalArgumentException("Enter host:port or an invite");
            address = new InetSocketAddress(uri.getHost(), uri.getPort());
            try { connect(); } catch (IOException e) { close(); throw new IllegalStateException("Could not connect to the table", e); }
        }
        private void connect() throws IOException {
            disconnect();
            socket = new Socket(); socket.connect(address, 10000); socket.setSoTimeout(45000); socket.setTcpNoDelay(true);
            input = reader(socket); output = writer(socket);
            write(output, JSON.toJsonTree(Map.of("version", VERSION, "resume", token)));
            var hello = read(input);
            if (hello.has("error")) throw new IOException(hello.get("error").getAsString());
            if (hello.get("version").getAsInt() != VERSION) throw new IOException("Incompatible sync protocol");
            token = hello.get("resume").getAsString();
        }
        @Override public synchronized Object request(String method, JsonObject args) {
            if (unresolved != null) exchange(unresolved);
            unresolved = envelope(++sequence, method, args);
            return exchange(unresolved);
        }
        private Object exchange(JsonObject command) {
            long expected = command.get("sequence").getAsLong();
            for (int attempt = 0; attempt < 2; attempt++) try {
                if (socket == null) connect();
                write(output, command);
                var receipt = read(input);
                if (receipt.get("sequence").getAsLong() != expected) throw new IOException("Unexpected synchronization receipt");
                unresolved = null;
                return result(receipt, expected);
            } catch (IOException failure) {
                disconnect();
                if (attempt == 1) throw new IllegalStateException("Connection interrupted. Reconnect to synchronize the table.", failure);
            }
            throw new AssertionError();
        }
        /** A reconnect preserves the seat; the next read fetches a complete authoritative snapshot. */
        public synchronized void reconnect() { disconnect(); }
        private void disconnect() { if (socket != null) try { socket.close(); } catch (IOException ignored) { } socket = null; }
        @Override public synchronized void close() { disconnect(); }
    }

    private static JsonObject envelope(long sequence, String method, JsonObject args) {
        var command = new JsonObject(); command.addProperty("sequence", sequence); command.addProperty("method", method);
        command.add("arguments", args == null ? new JsonObject() : args.deepCopy()); return command;
    }
    private static Object result(JsonObject receipt, long sequence) {
        if (receipt.get("sequence").getAsLong() != sequence) throw new IllegalStateException("Unexpected synchronization receipt");
        if (receipt.has("error")) throw new IllegalArgumentException(receipt.get("error").getAsString());
        return JSON.fromJson(receipt.get("result"), Object.class);
    }
    private static BufferedReader reader(Socket socket) throws IOException { return new BufferedReader(new InputStreamReader(socket.getInputStream(), StandardCharsets.UTF_8)); }
    private static BufferedWriter writer(Socket socket) throws IOException { return new BufferedWriter(new OutputStreamWriter(socket.getOutputStream(), StandardCharsets.UTF_8)); }
    private static JsonObject read(BufferedReader input) throws IOException {
        var line = new StringBuilder();
        for (int c; (c = input.read()) != -1;) {
            if (c == '\n') return JsonParser.parseString(line.toString()).getAsJsonObject();
            if (line.length() >= MAX_FRAME) throw new IOException("Synchronization frame too large");
            line.append((char)c);
        }
        throw new EOFException("Connection closed");
    }
    private static void write(BufferedWriter output, JsonElement value) throws IOException {
        String line = JSON.toJson(value);
        if (line.length() > MAX_FRAME) throw new IOException("Synchronization frame too large");
        output.write(line); output.newLine(); output.flush();
    }
}
