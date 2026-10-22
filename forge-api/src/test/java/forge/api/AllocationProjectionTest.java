package forge.api;

import com.google.gson.JsonElement;
import com.google.gson.JsonObject;
import forge.card.CardStateName;
import forge.card.CardType;
import forge.game.card.CardView;
import forge.game.player.PlayerView;
import forge.game.zone.ZoneType;
import forge.trackable.TrackableProperty;
import org.testng.annotations.Test;

import java.util.*;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Allocation descriptions and answers at the actual Forge decision boundary. */
public class AllocationProjectionTest {
    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void combatShowsCurrentDuplicateRecipientsAndUsesTheExistingSpillThresholds() throws Exception {
        try (var game = new RulesScenario("allocation-combat-stats", Map.of(
                0, List.of("Groundbreaker", "Cunning Sparkmage", "Forest", "Forest"),
                1, List.of("Grizzly Bears", "Grizzly Bears")), "Giant Growth", "Rhys the Redeemed")) {
            var input = main(game);
            int opponent = game.state(1).get("viewerId").getAsInt();
            var bears = cards(player(input.state(), opponent), "Battlefield", "Grizzly Bears");
            String largeBear = text(bears.get(0), "visualId"), markedBear = text(bears.get(1), "visualId");
            String attacker = text(cards(viewer(input.state()), "Battlefield", "Groundbreaker").get(0), "visualId");
            input = castAt(game, input, "Giant Growth", largeBear);
            input = castAt(game, input, "Giant Growth", attacker);
            click(game, input, cards(viewer(input.state()), "Battlefield", "Cunning Sparkmage").get(0));
            input = game.until(i -> i.seat() == 0 && text(i.prompt(), "inputType").equals("InputSelectTargets"));
            click(game, input, occurrence(input.state(), markedBear));
            payAndResolve(game);

            input = game.until(i -> i.seat() == 0 && text(i.prompt(), "inputType").equals("InputAttack"));
            assertTrue(game.answer(input, Map.of("action", "attack", "attackerKey", text(occurrence(input.state(), attacker), "key"), "defenderPlayerId", opponent)));
            input = game.until(i -> i.seat() == 0 && text(i.prompt(), "inputType").equals("InputAttack"));
            assertTrue(game.answer(input, Map.of("action", "ok")));
            for (String blocker : List.of(largeBear, markedBear)) {
                input = game.until(i -> i.seat() == 1 && text(i.prompt(), "inputType").equals("InputBlock"));
                assertTrue(game.answer(input, Map.of("action", "block", "attackerKey", text(occurrence(input.state(), attacker), "key"),
                        "blockerKey", text(occurrence(input.state(), blocker), "key"))));
            }
            input = game.until(i -> i.seat() == 1 && text(i.prompt(), "inputType").equals("InputBlock"));
            assertTrue(game.answer(input, Map.of("action", "ok")));
            var allocation = game.until(i -> text(i.prompt(), "kind").equals("allocate"));
            var prompt = allocation.prompt();
            assertEquals(allocation.seat(), 0);
            assertEquals(text(prompt, "allocationKind"), "combat");
            assertEquals(text(prompt, "amountLabel"), "combat damage");
            assertEquals(prompt.get("amount").getAsInt(), 9);
            assertEquals(text(prompt, "sourceCardId"), attacker);
            assertEquals(prompt.get("sourceControllerId").getAsInt(), allocation.state().get("viewerId").getAsInt());
            var source = prompt.getAsJsonObject("sourceCard");
            assertEquals(source.get("power").getAsInt(), 9);
            assertEquals(source.get("toughness").getAsInt(), 4);
            assertTrue(source.getAsJsonArray("combatKeywords").asList().stream().anyMatch(k -> k.getAsString().equals("trample")));
            assertEquals(text(source, "key"), "", "Displaying an allocation source does not mint an action key");
            var choices = choices(prompt);
            assertEquals(choices.size(), 3);
            var large = choices.stream().filter(c -> text(c, "cardId").equals(largeBear)).findFirst().orElseThrow();
            var marked = choices.stream().filter(c -> text(c, "cardId").equals(markedBear)).findFirst().orElseThrow();
            var defender = choices.stream().filter(c -> c.get("isDefender").getAsBoolean()).findFirst().orElseThrow();
            assertNotEquals(text(large, "id"), text(marked, "id"));
            assertEquals(large.get("controllerId").getAsInt(), opponent);
            assertEquals(marked.get("ownerId").getAsInt(), opponent);
            assertEquals(large.getAsJsonObject("card").get("power").getAsInt(), 5);
            assertEquals(large.getAsJsonObject("card").get("toughness").getAsInt(), 5);
            assertEquals(marked.getAsJsonObject("card").get("damage").getAsInt(), 1);
            assertEquals(marked.getAsJsonObject("card").get("toughness").getAsInt(), 2);
            assertEquals(large.get("lethal").getAsInt(), 5);
            assertEquals(marked.get("lethal").getAsInt(), 1, "Forge already subtracts damage marked this turn");
            assertEquals(defender.get("playerId").getAsInt(), opponent);
            assertFalse(defender.has("card"));
            for (var choice : choices) {
                assertEquals(choice.get("minimum").getAsInt(), 0);
                assertEquals(choice.get("maximum").getAsInt(), 9);
            }
            var invalid = new ArrayList<>(Collections.nCopies(3, 0));
            invalid.set(large.get("index").getAsInt(), 4);
            invalid.set(marked.get("index").getAsInt(), 1);
            invalid.set(defender.get("index").getAsInt(), 4);
            game.rejected(allocation, Map.of("values", invalid), "Assign lethal damage to blockers before the defender");
            game.rejected(allocation, "skip", "Assign every amount");
            invalid.set(large.get("index").getAsInt(), 5);
            invalid.set(defender.get("index").getAsInt(), 3);
            assertTrue(game.answer(allocation, Map.of("values", invalid)));
            var resolved = game.until(i -> player(i.state(), opponent).get("life").getAsInt() == 37
                    && cards(player(i.state(), opponent), "Graveyard", "Grizzly Bears").size() == 2
                    && cards(viewer(i.state()), "Graveyard", "Groundbreaker").size() == 1);
            assertEquals(player(resolved.state(), opponent).get("life").getAsInt(), 37);
        }
    }

