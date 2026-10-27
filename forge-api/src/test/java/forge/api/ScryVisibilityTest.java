package forge.api;

import com.google.gson.Gson;
import forge.game.zone.ZoneType;
import org.testng.annotations.Test;
import org.testng.annotations.DataProvider;

import java.util.List;
import java.util.Map;
import java.util.stream.Collectors;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** A known top window is distinct from inspecting an unordered library search. */
public class ScryVisibilityTest {
    private static final Gson JSON = new Gson();

    @DataProvider public Object[][] persistentTopPermissions() { return new Object[][] {{false}, {true}}; }

    @Test(dataProvider = "persistentTopPermissions", timeOut = 90000)
    public void acceptingARealPonderOrderEndsItsTemporaryWindowBeforeTheRulesThreadResumes(boolean persistentTop) throws Exception {
        try (var scenario = new RulesScenario("ponder-consumed-window-" + persistentTop,
                Map.of(0, persistentTop ? List.of("Island", "Mystic Forge") : List.of("Island")), "Ponder", "Rhys the Redeemed")) {
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
            var order = scenario.until(input -> text(input.prompt(), "kind").equals("choice")
                    && input.prompt().has("ordered") && input.prompt().get("ordered").getAsBoolean());
            assertEquals(order.prompt().getAsJsonArray("choices").size(), 3);
            assertEquals(zone(viewer(order.state()), "Library").getAsJsonArray("cards").size(), 3);
            int handBefore = count(viewer(order.state()), "Hand");
            scenario.table.game.getAction().withState(() -> {
                assertTrue(scenario.answer(order, Map.of("choices", List.of(2, 0, 1))));
                // Keep the rules thread behind its existing state boundary so
                // it cannot yet call endTempShowCards or publish a new input.
                var owner = scenario.table.game.getPlayers().get(0);
                for (int index = 0; index < 3; index++)
                    assertTrue(owner.getCardsIn(ZoneType.Library).get(index).mayPlayerLook(owner));
                var busy = scenario.state(0);
                assertTrue(busy.get("prompt").isJsonNull());
                var library = zone(viewer(busy), "Library");
                assertEquals(library.getAsJsonArray("cards").size(), persistentTop ? 1 : 0,
                        "A consumed ordering scope cannot leave its temporary hidden-zone window in the resolving snapshot");
                assertEquals(library.get("topCard").isJsonNull(), !persistentTop);
                for (var card : library.getAsJsonArray("cards")) {
                    var retained = card.getAsJsonObject();
                    assertEquals(text(retained, "key"), "");
                    assertFalse(retained.get("selectable").getAsBoolean());
                }
                assertEquals(count(viewer(busy), "Hand"), handBefore);
                assertFalse(zone(viewer(busy), "Hand").getAsJsonArray("cards").isEmpty(), "The player's own hand stays readable");
                assertThrows(IllegalArgumentException.class, () -> scenario.table.seat(0).action(request(order, Map.of("choices", List.of(0, 1, 2)))));
            });
            var shuffle = scenario.until(input -> text(input.prompt(), "inputType").equals("InputConfirm"));
            assertTrue(scenario.answer(shuffle, Map.of("action", "cancel")));
            var resolved = scenario.until(input -> input.seat() == 0 && text(input.prompt(), "inputType").equals("InputPassPriority")
                    && input.state().getAsJsonArray("stack").isEmpty());
            assertEquals(count(viewer(resolved.state()), "Hand"), handBefore + 1, "The real Ponder still finishes its draw after the shuffle choice");
            assertEquals(zone(viewer(resolved.state()), "Library").getAsJsonArray("cards").size(), persistentTop ? 1 : 0);
        }
    }

