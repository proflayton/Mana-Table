package forge.api;

import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import org.testng.annotations.DataProvider;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.time.Duration;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Real draw/counter triggers, a real targeted bounce, and the resulting public stack. */
public class ToothyLeavesBattlefieldTest {
    private static final String TOOTHY = "Toothy, Imaginary Friend";

    @Test(timeOut = 90000)
    public void returningToothyToHandDrawsForItsLastKnownCounters() throws Exception {
        try (var scenario = new RulesScenario("toothy-bounce-draw",
                Map.of(0, List.of(TOOTHY, "Temple Bell", "Temple Bell", "Seal of Removal")))) {
            var main = priority(scenario);
            assertEquals(counters(card(main.state(), "Battlefield", TOOTHY)), 1,
                    "The actual first-turn draw puts a real +1/+1 counter on Toothy");
            main = drawCounters(scenario, main, 2);
            leaveAndAssert(scenario, main, "Seal of Removal", "Hand", null);
        }
    }

    @DataProvider public Object[][] otherDestinations() {
        return new Object[][] {{"Seal of Doom", "Graveyard"}, {"Mangara of Corondor", "Exile"}};
    }

    @Test(dataProvider = "otherDestinations", timeOut = 90000)
    public void otherBattlefieldExitsRetainTheDrawCount(String effect, String destination) throws Exception {
        try (var scenario = new RulesScenario("toothy-" + destination.toLowerCase() + "-draw",
                Map.of(0, List.of(TOOTHY, "Temple Bell", "Temple Bell", effect, "Thousand-Year Elixir")))) {
            var main = drawCounters(scenario, priority(scenario), 2);
            leaveAndAssert(scenario, main, effect, destination, null);
        }
    }

    @DataProvider public Object[][] commanderBounceChoices() { return new Object[][] {{false}, {true}}; }

    @Test(dataProvider = "commanderBounceChoices", timeOut = 90000)
    public void bouncedCommanderDrawsWhetherReturnedToHandOrCommand(boolean useCommandZone) throws Exception {
        try (var scenario = new RulesScenario("toothy-commander-bounce-" + useCommandZone,
                Map.of(0, List.of("Island", "Island", "Island", "Island", "Temple Bell", "Temple Bell", "Temple Bell", "Seal of Removal")),
                "Island", TOOTHY)) {
            var main = priority(scenario);
            assertTrue(scenario.answer(main, Map.of("action", "card", "key", text(card(main.state(), "Command", TOOTHY), "key"))));
            var payment = scenario.until(input -> text(input.prompt(), "inputType").startsWith("InputPayMana"));
            assertEquals(payment.seat(), 0);
            assertTrue(scenario.answer(payment, Map.of("action", "ok")));
            awaitPayment(scenario);
            var partnerTarget = scenario.until(input -> text(input.prompt(), "inputType").equals("InputSelectTargets"));
            assertEquals(partnerTarget.seat(), 0);
            assertTrue(scenario.answer(partnerTarget, Map.of("action", "player", "playerId", partnerTarget.state().get("viewerId").getAsInt())));
            var search = scenario.until(input -> text(input.prompt(), "inputType").equals("InputConfirm"));
            assertTrue(text(search.prompt(), "message").toLowerCase().contains("library"));
            assertTrue(scenario.answer(search, Map.of("action", "cancel")));
            main = priority(scenario);
            assertEquals(counters(card(main.state(), "Battlefield", TOOTHY)), 0,
                    "Casting the commander happens after the first-turn draw");
            main = drawCounters(scenario, main, 3);
            leaveAndAssert(scenario, main, "Seal of Removal", useCommandZone ? "Command" : "Hand", useCommandZone);
        }
    }

    private static RulesScenario.Input drawCounters(RulesScenario scenario, RulesScenario.Input main, int activations) throws Exception {
        for (int activation = 0; activation < activations; activation++) {
            var bell = zone(viewer(main.state()), "Battlefield").getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                    .filter(value -> text(value, "name").equals("Temple Bell") && !value.get("tapped").getAsBoolean()).findFirst().orElseThrow();
            assertTrue(scenario.answer(main, Map.of("action", "card", "key", text(bell, "key"))));
            main = priority(scenario);
        }
        assertEquals(counters(card(main.state(), "Battlefield", TOOTHY)), 3,
                "The actual draws resolve three +1/+1 counter triggers");
        return main;
    }

