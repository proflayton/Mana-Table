package forge.api;

import com.google.gson.JsonArray;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;

import java.io.IOException;
import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.time.Duration;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Locale;
import java.util.Set;
import java.util.regex.Pattern;

/** Public, read-only Archidekt imports. The untouched source JSON is kept with the baseline. */
final class ArchidektSource {
    private static final Pattern PATH = Pattern.compile("^/decks/([1-9][0-9]{0,17})(?:/[^?#]*)?/?$");
    private static final int MAX_BYTES = 4_000_000;
    record Row(String cardId, CardCatalog.CardInfo card, int quantity, String section,
               List<String> categories, boolean included) { }
    record Remote(String remoteId, String url, String name, String format, String updatedAt,
                  List<Row> cards, List<String> problems, List<String> warnings, JsonObject original) { }

    static String deckId(String value) {
        if (value == null) throw new IllegalArgumentException("Paste an Archidekt deck URL");
        String url = value.strip();
        if (url.matches("(?i)archidekt:[1-9][0-9]{0,17}")) return url.substring(url.indexOf(':') + 1);
        try {
            URI uri = URI.create(url);
            if (!"https".equalsIgnoreCase(uri.getScheme()) || uri.getUserInfo() != null
                    || uri.getPort() != -1 || !("archidekt.com".equalsIgnoreCase(uri.getHost())
                    || "www.archidekt.com".equalsIgnoreCase(uri.getHost()))) throw new IllegalArgumentException();
            var path = PATH.matcher(uri.getPath());
            if (!path.matches()) throw new IllegalArgumentException();
            return path.group(1);
        } catch (IllegalArgumentException error) {
            throw new IllegalArgumentException("Use a public https://archidekt.com/decks/12345 deck URL");
        }
    }

    static Remote fetch(String value, CardCatalog catalog) throws IOException, InterruptedException {
        String id = deckId(value);
        var client = HttpClient.newBuilder().connectTimeout(Duration.ofSeconds(8))
                .followRedirects(HttpClient.Redirect.NEVER).build();
        var request = HttpRequest.newBuilder(URI.create("https://archidekt.com/api/decks/" + id + "/"))
                .timeout(Duration.ofSeconds(18)).header("Accept", "application/json")
                .header("User-Agent", "ManaTable/0.2 public-deck-import").GET().build();
        var pending = client.sendAsync(request, response -> new LimitedBody());
        try {
            var response = pending.get(20, java.util.concurrent.TimeUnit.SECONDS);
            if (response.statusCode() != 200) throw new IOException(response.statusCode() == 429
                    ? "Archidekt is limiting requests. Your saved deck is safe; try again later."
                    : "Archidekt returned " + response.statusCode() + ". Check that the deck is public. Your saved deck is safe.");
            byte[] bytes = response.body();
            try { return parse(id, JsonParser.parseString(new String(bytes, java.nio.charset.StandardCharsets.UTF_8)).getAsJsonObject(), catalog); }
            catch (IllegalStateException error) { throw new IOException("Archidekt returned an unexpected deck response", error); }
        } catch (java.util.concurrent.TimeoutException error) {
            pending.cancel(true);
            throw new IOException("Archidekt did not finish responding. Your saved deck is safe; try again later.", error);
        } catch (java.util.concurrent.ExecutionException error) {
            throw new IOException("Could not read the public Archidekt deck. Your saved deck is safe.", error.getCause());
        } finally { if (!pending.isDone()) pending.cancel(true); }
    }

