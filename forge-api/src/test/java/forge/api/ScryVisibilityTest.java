package forge.api;

import com.google.gson.Gson;
import forge.game.zone.ZoneType;
import org.testng.annotations.Test;

import java.util.List;
import java.util.Map;
import java.util.stream.Collectors;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** A known top window is distinct from inspecting an unordered library search. */
public class ScryVisibilityTest {
    private static final Gson JSON = new Gson();

    @Test(timeOut = 90000)
    public void aLookAtTopPermissionDisclosesALandWithoutGrantingAPlayAndEndsWithItsSource() throws Exception {
        try (var scenario = new RulesScenario("top-look-read-only-permission", Map.of(0,
                List.of("Mystic Forge", "Seal of Primordium")), "Wastes", "Traxos, Scourge of Kroog")) {
            var main = scenario.until(input -> input.seat() == 0 && text(input.prompt(), "inputType").equals("InputPassPriority")
                    && text(input.state(), "phaseKey").equals("MAIN1"));
            var library = zone(viewer(main.state()), "Library");
            assertEquals(library.getAsJsonArray("cards").size(), 1);
            var top = library.getAsJsonObject("topCard");
            assertEquals(text(top, "name"), "Wastes");
            assertFalse(text(top, "visualId").isEmpty());
            assertEquals(text(library.getAsJsonArray("cards").get(0).getAsJsonObject(), "visualId"), text(top, "visualId"));
            var routes = JSON.toJsonTree(scenario.table.seat(0).sourceActions(request(main, Map.of("key", text(top, "key"))))).getAsJsonObject();
            assertTrue(routes.getAsJsonArray("actions").isEmpty(), "Mystic Forge does not let its owner play the top land");
            for (var access : main.state().getAsJsonArray("playAccesses"))
                assertNotEquals(text(access.getAsJsonObject(), "zone"), "Library");
            scenario.table.game.getAction().withState(() -> {
                var owner = scenario.table.game.getPlayers().get(0);
                var cards = owner.getCardsIn(ZoneType.Library);
                assertEquals(text(top, "visualId"), scenario.table.identities.id(cards.get(0).getView()));
                for (int index = 0; index < cards.size(); index++) assertEquals(cards.get(index).getView().canBeShownTo(owner.getView()), index == 0);
            });
            for (int seat = 1; seat < 4; seat++) {
                var hidden = zone(player(scenario.state(seat), main.state().get("viewerId").getAsInt()), "Library");
                assertTrue(hidden.getAsJsonArray("cards").isEmpty());
                assertTrue(hidden.get("topCard").isJsonNull());
            }
            var permanents = zone(viewer(main.state()), "Battlefield").getAsJsonArray("cards").asList().stream()
                    .map(value -> value.getAsJsonObject()).toList();
            var seal = permanents.stream().filter(card -> text(card, "name").equals("Seal of Primordium")).findFirst().orElseThrow();
            var forge = permanents.stream().filter(card -> text(card, "name").equals("Mystic Forge")).findFirst().orElseThrow();
            var catalog = JSON.toJsonTree(scenario.table.seat(0).sourceActions(request(main, Map.of("key", text(seal, "key"))))).getAsJsonObject();
            var activate = catalog.getAsJsonArray("actions").asList().stream().map(value -> value.getAsJsonObject())
                    .filter(action -> text(action, "kind").equals("activate")).findFirst().orElseThrow();
            assertTrue(scenario.answer(main, Map.of("action", "useSourceAction", "key", text(seal, "key"), "actionId", text(activate, "id"))));
            var target = scenario.until(input -> text(input.prompt(), "inputType").equals("InputSelectTargets"));
            var currentForge = zone(viewer(target.state()), "Battlefield").getAsJsonArray("cards").asList().stream()
                    .map(value -> value.getAsJsonObject()).filter(card -> text(card, "visualId").equals(text(forge, "visualId"))).findFirst().orElseThrow();
            assertTrue(scenario.answer(target, Map.of("action", "card", "key", text(currentForge, "key"))));
            var resolved = scenario.until(input -> input.seat() == 0 && text(input.prompt(), "inputType").equals("InputPassPriority")
                    && input.state().getAsJsonArray("stack").isEmpty());
            assertTrue(zone(viewer(resolved.state()), "Graveyard").getAsJsonArray("cards").asList().stream()
                    .anyMatch(value -> text(value.getAsJsonObject(), "name").equals("Mystic Forge")));
            var hidden = zone(viewer(resolved.state()), "Library");
            assertTrue(hidden.getAsJsonArray("cards").isEmpty(), "Destroying the permission source revokes its read-only disclosure");
            assertTrue(hidden.get("topCard").isJsonNull());
        }
    }

