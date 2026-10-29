package forge.api;

import com.google.gson.Gson;
import com.google.gson.JsonObject;
import org.testng.annotations.DataProvider;
import org.testng.annotations.Test;
import java.util.List;
import java.util.Map;
import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Compare scoped nominal funding facts with payment through the same live input.
 * A deferred bulk availability scan is not evidence that a route is absent. */
public class ManaAvailabilityExperienceTest {
    private static final Gson JSON = new Gson();

    @DataProvider public Object[][] payments() {
        return new Object[][] {
                {"Ashnod's Altar", List.of("Basalt Monolith"), true},
                {"Blasting Station", List.of("Basalt Monolith"), true},
                {"Blasting Station", List.of("Wastes", "Wastes", "Wastes"), true},
                {"Blasting Station", List.of("Sol Ring", "Victory Chimes"), true},
                {"Ultima", List.of("Basalt Monolith", "Plains", "Plains"), true},
                {"Opt", List.of("Basalt Monolith"), false},
                {"Opt", List.of("Sol Ring", "Victory Chimes"), false},
                {"Endbringer", List.of("Basalt Monolith", "Forest", "Forest", "Forest"), true},
                {"Endbringer", List.of("Forest", "Forest", "Forest", "Forest", "Forest", "Forest"), false}
        };
    }

    @Test(dataProvider = "payments", groups = "canonical-interaction", timeOut = 90000)
    public void currentManaCueDistinguishesGenericColoredAndColorlessRequirements(String spell, List<String> permanents, boolean payable) throws Exception {
        String fixture = "mana-availability-" + spell.replaceAll("[^A-Za-z]", "") + "-"
                + String.join("-", permanents).replaceAll("[^A-Za-z-]", "");
        try (var game = new RulesScenario(fixture,
                Map.of(0, permanents), spell, "Omnath, Locus of the Void")) {
            var input = game.until(next -> next.seat() == 0 && priority(next) && text(next.state(), "phaseKey").equals("MAIN1"));
            if (permanents.contains("Basalt Monolith")) {
                var monolith = card(input.state(), "Battlefield", "Basalt Monolith");
                var actions = JSON.toJsonTree(game.table.seat(0).sourceActions(request(input, Map.of("key", text(monolith, "key"))))).getAsJsonObject();
                var tap = actions.getAsJsonArray("actions").asList().stream().map(value -> value.getAsJsonObject())
                        .filter(action -> text(action, "text").contains("Add")).findFirst().orElseThrow();
                assertTrue(game.answer(input, Map.of("action", "useSourceAction", "key", text(actions, "sourceCardKey"), "actionId", text(tap, "id"))));
                input = game.until(next -> next.seat() == 0 && priority(next)
                        && viewer(next.state()).getAsJsonObject("mana").get("C").getAsInt() == 3);
                assertTrue(card(input.state(), "Battlefield", "Basalt Monolith").get("tapped").getAsBoolean());
            }
            if (payable && permanents.contains("Victory Chimes")) {
                assertFalse(input.prompt().get("canAutoPass").getAsBoolean(), "An unplanned manual mana route cannot authorize Auto");
                input = floatChimes(game, input);
            }
            var offered = readCast(game, input, spell);
            input = offered.input();
            int handBefore = count(viewer(input.state()), "Hand");
            if (input.state().has("undo") && !input.state().getAsJsonObject("undo").get("offer").isJsonNull()) {
                assertFalse(input.prompt().get("canAutoPass").getAsBoolean(),
                        "A manual correction window leaves bulk availability unverified while exact source actions remain answerable");
            }
            assertTrue(game.answer(input, Map.of("action", "useSourceAction", "expectedTask", "priority",
                    "key", text(offered.catalog(), "sourceCardKey"), "actionId", text(offered.action(), "id"))));
            var casting = game.until(next -> next.seat() == 0 && (text(next.prompt(), "inputType").startsWith("InputPayMana")
                    || priority(next) && stacked(next.state(), spell)));
            if (!payable) {
                assertTrue(text(casting.prompt(), "inputType").startsWith("InputPayMana"),
                        "A timing-legal cast offer must not turn missing colored or colorless mana into a completed payment");
                HeadlessPlatform.query(game.table, () -> null);
                var unpaid = game.state(0);
                assertEquals(text(unpaid.getAsJsonObject("prompt"), "id"), text(casting.prompt(), "id"));
                casting = new Input(0, unpaid, unpaid.getAsJsonObject("prompt"));
                String missing = spell.equals("Opt") ? "{U}" : "{C}";
                assertTrue(casting.prompt().getAsJsonArray("unpaidManaParts").asList().stream()
                        .map(value -> value.getAsJsonObject()).anyMatch(part -> text(part, "symbol").equals(missing)
                                && part.get("count").getAsInt() > 0), "The actual unpaid bill retains the unsatisfied " + missing + " requirement");
                assertFalse(casting.prompt().get("okEnabled").getAsBoolean(), "Auto cannot complete the actual missing-mana payment");
                assertFalse(stacked(casting.state(), spell));
                var unchanged = game.state(0);
                var denied = expectThrows(IllegalArgumentException.class,
                        () -> game.session(0).action(request(new Input(0, unchanged, unchanged.getAsJsonObject("prompt")), Map.of("action", "ok"))));
                assertEquals(denied.getMessage(), "Continue is not available");
                assertEquals(game.state(0), unchanged, "Attempting disabled payment cannot spend resources or cast the spell");
                assertTrue(game.answer(casting, Map.of("action", "cancel", "expectedTask", "mana")));
                String previousPriorityId = text(input.prompt(), "id");
                var cancelled = game.until(next -> next.seat() == 0 && priority(next)
                        && !text(next.prompt(), "id").equals(previousPriorityId));
                assertFalse(stacked(cancelled.state(), spell));
                assertEquals(count(viewer(cancelled.state()), "Hand"), handBefore,
                        "Cancelling the unpaid cast returns the card through the ordinary rules path");
                return;
            }
            if (text(casting.prompt(), "inputType").startsWith("InputPayMana")) {
                assertTrue(game.answer(casting, Map.of("action", "ok")));
                long deadline = System.nanoTime() + java.time.Duration.ofSeconds(5).toNanos();
                JsonObject state = game.state(0);
                while (!stacked(state, spell) || state.get("prompt").isJsonNull()
                        || !text(state.getAsJsonObject("prompt"), "inputType").equals("InputPassPriority")) {
                    assertTrue(System.nanoTime() < deadline, "Accepted payment must reach casting priority: " + state);
                    Thread.sleep(5);
                    state = game.state(0);
                }
                casting = new Input(0, state, state.getAsJsonObject("prompt"));
            }
            assertTrue(stacked(casting.state(), spell), "The exact offered route must complete its actual legal payment");
            assertEquals(text(casting.state().getAsJsonArray("stack").get(0).getAsJsonObject(), "kind"), "spell");
        }
    }

