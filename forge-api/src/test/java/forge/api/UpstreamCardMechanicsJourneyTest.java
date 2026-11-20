package forge.api;

import com.google.gson.Gson;
import com.google.gson.JsonObject;
import forge.game.zone.ZoneType;
import org.testng.annotations.Test;

import java.util.List;
import java.util.Map;

import static forge.api.CardDataRules.*;
import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Named new-mechanic coverage, not a claim that every imported script was played. */
public class UpstreamCardMechanicsJourneyTest {
    private static final String VOYAGER = "U.S.S. Voyager, Lone Wanderer";

    @Test(timeOut = 90000)
    public void stationPaysAnotherOwnedCreaturesTapAndCrossesSixToEightCharge() throws Exception {
        try (var game = new RulesScenario("upstream-station-threshold", Map.of(
                0, List.of(VOYAGER, "Colossal Dreadmaw", "Grizzly Bears"), 1, List.of("Grizzly Bears")))) {
            var upkeep = game.until(input -> input.seat() == 0 && priority(input) && text(input.state(), "phaseKey").equals("UPKEEP"));
            var ship = cards(viewer(upkeep.state()), "Battlefield", VOYAGER).get(0);
            var unavailable = new Gson().toJsonTree(game.session(0).sourceActions(request(upkeep, Map.of("key", text(ship, "key"))))).getAsJsonObject();
            assertTrue(unavailable.getAsJsonArray("actions").isEmpty(), "Station is unavailable outside sorcery timing");
            save(game, "00-upkeep-no-station.json", unavailable);
            var input = main(game, 0, 1);
            String shipId = text(cards(viewer(input.state()), "Battlefield", VOYAGER).get(0), "visualId");
            assertEquals(charge(occurrence(input.state(), shipId)), 0);
            assertFalse(text(occurrence(input.state(), shipId), "type").contains("Creature"));
            for (var cost : List.of("Colossal Dreadmaw", "Grizzly Bears")) {
                invoke(game, input, occurrence(input.state(), shipId));
                var selection = game.until(current -> current.seat() == 0 && text(current.prompt(), "inputType").equals("InputSelectCardsFromList"));
                var own = cards(viewer(selection.state()), "Battlefield", cost).get(0);
                int opponent = game.state(1).get("viewerId").getAsInt();
                var foreign = cards(player(selection.state(), opponent), "Battlefield", "Grizzly Bears").get(0);
                assertTrue(own.get("selectable").getAsBoolean());
                assertFalse(foreign.get("selectable").getAsBoolean(), "The Station tap cost cannot use an opponent's creature");
                assertFalse(occurrence(selection.state(), shipId).get("selectable").getAsBoolean(), "Station requires another creature");
                game.rejected(selection, Map.of("action", "card", "key", text(foreign, "key")), "That card cannot be selected for this choice");
                save(game, cost.equals("Colossal Dreadmaw") ? "01-six-power-cost.json" : "03-two-power-cost.json", selection.state());
                assertTrue(game.answer(selection, Map.of("action", "card", "key", text(own, "key"))));
                input = settleStation(game);
                assertTrue(cards(viewer(input.state()), "Battlefield", cost).get(0).get("tapped").getAsBoolean());
                var currentShip = occurrence(input.state(), shipId);
                assertFalse(currentShip.get("tapped").getAsBoolean(), "Station taps its chosen creature, not the Spacecraft");
                if (cost.equals("Colossal Dreadmaw")) {
                    assertEquals(charge(currentShip), 6);
                    assertFalse(text(currentShip, "type").contains("Creature"));
                    assertFalse(cards(viewer(input.state()), "Battlefield", "Grizzly Bears").get(0).get("tapped").getAsBoolean());
                    save(game, "02-six-charge.json", input.state());
                } else {
                    assertEquals(charge(currentShip), 8);
                    assertTrue(text(currentShip, "type").contains("Artifact") && text(currentShip, "type").contains("Creature"));
                    assertEquals(currentShip.get("power").getAsInt(), 3); assertEquals(currentShip.get("toughness").getAsInt(), 3);
                    assertTrue(keyword(currentShip, "flying") && keyword(currentShip, "vigilance"));
                    save(game, "04-eight-charge-creature.json", input.state());
                }
            }
        }
    }