    @Test(timeOut = 90000)
    public void anActualScryDisclosesOnlyItsTwoOccurrencesAndRevokesThemAfterOrdering() throws Exception {
        try (var scenario = new RulesScenario("scry-authorized-window", Map.of(0, List.of("Island")),
                "Preordain", "Rhys the Redeemed")) {
            var main = scenario.until(input -> input.seat() == 0 && text(input.prompt(), "inputType").equals("InputPassPriority")
                    && text(input.state(), "phaseKey").equals("MAIN1"));
            var spell = zone(viewer(main.state()), "Hand").getAsJsonArray("cards").get(0).getAsJsonObject();
            var catalog = JSON.toJsonTree(scenario.table.seat(0).sourceActions(request(main, Map.of("key", text(spell, "key"))))).getAsJsonObject();
            var cast = catalog.getAsJsonArray("actions").asList().stream().map(value -> value.getAsJsonObject())
                    .filter(action -> text(action, "kind").equals("cast")).findFirst().orElseThrow();
            assertTrue(scenario.answer(main, Map.of("action", "useSourceAction", "key", text(spell, "key"), "actionId", text(cast, "id"))));
            var payment = scenario.until(input -> text(input.prompt(), "inputType").startsWith("InputPayMana")
                    && input.prompt().get("okEnabled").getAsBoolean());
            assertTrue(scenario.answer(payment, Map.of("action", "ok")));
            var scry = scenario.until(input -> text(input.prompt(), "kind").equals("choice"));
            assertEquals(scry.seat(), 0);
            assertEquals(scry.prompt().getAsJsonArray("choices").size(), 2);
            int handBefore = count(viewer(scry.state()), "Hand");
            var actualTop = scenario.table.game.getAction().readState(() -> {
                var owner = scenario.table.game.getPlayers().get(0);
                var library = owner.getCardsIn(ZoneType.Library);
                var top = List.of(library.get(0), library.get(1));
                var projected = zone(viewer(scry.state()), "Library").getAsJsonArray("cards").asList().stream()
                        .map(value -> text(value.getAsJsonObject(), "visualId")).collect(Collectors.toSet());
                assertEquals(projected, top.stream().map(card -> scenario.table.identities.id(card.getView())).collect(Collectors.toSet()),
                        "Only the actual two peeked physical occurrences are in the owner's library projection");
                for (int index = 0; index < library.size(); index++) {
                    var card = library.get(index).getView();
                    assertEquals(card.canBeShownTo(owner.getView()), index < 2);
                    for (int seat = 1; seat < 4; seat++) assertFalse(card.canBeShownTo(scenario.table.game.getPlayers().get(seat).getView()));
                }
                var spectator = JSON.toJsonTree(SpectatorObservation.project(scenario.table,
                        scry.state().get("boardRevision").getAsLong(), null)).getAsJsonObject();
                for (var value : spectator.getAsJsonArray("players"))
                    assertTrue(zone(value.getAsJsonObject(), "Library").getAsJsonArray("cards").isEmpty());
                return top.stream().map(card -> card.getId()).toList();
            });
            for (int seat = 1; seat < 4; seat++) {
                var other = scenario.state(seat);
                assertTrue(other.get("prompt").isJsonNull());
                assertTrue(zone(player(other, scry.state().get("viewerId").getAsInt()), "Library").getAsJsonArray("cards").isEmpty());
            }
            assertTrue(scenario.answer(scry, Map.of("choices", List.of())));
            var order = scenario.until(input -> text(input.prompt(), "kind").equals("choice")
                    && input.prompt().has("ordered") && input.prompt().get("ordered").getAsBoolean());
            assertEquals(order.prompt().getAsJsonArray("choices").size(), 2);
            assertTrue(scenario.answer(order, Map.of("choices", List.of(1, 0))));
            var resolved = scenario.until(input -> input.seat() == 0 && text(input.prompt(), "inputType").equals("InputPassPriority")
                    && input.state().getAsJsonArray("stack").isEmpty());
            assertEquals(count(viewer(resolved.state()), "Hand"), handBefore + 1);
            scenario.table.game.getAction().withState(() -> {
                var owner = scenario.table.game.getPlayers().get(0);
                assertTrue(owner.getCardsIn(ZoneType.Hand).stream().anyMatch(card -> card.getId() == actualTop.get(1)),
                        "The ordinary engine order choice draws the selected physical top card");
                assertEquals(owner.getCardsIn(ZoneType.Library).get(0).getId(), actualTop.get(0).intValue());
            });
            for (int seat = 0; seat < 4; seat++) {
                var state = scenario.state(seat);
                for (var value : state.getAsJsonArray("players"))
                    assertTrue(zone(value.getAsJsonObject(), "Library").getAsJsonArray("cards").isEmpty(),
                            "Temporary peek permission ends after scry finishes");
            }
        }
    }
}
