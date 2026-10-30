package forge.api;

import com.google.gson.Gson;
import com.google.gson.JsonObject;
import org.testng.annotations.DataProvider;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.util.List;
import java.util.Map;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Counter costs use the actual input and legal removal rules, without source-card clicks for forced payment. */
public class CounterCostExperienceTest {
    private static final Gson JSON = new Gson();
    private static final String MARE = "Loch Mare";

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void aFixedSourceWithOneRemovableCounterTypePaysAndDrawsWithoutASecondChoice() throws Exception {
        try (var game = RulesScenario.automated("counter-cost-loch-mare", Map.of(0,
                List.of("Island", "Island", "Island", "Island", MARE)), "Island", "Rhys the Redeemed", true)) {
            var main = main(game);
            var source = card(main.state(), MARE);
            int handBefore = count(viewer(main.state()), "Hand");
            int countersBefore = source.getAsJsonObject("counters").get("-1/-1").getAsInt();
            assertEquals(countersBefore, 3, "The real enters replacement provides Loch Mare's counters");
            main = floatBlue(game, main, 2);
            source = card(main.state(), MARE);
            activate(game, main, source, "Draw a card");
            var next = payAndReachCounterOrStack(game);
            Files.writeString(game.evidence.resolve("after-mana.json"), JSON.toJson(next.state()));
            assertNotEquals(text(next.prompt(), "inputType"), "InputSelectCardToRemoveCounter",
                    "Paying the fixed source's only counter type must not require selecting the source again");
            assertEquals(card(next.state(), MARE).getAsJsonObject("counters").get("-1/-1").getAsInt(), countersBefore - 1);
            assertEquals(viewer(next.state()).getAsJsonObject("mana").get("U").getAsInt(), 0, "The actual mana cost was paid");
            var stacked = next.state().getAsJsonArray("stack").asList().stream().map(value -> value.getAsJsonObject())
                    .filter(item -> text(item, "name").equals(MARE)).findFirst().orElseThrow();
            assertEquals(text(stacked, "kind"), "activated", "The actual paid activation supplies its stack action kind");
            var resolved = game.until(i -> i.seat() == 0 && priority(i) && i.state().getAsJsonArray("stack").isEmpty());
            assertEquals(count(viewer(resolved.state()), "Hand"), handBefore + 1, "The exact draw ability resolves once");
            assertEquals(card(resolved.state(), MARE).getAsJsonObject("counters").get("-1/-1").getAsInt(), countersBefore - 1);
        }
    }

    @DataProvider public Object[][] removalRestrictions() { return new Object[][] {{false, 1}, {true, 1}, {false, 2}}; }

