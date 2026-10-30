package forge.api;

import com.google.gson.JsonArray;
import com.google.gson.JsonElement;

import java.lang.reflect.Array;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.util.*;

/** Closed, typed parameter identity; never falls back to an object's toString. */
final class ApiTestParameters {
    private ApiTestParameters() { }

    static String sha256(Object[] parameters) { return hash(canonical(parameters).getBytes(StandardCharsets.UTF_8)); }
    static String canonical(Object value) {
        String encoded = encode(value, new IdentityHashMap<>(), 0).toString();
        if (encoded.length() > 1_048_576) throw new IllegalArgumentException("API test parameters exceed the identity bound");
        return encoded;
    }

    private static JsonElement encode(Object value, IdentityHashMap<Object, Boolean> visiting, int depth) {
        if (depth > 64) throw new IllegalArgumentException("API test parameter nesting exceeds the identity bound");
        JsonArray result = new JsonArray();
        if (value == null) { result.add("null"); return result; }
        Class<?> type = value.getClass();
        if (type == String.class) { result.add("string"); result.add((String) value); return result; }
        if (type == Boolean.class) { result.add("boolean"); result.add((Boolean) value); return result; }
        if (type == Character.class) { result.add("char"); result.add((int) (Character) value); return result; }
        if (type == Byte.class || type == Short.class || type == Integer.class || type == Long.class) {
            result.add(type.getName()); result.add(((Number) value).longValue()); return result;
        }
        if (type == Float.class || type == Double.class) {
            result.add(type.getName());
            result.add(type == Float.class ? Float.toHexString((Float) value) : Double.toHexString((Double) value));
            return result;
        }
        if (value instanceof Enum<?> item) {
            result.add("enum"); result.add(item.getDeclaringClass().getName()); result.add(item.name()); return result;
        }
        if (visiting.put(value, Boolean.TRUE) != null) throw new IllegalArgumentException("Cyclic API test parameters");
        try {
            if (type.isArray()) {
                result.add("array"); result.add(type.getComponentType().getName());
                JsonArray items = new JsonArray();
                int length = Array.getLength(value);
                if (length > 100_000) throw new IllegalArgumentException("API test parameter array exceeds the identity bound");
                for (int i = 0; i < length; i++) items.add(encode(Array.get(value, i), visiting, depth + 1));
                result.add(items); return result;
            }
            if (value instanceof Map<?, ?> map) {
                if (map.size() > 100_000) throw new IllegalArgumentException("API test parameter map exceeds the identity bound");
                result.add("map");
                var entries = new TreeMap<String, JsonArray>();
                for (var entry : map.entrySet()) {
                    JsonArray pair = new JsonArray();
                    JsonElement key = encode(entry.getKey(), visiting, depth + 1);
                    pair.add(key); pair.add(encode(entry.getValue(), visiting, depth + 1));
                    if (entries.put(key.toString(), pair) != null) throw new IllegalArgumentException("Ambiguous API test map keys");
                }
                JsonArray items = new JsonArray(); entries.values().forEach(items::add); result.add(items); return result;
            }
            if (value instanceof List<?> list) {
                if (list.size() > 100_000) throw new IllegalArgumentException("API test parameter list exceeds the identity bound");
                result.add("list"); JsonArray items = new JsonArray();
                for (Object item : list) items.add(encode(item, visiting, depth + 1));
                result.add(items); return result;
            }
            if (value instanceof Set<?> set) {
                if (set.size() > 100_000) throw new IllegalArgumentException("API test parameter set exceeds the identity bound");
                result.add("set"); var sorted = new TreeMap<String, JsonElement>();
                for (Object item : set) {
                    JsonElement encoded = encode(item, visiting, depth + 1);
                    if (sorted.put(encoded.toString(), encoded) != null) throw new IllegalArgumentException("Ambiguous API test set items");
                }
                JsonArray items = new JsonArray(); sorted.values().forEach(items::add); result.add(items); return result;
            }
            throw new IllegalArgumentException("Unsupported API test parameter type: " + type.getName());
        } finally { visiting.remove(value); }
    }

    static String hash(byte[] bytes) {
        try { return HexFormat.of().formatHex(MessageDigest.getInstance("SHA-256").digest(bytes)); }
        catch (NoSuchAlgorithmException impossible) { throw new IllegalStateException(impossible); }
    }
}