    @Test(dataProvider = "persistentTopPermissions", timeOut = 90000)
    public void acknowledgingARealPeekEndsTemporaryOpponentHandDisclosureAndPreservesContinuousGrants(boolean persistentHand) throws Exception {
        try (var scenario = new RulesScenario("peek-consumed-window-" + persistentHand,
                Map.of(0, persistentHand ? List.of("Island", "Telepathy") : List.of("Island")), "Peek", "Rhys the Redeemed")) {
            var main = scenario.until(input -> input.seat() == 0 && text(input.prompt(), "inputType").equals("InputPassPriority")
                    && text(input.state(), "phaseKey").equals("MAIN1"));
            var spell = zone(viewer(main.state()), "Hand").getAsJsonArray("cards").get(0).getAsJsonObject();
            var catalog = JSON.toJsonTree(scenario.table.seat(0).sourceActions(request(main, Map.of("key", text(spell, "key"))))).getAsJsonObject();
            var cast = catalog.getAsJsonArray("actions").get(0).getAsJsonObject();
            assertTrue(scenario.answer(main, Map.of("action", "useSourceAction", "key", text(spell, "key"), "actionId", text(cast, "id"))));
            var target = scenario.until(input -> text(input.prompt(), "inputType").equals("InputSelectTargets"));
            assertTrue(scenario.answer(target, Map.of("action", "player", "playerId", 1)));
            var payment = scenario.until(input -> text(input.prompt(), "inputType").startsWith("InputPayMana")
                    && input.prompt().get("okEnabled").getAsBoolean());
            assertTrue(scenario.answer(payment, Map.of("action", "ok")));
            var reveal = scenario.until(input -> text(input.prompt(), "kind").equals("reveal"));
            int revealed = zone(player(reveal.state(), 1), "Hand").getAsJsonArray("cards").size();
            assertTrue(revealed > 0);
            int ownHand = count(viewer(reveal.state()), "Hand");
            scenario.table.game.getAction().withState(() -> {
                assertTrue(scenario.answer(reveal, Map.of("action", "ok")));
                var owner = scenario.table.game.getPlayers().get(0);
                for (var card : scenario.table.game.getPlayers().get(1).getCardsIn(ZoneType.Hand))
                    assertTrue(card.mayPlayerLook(owner), "The rules thread has not yet removed the temporary reveal permission");
                var busy = scenario.state(0);
                assertTrue(busy.get("prompt").isJsonNull());
                var hand = zone(player(busy, 1), "Hand").getAsJsonArray("cards");
                assertEquals(hand.size(), persistentHand ? revealed : 0,
                        "Only a continuous rules grant can keep opponent-hand faces after their reveal acknowledgement ends");
                for (var card : hand) assertEquals(text(card.getAsJsonObject(), "key"), "");
                assertEquals(zone(viewer(busy), "Hand").getAsJsonArray("cards").size(), ownHand);
                assertTrue(zone(viewer(busy), "Battlefield").getAsJsonArray("cards").size() >= 1, "Public permanents stay readable");
                assertThrows(IllegalArgumentException.class, () -> scenario.table.seat(0).action(request(reveal, Map.of("action", "ok"))));
            });
            var resolved = scenario.until(input -> input.seat() == 0 && text(input.prompt(), "inputType").equals("InputPassPriority")
                    && input.state().getAsJsonArray("stack").isEmpty());
            assertEquals(count(viewer(resolved.state()), "Hand"), ownHand + 1);
            assertEquals(zone(player(resolved.state(), 1), "Hand").getAsJsonArray("cards").size(), persistentHand ? revealed : 0);
        }
    }