    @Test(timeOut = 90000)
    public void aDividedSpellKeepsCardAndPlayerTargetsDistinctAndEnforcesItsMinimums() throws Exception {
        try (var game = new RulesScenario("allocation-divided-damage", Map.of(
                0, List.of("Mountain", "Mountain", "Mountain"), 1, List.of("Grizzly Bears")), "Arc Lightning", "Rhys the Redeemed")) {
            var input = main(game);
            int opponent = game.state(1).get("viewerId").getAsInt();
            String bear = text(cards(player(input.state(), opponent), "Battlefield", "Grizzly Bears").get(0), "visualId");
            click(game, input, cards(viewer(input.state()), "Hand", "Arc Lightning").get(0));
            input = targeting(game);
            click(game, input, occurrence(input.state(), bear));
            input = targeting(game);
            assertTrue(game.answer(input, Map.of("action", "player", "playerId", opponent)));
            input = targeting(game);
            assertTrue(game.answer(input, Map.of("action", "ok")));
            var allocation = game.until(i -> text(i.prompt(), "kind").equals("allocate"));
            assertEquals(text(allocation.prompt(), "allocationKind"), "generic");
            assertEquals(text(allocation.prompt(), "amountLabel").toLowerCase(Locale.ROOT), "damage");
            assertEquals(text(allocation.prompt().getAsJsonObject("sourceCard"), "name"), "Arc Lightning");
            assertFalse(text(allocation.prompt(), "sourceCardId").isEmpty());
            var choices = choices(allocation.prompt());
            assertEquals(choices.size(), 2);
            var card = choices.stream().filter(c -> text(c, "cardId").equals(bear)).findFirst().orElseThrow();
            var player = choices.stream().filter(c -> c.has("playerId")).findFirst().orElseThrow();
            assertEquals(card.getAsJsonObject("card").get("power").getAsInt(), 2);
            assertEquals(card.get("controllerId").getAsInt(), opponent);
            assertEquals(player.get("playerId").getAsInt(), opponent);
            assertTrue(choices.stream().allMatch(c -> c.get("minimum").getAsInt() == 1 && c.get("maximum").getAsInt() == 3));
            var amounts = new ArrayList<>(List.of(0, 0));
            amounts.set(card.get("index").getAsInt(), 0);
            amounts.set(player.get("index").getAsInt(), 3);
            game.rejected(allocation, Map.of("values", amounts), "Invalid amount");
            game.rejected(allocation, Map.of("values", List.of(2, 2)), "The assigned amounts must total 3");
            amounts.set(card.get("index").getAsInt(), 2);
            amounts.set(player.get("index").getAsInt(), 1);
            for (int seat = 1; seat < 4; seat++) assertFalse(game.state(seat).toString().contains(text(card, "id")), "Allocation handles stay scoped to its deciding seat");
            assertTrue(game.answer(allocation, Map.of("values", amounts)));
            var resolved = payAndResolve(game);
            assertEquals(player(resolved.state(), opponent).get("life").getAsInt(), 39);
            assertEquals(cards(player(resolved.state(), opponent), "Graveyard", "Grizzly Bears").size(), 1);
        }
    }

