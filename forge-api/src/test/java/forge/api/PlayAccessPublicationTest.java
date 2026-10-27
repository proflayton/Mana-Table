package forge.api;

import com.google.gson.Gson;
import com.google.gson.JsonObject;
import org.testng.annotations.Test;

import java.util.Collections;
import java.util.List;
import java.util.Map;
import java.nio.file.Files;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Access discovery and zone disclosure must describe the same published occurrence. */
public class PlayAccessPublicationTest {
    private static final Gson JSON = new Gson();

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void anOptionalSearchOfferPreservesAnIndependentTopPermissionUntilActualLibraryInspection() throws Exception {
        try (var scenario = new RulesScenario("play-access-top-during-search-offer", Map.of(0,
                List.of("Mystic Forge", "Wastes", "Wastes", "Wastes", "Wastes")), "Solemn Simulacrum", "Traxos, Scourge of Kroog")) {
            var main = priority(scenario);
            String originalTop = assertIndependentTop(scenario.state(0), "Solemn Simulacrum");
            use(scenario, main, card(main.state(), "Hand", "Solemn Simulacrum"), "cast");
            var payment = scenario.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").startsWith("InputPayMana"));
            assertTrue(payment.prompt().get("okEnabled").getAsBoolean());
            assertTrue(scenario.answer(payment, Map.of("action", "ok")));
            var offer = scenario.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").equals("InputConfirm")
                    && next.prompt().has("librarySearch"));
            assertEquals(assertIndependentTop(offer.state(), "Solemn Simulacrum"), originalTop,
                    "An optional search offer does not revoke the existing independent top-card permission");
            assertNotEquals(text(offer.prompt(), "context"), "librarySearch", "Offering a search is not inspecting the library");
            var access = offer.state().getAsJsonArray("playAccesses").asList().stream().map(value -> value.getAsJsonObject())
                    .filter(value -> text(value, "cardId").equals(originalTop)).findFirst().orElseThrow();
            assertTrue(access.getAsJsonArray("routes").asList().stream().map(value -> value.getAsJsonObject())
                    .allMatch(route -> !route.get("canStart").getAsBoolean()), "The independent face grants no cast action during confirmation");
            scenario.table.game.getAction().withState(() -> {
                var owner = scenario.table.game.getPlayers().get(0);
                var top = owner.getCardsIn(forge.game.zone.ZoneType.Library).get(0);
                assertTrue(top.mayPlayerLookWithoutTemporary(owner), "Mystic Forge's continuous permission exists before accepting the search");
                assertEquals(scenario.table.identities.id(top.getView()), originalTop);
            });
            for (int audience : List.of(1, 2, 3)) {
                var hidden = zone(player(scenario.state(audience), 0), "Library");
                assertTrue(hidden.getAsJsonArray("cards").isEmpty()); assertTrue(hidden.get("topCard").isJsonNull());
            }
            var disclosedTop = zone(viewer(offer.state()), "Library").getAsJsonObject("topCard");
            assertFalse(disclosedTop.get("selectable").getAsBoolean()); assertFalse(disclosedTop.get("highlighted").getAsBoolean());
            assertThrows(IllegalArgumentException.class, () -> scenario.table.seat(0).sourceActions(request(offer,
                    Map.of("key", text(disclosedTop, "key")))));
            Files.writeString(scenario.evidence.resolve("independent-top-at-search-offer.json"), JSON.toJson(offer.state()));
            assertTrue(scenario.answer(offer, Map.of("action", "ok")));
            var inspecting = scenario.until(next -> next.seat() == 0 && text(next.prompt(), "context").equals("librarySearch"));
            assertTrue(inspecting.prompt().getAsJsonArray("libraryCards").size() > 80,
                    "The actual no-basic-land search opens its real bounded reveal window");
            for (int audience = 0; audience < 4; audience++) {
                var state = scenario.state(audience);
                for (var owner : state.getAsJsonArray("players")) {
                    var library = zone(owner.getAsJsonObject(), "Library");
                    assertTrue(library.getAsJsonArray("cards").isEmpty()); assertTrue(library.get("topCard").isJsonNull());
                }
                assertTrue(state.getAsJsonArray("playAccesses").asList().stream()
                        .noneMatch(value -> text(value.getAsJsonObject(), "zone").equals("Library")), "Actual inspection suppresses detached top access too");
            }
            Files.writeString(scenario.evidence.resolve("actual-search-suppresses-top.json"), JSON.toJson(inspecting.state()));
            assertTrue(scenario.answer(inspecting, Map.of("action", "ok")));
            var resolved = priority(scenario);
            assertIndependentTop(resolved.state(), "Solemn Simulacrum");
            assertEquals(zone(viewer(resolved.state()), "Battlefield").getAsJsonArray("cards").size(), 6,
                    "Failing to find a basic land neither fabricates a land nor removes the independent permission source");
        }
    }

    private static String assertIndependentTop(JsonObject state, String name) {
        var library = zone(viewer(state), "Library");
        assertTrue(library.get("count").getAsInt() > 80); assertEquals(library.getAsJsonArray("cards").size(), 1);
        var top = library.getAsJsonObject("topCard"); assertEquals(text(top, "name"), name);
        assertEquals(text(library.getAsJsonArray("cards").get(0).getAsJsonObject(), "visualId"), text(top, "visualId"));
        var access = state.getAsJsonArray("playAccesses").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "zone").equals("Library") && text(value, "cardId").equals(text(top, "visualId"))).findFirst().orElseThrow();
        assertTrue(access.getAsJsonArray("routes").asList().stream().map(value -> value.getAsJsonObject())
                .anyMatch(route -> text(route, "sourceName").equals("Mystic Forge")));
        return text(top, "visualId");
    }

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void anOpponentsFarseekSearchSuppressesAndRestoresTheSameMysticForgeTopAccess() throws Exception {
        try (var scenario = new RulesScenario("play-access-opponent-library-search",
                Map.of(0, List.of("Forest", "Plains"), 1, List.of("Mystic Forge")),
                "Farseek", "Rhys the Redeemed", Map.of(1, Collections.nCopies(99, "Hangarback Walker")))) {
            var input = priority(scenario);
            assertTopAccess(scenario.state(1));
            for (String land : List.of("Forest", "Plains")) {
                use(scenario, input, card(input.state(), "Battlefield", land), "activate");
                input = priority(scenario);
            }
            use(scenario, input, card(input.state(), "Hand", "Farseek"), "cast");
            var search = scenario.until(next -> text(next.prompt(), "context").equals("librarySearch")
                    || text(next.prompt(), "inputType").startsWith("InputPayMana"));
            if (text(search.prompt(), "inputType").startsWith("InputPayMana")) {
                assertTrue(search.prompt().get("okEnabled").getAsBoolean(), "The actual floated G/W pool pays {1}{G}");
                assertTrue(scenario.answer(search, Map.of("action", "ok")));
                search = scenario.until(next -> text(next.prompt(), "context").equals("librarySearch"));
            }
            assertEquals(search.seat(), 0);
            assertEquals(text(search.prompt(), "kind"), "reveal",
                    "An all-Farseek library still opens the actual search reveal before failing to find a land");
            assertTrue(search.prompt().getAsJsonArray("libraryCards").size() > 80);
            for (int audience = 0; audience < 4; audience++) {
                var state = scenario.state(audience);
                for (var owner : state.getAsJsonArray("players")) {
                    var library = zone(owner.getAsJsonObject(), "Library");
                    assertTrue(library.getAsJsonArray("cards").isEmpty());
                    assertTrue(library.get("topCard").isJsonNull());
                }
                assertTrue(state.getAsJsonArray("playAccesses").asList().stream()
                        .noneMatch(value -> text(value.getAsJsonObject(), "zone").equals("Library")),
                        "A suppressed library occurrence cannot survive as a detached play-access entry for audience " + audience);
            }
            assertTrue(scenario.answer(search, Map.of("action", "ok")));
            var resolved = priority(scenario);
            assertNotNull(card(resolved.state(), "Graveyard", "Farseek"), "The real spell resolves after its no-match search");
            assertEquals(zone(viewer(resolved.state()), "Battlefield").getAsJsonArray("cards").size(), 2,
                    "No matching land was fabricated or moved into play");
            assertTopAccess(scenario.state(1));
        }
    }

    private static RulesScenario.Input priority(RulesScenario scenario) throws Exception {
        return scenario.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").equals("InputPassPriority")
                && text(next.state(), "phaseKey").equals("MAIN1") && next.state().getAsJsonArray("stack").isEmpty());
    }

    private static void assertTopAccess(JsonObject state) {
        var top = zone(viewer(state), "Library").getAsJsonObject("topCard");
        assertNotNull(top);
        assertEquals(text(top, "name"), "Hangarback Walker");
        var access = state.getAsJsonArray("playAccesses").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "zone").equals("Library")).findFirst().orElseThrow();
        assertEquals(text(access, "cardId"), text(top, "visualId"));
        assertEquals(text(access.getAsJsonArray("routes").get(0).getAsJsonObject(), "sourceName"), "Mystic Forge");
    }

    private static JsonObject card(JsonObject state, String zone, String name) {
        return zone(viewer(state), zone).getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "name").equals(name)).findFirst().orElseThrow();
    }

    private static void use(RulesScenario scenario, RulesScenario.Input input, JsonObject card, String kind) {
        var catalog = JSON.toJsonTree(scenario.table.seat(input.seat()).sourceActions(request(input,
                Map.of("key", text(card, "key"))))).getAsJsonObject();
        var action = catalog.getAsJsonArray("actions").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "kind").equals(kind)).findFirst().orElseThrow();
        assertTrue(scenario.answer(input, Map.of("action", "useSourceAction", "key", text(card, "key"), "actionId", text(action, "id"))));
    }
}
