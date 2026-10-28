package forge.api;

import com.google.common.eventbus.Subscribe;
import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import forge.game.ExecutionTracker;
import forge.game.event.GameEventCardChangeZone;
import forge.game.event.GameEventCardDamaged;
import org.testng.annotations.DataProvider;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.util.*;
import java.util.concurrent.CopyOnWriteArrayList;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Real card scripts and current production answers prove rules ownership.
 * These journeys deliberately do not attribute an SBA to the last stack item. */
public class ExecutionRulesJourneyTest {
    private static final com.google.gson.Gson JSON = new GsonBuilder().setPrettyPrinting().serializeNulls().create();

    @DataProvider public Object[][] damageRules() {
        return new Object[][] {
                {"Grizzly Bears", false, false},
                {"Grizzly Bears", true, false},
                {"Phantom Flock", false, true},
                {"Jace Beleren", false, false}
        };
    }

    @Test(groups = "canonical-interaction", dataProvider = "damageRules", timeOut = 90000)
    public void replacementPreventionBufferedEffectsAndStateBasedDeathsKeepTheirActualOwners(
            String victim, boolean prevent, boolean buffered) throws Exception {
        var defenders = new ArrayList<>(List.of(victim));
        if (prevent) defenders.add("Mark of Asylum");
        boolean walker = victim.equals("Jace Beleren");
        String name = "execution-rules-damage-" + victim.replace(" ", "") + "-" + prevent;
        // Registered pregame extras do not receive planeswalker starting
        // loyalty. Cast Jace through the ordinary game instead of inventing it.
        var scenario = walker ? new RulesScenario(name,
                Map.of(0, List.of("Mountain", "Reliquary Tower"), 1, List.of("Island", "Island", "Island")),
                "Lightning Bolt", "Rhys the Redeemed", Map.of(1, Collections.nCopies(99, "Jace Beleren")))
                : new RulesScenario(name, Map.of(0, List.of("Mountain"), 1, defenders),
                "Lightning Bolt", "Rhys the Redeemed", true);
        try (var game = scenario) {
            Input main;
            if (walker) {
                var cast = game.until(input -> input.seat() == 1 && priority(input)
                        && text(input.state(), "phaseKey").equals("MAIN1")
                        && input.state().get("activePlayerId").getAsInt() == input.state().get("viewerId").getAsInt());
                start(game, cast, card(viewer(cast.state()), "Hand", victim), "");
                var payment = game.until(input -> input.seat() == 1 && text(input.prompt(), "inputType").startsWith("InputPayMana"));
                assertTrue(payment.prompt().get("okEnabled").getAsBoolean());
                assertTrue(game.answer(payment, Map.of("action", "ok")));
                main = settled(game);
                int owner = game.state(1).get("viewerId").getAsInt();
                assertEquals(counters(card(player(main.state(), owner), "Battlefield", victim), "Loyalty"), 3,
                        "The real planeswalker spell supplied its normal starting loyalty before damage");
            } else main = main(game);
            int targetPlayer = game.state(1).get("viewerId").getAsInt();
            String cardId = text(card(player(main.state(), targetPlayer), "Battlefield", victim), "visualId");
            var committed = new CopyOnWriteArrayList<ExecutionTracker.Committed>();
            var late = new CopyOnWriteArrayList<ExecutionTracker.Committed>();
            var completed = Collections.synchronizedSet(new HashSet<String>());
            game.table.game.subscribeToEvents(new Object() {
                @Subscribe public void changed(ExecutionTracker.Transition transition) {
                    if (transition.occurrence().step().equals("complete")) completed.add(transition.occurrence().id());
                }
                @Subscribe public void committed(ExecutionTracker.Committed event) {
                    committed.add(event);
                    if (event.execution() != null && completed.contains(event.execution().id())) late.add(event);
                }
            });
            var stacked = bolt(game, main, targetPlayer, victim);
            String spellId = text(stacked.state().getAsJsonArray("stack").get(0).getAsJsonObject(), "executionId");
            Files.writeString(game.evidence.resolve("stacked.json"), JSON.toJson(stacked.state()));
            var settled = settled(game);
            assertEquals(text(occurrence(settled.state(), spellId), "outcome"), "resolved",
                    "Prevention changes the committed result, not the spell's successful resolution");
            assertTrue(late.isEmpty(), "No committed change can follow its execution's completion: " + late);
            var damage = committed.stream().filter(event -> event.event() instanceof GameEventCardDamaged).toList();
            var replacements = occurrences(settled.state()).stream().filter(value -> text(value, "ruleKind").equals("replacement")
                    && text(value, "parentId").equals(spellId)).toList();
            if (prevent || buffered) {
                assertTrue(damage.isEmpty(), "Fully prevented damage is not a committed damage fact");
                assertFalse(replacements.isEmpty());
                var applied = replacements.stream().filter(value -> text(value, "ruleResult")
                        .equals(prevent ? "prevented" : "replaced")).findFirst().orElseThrow();
                assertEquals(text(applied, "budgetRootId"), spellId);
                assertEquals(text(applied, "sourceName"), prevent ? "Mark of Asylum" : victim);
                assertEquals(text(applied, "outcome"), "resolved");
                card(player(settled.state(), targetPlayer), "Battlefield", victim);
                if (buffered) {
                    var counter = activities(settled.state()).stream().filter(value -> text(value, "cardId").equals(cardId)
                            && text(value, "kind").equals("counters") && value.getAsJsonObject("change").get("after").getAsInt() == 2)
                            .findFirst().orElseThrow();
                    var followUp = occurrence(settled.state(), text(counter, "executionId"));
                    assertEquals(text(followUp, "ruleKind"), "replacement");
                    assertEquals(text(followUp, "parentId"), spellId);
                    assertNotEquals(text(followUp, "id"), text(applied, "id"),
                            "An aggregate buffered effect is not arbitrarily assigned one per-target application");
                    assertEquals(text(counter, "executionStep"), "resolution");
                }
            } else {
                assertEquals(damage.size(), 1);
                var receipt = (GameEventCardDamaged) damage.get(0).event();
                assertEquals(receipt.amount(), 3);
                assertEquals(receipt.recipientControllerId(), Integer.valueOf(targetPlayer));
                assertEquals(receipt.sourceControllerId(), Integer.valueOf(main.state().get("viewerId").getAsInt()));
                assertEquals(receipt.combat(), Boolean.FALSE);
                assertNotNull(receipt.cause()); assertEquals(receipt.cause().instanceId(), spellId);
                assertEquals(receipt.cause().controller().getId(), main.state().get("viewerId").getAsInt());
                if (victim.equals("Jace Beleren")) assertEquals(receipt.type(), GameEventCardDamaged.DamageType.LoyaltyLoss);
                assertEquals(damage.get(0).execution().id(), spellId);
                var moved = committed.stream().filter(value -> value.event() instanceof GameEventCardChangeZone movement
                        && movement.from() != null && movement.from().zoneType() == forge.game.zone.ZoneType.Battlefield
                        && movement.card().getName().equals(victim)).findFirst().orElseThrow();
                assertNotNull(moved.execution());
                var stateCheck = occurrence(settled.state(), moved.execution().id());
                assertEquals(text(stateCheck, "ruleKind"), "stateBased");
                assertEquals(text(stateCheck, "parentId"), "",
                        "This actual priority-time check has no active owner; the prior stack item is not invented causality");
                assertEquals(text(stateCheck, "outcome"), "resolved");
                assertEquals(text(stateCheck, "actorPlayerId"), "");
                assertNull(((GameEventCardChangeZone) moved.event()).cause(), "Rules death is distinct from explicit spell removal");
                card(player(settled.state(), targetPlayer), "Graveyard", victim);
            }
            for (int seat = 0; seat < 4; seat++)
                Files.writeString(game.evidence.resolve("resolved-seat-" + seat + ".json"), JSON.toJson(game.state(seat)));
        }
    }

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void anOptionalReplacementDeclineAndReuseHaveDistinctOutcomesAndCommittedDamage() throws Exception {
        try (var game = new RulesScenario("execution-rules-optional-reuse", Map.of(0, List.of("Mountain", "Mountain"),
                1, List.of("Battletide Alchemist")), "Lightning Bolt", "Rhys the Redeemed")) {
            var main = main(game);
            int target = game.state(1).get("viewerId").getAsInt();
            var ids = new ArrayList<String>();
            for (boolean accept : List.of(false, true)) {
                var stacked = bolt(game, main, target, null);
                String spell = text(stacked.state().getAsJsonArray("stack").get(0).getAsJsonObject(), "executionId");
                var choice = game.until(input -> input.seat() == 1 && text(input.prompt(), "inputType").equals("InputConfirm"));
                String replacement = text(choice.prompt(), "executionId");
                assertFalse(replacement.isBlank()); ids.add(replacement);
                var choosing = occurrence(choice.state(), replacement);
                assertEquals(text(choosing, "ruleKind"), "replacement");
                assertEquals(text(choosing, "parentId"), spell);
                assertEquals(text(choosing, "step"), "choice");
                assertTrue(game.answer(choice, Map.of("action", accept ? "ok" : "cancel")));
                main = settled(game);
                var result = occurrence(main.state(), replacement);
                assertEquals(text(result, "outcome"), accept ? "resolved" : "declined");
                assertEquals(text(result, "ruleResult"), accept ? "updated" : "notReplaced");
                assertEquals(text(occurrence(main.state(), spell), "outcome"), "resolved");
                assertEquals(player(main.state(), target).get("life").getAsInt(), accept ? 35 : 37);
            }
            assertNotEquals(ids.get(0), ids.get(1), "Reusing one replacement template creates a fresh actual application");
            Files.writeString(game.evidence.resolve("resolved.json"), JSON.toJson(main.state()));
        }
    }

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void pirReplacementChangesTheCommittedCounterDeltaInsideTheActualDrawTrigger() throws Exception {
        try (var game = new RulesScenario("execution-rules-pir", Map.of(0, List.of("Loch Mare", "Pir, Imaginative Rascal",
                "Chasm Skulker", "Island", "Island", "Island")), "Island", "Rhys the Redeemed", true)) {
            var main = main(game);
            var source = card(viewer(main.state()), "Battlefield", "Chasm Skulker");
            int before = counters(source, "+1/+1");
            long baseline = main.state().getAsJsonObject("executions").get("lastSequence").getAsLong();
            start(game, main, card(viewer(main.state()), "Battlefield", "Loch Mare"), "Draw a card");
            var payment = game.until(input -> input.seat() == 0 && text(input.prompt(), "inputType").startsWith("InputPayMana"));
            assertTrue(game.answer(payment, Map.of("action", "ok")));
            var settled = settled(game);
            assertEquals(counters(card(viewer(settled.state()), "Battlefield", "Chasm Skulker"), "+1/+1"), before + 2);
            var transitionIds = settled.state().getAsJsonObject("executions").getAsJsonArray("transitions").asList().stream()
                    .map(value -> value.getAsJsonObject()).filter(value -> value.get("sequence").getAsLong() > baseline)
                    .map(value -> text(value, "executionId")).toList();
            var replacement = occurrences(settled.state()).stream().filter(value -> text(value, "ruleKind").equals("replacement")
                    && text(value, "sourceName").equals("Pir, Imaginative Rascal") && transitionIds.contains(text(value, "id")))
                    .findFirst().orElseThrow();
            assertEquals(text(replacement, "ruleResult"), "updated");
            var trigger = occurrence(settled.state(), text(replacement, "parentId"));
            assertEquals(text(trigger, "kind"), "trigger"); assertEquals(text(trigger, "sourceName"), "Chasm Skulker");
            assertEquals(text(replacement, "budgetRootId"), text(trigger, "budgetRootId"));
            var result = activities(settled.state()).stream().filter(value -> text(value, "kind").equals("counters")
                    && text(value, "executionId").equals(text(trigger, "id"))).findFirst().orElseThrow();
            assertEquals(result.getAsJsonObject("change").get("before").getAsInt(), before);
            assertEquals(result.getAsJsonObject("change").get("after").getAsInt(), before + 2,
                    "The actual committed result follows replacement; planned +1 is not the result");
            Files.writeString(game.evidence.resolve("resolved.json"), JSON.toJson(settled.state()));
        }
    }