    @Test(timeOut = 90000)
    public void counterAllocationUsesTheEngineLabelAndActuallyChangesBothRecipients() throws Exception {
        try (var game = new RulesScenario("allocation-divided-counters", Map.of(0, List.of(
                "Forest", "Forest", "Forest", "Forest", "Forest", "Grizzly Bears", "Grizzly Bears")), "Storm the Seedcore", "Rhys the Redeemed")) {
            var input = main(game);
            var bears = cards(viewer(input.state()), "Battlefield", "Grizzly Bears");
            List<String> ids = bears.stream().map(c -> text(c, "visualId")).toList();
            click(game, input, cards(viewer(input.state()), "Hand", "Storm the Seedcore").get(0));
            for (String id : ids) {
                input = targeting(game);
                click(game, input, occurrence(input.state(), id));
            }
            input = targeting(game);
            assertTrue(game.answer(input, Map.of("action", "ok")));
            var allocation = game.until(i -> text(i.prompt(), "kind").equals("allocate"));
            assertEquals(text(allocation.prompt(), "allocationKind"), "generic");
            assertEquals(text(allocation.prompt(), "amountLabel"), "counters");
            assertEquals(allocation.prompt().get("amount").getAsInt(), 4);
            assertEquals(choices(allocation.prompt()).stream().map(c -> text(c, "cardId")).collect(java.util.stream.Collectors.toSet()), Set.copyOf(ids));
            assertTrue(game.answer(allocation, Map.of("values", List.of(1, 3))));
            input = payAndResolve(game);
            var recipients = cards(viewer(input.state()), "Battlefield", "Grizzly Bears");
            assertEquals(recipients.stream().map(c -> c.get("power").getAsInt()).sorted().toList(), List.of(3, 5));
            assertEquals(recipients.stream().map(c -> c.getAsJsonObject("counters").get("+1/+1").getAsInt()).sorted().toList(), List.of(1, 3));
        }
    }

