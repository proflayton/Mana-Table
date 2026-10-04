package forge.api;

import com.google.gson.JsonObject;
import forge.api.sync.SyncTransport;
import java.util.*;

/** The same client-side seat endpoint for loopback and TCP synchronization. */
final class SeatConnection implements MatchEndpoint, AutoCloseable {
    private final SyncTransport.Connection connection;
    private final Runnable dispose;
    SeatConnection(SyncTransport.Connection connection) { this(connection, () -> { }); }
    private SeatConnection(SyncTransport.Connection connection, Runnable dispose) { this.connection = connection; this.dispose = dispose; }
    static SeatConnection local(MatchSession session) {
        var server = new SyncTransport.Server((seat, method, p) -> switch (method) {
            case "observe" -> session.state(); case "reply" -> session.action(p); case "concede" -> session.concede(p);
            default -> throw new IllegalArgumentException("Unknown seat command");
        }, () -> { throw new IllegalStateException("This seat is local"); });
        return new SeatConnection(server.local(0), () -> { server.close(); session.closeTable(); });
    }
    @SuppressWarnings("unchecked") public Map<String, Object> state() { return (Map<String, Object>)connection.request("observe", new JsonObject()); }
    @SuppressWarnings("unchecked") public Map<String, Object> action(JsonObject command) { return (Map<String, Object>)connection.request("reply", command); }
    @SuppressWarnings("unchecked") public Map<String, Object> concede(JsonObject command) { return (Map<String, Object>)connection.request("concede", command); }
    public boolean finished() { var snapshot = state(); return snapshot != null && List.of("finished", "error").contains(snapshot.get("status")); }
    @Override public void close() { dispose.run(); connection.close(); }
}