    private static Input bolt(RulesScenario game, Input main, int playerId, String victim) throws Exception {
        start(game, main, card(viewer(main.state()), "Hand", "Lightning Bolt"), "");
        var target = game.until(input -> input.seat() == 0 && text(input.prompt(), "inputType").equals("InputSelectTargets"));
        assertTrue(game.answer(target, victim == null ? Map.of("action", "player", "playerId", playerId)
                : Map.of("action", "card", "key", text(card(player(target.state(), playerId), "Battlefield", victim), "key"))));
        var payment = game.until(input -> input.seat() == 0 && text(input.prompt(), "inputType").startsWith("InputPayMana"));
        assertTrue(payment.prompt().get("okEnabled").getAsBoolean());
        assertTrue(game.answer(payment, Map.of("action", "ok")));
        return game.until(input -> input.seat() == 0 && priority(input) && input.state().getAsJsonArray("stack").size() == 1);
    }
    private static Input main(RulesScenario game) throws Exception {
        return game.until(input -> input.seat() == 0 && priority(input) && text(input.state(), "phaseKey").equals("MAIN1"));
    }
    private static Input settled(RulesScenario game) throws Exception {
        return game.until(input -> input.seat() == 0 && priority(input) && input.state().getAsJsonArray("stack").isEmpty());
    }
    private static boolean priority(Input input) { return text(input.prompt(), "inputType").equals("InputPassPriority"); }
    private static JsonObject card(JsonObject player, String zone, String name) {
        return zone(player, zone).getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "name").equals(name)).findFirst().orElseThrow();
    }
    private static int counters(JsonObject card, String type) {
        return card.getAsJsonObject("counters").has(type) ? card.getAsJsonObject("counters").get(type).getAsInt() : 0;
    }
    private static List<JsonObject> occurrences(JsonObject state) {
        return state.getAsJsonObject("executions").getAsJsonArray("occurrences").asList().stream().map(value -> value.getAsJsonObject()).toList();
    }
    private static JsonObject occurrence(JsonObject state, String id) {
        return occurrences(state).stream().filter(value -> text(value, "id").equals(id)).findFirst().orElseThrow();
    }
    private static List<JsonObject> activities(JsonObject state) {
        return state.getAsJsonArray("activity").asList().stream().map(value -> value.getAsJsonObject()).toList();
    }
    private static void start(RulesScenario game, Input input, JsonObject card, String text) {
        var catalog = JSON.toJsonTree(game.table.seat(input.seat()).sourceActions(request(input, Map.of("key", text(card, "key"))))).getAsJsonObject();
        var action = catalog.getAsJsonArray("actions").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "text").contains(text)).findFirst().orElseThrow();
        assertTrue(game.answer(input, Map.of("action", "useSourceAction", "key", text(catalog, "sourceCardKey"), "actionId", text(action, "id"))));
    }
}
