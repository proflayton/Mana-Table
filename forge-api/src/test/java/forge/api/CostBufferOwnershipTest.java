package forge.api;

import com.google.common.eventbus.Subscribe;
import com.google.gson.Gson;
import com.google.gson.JsonObject;
import forge.game.GameEntityCounterTable;
import forge.game.card.CardCollection;
import forge.game.card.CardZoneTable;
import forge.game.cost.CostPartWithList;
import forge.game.cost.CostPayment;
import forge.game.cost.CostPutCounter;
import forge.game.cost.CostSacrifice;
import forge.game.cost.CostTapType;
import forge.game.event.GameEventCardCounters;
import forge.game.zone.ZoneType;
import org.testng.annotations.DataProvider;
import org.testng.annotations.Test;

import java.lang.reflect.Field;
import java.nio.file.Files;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Copied scratch buffers cannot consume another real payment's evidence. */
public class CostBufferOwnershipTest {
    private static final Gson JSON = new Gson();
    @DataProvider public Object[][] copies() { return new Object[][] {{false}, {true}}; }

    @Test(dataProvider = "copies", timeOut = 90000)
    public void copiedTapEvidenceCannotPreventPartialRefundOrContaminateTheNextActivation(boolean clearCopy) throws Exception {
        try (var game = RulesScenario.automated("cost-buffer-tap-" + clearCopy, Map.of(0, List.of(
                "Selesnya Evangel", "Grizzly Bears", "Birds of Paradise", "Forest", "Concordant Crossroads")),
                "Forest", "Rhys the Redeemed")) {
            var input = main(game);
            var evidence = new ArrayList<Object>();
            for (int attempt = 0; attempt < 3; attempt++) {
                use(game, input, card(input.state(), "Battlefield", "Selesnya Evangel"), "activate", null);
                var order = game.until(i -> i.seat() == 0 && ordered(i));
                order(game, order, "Tap ", "{T}");
                var selection = game.until(i -> i.seat() == 0 && text(i.prompt(), "inputType").equals("InputSelectCardsFromList"));
                var paid = HeadlessPlatform.query(game.table, () -> game.table.game.costPaymentStack.peek());
                assertTrue(paid.cost() instanceof CostTapType);
                String creature = attempt == 1 ? "Birds of Paradise" : "Grizzly Bears";
                assertTrue(game.answer(selection, Map.of("action", "card", "key", text(card(selection.state(), "Battlefield", creature), "key"))));
                var bill = game.until(i -> i.seat() == 0 && payment(i));
                int currentAttempt = attempt;
                HeadlessPlatform.query(game.table, () -> {
                    assertSame(game.table.game.costPaymentStack.peek().payment(), paid.payment());
                    var part = (CostPartWithList)paid.cost();
                    assertEquals(part.getCardList().size(), 1);
                    assertEquals(part.getLKIList().size(), 1);
                    assertEquals(part.getCardList().get(0).getName(), creature);
                    if (clearCopy) {
                        var copy = (CostPartWithList)part.copy();
                        assertSame(copy.getCardList().get(0), part.getCardList().get(0), "Copied evidence retains the same physical resource");
                        assertSame(copy.getLKIList().get(0), part.getLKIList().get(0), "LKI is preserved rather than replaced with a live card");
                        copy.resetLists();
                    }
                    evidence.add(Map.of("attempt", currentAttempt, "creature", creature, "clearCopy", clearCopy,
                            "retainedPaidCards", part.getCardList().size(), "retainedLki", part.getLKIList().size()));
                    Files.writeString(game.evidence.resolve("tap-copy-consumer.json"), JSON.toJson(evidence));
                    return null;
                });
                if (attempt < 2) {
                    assertTrue(game.answer(bill, Map.of("action", "cancel")));
                    input = game.until(i -> i.seat() == 0 && priority(i));
                    for (String name : List.of("Selesnya Evangel", "Grizzly Bears", "Birds of Paradise", "Forest"))
                        assertFalse(card(input.state(), "Battlefield", name).get("tapped").getAsBoolean(),
                                "Actual partial-payment cancellation restores only this attempt's paid tap: " + name);
                    assertEquals(tokens(input.state(), "Saproling"), 0L);
                } else {
                    use(game, bill, card(bill.state(), "Battlefield", "Forest"), "activate", Map.of("G", 1));
                    input = game.until(i -> i.seat() == 0 && priority(i) && i.state().getAsJsonArray("stack").isEmpty()
                            && tokens(i.state(), "Saproling") == 1);
                    assertTrue(card(input.state(), "Battlefield", creature).get("tapped").getAsBoolean());
                    assertTrue(card(input.state(), "Battlefield", "Selesnya Evangel").get("tapped").getAsBoolean());
                    assertFalse(card(input.state(), "Battlefield", "Birds of Paradise").get("tapped").getAsBoolean(),
                            "A successful new attempt cannot retain the previous attempt's chosen creature");
                }
            }
        }
    }