    @Test(timeOut = 90000)
    public void callbackLimitsLocalisedLabelsAndSkipReturnExactlyTheRequestedSemantics() throws Exception {
        try (var game = new RulesScenario("allocation-callback-limits", Map.of(0, List.of("Groundbreaker", "Grizzly Bears", "Grizzly Bears")))) {
            main(game);
            var cards = game.table.game.getPlayers().get(0).getCardsIn(ZoneType.Battlefield);
            var source = cards.stream().filter(c -> c.getName().equals("Groundbreaker")).findFirst().orElseThrow().getView();
            var targets = cards.stream().filter(c -> c.getName().equals("Grizzly Bears")).map(c -> c.getView()).toList();
            var limits = new LinkedHashMap<Object, Integer>();
            limits.put(targets.get(0), 1); limits.put(targets.get(1), 2);
            var worker = Executors.newSingleThreadExecutor();
            try {
                var response = worker.submit(() -> game.table.seat(0).gui().assignGenericAmount(source, limits, 3, false, "lblShield"));
                var allocation = game.until(i -> text(i.prompt(), "kind").equals("allocate"));
                assertEquals(text(allocation.prompt(), "amountLabel"), "shield");
                assertEquals(text(allocation.prompt(), "allocationKind"), "generic");
                assertEquals(choices(allocation.prompt()).stream().map(c -> c.get("maximum").getAsInt()).toList(), List.of(1, 2));
                game.rejected(allocation, Map.of("values", List.of(2, 1)), "An amount exceeds its target's limit");
                assertTrue(game.answer(allocation, Map.of("values", List.of(1, 2))));
                assertEquals(response.get(5, TimeUnit.SECONDS), Map.of(targets.get(0), 1, targets.get(1), 2));
                var skip = worker.submit(() -> game.table.seat(0).gui().assignCombatDamage(source, targets, 6,
                        game.table.game.getPlayers().get(1).getView(), true, true));
                allocation = game.until(i -> text(i.prompt(), "kind").equals("allocate"));
                assertTrue(allocation.prompt().get("maySkip").getAsBoolean());
                assertTrue(allocation.prompt().get("overrideOrder").getAsBoolean());
                assertTrue(game.answer(allocation, Map.of("action", "skip")));
                assertNull(skip.get(5, TimeUnit.SECONDS), "Skip lets Forge move to another assigning source; it does not commit zeros");

                var other = game.table.game.getPlayers().get(1);
                var hiddenHand = other.getCardsIn(ZoneType.Hand).iterator().next().getView();
                var hiddenLibrary = other.getCardsIn(ZoneType.Library).iterator().next().getView();
                var hiddenTargets = new LinkedHashMap<Object, Integer>();
                hiddenTargets.put(hiddenHand, 2); hiddenTargets.put(hiddenLibrary, 2);
                var privateResponse = worker.submit(() -> game.table.seat(0).gui().assignGenericAmount(hiddenHand, hiddenTargets, 2, false, "lblCounters"));
                allocation = game.until(i -> text(i.prompt(), "kind").equals("allocate"));
                assertEquals(text(allocation.prompt(), "amountLabel"), "counters");
                assertFalse(allocation.prompt().has("sourceCardId"));
                assertFalse(allocation.prompt().has("sourceControllerId"));
                var faces = new ArrayList<JsonObject>();
                faces.add(allocation.prompt().getAsJsonObject("sourceCard"));
                for (var choice : choices(allocation.prompt())) {
                    assertFalse(text(choice, "id").isEmpty(), "Anonymous recipients still have prompt-scoped row identities");
                    assertFalse(choice.has("cardId"));
                    assertFalse(choice.has("ownerId"));
                    assertFalse(choice.has("controllerId"));
                    faces.add(choice.getAsJsonObject("card"));
                }
                for (var face : faces) {
                    assertEquals(text(face, "name"), "Face-down or hidden card");
                    assertFalse(face.has("visualId"));
                    assertFalse(face.has("combatId"));
                    assertEquals(text(face, "key"), "");
                    assertEquals(text(face, "text"), "");
                }
                assertTrue(game.answer(allocation, Map.of("values", List.of(1, 1))));
                assertEquals(privateResponse.get(5, TimeUnit.SECONDS), Map.of(hiddenHand, 1, hiddenLibrary, 1));
            } finally { worker.shutdownNow(); }
        }
    }