    @Test(groups = "canonical-interaction", dataProvider = "removalRestrictions", timeOut = 90000)
    public void mixedCounterTypesRemainAChoiceUnlessTheRulesForbidRemovingOne(boolean stunRemovalForbidden, int amount) throws Exception {
        var battlefield = new java.util.HashMap<Integer, List<String>>();
        battlefield.put(0, List.of("Island", "Island", "Island", "Island", "Island", MARE));
        if (stunRemovalForbidden) battlefield.put(1, List.of("Fear of Sleep Paralysis"));
        try (var game = RulesScenario.automated("counter-cost-mixed-" + stunRemovalForbidden + "-" + amount, battlefield,
                "Cryogenic Stasis", "Rhys the Redeemed", true)) {
            var input = floatBlue(game, main(game), 2);
            var stasis = zone(viewer(input.state()), "Hand").getAsJsonArray("cards").get(0).getAsJsonObject();
            activate(game, input, stasis, "Tap target creature");
            finishCast(game, MARE);
            input = game.until(i -> i.seat() == 0 && priority(i) && i.state().getAsJsonArray("stack").isEmpty());
            assertEquals(card(input.state(), MARE).getAsJsonObject("counters").get("Stun").getAsInt(), 1);
            assertEquals(card(input.state(), MARE).getAsJsonObject("counters").get("-1/-1").getAsInt(), 3);
            int handBefore = count(viewer(input.state()), "Hand");
            input = floatBlue(game, input, amount == 1 ? 2 : 3);
            activate(game, input, card(input.state(), MARE), amount == 1 ? "Draw a card" : "Tap target creature");
            var counter = payAndReachCounterOrStack(game, amount == 1 ? null : MARE);
            if (stunRemovalForbidden) {
                assertTrue(priority(counter), "A forbidden stun removal is not a second legal payment choice");
            } else {
                assertEquals(text(counter.prompt(), "inputType"), "InputSelectCardToRemoveCounter");
                assertPayment(game, counter, amount, amount);
                assertTrue(card(counter.state(), MARE).get("selectable").getAsBoolean(),
                        "The actual counter input publishes the source as selectable for the native client");
                Files.writeString(game.evidence.resolve("explicit-counter-source.json"), JSON.toJson(counter.state()));
                assertTrue(game.answer(counter, Map.of("action", "card", "key", text(card(counter.state(), MARE), "key"))));
                var type = game.until(i -> text(i.prompt(), "kind").equals("choice"));
                assertEquals(type.prompt().getAsJsonArray("choices").size(), 2, "Both removable counter types require a player choice");
                assertTypePayment(game, type, MARE, text(card(counter.state(), MARE), "visualId"), 3, 1);
                chooseType(game, type, amount == 1 ? "STUN" : "M1M1");
                if (amount == 2) {
                    counter = game.until(i -> text(i.prompt(), "inputType").equals("InputSelectCardToRemoveCounter"));
                    assertPayment(game, counter, 2, 1);
                    assertTrue(game.answer(counter, Map.of("action", "card", "key", text(card(counter.state(), MARE), "key"))));
                    type = game.until(i -> text(i.prompt(), "kind").equals("choice"));
                    assertTypePayment(game, type, MARE, text(card(counter.state(), MARE), "visualId"), 2, 1);
                    chooseType(game, type, "STUN");
                }
            }
            var resolved = game.until(i -> i.seat() == 0 && priority(i) && i.state().getAsJsonArray("stack").isEmpty());
            var counters = card(resolved.state(), MARE).getAsJsonObject("counters");
            assertEquals(counters.has("Stun") ? counters.get("Stun").getAsInt() : 0, stunRemovalForbidden || amount == 2 ? 1 : 0);
            assertEquals(counters.get("-1/-1").getAsInt(), stunRemovalForbidden || amount == 2 ? 2 : 3);
            assertEquals(count(viewer(resolved.state()), "Hand"), handBefore + (amount == 1 ? 1 : 0));
        }
    }

    @DataProvider public Object[][] distributedAnswers() { return new Object[][] {{false, false}, {true, false}, {false, true}}; }