    @Test(dataProvider = "copies", timeOut = 90000)
    public void copiedCounterEvidenceCannotPreventItsPartialRefundOrAccumulateAcrossAttempts(boolean clearCopy) throws Exception {
        String name = "Yisan, the Wanderer Bard";
        try (var game = RulesScenario.automated("cost-buffer-counter-" + clearCopy,
                Map.of(0, List.of(name, "Forest", "Forest", "Forest", "Concordant Crossroads")),
                "Llanowar Elves", "Rhys the Redeemed")) {
            var probe = new CounterPayments(game);
            game.table.game.subscribeToEvents(probe);
            var input = main(game);
            for (int attempt = 0; attempt < 3; attempt++) {
                use(game, input, card(input.state(), "Battlefield", name), "activate", null);
                var order = game.until(i -> i.seat() == 0 && ordered(i));
                order(game, order, "Put ", "{T}");
                var bill = game.until(i -> i.seat() == 0 && payment(i));
                var heldBill = bill;
                int[] retainedBatch = new int[1];
                var paid = HeadlessPlatform.query(game.table, () -> {
                    assertNotNull(probe.part, "The actual counter event exposes its live CostPayment owner");
                    assertSame(game.table.game.costPaymentStack.peek().payment(), probe.payment);
                    var part = probe.part;
                    assertEquals(part.getCardList().size(), 1);
                    var table = field(part, CostPutCounter.class, "counterTable", GameEntityCounterTable.class);
                    assertEquals(table.totalValues(), 1);
                    if (clearCopy) {
                        var copy = (CostPutCounter)part.copy();
                        var copiedTable = field(copy, CostPutCounter.class, "counterTable", GameEntityCounterTable.class);
                        assertEquals(copiedTable.totalValues(), 1, "A paid snapshot retains its counter batch");
                        var cell = copiedTable.cellSet().iterator().next();
                        cell.getValue().add(part.getCounter(), 2);
                        copy.resetLists();
                    }
                    retainedBatch[0] = table.totalValues();
                    Files.writeString(game.evidence.resolve("counter-copy-consumer-" + probe.payments + ".json"), JSON.toJson(Map.of(
                            "clearCopy", clearCopy, "retainedPaidCards", part.getCardList().size(),
                            "retainedCounterBatch", table.totalValues(), "state", heldBill.state())));
                    return part;
                });
                if (attempt < 2) {
                    assertTrue(game.answer(bill, Map.of("action", "cancel")));
                    input = game.until(i -> i.seat() == 0 && priority(i));
                    var counters = card(input.state(), "Battlefield", name).getAsJsonObject("counters");
                    assertEquals(counters.has("Verse") ? counters.get("Verse").getAsInt() : 0, 0,
                            "The actual adjusted paid-cost consumer must refund its own added counter");
                    assertFalse(card(input.state(), "Battlefield", name).get("tapped").getAsBoolean());
                } else {
                    assertEquals(paid.getCardList().size(), 1);
                    use(game, bill, card(bill.state(), "Battlefield", "Forest"), "activate", Map.of("G", 1));
                    bill = game.until(i -> i.seat() == 0 && payment(i));
                    var forests = zone(viewer(bill.state()), "Battlefield").getAsJsonArray("cards").asList().stream()
                            .map(value -> value.getAsJsonObject()).filter(value -> text(value, "name").equals("Forest")
                                    && !value.get("tapped").getAsBoolean()).toList();
                    for (int land = 0; land < 2; land++) {
                        use(game, bill, cardById(bill.state(), text(forests.get(land), "visualId")), "activate", Map.of("G", 1));
                        if (land == 0) bill = game.until(i -> i.seat() == 0 && payment(i));
                    }
                    input = game.until(i -> i.seat() == 0 && priority(i) && !i.state().getAsJsonArray("stack").isEmpty());
                    assertEquals(card(input.state(), "Battlefield", name).getAsJsonObject("counters").get("Verse").getAsInt(), 1);
                    assertTrue(card(input.state(), "Battlefield", name).get("tapped").getAsBoolean());
                    assertEquals(paid.getCardList().size(), 0, "Successful payment clears only its completed scratch buffer");
                    assertEquals(probe.payments, 3);
                }
                assertEquals(retainedBatch[0], 1,
                        "A copied counter multiset cannot alter the retained batch of the actual payment");
            }
        }
    }

