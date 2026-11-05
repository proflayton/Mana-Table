package forge.api;

import com.google.gson.JsonObject;
import java.util.Map;

/** A seat's capability: exactly the same API over a local call or a sync connection. */
interface MatchEndpoint {
    Map<String, Object> state();
    Map<String, Object> state(boolean privateActionFaces);
    Map<String, Object> decisionObservation();
    Map<String, Object> decisionObservation(boolean privateActionFaces);
    Map<String, Object> action(JsonObject command);
    Map<String, Object> sourceActions(JsonObject command);
    default Map<String, Object> presence(JsonObject update) { return null; }
    Map<String, Object> history(JsonObject query);
    Map<String, Object> concede(JsonObject command);
    Map<String, Object> tempoPresented(JsonObject command);
    boolean finished();

    static boolean privateActionFaces(JsonObject request) {
        if (request == null || !request.has("privateActionFaces")) return false;
        var value = request.get("privateActionFaces");
        if (!value.isJsonPrimitive() || !value.getAsJsonPrimitive().isBoolean())
            throw new IllegalArgumentException("Private action faces require a boolean projection option");
        return value.getAsBoolean();
    }
}