    @Test(timeOut = 90000)
    public void realGraveyardAssimilationChangesControlTypesAndCountersButNotOwner() throws Exception {
        try (var game = new RulesScenario("upstream-assimilate-graveyard", Map.of(
                0, List.of("Death Is Irrelevant", "Mountain"), 1, List.of("Grizzly Bears")), "Lightning Bolt", "Rhys the Redeemed")) {
            var input = main(game, 0, 1);
            int actor = input.state().get("viewerId").getAsInt(), opponent = game.state(1).get("viewerId").getAsInt();
            invoke(game, input, cards(viewer(input.state()), "Battlefield", "Mountain").get(0));
            input = main(game, 0, input.state().get("turn").getAsInt());
            assertEquals(viewer(input.state()).getAsJsonObject("mana").get("R").getAsInt(), 1);
            invoke(game, input, cards(viewer(input.state()), "Hand", "Lightning Bolt").get(0));
            var targets = game.until(current -> current.seat() == 0 && text(current.prompt(), "inputType").equals("InputSelectTargets"));
            var bear = cards(player(targets.state(), opponent), "Battlefield", "Grizzly Bears").get(0);
            assertTrue(game.answer(targets, Map.of("action", "card", "key", text(bear, "key"))));
            var killed = settle(game, 0);
            assertTrue(cards(player(killed.state(), opponent), "Battlefield", "Grizzly Bears").isEmpty());
            assertEquals(cards(player(killed.state(), opponent), "Graveyard", "Grizzly Bears").size(), 1);
            save(game, "00-real-bolt-graveyard.json", killed.state());
            var trigger = game.until(current -> current.seat() == 0 && text(current.state(), "phaseKey").equals("END_OF_TURN")
                    && text(current.prompt(), "inputType").equals("InputSelectTargets"));
            assertEquals(text(trigger.prompt().getAsJsonObject("sourceCard"), "name"), "Death Is Irrelevant");
            var graveyardBear = cards(player(trigger.state(), opponent), "Graveyard", "Grizzly Bears").get(0);
            assertTrue(graveyardBear.get("selectable").getAsBoolean());
            save(game, "01-actual-assimilate-target.json", trigger.state());
            assertTrue(game.answer(trigger, Map.of("action", "card", "key", text(graveyardBear, "key"))));
            var resolved = settle(game, 0);
            var assimilated = cards(viewer(resolved.state()), "Battlefield", "Grizzly Bears").get(0);
            assertTrue(cards(player(resolved.state(), opponent), "Graveyard", "Grizzly Bears").isEmpty());
            String type = text(assimilated, "type");
            assertTrue(type.contains("Artifact") && type.contains("Creature") && type.contains("Borg"));
            assertFalse(type.contains("Bear"), "Assimilate removes the prior creature subtype");
            assertEquals(assimilated.getAsJsonObject("counters").get("+1/+1").getAsInt(), 1);
            assertEquals(assimilated.get("power").getAsInt(), 3); assertEquals(assimilated.get("toughness").getAsInt(), 3);
            assertTrue(keyword(assimilated, "indestructible"), "Death Is Irrelevant's separate continuous ability applies to the new artifact creature");
            assertTrue(game.table.game.getAction().readState(() -> game.table.game.getCardsIn(ZoneType.Battlefield).stream()
                    .anyMatch(card -> card.getName().equals("Grizzly Bears") && card.getOwner().getId() == opponent && card.getController().getId() == actor)));
            assertEquals(resolved.state().get("turn"), killed.state().get("turn"));
            assertEquals(text(resolved.state(), "phaseKey"), "END_OF_TURN");
            save(game, "02-actual-assimilated-permanent.json", resolved.state());
        }
    }

    private static int charge(JsonObject card) {
        var value = card.getAsJsonObject("counters").get("Charge");
        if (value == null) value = card.getAsJsonObject("counters").get("charge");
        return value == null ? 0 : value.getAsInt();
    }
    private static boolean keyword(JsonObject card, String name) {
        return card.getAsJsonArray("combatKeywords").asList().stream().anyMatch(value -> value.getAsString().equals(name));
    }
    private static Input settleStation(RulesScenario game) throws Exception {
        for (int count = 0; count < 8; count++) {
            var input = game.until(current -> current.seat() == 0 && (text(current.prompt(), "inputType").equals("InputSelectCardsFromList")
                    || text(current.prompt(), "inputType").startsWith("InputPayMana") || priority(current) && current.state().getAsJsonArray("stack").isEmpty()));
            if (priority(input)) return input;
            assertTrue(input.prompt().get("okEnabled").getAsBoolean());
            assertTrue(game.answer(input, Map.of("action", "ok")));
        }
        throw new AssertionError("Station did not finish its actual tap-cost continuation");
    }
}