    @Test(dataProvider = "copies", timeOut = 90000)
    public void copiedSacrificeTableRetainsItsLkiAndRealDeathAndDrawResults(boolean clearCopy) throws Exception {
        try (var game = RulesScenario.automated("cost-buffer-sacrifice-" + clearCopy,
                Map.of(0, List.of("Kalonian Hydra", "Zulaport Cutthroat", "Forest", "Forest")),
                "Life's Legacy", "Rhys the Redeemed", true)) {
            var input = main(game);
            int hand = count(viewer(input.state()), "Hand");
            assertEquals(card(input.state(), "Battlefield", "Kalonian Hydra").get("power").getAsString(), "4");
            use(game, input, zone(viewer(input.state()), "Hand").getAsJsonArray("cards").get(0).getAsJsonObject(), "cast", null);
            var order = game.until(i -> i.seat() == 0 && ordered(i));
            assertTrue(game.answer(order, Map.of("choices", order.prompt().getAsJsonArray("choices").asList().stream()
                    .map(value -> value.getAsJsonObject()).sorted(java.util.Comparator.comparing(value -> !text(value, "label").startsWith("Sacrifice")))
                    .map(value -> value.get("index").getAsInt()).toList())));
            var sacrifice = game.until(i -> i.seat() == 0 && text(i.prompt(), "inputType").equals("InputSelectCardsFromList"));
            var paid = HeadlessPlatform.query(game.table, () -> (CostSacrifice)game.table.game.costPaymentStack.peek().cost());
            assertTrue(game.answer(sacrifice, Map.of("action", "card", "key", text(card(sacrifice.state(), "Battlefield", "Kalonian Hydra"), "key"))));
            var bill = game.until(i -> i.seat() == 0 && payment(i));
            var heldBill = bill;
            HeadlessPlatform.query(game.table, () -> {
                assertEquals(paid.getLKIList().size(), 1);
                assertEquals(paid.getLKIList().get(0).getNetPower(), 4);
                var table = field(paid, CostPartWithList.class, "table", CardZoneTable.class);
                if (clearCopy) {
                    var copy = (CostSacrifice)paid.copy();
                    var copyTable = field(copy, CostPartWithList.class, "table", CardZoneTable.class);
                    assertSame(copyTable.allCards().iterator().next(), table.allCards().iterator().next());
                    copyTable.cellSet().iterator().next().getValue().clear();
                    ((CardCollection)copyTable.getLastStateBattlefield()).clear();
                    ((CardCollection)copyTable.getLastStateGraveyard()).clear();
                    copy.resetLists();
                }
                int retained = table.filterCards(List.of(ZoneType.Battlefield), List.of(ZoneType.Graveyard), null,
                        paid.getCardList().isEmpty() ? null : paid.getCardList().get(0), null).size();
                Files.writeString(game.evidence.resolve("sacrifice-copy-consumer.json"), JSON.toJson(Map.of(
                        "clearCopy", clearCopy, "retainedZoneCards", retained, "retainedLki", paid.getLKIList().size(), "state", heldBill.state())));
                assertEquals(retained, 1, "The real cost's zone batch retains its sacrificed occurrence after another copy is cleared");
                assertEquals(paid.getLKIList().size(), 1);
                return null;
            });
            for (int land = 0; land < 2; land++) {
                var forests = zone(viewer(bill.state()), "Battlefield").getAsJsonArray("cards").asList().stream()
                        .map(value -> value.getAsJsonObject()).filter(value -> text(value, "name").equals("Forest") && !value.get("tapped").getAsBoolean()).toList();
                use(game, bill, forests.get(0), "activate", Map.of("G", 1));
                if (land == 0) bill = game.until(i -> i.seat() == 0 && payment(i));
            }
            var resolved = game.until(i -> i.seat() == 0 && priority(i) && i.state().getAsJsonArray("stack").isEmpty()
                    && count(viewer(i.state()), "Hand") == hand + 3);
            assertEquals(count(viewer(resolved.state()), "Graveyard"), 2);
            assertEquals(viewer(resolved.state()).get("life").getAsInt(), 41);
            for (int seat = 1; seat < 4; seat++) assertEquals(player(resolved.state(), game.table.game.getRegisteredPlayers().get(seat).getId()).get("life").getAsInt(), 39);
        }
    }

