package forge.api;

import com.google.gson.JsonObject;
import java.util.Map;

/** A seat's capability: exactly the same API over a local call or a sync connection. */
interface MatchEndpoint {
    Map<String, Object> state();
    Map<String, Object> action(JsonObject command);
    Map<String, Object> concede(JsonObject command);
    boolean finished();
}
