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
            case "observe" -> session.state(MatchEndpoint.privateActionFaces(p));
            case "decisionObservation" -> session.decisionObservation(MatchEndpoint.privateActionFaces(p));
            case "reply" -> session.action(p); case "sourceActions" -> session.sourceActions(p); case "concede" -> session.concede(p);
            case "tempoPresented" -> session.tempoPresented(p);
            case "history" -> session.history(p);
            default -> throw new IllegalArgumentException("Unknown seat command");
        }, () -> { throw new IllegalStateException("This seat is local"); }, ignored -> { }, Set.of("match-history", "owned-decision-observation", "private-action-faces"));
        return new SeatConnection(server.local(0), () -> { server.close(); session.closeTable(); });
    }
    @SuppressWarnings("unchecked") public Map<String, Object> state() { return (Map<String, Object>)connection.request("observe", new JsonObject()); }
    @SuppressWarnings("unchecked") public Map<String, Object> state(boolean privateActionFaces) {
        return (Map<String, Object>)connection.request("observe", projection(privateActionFaces));
    }
    public Map<String, Object> decisionObservation() { return decisionObservation(false); }
    @SuppressWarnings("unchecked") public Map<String, Object> decisionObservation(boolean privateActionFaces) {
        if (!connection.capabilities().contains("owned-decision-observation"))
            throw new SyncTransport.CodedFailure("unsupported-capability", "This table host does not expose owned decision observations.");
        return (Map<String, Object>)connection.request("decisionObservation", projection(privateActionFaces));
    }
    private JsonObject projection(boolean privateActionFaces) {
        var request = new JsonObject();
        if (privateActionFaces && connection.capabilities().contains("private-action-faces")) request.addProperty("privateActionFaces", true);
        return request;
    }
    @SuppressWarnings("unchecked") public Map<String, Object> action(JsonObject command) { return (Map<String, Object>)connection.request("reply", command); }
    @SuppressWarnings("unchecked") public Map<String, Object> sourceActions(JsonObject command) { return (Map<String, Object>)connection.request("sourceActions", command); }
    @SuppressWarnings("unchecked") public Map<String, Object> history(JsonObject query) {
        if (!connection.capabilities().contains("match-history")) throw new HistoryUnavailableException();
        return (Map<String, Object>)connection.request("history", query);
    }
    @SuppressWarnings("unchecked") public Map<String, Object> concede(JsonObject command) { return (Map<String, Object>)connection.request("concede", command); }
    @SuppressWarnings("unchecked") public Map<String, Object> tempoPresented(JsonObject command) { return (Map<String, Object>)connection.request("tempoPresented", command); }
    public boolean finished() { var snapshot = state(); return snapshot != null && List.of("finished", "error").contains(snapshot.get("status")); }
    @Override public void close() { dispose.run(); connection.close(); }
}