    @Test(dataProvider = "copies", timeOut = 90000)
    public void aResolutionListCostStillAcceptsOrDeclinesThroughItsActualEngineConsumer(boolean pay) throws Exception {
        String demon = "Demonlord of Ashmouth";
        try (var game = RulesScenario.automated("cost-buffer-resolution-" + pay,
                Map.of(0, List.of("Sol Ring", "Swamp", "Swamp", "Grizzly Bears")), demon, "Rhys the Redeemed")) {
            var input = main(game);
            for (String source : List.of("Sol Ring", "Swamp", "Swamp")) {
                var producer = zone(viewer(input.state()), "Battlefield").getAsJsonArray("cards").asList().stream()
                        .map(value -> value.getAsJsonObject()).filter(value -> text(value, "name").equals(source)
                                && !value.get("tapped").getAsBoolean()).findFirst().orElseThrow();
                use(game, input, producer, "activate", source.equals("Sol Ring") ? Map.of("C", 2) : Map.of("B", 1));
                input = game.until(i -> i.seat() == 0 && priority(i));
            }
            use(game, input, card(input.state(), "Hand", demon), "cast", null);
            for (String color : List.of("B", "B", "C", "C")) {
                var bill = game.until(i -> i.seat() == 0 && payment(i));
                assertTrue(game.answer(bill, Map.of("action", "spendPoolMana", "manaColor", color)));
            }
            var sacrifice = game.until(i -> i.seat() == 0 && text(i.prompt(), "inputType").equals("InputSelectCardsFromList"));
            assertTrue(card(sacrifice.state(), "Battlefield", "Grizzly Bears").get("selectable").getAsBoolean());
            assertFalse(card(sacrifice.state(), "Battlefield", demon).get("selectable").getAsBoolean());
            assertTrue(game.answer(sacrifice, pay ? Map.of("action", "card", "key", text(card(sacrifice.state(), "Battlefield", "Grizzly Bears"), "key"))
                    : Map.of("action", "cancel")));
            var resolved = game.until(i -> i.seat() == 0 && priority(i) && i.state().getAsJsonArray("stack").isEmpty());
            assertNotNull(card(resolved.state(), pay ? "Battlefield" : "Exile", demon));
            assertNotNull(card(resolved.state(), pay ? "Graveyard" : "Battlefield", "Grizzly Bears"));
            assertEquals(count(viewer(resolved.state()), "Graveyard"), pay ? 1 : 0);
            assertEquals(viewer(resolved.state()).getAsJsonObject("mana").entrySet().stream()
                    .mapToInt(value -> value.getValue().getAsInt()).sum(), 0);
        }
    }