    private record CastOffer(Input input, JsonObject catalog, JsonObject action) { }

    private static CastOffer readCast(RulesScenario game, Input input, String spell) {
        // Drain the preceding normal publication before comparing query purity.
        HeadlessPlatform.query(game.table, () -> null);
        var before = game.state(0);
        assertEquals(text(before.getAsJsonObject("prompt"), "id"), text(input.prompt(), "id"));
        var current = new Input(0, before, before.getAsJsonObject("prompt"));
        var hand = card(before, "Hand", spell);
        var catalog = JSON.toJsonTree(game.session(0).sourceActions(request(current,
                Map.of("key", text(hand, "key"), "expectedRevision", before.get("revision"))))).getAsJsonObject();
        assertEquals(text(catalog, "gameId"), text(before, "id"));
        assertEquals(text(catalog, "decisionId"), text(current.prompt(), "id"));
        assertEquals(catalog.get("revision"), before.get("revision"));
        assertEquals(text(catalog, "sourceCardKey"), text(hand, "key"));
        assertEquals(text(catalog, "sourceCardId"), text(hand, "visualId"));
        var casts = catalog.getAsJsonArray("actions").asList().stream().map(value -> value.getAsJsonObject())
                .filter(action -> text(action, "kind").equals("cast")).toList();
        assertEquals(casts.size(), 1, "The current timing-legal source retains one exact cast route independently of funding certainty");
        var action = casts.get(0);
        assertFalse(text(action, "id").isBlank());
        var preview = action.getAsJsonObject("costPreview");
        assertNotNull(preview, "The offered route describes its own nominal bill rather than borrowing card-face text");
        assertEquals(text(preview, "manaCostKnowledge"), "Unknown", "A nominal bill does not claim the final adjusted cost");
        var mana = preview.getAsJsonArray("costs").asList().stream().map(value -> value.getAsJsonObject())
                .filter(cost -> text(cost, "kind").equals("Mana")).toList();
        assertEquals(mana.size(), 1);
        assertEquals(mana.get(0).get("amount").getAsInt(), switch (spell) {
            case "Opt" -> 0;
            case "Endbringer" -> 5;
            default -> 3;
        });
        assertEquals(mana.get(0).getAsJsonArray("manaSymbols").asList().stream().map(value -> value.getAsString()).toList(),
                switch (spell) {
                    case "Opt" -> List.of("{U}");
                    case "Ultima" -> List.of("{W}", "{W}");
                    case "Endbringer" -> List.of("{C}");
                    default -> List.of();
                });
        var funding = preview.getAsJsonObject("funding");
        assertEquals(text(funding, "knowledge"), "Unknown", "A battlefield funding subset cannot prove all routes absent");
        for (var pool : funding.getAsJsonObject("pool").entrySet())
            assertTrue(pool.getValue().getAsInt() >= 0 && pool.getValue().getAsInt()
                    <= viewer(before).getAsJsonObject("mana").get(pool.getKey()).getAsInt(), "A read-only preview cannot invent floating mana");
        for (var source : funding.getAsJsonArray("sources"))
            assertTrue(zone(viewer(before), "Battlefield").getAsJsonArray("cards").asList().stream()
                    .anyMatch(value -> text(value.getAsJsonObject(), "visualId").equals(text(source.getAsJsonObject(), "sourceCardId"))),
                    "Funding sources belong to current disclosed owned battlefield occurrences");
        assertEquals(game.state(0), before, "Reading cost/funding facts preserves the full observation, action scope and retained Undo metadata");
        return new CastOffer(current, catalog, action);
    }

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void landfallManaIsAnnouncedAsATriggerBeforeItActuallyProducesMana() throws Exception {
        String omnath = "Omnath, Locus of the Void";
        try (var game = new RulesScenario("stack-kind-landfall-mana", Map.of(0, List.of(omnath)))) {
            var main = game.until(next -> next.seat() == 0 && priority(next) && text(next.state(), "phaseKey").equals("MAIN1"));
            var forest = zone(viewer(main.state()), "Hand").getAsJsonArray("cards").get(0).getAsJsonObject();
            assertTrue(game.answer(main, Map.of("action", "card", "key", text(forest, "key"))));
            var pending = game.until(next -> next.seat() == 0 && priority(next) && stacked(next.state(), omnath));
            assertEquals(text(pending.state().getAsJsonArray("stack").get(0).getAsJsonObject(), "kind"), "triggered");
            assertEquals(viewer(pending.state()).getAsJsonObject("mana").get("C").getAsInt(), 0,
                    "The pending trigger has not produced its mana yet");
            assertTrue(game.answer(pending, Map.of("action", "ok")));
            var resolved = game.until(next -> next.seat() == 0 && priority(next) && next.state().getAsJsonArray("stack").isEmpty()
                    && viewer(next.state()).getAsJsonObject("mana").get("C").getAsInt() == 2);
            assertEquals(viewer(resolved.state()).getAsJsonObject("mana").get("C").getAsInt(), 2);
        }
    }