    private static void leaveAndAssert(RulesScenario scenario, RulesScenario.Input main,
                                       String effect, String destination, Boolean commandChoice) throws Exception {
        int expectedDraws = counters(card(main.state(), "Battlefield", TOOTHY));
        int owner = main.state().get("viewerId").getAsInt();
        int handBefore = count(viewer(main.state()), "Hand");
        int libraryBefore = count(viewer(main.state()), "Library");
        int drawsBefore = draws(main.state(), owner);
        int returnedToHand = destination.equals("Hand") ? 1 : 0;
        var before = main.state().deepCopy();
        assertTrue(scenario.answer(main, Map.of("action", "card", "key", text(card(main.state(), "Battlefield", effect), "key"))));
        var target = scenario.until(input -> text(input.prompt(), "inputType").equals("InputSelectTargets"));
        assertEquals(target.seat(), 0);
        assertTrue(scenario.answer(target, Map.of("action", "card", "key", text(card(target.state(), "Battlefield", TOOTHY), "key"))));
        var pending = scenario.until(input -> input.seat() == 0 && text(input.prompt(), "inputType").equals("InputPassPriority")
                && input.state().getAsJsonArray("stack").asList().stream().map(value -> value.getAsJsonObject())
                .anyMatch(item -> text(item, "name").equals(effect)));
        String previousPriority = text(pending.prompt(), "id");
        assertTrue(scenario.answer(pending, Map.of("action", "ok")));
        JsonObject replacement = null;
        if (commandChoice != null) {
            var confirm = scenario.until(input -> text(input.prompt(), "inputType").equals("InputConfirm")
                    && text(input.prompt(), "message").toLowerCase().contains("command"));
            assertEquals(confirm.seat(), 0);
            assertTrue(text(confirm.prompt(), "message").toLowerCase().contains("command"), confirm.prompt().toString());
            replacement = confirm.state().deepCopy();
            assertTrue(scenario.answer(confirm, Map.of("action", commandChoice ? "ok" : "cancel")));
        }
        var trigger = commandChoice != null ? completedCommanderExit(scenario, previousPriority, before, destination, effect) : scenario.until(input -> input.seat() == 0
                && text(input.prompt(), "inputType").equals("InputPassPriority")
                && hasCard(viewer(input.state()), destination, TOOTHY));
        var onStack = trigger.state().deepCopy();
        var onStackViews = List.of(scenario.state(0), scenario.state(1), scenario.state(2), scenario.state(3));
        var evidence = new LinkedHashMap<String, Object>();
        evidence.put("beforeBounce", before);
        evidence.put("commandReplacement", replacement);
        evidence.put("triggerOnStack", onStack);
        evidence.put("triggerViewsBySeat", onStackViews);
        evidence.put("expectedDraws", expectedDraws);
        writeEvidence(scenario, evidence);
        assertTrue(trigger.state().getAsJsonArray("stack").asList().stream().map(value -> value.getAsJsonObject())
                        .anyMatch(item -> text(item, "name").equals(TOOTHY)),
                "Moving Toothy from the battlefield to " + destination + " must create its leaves-the-battlefield trigger");
        assertEquals(count(viewer(trigger.state()), "Hand"), handBefore + returnedToHand,
                "The zone change completes before its draw trigger resolves");
        assertEquals(count(viewer(trigger.state()), "Library"), libraryBefore,
                "Displaying the trigger must not draw before the stack resolves");
        assertEquals(counters(card(trigger.state(), destination, TOOTHY)), 0,
                "The moved card loses counters, while its trigger must retain last-known information");
        for (var view : onStackViews) {
            var item = view.getAsJsonArray("stack").asList().stream().map(value -> value.getAsJsonObject())
                    .filter(value -> text(value, "name").equals(TOOTHY)).findFirst().orElseThrow();
            assertTrue(item.get("ability").getAsBoolean());
            assertTrue(text(item, "text").contains("leaves the battlefield"));
            assertEquals(counters(item.getAsJsonObject("card")), expectedDraws,
                    "Every seat sees the public last-known battlefield source, not its new private hand identity");
        }
        assertTrue(scenario.answer(trigger, Map.of("action", "ok")));
        var resolved = priority(scenario);
        evidence.put("afterResolution", resolved.state());
        writeEvidence(scenario, evidence);
        assertEquals(count(viewer(resolved.state()), "Hand"), handBefore + returnedToHand + expectedDraws,
                "Toothy draws for its battlefield counters even though the moved card has none");
        assertEquals(count(viewer(resolved.state()), "Library"), libraryBefore - expectedDraws);
        assertEquals(draws(resolved.state(), owner) - drawsBefore, expectedDraws);
        assertEquals(resolved.state().get("turn"), before.get("turn"), "This is trigger resolution, not later turn draws");
        for (int seat = 1; seat < 4; seat++) {
            var state = scenario.state(seat);
            assertEquals(count(player(state, owner), "Hand"), handBefore + returnedToHand + expectedDraws);
            assertTrue(zone(player(state, owner), "Hand").getAsJsonArray("cards").isEmpty(), "Other players see the hand count without the private cards");
        }
    }