    @Test(timeOut = 90000)
    public void aRealOpponentHandChoiceDisclosesOnlyItsCurrentAnonymousFacesAndRevokesThemAtAcceptance() throws Exception {
        try (var scenario = new RulesScenario("thought-knot-current-hand-choice", Map.of(0,
                List.of("Wastes", "Wastes", "Wastes", "Wastes")), "Thought-Knot Seer", "Traxos, Scourge of Kroog",
                Map.of(1, List.of("Thought-Knot Seer", "Thought-Knot Seer", "Thought-Knot Seer", "Thought-Knot Seer",
                        "Thought-Knot Seer", "Thought-Knot Seer", "Forest")))) {
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
            var target = scenario.until(input -> text(input.prompt(), "inputType").equals("InputSelectTargets"));
            assertTrue(scenario.answer(target, Map.of("action", "player", "playerId", 1)));
            var choice = scenario.until(input -> text(input.prompt(), "kind").equals("reveal") || text(input.prompt(), "kind").equals("choice"));
            int acknowledgements = 0;
            while (text(choice.prompt(), "kind").equals("reveal")) {
                assertTrue(++acknowledgements <= 4, "The real public hand reveal has at most one acknowledgement per audience");
                assertTrue(scenario.answer(choice, Map.of("action", "ok")));
                choice = scenario.until(input -> text(input.prompt(), "kind").equals("reveal") || text(input.prompt(), "kind").equals("choice"));
            }
            assertEquals(choice.seat(), 0);
            assertTrue(acknowledgements > 0, "The real reveal step precedes the nonland choice");
            var current = choice;
            var hand = zone(player(current.state(), 1), "Hand").getAsJsonArray("cards");
            var choices = current.prompt().getAsJsonArray("choices");
            assertEquals(hand.size(), 7);
            assertEquals(choices.size(), 6, "The basic land is readable but cannot be selected for this nonland effect");
            var extras = current.prompt().getAsJsonArray("disclosedCards");
            assertNotNull(extras, "A separate earlier reveal cannot describe this current choice's noneligible readable faces");
            assertEquals(extras.size(), 1);
            var extra = extras.get(0).getAsJsonObject();
            assertFalse(extra.has("index"));
            var land = extra.getAsJsonObject("card");
            assertEquals(text(land, "name"), "Forest");
            for (String forbidden : List.of("key", "visualId", "combatId", "selectable", "highlighted")) assertFalse(land.has(forbidden));
            var disclosed = java.util.stream.Stream.concat(choices.asList().stream(), extras.asList().stream())
                    .map(value -> text(value.getAsJsonObject().getAsJsonObject("card"), "name")).sorted().toList();
            assertEquals(hand.asList().stream().map(value -> text(value.getAsJsonObject(), "name")).sorted().toList(), disclosed,
                    "Current eligible choices plus noneligible disclosures justify exactly the current read-only hand multiset");
            for (var value : hand) {
                var card = value.getAsJsonObject();
                assertTrue(text(card, "key").startsWith(text(current.prompt(), "id") + ":c"));
                assertEquals(text(card, "visualId"), ""); assertEquals(text(card, "combatId"), "");
                assertFalse(card.get("selectable").getAsBoolean()); assertFalse(card.get("highlighted").getAsBoolean());
                assertThrows(IllegalArgumentException.class,
                        () -> scenario.table.seat(0).sourceActions(request(current, Map.of("key", text(card, "key")))));
            }
            for (var value : choices) {
                var option = value.getAsJsonObject();
                assertEquals(text(option, "cardId"), "");
                assertEquals(text(option.getAsJsonObject("card"), "key"), "");
                assertEquals(text(option.getAsJsonObject("card"), "visualId"), "");
                assertEquals(text(option.getAsJsonObject("card"), "combatId"), "");
            }
            scenario.rejected(current, Map.of("choices", List.of(choices.size())), "Invalid or duplicate choice");
            for (int seat : List.of(2, 3))
                assertTrue(zone(player(scenario.state(seat), 1), "Hand").getAsJsonArray("cards").isEmpty(),
                        "Other audiences do not inherit the chooser's temporary window after acknowledging the public reveal");
            assertEquals(zone(viewer(scenario.state(1)), "Hand").getAsJsonArray("cards").size(), 7);
            scenario.table.game.getAction().withState(() -> {
                var actor = scenario.table.game.getPlayers().get(0);
                var actualHand = scenario.table.game.getPlayers().get(1).getCardsIn(ZoneType.Hand);
                for (var card : actualHand) {
                    assertTrue(card.mayPlayerLook(actor), "The blocking current choice has a real temporary rules permission");
                    assertFalse(card.mayPlayerLookWithoutTemporary(actor));
                }
                var spectator = JSON.toJsonTree(SpectatorObservation.project(scenario.table,
                        current.state().get("boardRevision").getAsLong(), null)).getAsJsonObject();
                assertTrue(zone(player(spectator, 1), "Hand").getAsJsonArray("cards").isEmpty());
                assertTrue(scenario.answer(current, Map.of("choices", List.of(0))));
                for (var card : actualHand) assertTrue(card.mayPlayerLook(actor), "The held rules thread has not yet ended tempShow");
                var consumed = scenario.state(0);
                assertTrue(consumed.get("prompt").isJsonNull());
                assertTrue(zone(player(consumed, 1), "Hand").getAsJsonArray("cards").isEmpty(),
                        "Accepting the option immediately revokes its window, even before the engine thread resumes");
                assertThrows(IllegalArgumentException.class,
                        () -> scenario.table.seat(0).action(request(current, Map.of("choices", List.of(1)))));
            });
            var resolved = scenario.until(input -> text(input.prompt(), "kind").equals("reveal")
                    || input.seat() == 0 && text(input.prompt(), "inputType").equals("InputPassPriority")
                    && input.state().getAsJsonArray("stack").isEmpty());
            int exileAcknowledgements = 0;
            while (text(resolved.prompt(), "kind").equals("reveal")) {
                assertTrue(++exileAcknowledgements <= 4, "The selected public exile has at most one acknowledgement per audience");
                assertTrue(scenario.answer(resolved, Map.of("action", "ok")));
                resolved = scenario.until(input -> text(input.prompt(), "kind").equals("reveal")
                        || input.seat() == 0 && text(input.prompt(), "inputType").equals("InputPassPriority")
                        && input.state().getAsJsonArray("stack").isEmpty());
            }
            assertEquals(count(player(resolved.state(), 1), "Hand"), 6);
            assertEquals(count(player(resolved.state(), 1), "Exile"), 1, "The ordinary selected card is actually exiled");
            assertTrue(zone(player(resolved.state(), 1), "Hand").getAsJsonArray("cards").isEmpty());
            scenario.table.game.getAction().withState(() -> {
                var actor = scenario.table.game.getPlayers().get(0);
                for (var card : scenario.table.game.getPlayers().get(1).getCardsIn(ZoneType.Hand)) assertFalse(card.mayPlayerLook(actor));
            });
        }
    }

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