    private static final class CounterPayments {
        private final RulesScenario game;
        CostPutCounter part;
        CostPayment payment;
        int payments;
        CounterPayments(RulesScenario game) { this.game = game; }
        @Subscribe public void counters(GameEventCardCounters event) {
            var entry = game.table.game.costPaymentStack.peek();
            if (entry != null && entry.cost() instanceof CostPutCounter cost && event.newValue() > event.oldValue()) {
                part = cost; payment = entry.payment(); payments++;
            }
        }
    }
    private static <T> T field(Object value, Class<?> owner, String name, Class<T> type) throws Exception {
        Field field = owner.getDeclaredField(name); field.setAccessible(true); return type.cast(field.get(value));
    }
    private static Input main(RulesScenario game) throws Exception {
        return game.until(i -> i.seat() == 0 && priority(i) && text(i.state(), "phaseKey").equals("MAIN1"));
    }
    private static boolean priority(Input input) { return text(input.prompt(), "inputType").equals("InputPassPriority"); }
    private static boolean payment(Input input) { return text(input.prompt(), "inputType").startsWith("InputPayMana"); }
    private static boolean ordered(Input input) { return text(input.prompt(), "kind").equals("choice") && input.prompt().has("ordered") && input.prompt().get("ordered").getAsBoolean(); }
    private static void order(RulesScenario game, Input input, String first, String last) {
        var choices = input.prompt().getAsJsonArray("choices").asList().stream().map(value -> value.getAsJsonObject()).toList();
        assertTrue(game.answer(input, Map.of("choices", choices.stream().sorted(java.util.Comparator.comparingInt(
                value -> text(value, "label").startsWith(first) ? 0 : text(value, "label").equals(last) ? 2 : 1))
                .map(value -> value.get("index").getAsInt()).toList())));
    }
    private static long tokens(JsonObject state, String name) { return zone(viewer(state), "Battlefield").getAsJsonArray("cards").asList().stream().filter(value -> text(value.getAsJsonObject(), "name").contains(name)).count(); }
    private static JsonObject card(JsonObject state, String zone, String name) { return zone(viewer(state), zone).getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject()).filter(value -> text(value, "name").equals(name)).findFirst().orElseThrow(); }
    private static JsonObject cardById(JsonObject state, String id) { return zone(viewer(state), "Battlefield").getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject()).filter(value -> text(value, "visualId").equals(id)).findFirst().orElseThrow(); }
    private static void use(RulesScenario game, Input input, JsonObject source, String kind, Map<String, Integer> output) {
        var catalog = JSON.toJsonTree(game.session(input.seat()).sourceActions(request(input, Map.of("key", text(source, "key"))))).getAsJsonObject();
        var action = catalog.getAsJsonArray("actions").asList().stream().map(value -> value.getAsJsonObject()).filter(value -> text(value, "kind").equals(kind)).findFirst().orElseThrow();
        var answer = new LinkedHashMap<String, Object>(Map.of("action", "useSourceAction", "key", text(source, "key"), "actionId", text(action, "id")));
        if (output != null) answer.put("manaOutput", Map.of("colors", output));
        assertTrue(game.answer(input, answer));
    }
}