    @Test(groups = "canonical-interaction", dataProvider = "distributedAnswers", timeOut = 90000)
    public void aNonSourceCostPublishesItsRealCandidatesAndPreservesTheChosenPermanentOrCancellation(boolean cancel, boolean mixed) throws Exception {
        try (var game = RulesScenario.automated("counter-cost-distributed-" + cancel + "-" + mixed, Map.of(0,
                List.of("Concordant Crossroads", "Soul Diviner", MARE, MARE, "Island", "Island")),
                mixed ? "Cryogenic Stasis" : "Island", "Rhys the Redeemed", true)) {
            var input = main(game);
            if (mixed) {
                input = floatBlue(game, input, 2);
                activate(game, input, zone(viewer(input.state()), "Hand").getAsJsonArray("cards").get(0).getAsJsonObject(), "Tap target creature");
                finishCast(game, MARE);
                input = game.until(i -> i.seat() == 0 && priority(i) && i.state().getAsJsonArray("stack").isEmpty());
            }
            int handBefore = count(viewer(input.state()), "Hand");
            activate(game, input, card(input.state(), "Soul Diviner"), "Draw a card");
            var choice = payAndReachCounterOrStack(game);
            assertEquals(text(choice.prompt(), "inputType"), "InputSelectCardToRemoveCounter");
            assertPayment(game, choice, 1, 1);
            var mares = zone(viewer(choice.state()), "Battlefield").getAsJsonArray("cards").asList().stream()
                    .map(value -> value.getAsJsonObject()).filter(value -> text(value, "name").equals(MARE)).toList();
            assertEquals(mares.size(), 2);
            assertTrue(mares.stream().allMatch(value -> value.get("selectable").getAsBoolean()));
            assertFalse(card(choice.state(), "Soul Diviner").get("selectable").getAsBoolean(), "A source without removable counters is not a cost candidate");
            int selected = mixed ? 0 : 1;
            assertTrue(game.answer(choice, cancel ? Map.of("action", "cancel")
                    : Map.of("action", "card", "key", text(mares.get(selected), "key"))));
            if (mixed) {
                var type = game.until(i -> text(i.prompt(), "kind").equals("choice"));
                assertTypePayment(game, type, "Soul Diviner", text(mares.get(selected), "visualId"), 3, 1);
                assertNotEquals(text(type.prompt().getAsJsonObject("sourceCard"), "visualId"), text(mares.get(selected), "visualId"),
                        "The ability source remains distinct from the permanent paying its counter");
                chooseType(game, type, "M1M1");
            }
            var resolved = game.until(i -> i.seat() == 0 && priority(i) && i.state().getAsJsonArray("stack").isEmpty());
            assertEquals(count(viewer(resolved.state()), "Hand"), handBefore + (cancel ? 0 : 1));
            for (int index = 0; index < mares.size(); index++) {
                String id = text(mares.get(index), "visualId");
                var actual = zone(viewer(resolved.state()), "Battlefield").getAsJsonArray("cards").asList().stream()
                        .map(value -> value.getAsJsonObject()).filter(value -> text(value, "visualId").equals(id)).findFirst().orElseThrow();
                assertEquals(actual.getAsJsonObject("counters").get("-1/-1").getAsInt(), !cancel && index == selected ? 2 : 3);
            }
            assertEquals(card(resolved.state(), "Soul Diviner").get("tapped").getAsBoolean(), !cancel);
            assertThrows(IllegalArgumentException.class, () -> game.session(0).action(request(choice,
                    Map.of("action", "card", "key", text(mares.get(1), "key")))));
        }
    }

    private static void finishCast(RulesScenario game, String target) throws Exception {
        for (int step = 0; step < 5; step++) {
            var input = game.until(i -> paymentOrCostOrder(i)
                    || text(i.prompt(), "inputType").equals("InputSelectTargets")
                    || priority(i) && !i.state().getAsJsonArray("stack").isEmpty());
            if (priority(input)) return;
            if (payExplicitCostStep(game, input)) continue;
            assertTrue(game.answer(input, Map.of("action", "card", "key", text(card(input.state(), target), "key"))));
        }
        fail("The targeted spell did not reach the stack");
    }

    private static Input payAndReachCounterOrStack(RulesScenario game) throws Exception {
        return payAndReachCounterOrStack(game, null);
    }

    private static Input payAndReachCounterOrStack(RulesScenario game, String target) throws Exception {
        // Externally controlled seats explicitly choose the real cost order and
        // spend the already-floated blue units. Forge's Auto payment is unavailable.
        for (int step = 0; step < 8; step++) {
            var input = game.until(i -> paymentOrCostOrder(i)
                    || text(i.prompt(), "inputType").equals("InputSelectTargets")
                    || text(i.prompt(), "inputType").equals("InputSelectCardToRemoveCounter")
                    || priority(i) && !i.state().getAsJsonArray("stack").isEmpty());
            if (text(input.prompt(), "inputType").equals("InputSelectTargets")) {
                assertNotNull(target, "Only the explicitly targeted fixture ability chooses a target");
                assertTrue(game.answer(input, Map.of("action", "card", "key", text(card(input.state(), target), "key"))));
                continue;
            }
            if (!payExplicitCostStep(game, input)) return input;
        }
        throw new AssertionError("The actual mana payment did not complete");
    }

    private static boolean paymentOrCostOrder(Input input) {
        return text(input.prompt(), "inputType").startsWith("InputPayMana")
                || text(input.prompt(), "kind").equals("choice") && input.prompt().has("ordered")
                    && input.prompt().get("ordered").getAsBoolean();
    }

