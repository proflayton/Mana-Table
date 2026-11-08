package forge.api;

import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import com.google.gson.JsonElement;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import com.google.gson.JsonPrimitive;
import org.testng.annotations.DataProvider;
import org.testng.annotations.Test;

import java.io.ByteArrayOutputStream;
import java.io.PrintStream;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.LinkedHashMap;
import java.util.List;

import static org.testng.Assert.*;

public class DesktopReplySerializationTest {
    private static final Gson JSON = new GsonBuilder().serializeNulls().create();

    @DataProvider(name = "replies")
    public Object[][] replies() {
        return new Object[][] {
                { "null" }, { "scalar" }, { "long" }, { "ordered nested values" },
                { "wire tree" }, { "continuation record" }, { "observation record" }
        };
    }

    private static Object result(String name) {
        var nested = new LinkedHashMap<String, Object>();
        nested.put("name", "Mind's Eye: \"choose\"\n<>&\u2028🎴");
        nested.put("missing", null);
        nested.put("values", Arrays.asList(true, false, 0, -0.0, Long.MAX_VALUE, (1L << 53) + 1, null));
        return switch (name) {
            case "null" -> null;
            case "scalar" -> "line\nquote\" slash\\ Unicode 🎴";
            case "long" -> Long.MAX_VALUE;
            case "ordered nested values" -> nested;
            case "wire tree" -> JsonParser.parseString("{\"null\":null,\"list\":[1,true,{\"value\":9007199254740993}]}");
            case "continuation record" -> new PriorityContinuation.Frame(17, "grant", PriorityContinuation.Mode.NO_RESPONSE,
                    PriorityContinuation.Status.HELD, "awaiting-observation", 3, List.of("MAIN1", "END_OF_TURN"));
            case "observation record" -> new PriorityObservation.Frame((1L << 53) + 1, Long.MAX_VALUE, 17, 19, true, false);
            default -> throw new IllegalArgumentException("Unknown serialization case");
        };
    }

    @Test(dataProvider = "replies")
    public void successRepliesPreserveExactUtf8Bytes(String name) {
        Object result = result(name);
        JsonElement requestId = new JsonPrimitive((1L << 53) + 1);
        assertEquals(actual(requestId, result), previous(requestId, result), "Changed reply bytes for " + name);
        assertEquals(actual(null, result), previous(null, result), "Changed missing-id reply bytes for " + name);
    }

    @Test public void serializationFailureCannotWriteAPartialSuccessFrame() {
        var request = new JsonObject(); request.addProperty("id", Long.MAX_VALUE);
        var invalid = new LinkedHashMap<String, Object>();
        invalid.put("validPrefix", "x".repeat(65536));
        invalid.put("invalidNumber", Double.NaN);
        var bytes = new ByteArrayOutputStream();
        try (var protocol = new PrintStream(bytes, true, StandardCharsets.UTF_8)) {
            assertThrows(IllegalArgumentException.class, () -> DesktopEngine.writeSuccessReply(protocol, request.get("id"), invalid));
            assertEquals(bytes.size(), 0, "A failed result must not publish even the valid prefix of a frame");
            protocol.println(JSON.toJson(DesktopEngine.errorReply(request, new IllegalArgumentException("controlled serialization failure"))));
        }
        String[] lines = bytes.toString(StandardCharsets.UTF_8).strip().split("\\R");
        assertEquals(lines.length, 1);
        var error = JsonParser.parseString(lines[0]).getAsJsonObject();
        assertEquals(error.get("id"), request.get("id"));
        assertFalse(error.has("result"));
        assertEquals(error.get("error").getAsString(), "controlled serialization failure");
    }

    @Test public void completedRepliesDoNotRetainMutableResults() {
        var values = new ArrayList<>(List.of("first"));
        byte[] encoded = actual(new JsonPrimitive(7), values);
        values.add("later");
        assertEquals(JsonParser.parseString(new String(encoded, StandardCharsets.UTF_8)).getAsJsonObject()
                .getAsJsonArray("result").size(), 1);
    }

    private static byte[] actual(JsonElement requestId, Object result) {
        var bytes = new ByteArrayOutputStream();
        try (var protocol = new PrintStream(bytes, true, StandardCharsets.UTF_8)) {
            DesktopEngine.writeSuccessReply(protocol, requestId, result);
        }
        return bytes.toByteArray();
    }

    private static byte[] previous(JsonElement requestId, Object result) {
        var reply = new JsonObject(); reply.add("id", requestId); reply.add("result", JSON.toJsonTree(result));
        return (JSON.toJson(reply) + System.lineSeparator()).getBytes(StandardCharsets.UTF_8);
    }
}