    @Test
    public void faceDownBattlefieldStatsStayPublicWithoutOriginalIdentityOrAlternateFaces() {
        var owner = new PlayerView(1, null);
        var opponent = new PlayerView(2, null);
        var card = new CardView(3, null);
        card.set(TrackableProperty.Controller, owner);
        card.set(TrackableProperty.Owner, owner);
        card.set(TrackableProperty.Zone, ZoneType.Battlefield);
        card.set(TrackableProperty.Facedown, true);
        card.set(TrackableProperty.DoubleFaced, true);
        card.set(TrackableProperty.Damage, 1);
        var face = card.new CardStateView(3, CardStateName.FaceDown, null);
        face.set(TrackableProperty.Name, "Private printed identity");
        face.set(TrackableProperty.Type, CardType.parse("Creature", false));
        face.set(TrackableProperty.Power, 4);
        face.set(TrackableProperty.Toughness, 5);
        card.set(TrackableProperty.CurrentState, face);
        var original = card.new CardStateView(3, CardStateName.Original, null);
        original.set(TrackableProperty.Name, "Private alternate identity");
        card.set(TrackableProperty.AlternateState, original);
        for (var viewer : List.of(owner, opponent)) {
            var details = MatchSession.allocationCardDetails(card, viewer);
            assertEquals(details.get("type"), "Creature");
            assertEquals(details.get("power"), 4);
            assertEquals(details.get("toughness"), 5);
            assertEquals(details.get("damage"), 1);
            assertEquals(details.get("text"), "");
            assertEquals(details.get("manaCost"), "");
            assertFalse(details.containsKey("otherFace"));
            assertFalse(details.containsKey("artName"));
            assertFalse(details.toString().contains("Private"));
            assertEquals(details.get("key"), "");
        }
        card.set(TrackableProperty.Zone, ZoneType.Hand);
        var concealed = MatchSession.allocationCardDetails(card, opponent);
        assertFalse(MatchSession.isAllocationOccurrence(card, opponent));
        assertEquals(concealed.get("type"), "");
        assertNull(concealed.get("power"));
        assertFalse(concealed.containsKey("damage"));
        card.set(TrackableProperty.Zone, ZoneType.Library);
        assertFalse(MatchSession.isAllocationOccurrence(card, owner));
    }

    private static RulesScenario.Input main(RulesScenario game) throws Exception {
        return game.until(i -> i.seat() == 0 && text(i.prompt(), "inputType").equals("InputPassPriority")
                && text(i.state(), "phaseKey").equals("MAIN1") && i.state().getAsJsonArray("stack").isEmpty());
    }
    private static RulesScenario.Input targeting(RulesScenario game) throws Exception {
        return game.until(i -> i.seat() == 0 && text(i.prompt(), "inputType").equals("InputSelectTargets"));
    }
    private static RulesScenario.Input castAt(RulesScenario game, RulesScenario.Input input, String spell, String target) throws Exception {
        click(game, input, cards(viewer(input.state()), "Hand", spell).get(0));
        input = targeting(game);
        click(game, input, occurrence(input.state(), target));
        return payAndResolve(game);
    }
    private static RulesScenario.Input payAndResolve(RulesScenario game) throws Exception {
        for (int tries = 0; tries < 12; tries++) {
            var input = game.until(i -> i.seat() == 0 && (text(i.prompt(), "inputType").startsWith("InputPayMana")
                    || text(i.prompt(), "inputType").equals("InputPassPriority") && i.state().getAsJsonArray("stack").isEmpty()));
            if (text(input.prompt(), "inputType").equals("InputPassPriority")) return input;
            assertTrue(input.prompt().get("okEnabled").getAsBoolean(), input.prompt().toString());
            if (!game.answer(input, Map.of("action", "ok"))) {
                var fresh = game.state(0).get("prompt");
                assertTrue(fresh == null || fresh.isJsonNull() || !text(fresh.getAsJsonObject(), "id").equals(text(input.prompt(), "id")),
                        "Only an explicitly superseded payment input can be skipped");
            }
        }
        throw new AssertionError("Payment did not finish");
    }
    private static void click(RulesScenario game, RulesScenario.Input input, JsonObject card) {
        assertTrue(game.answer(input, Map.of("action", "card", "key", text(card, "key"))));
    }
    private static List<JsonObject> cards(JsonObject player, String zoneName, String name) {
        return zone(player, zoneName).getAsJsonArray("cards").asList().stream().map(JsonElement::getAsJsonObject)
                .filter(c -> text(c, "name").equals(name)).toList();
    }
    private static JsonObject occurrence(JsonObject state, String id) {
        return state.getAsJsonArray("players").asList().stream().map(JsonElement::getAsJsonObject)
                .flatMap(p -> p.getAsJsonArray("zones").asList().stream()).map(JsonElement::getAsJsonObject)
                .flatMap(z -> z.getAsJsonArray("cards").asList().stream()).map(JsonElement::getAsJsonObject)
                .filter(c -> text(c, "visualId").equals(id)).findFirst().orElseThrow();
    }
    private static List<JsonObject> choices(JsonObject prompt) {
        return prompt.getAsJsonArray("choices").asList().stream().map(JsonElement::getAsJsonObject).toList();
    }
}