    private static boolean payExplicitCostStep(RulesScenario game, Input input) {
        if (text(input.prompt(), "kind").equals("choice") && input.prompt().has("ordered")
                && input.prompt().get("ordered").getAsBoolean()) {
            var choices = input.prompt().getAsJsonArray("choices");
            assertEquals(input.prompt().get("min").getAsInt(), choices.size());
            assertEquals(input.prompt().get("max").getAsInt(), choices.size());
            var observation = game.table.game.getAction().readState(() ->
                    ManyPublicationTest.assertCurrentSelection(game.table, game.session(input.seat())));
            assertEquals(text(observation.getAsJsonObject("semantics"), "orderingPurpose"), "CostPayment");
            var order = choices.asList().stream().map(value -> value.getAsJsonObject().get("index").getAsInt()).toList();
            assertEquals(observation.getAsJsonObject("semantics").get("defaultOrder"), JSON.toJsonTree(order));
            assertTrue(game.answer(input, Map.of("choices", order)), "Choose the actual offered default cost order");
            return true;
        }
        if (!text(input.prompt(), "inputType").startsWith("InputPayMana")) return false;
        assertFalse(input.prompt().get("okEnabled").getAsBoolean(), "The externally controlled fixture cannot use Forge Auto");
        assertTrue(input.prompt().getAsJsonArray("usablePoolManaColors").asList().stream().anyMatch(value -> value.getAsString().equals("U")),
                "This fixture pays only its already-floated blue mana");
        assertTrue(viewer(input.state()).getAsJsonObject("mana").get("U").getAsInt() > 0);
        assertFalse(input.prompt().getAsJsonArray("unpaidManaParts").isEmpty());
        assertTrue(game.answer(input, Map.of("action", "spendPoolMana", "manaColor", "U")), "Spend one actual blue unit against this current bill");
        return true;
    }

    private static void assertPayment(RulesScenario game, Input input, int amount, int remaining) {
        var payment = input.prompt().getAsJsonObject("counterPayment");
        assertNotNull(payment, "The engine classifies the counter cost independently of prompt prose");
        assertEquals(payment.get("amount").getAsInt(), amount);
        assertEquals(payment.get("remaining").getAsInt(), remaining);
        game.table.game.getAction().readState(() -> {
            var observation = ManyPublicationTest.assertCurrentSelection(game.table, game.session(input.seat()));
            var actual = (forge.player.HumanCostDecision.InputSelectCardToRemoveCounter)
                    ((forge.player.PlayerControllerHuman)game.table.game.getRegisteredPlayers().get(input.seat()).getController()).getInputQueue().getInput();
            assertEquals(observation.getAsJsonObject("state").getAsJsonObject("prompt").get("counterPayment"), JSON.toJsonTree(payment));
            assertEquals(text(observation.getAsJsonObject("semantics"), "intent"), "RemoveCounters");
            assertTrue(observation.getAsJsonObject("semantics").get("selectionIsCost").getAsBoolean());
            var selected = actual.getSelected();
            var highlighted = zone(viewer(observation.getAsJsonObject("state")), "Battlefield").getAsJsonArray("cards").asList().stream()
                    .map(value -> value.getAsJsonObject()).filter(value -> value.get("highlighted").getAsBoolean()).toList();
            assertEquals(highlighted.size(), selected.size(), "Highlights count distinct physical counter donors, not allocated counters");
            for (var donor : selected) {
                assertTrue(donor instanceof forge.game.card.Card);
                String id = game.table.identities.id(((forge.game.card.Card)donor).getView());
                assertTrue(highlighted.stream().anyMatch(card -> text(card, "visualId").equals(id)));
            }
            return null;
        });
    }