    static Remote parse(String id, JsonObject json, CardCatalog catalog) {
        var problems = new ArrayList<String>();
        var warnings = new LinkedHashSet<String>();
        if (!text(json, "id", id).equals(id)) throw new IllegalArgumentException("Archidekt returned a different deck");
        if (!json.has("cards") || !json.get("cards").isJsonArray()) throw new IllegalArgumentException("Archidekt response has no card list");
        if (json.getAsJsonArray("cards").size() > 2000) throw new IllegalArgumentException("Archidekt deck contains too many entries");
        Set<String> excluded = new LinkedHashSet<>();
        if (json.has("categories") && json.get("categories").isJsonArray()) for (var item : json.getAsJsonArray("categories")) {
            if (!item.isJsonObject()) continue;
            var category = item.getAsJsonObject();
            if (category.has("includedInDeck") && !category.get("includedInDeck").getAsBoolean()
                    && !text(category, "name", "").equalsIgnoreCase("Sideboard")) excluded.add(text(category, "name", ""));
        }
        var merged = new LinkedHashMap<String, Row>();
        int line = 0;
        for (var item : json.getAsJsonArray("cards")) {
            line++;
            if (!item.isJsonObject()) { problems.add("Entry " + line + " is not a card"); continue; }
            var row = item.getAsJsonObject();
            if (row.has("deletedAt") && !row.get("deletedAt").isJsonNull()) continue;
            int quantity;
            try { quantity = row.has("quantity") ? row.get("quantity").getAsInt() : 1; }
            catch (RuntimeException error) { problems.add("Entry " + line + " has an invalid quantity"); continue; }
            if (quantity == 0) continue;
            if (quantity < 0 || quantity > 1000) { problems.add("Entry " + line + " quantity must be 1..1000"); continue; }
            JsonObject card = row.has("card") && row.get("card").isJsonObject() ? row.getAsJsonObject("card") : new JsonObject();
            JsonObject oracle = card.has("oracleCard") && card.get("oracleCard").isJsonObject() ? card.getAsJsonObject("oracleCard") : new JsonObject();
            String name = text(oracle, "name", text(card, "displayName", ""));
            CardCatalog.CardInfo mapped = catalog.named(name);
            // Some Archidekt split-card names include both faces, while Forge indexes the front.
            if (mapped == null && name.contains(" // ")) mapped = catalog.named(name.substring(0, name.indexOf(" // ")));
            var categories = strings(row.has("categories") && row.get("categories").isJsonArray() ? row.getAsJsonArray("categories") : new JsonArray());
            String section = categories.stream().anyMatch(tag -> tag.equalsIgnoreCase("Commander")) ? "Commander"
                    : categories.stream().anyMatch(tag -> tag.equalsIgnoreCase("Sideboard")) ? "Sideboard" : "Main";
            boolean included = categories.stream().noneMatch(tag -> excluded.contains(tag) || tag.equalsIgnoreCase("Maybeboard") || tag.equalsIgnoreCase("Considering"));
            if (!included) section = "Considering";
            if (included && row.has("companion") && !row.get("companion").isJsonNull() && row.get("companion").getAsBoolean()) {
                section = "Sideboard";
                warnings.add("Companions are preserved in Sideboard; companion eligibility is not verified by this importer.");
            }
            if (mapped == null) { problems.add("Cannot map " + (name.isBlank() ? "entry " + line : name) + " to the Forge card library (" + section + ")"); continue; }
            JsonObject edition = card.has("edition") && card.get("edition").isJsonObject() ? card.getAsJsonObject("edition") : new JsonObject();
            String set = text(edition, "editioncode", "");
            String collector = text(card, "collectorNumber", "");
            boolean foil = text(row, "modifier", "").toLowerCase(Locale.ROOT).contains("foil");
            var printings = catalog.printings(mapped.name());
            String mappedName = mapped.name();
            var exact = printings.stream().filter(p -> p.name().equalsIgnoreCase(mappedName))
                    .filter(p -> set.isBlank() || p.edition().equalsIgnoreCase(set))
                    .filter(p -> collector.isBlank() || collector.equals(p.collectorNumber())).filter(p -> p.foil() == foil).findFirst();
            if (exact.isPresent()) mapped = exact.get();
            else {
                // Keep the requested set/finish when only its collector number is unavailable.
                mapped = printings.stream().filter(p -> set.isBlank() || p.edition().equalsIgnoreCase(set))
                        .filter(p -> p.foil() == foil).findFirst().orElse(mapped);
                if (!set.isBlank()) warnings.add("Printing substituted for " + mapped.name() + "; original printing, finish, notes and categories remain in the source baseline.");
            }
            String key = section + "\n" + mapped.id();
            var previous = merged.get(key);
            if (previous == null) merged.put(key, new Row(mapped.id(), mapped, quantity, section, categories, included));
            else {
                int total = previous.quantity() + quantity;
                if (total > 1000) { problems.add(mapped.name() + " has more than 1000 copies"); continue; }
                var tags = new LinkedHashSet<>(previous.categories()); tags.addAll(categories);
                merged.put(key, new Row(mapped.id(), mapped, total, section, List.copyOf(tags), included));
            }
        }
        if (merged.isEmpty() && problems.isEmpty()) problems.add("The source contains no cards to import");
        int code = json.has("deckFormat") ? json.get("deckFormat").getAsInt() : 0;
        String format = code == 3 ? "Commander" : "Constructed";
        if (code == 11 || code == 12) problems.add((code == 11 ? "1v1 Commander" : "Duel Commander")
                + " is not supported for game tests. Import a regular Commander source deck; Mana Table does not convert this variant's rules automatically.");
        if (!Set.of(0, 1, 2, 3, 4, 5, 8, 9, 10, 11, 12).contains(code)) warnings.add("This source format is imported as Constructed; verify its rules before playing.");
        return new Remote(id, "https://archidekt.com/decks/" + id, text(json, "name", "Archidekt deck " + id), format,
                text(json, "updatedAt", ""), List.copyOf(merged.values()), List.copyOf(problems), List.copyOf(warnings), json.deepCopy());
    }

    private static final class LimitedBody implements HttpResponse.BodySubscriber<byte[]> {
        private final java.util.concurrent.CompletableFuture<byte[]> result = new java.util.concurrent.CompletableFuture<>();
        private final java.io.ByteArrayOutputStream bytes = new java.io.ByteArrayOutputStream();
        private java.util.concurrent.Flow.Subscription subscription;
        @Override public java.util.concurrent.CompletionStage<byte[]> getBody() { return result; }
        @Override public void onSubscribe(java.util.concurrent.Flow.Subscription subscription) { this.subscription = subscription; subscription.request(1); }
        @Override public void onNext(List<java.nio.ByteBuffer> buffers) {
            for (var buffer : buffers) {
                if ((long) bytes.size() + buffer.remaining() > MAX_BYTES) {
                    subscription.cancel(); result.completeExceptionally(new IOException("Archidekt deck response is too large")); return;
                }
                byte[] chunk = new byte[buffer.remaining()]; buffer.get(chunk); bytes.writeBytes(chunk);
            }
            subscription.request(1);
        }
        @Override public void onError(Throwable error) { result.completeExceptionally(error); }
        @Override public void onComplete() { result.complete(bytes.toByteArray()); }
    }

    private static List<String> strings(JsonArray values) {
        var result = new LinkedHashSet<String>();
        for (var value : values) if (value.isJsonPrimitive() && value.getAsJsonPrimitive().isString()) {
            String text = value.getAsString(); if (!text.isBlank() && text.length() <= 100) result.add(text);
        }
        return List.copyOf(result);
    }
    private static String text(JsonObject value, String key, String fallback) {
        return value.has(key) && !value.get(key).isJsonNull() ? value.get(key).getAsString() : fallback;
    }
}