    private static boolean priority(RulesScenario.Input input) { return text(input.prompt(), "inputType").equals("InputPassPriority"); }
    private static Input floatChimes(RulesScenario game, Input input) throws Exception {
        var chimes = card(input.state(), "Battlefield", "Victory Chimes");
        var actions = JSON.toJsonTree(game.table.seat(0).sourceActions(request(input, Map.of("key", text(chimes, "key"))))).getAsJsonObject();
        var action = actions.getAsJsonArray("actions").get(0).getAsJsonObject();
        assertTrue(game.answer(input, Map.of("action", "useSourceAction", "key", text(actions, "sourceCardKey"), "actionId", text(action, "id"))));
        var recipient = game.until(next -> next.seat() == 0 && !next.prompt().getAsJsonArray("playerChoices").isEmpty());
        assertTrue(game.answer(recipient, Map.of("action", "player", "playerId", input.state().get("viewerId").getAsInt())));
        return game.until(next -> next.seat() == 0 && priority(next) && viewer(next.state()).getAsJsonObject("mana").get("C").getAsInt() == 1);
    }
    private static JsonObject card(JsonObject state, String location, String name) {
        return zone(viewer(state), location).getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                .filter(card -> text(card, "name").equals(name)).findFirst().orElseThrow();
    }
    private static boolean stacked(JsonObject state, String name) {
        return state.getAsJsonArray("stack").asList().stream().map(value -> value.getAsJsonObject()).anyMatch(item -> text(item, "name").equals(name));
    }
}