    private static void writeEvidence(RulesScenario scenario, Map<String, Object> evidence) throws Exception {
        Files.writeString(scenario.evidence.resolve("toothy-observations.json"),
                new GsonBuilder().setPrettyPrinting().create().toJson(evidence));
    }

    private static RulesScenario.Input completedCommanderExit(RulesScenario scenario, String previousPriority,
                                                               JsonObject before, String destination, String effect) throws Exception {
        long previousEvent = before.getAsJsonArray("activity").asList().stream().mapToLong(value -> value.getAsJsonObject().get("id").getAsLong()).max().orElse(0);
        long deadline = System.nanoTime() + Duration.ofSeconds(5).toNanos();
        JsonObject state;
        do {
            state = scenario.state(0);
            var value = state.get("prompt");
            // Returning from a nested confirmation can briefly republish the
            // already-answered priority. Do not answer it again or inspect it as
            // the final post-resolution decision. Wait for the real resolution
            // event and a fresh priority without sending any further input.
            if (value != null && !value.isJsonNull()) {
                var prompt = value.getAsJsonObject();
                boolean resolved = state.getAsJsonArray("activity").asList().stream().map(entry -> entry.getAsJsonObject())
                        .anyMatch(entry -> entry.get("id").getAsLong() > previousEvent && text(entry, "kind").equals("resolved")
                                && text(entry, "cardName").equals(effect));
                if (text(prompt, "inputType").equals("InputPassPriority") && !text(prompt, "id").equals(previousPriority)
                        && hasCard(viewer(state), destination, TOOTHY) && resolved) return new RulesScenario.Input(0, state, prompt);
            }
            Thread.sleep(5);
        } while (System.nanoTime() < deadline);
        throw new AssertionError("The commander exit must finish resolving and publish fresh priority: " + state);
    }

    private static void awaitPayment(RulesScenario scenario) throws Exception {
        long deadline = System.nanoTime() + Duration.ofSeconds(5).toNanos();
        do {
            var state = scenario.state(0);
            var prompt = state.get("prompt");
            if (!state.getAsJsonArray("stack").isEmpty() || prompt != null && !prompt.isJsonNull()
                    && !text(prompt.getAsJsonObject(), "inputType").startsWith("InputPayMana")) return;
            Thread.sleep(5);
        } while (System.nanoTime() < deadline);
        fail("One accepted Auto must finish before another semantic decision");
    }

    private static RulesScenario.Input priority(RulesScenario scenario) throws Exception {
        return scenario.until(input -> input.seat() == 0 && text(input.state(), "phaseKey").equals("MAIN1")
                && text(input.prompt(), "inputType").equals("InputPassPriority") && input.state().getAsJsonArray("stack").isEmpty());
    }

    private static JsonObject card(JsonObject state, String zone, String name) {
        return zone(viewer(state), zone).getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "name").equals(name)).findFirst().orElseThrow(() -> new AssertionError(name + " missing from " + zone));
    }

    private static boolean hasCard(JsonObject player, String zone, String name) {
        return zone(player, zone).getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                .anyMatch(value -> text(value, "name").equals(name));
    }

    private static int counters(JsonObject card) {
        var amount = card.getAsJsonObject("counters").get("+1/+1");
        return amount == null ? 0 : amount.getAsInt();
    }

    private static int draws(JsonObject state, int playerId) {
        return state.getAsJsonArray("activity").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "kind").equals("draw") && value.has("playerId") && !value.get("playerId").isJsonNull()
                        && value.get("playerId").getAsInt() == playerId).mapToInt(value -> value.get("count").getAsInt()).sum();
    }
}