    private static void assertTypePayment(RulesScenario game, Input input, String source, String affected, int minus, int stun) throws Exception {
        assertEquals(text(input.prompt(), "task"), "choice");
        assertEquals(text(input.prompt(), "purpose"), "");
        assertEquals(text(input.prompt().getAsJsonObject("sourceCard"), "name"), source);
        assertEquals(text(input.prompt().getAsJsonObject("sourceCard"), "key"), "");
        var payment = input.prompt().getAsJsonObject("counterPaymentChoice");
        assertNotNull(payment);
        assertEquals(payment.get("amount").getAsInt(), 1, "One type choice allocates one counter even during a larger payment");
        var permanent = payment.getAsJsonObject("permanent");
        assertEquals(text(permanent, "cardId"), affected);
        assertEquals(text(permanent, "zone"), "Battlefield");
        assertEquals(permanent.get("controllerId").getAsInt(), input.state().get("viewerId").getAsInt());
        var face = permanent.getAsJsonObject("card");
        assertEquals(text(face, "key"), "");
        assertEquals(text(face, "visualId"), affected);
        assertEquals(text(face, "name"), MARE);
        assertFalse(face.has("selectable"), "A cost preview does not grant a card action");
        assertFalse(face.has("highlighted"));
        var ordinary = input.prompt().getAsJsonArray("choices");
        assertEquals(payment.getAsJsonArray("options").size(), ordinary.size());
        var seen = new java.util.HashSet<Integer>();
        for (var element : payment.getAsJsonArray("options")) {
            var option = element.getAsJsonObject();
            int index = option.get("index").getAsInt();
            assertTrue(seen.add(index));
            assertEquals(ordinary.get(index).getAsJsonObject().get("index").getAsInt(), index);
            assertEquals(text(option, "label"), text(ordinary.get(index).getAsJsonObject(), "label"));
            assertEquals(option.get("available").getAsInt(), text(option, "type").equals("M1M1") ? minus : stun);
            assertTrue(java.util.Set.of("M1M1", "STUN").contains(text(option, "type")));
        }
        for (int seat = 1; seat < 4; seat++) {
            var other = game.state(seat);
            assertTrue(other.get("prompt").isJsonNull(), "Other seats do not receive counter payment choices");
            assertFalse(other.toString().contains("counterPaymentChoice"));
        }
        Files.writeString(game.evidence.resolve("counter-type-" + minus + ".json"), JSON.toJson(input.state()));
    }

    private static void chooseType(RulesScenario game, Input input, String type) {
        var option = input.prompt().getAsJsonObject("counterPaymentChoice").getAsJsonArray("options").asList().stream()
                .map(value -> value.getAsJsonObject()).filter(value -> text(value, "type").equals(type)).findFirst().orElseThrow();
        assertTrue(game.answer(input, Map.of("choices", List.of(option.get("index").getAsInt()))));
    }

    private static Input main(RulesScenario game) throws Exception {
        return game.until(i -> i.seat() == 0 && priority(i) && text(i.state(), "phaseKey").equals("MAIN1"));
    }
    private static boolean priority(Input input) { return text(input.prompt(), "inputType").equals("InputPassPriority"); }
    private static Input floatBlue(RulesScenario game, Input input, int amount) throws Exception {
        for (int mana = 1; mana <= amount; mana++) {
            var island = zone(viewer(input.state()), "Battlefield").getAsJsonArray("cards").asList().stream()
                    .map(value -> value.getAsJsonObject()).filter(value -> text(value, "name").equals("Island")
                            && !value.get("tapped").getAsBoolean()).findFirst().orElseThrow();
            activate(game, input, island, "Add {U}");
            int wanted = mana;
            input = game.until(next -> next.seat() == 0 && priority(next)
                    && viewer(next.state()).getAsJsonObject("mana").get("U").getAsInt() == wanted);
        }
        return input;
    }
    private static JsonObject card(JsonObject state, String name) {
        return zone(viewer(state), "Battlefield").getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                .filter(card -> text(card, "name").equals(name)).findFirst().orElseThrow();
    }
    private static void activate(RulesScenario game, Input input, JsonObject source, String contains) {
        var actions = JSON.toJsonTree(game.session(input.seat()).sourceActions(request(input,
                Map.of("key", text(source, "key"))))).getAsJsonObject();
        var action = actions.getAsJsonArray("actions").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "text").contains(contains)).findFirst().orElseThrow();
        assertTrue(game.answer(input, Map.of("action", "useSourceAction", "key", text(actions, "sourceCardKey"),
                "actionId", text(action, "id"))));
    }
}
