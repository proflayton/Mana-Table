package forge.api;

import com.google.gson.Gson;
import com.google.gson.JsonObject;
import forge.StaticData;
import forge.deck.Deck;
import forge.deck.DeckSection;
import forge.api.AutomatedPaymentJourneyTest.Driver;
import forge.api.AutomatedPaymentJourneyTest.Input;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.*;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;
import java.util.function.Predicate;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Consent covers current occurrences, not new actions or choices between polls. */
public class StackResolutionTest {
    private static final Gson JSON = new Gson();
    private static final Path RESOURCES = Path.of("../forge-gui/res");
    private static final String REVOKED = "The stack changed. Review the current effects before continuing.";

    @Test(groups = "canonical-interaction", timeOut = 120000)
    public void aRealSeventyTriggerDrawBurstKeepsEveryOccurrenceAndRevokesTransientInterruptions() throws Exception {
        if (StaticData.instance() == null) EngineResources.load(RESOURCES);
        var human = MatchTable.human(deck("Human", "Forest"), "Commander", "Human");
        var automated = MatchTable.automated(deck("Draw", "Blue Sun's Zenith"), "Commander", "Draw");
        var extras = new ArrayList<forge.item.IPaperCard>();
        for (String name : List.of("Chasm Skulker", "Toothy, Imaginary Friend", "Pir, Imaginative Rascal"))
            extras.add(StaticData.instance().getCommonCards().getCard(name));
        for (int index = 0; index < 42; index++) extras.add(StaticData.instance().getCommonCards().getCard("Island"));
        automated.addExtraCardsOnBattlefield(extras);
        Path evidence = Path.of("target/rules-regressions/stack-resolution-draw-burst"); Files.createDirectories(evidence);
        try (var table = new MatchTable("Commander", List.of(human, automated), RESOURCES, evidence)) {
            int actor = table.game.getPlayers().get(1).getId();
            var driver = new Driver(table, actor); table.start();
            var main = ready(driver, input -> input.playerId() == actor && priority(input)
                    && input.state().get("activePlayerId").getAsInt() == actor && text(input.state(), "phaseKey").equals("MAIN1")
                    && input.state().getAsJsonArray("stack").isEmpty());
            int skulkerBefore = counters(main.state(), "Chasm Skulker");
            int toothyBefore = counters(main.state(), "Toothy, Imaginary Friend");
            castDraw(driver, main, actor, 35);
            var burst = ready(driver, input -> input.playerId() == actor && priority(input)
                    && input.state().getAsJsonArray("stack").size() == 70);
            var frozen = ids(burst.state());
            assertEquals(frozen.size(), 70);
            var bySource = new HashMap<String, Integer>();
            for (var value : burst.state().getAsJsonArray("stack")) {
                var item = value.getAsJsonObject(); assertEquals(text(item, "kind"), "triggered");
                assertEquals(item.get("controllerId").getAsInt(), actor);
                assertTrue(item.getAsJsonArray("targets").isEmpty());
                bySource.merge(text(item.getAsJsonObject("card"), "name"), 1, Integer::sum);
            }
            assertEquals(bySource, Map.of("Chasm Skulker", 35, "Toothy, Imaginary Friend", 35),
                    "Pir changes each actual counter effect; it does not add a third stack trigger");
            assertEquals(burst.state().getAsJsonArray("activity").asList().stream().map(value -> value.getAsJsonObject())
                    .filter(entry -> text(entry, "kind").equals("ability") && frozen.contains(text(entry, "stackId")))
                    .map(entry -> text(entry, "stackId")).collect(java.util.stream.Collectors.toSet()), frozen,
                    "Event-time announcements identify every exact stack occurrence without joining by source or prose");
            long original = version(burst);
            Files.writeString(evidence.resolve("private-70-trigger-observation.json"), JSON.toJson(table.automatedSeat(actor).automatedObservation()));

            // A real response resolves before the later observation. The same
            // original IDs return, but consent must not silently survive it.
            castDraw(driver, burst, actor, 0);
            var response = ready(driver, input -> input.playerId() == actor && priority(input)
                    && input.state().getAsJsonArray("stack").size() == 71);
            Files.writeString(evidence.resolve("private-71-entry-observation.json"), JSON.toJson(table.automatedSeat(actor).automatedObservation()));
            assertTrue(version(response) > original);
            driver.answer(response, Map.of("action", "passPriority"));
            var returned = ready(driver, input -> input.playerId() == actor && priority(input) && ids(input.state()).equals(frozen));
            assertThrows(IllegalArgumentException.class, () -> returned.session().action(request(returned,
                    Map.of("action", "passPriority", "stackInterruptionVersion", original))));

            // A fixed mana activation never enters the stack, but still revokes.
            long beforeMana = version(returned);
            driver.use(returned, untappedIsland(returned.state()), "activate", Map.of("U", 1));
            var mana = ready(driver, input -> input.playerId() == actor && priority(input)
                    && viewer(input.state()).getAsJsonObject("mana").get("U").getAsInt() == 1);
            assertEquals(ids(mana.state()), frozen); assertTrue(version(mana) > beforeMana);
            assertThrows(IllegalArgumentException.class, () -> mana.session().action(request(mana,
                    Map.of("action", "passPriority", "stackInterruptionVersion", beforeMana))));

            var afterChoice = queuedChoice(driver, table, actor, mana);
            long armed = version(afterChoice);
            var next = afterChoice;
            var settled = new HashSet<String>();
            int passes = 0;
            while (!next.state().getAsJsonArray("stack").isEmpty()) {
                assertEquals(version(next), armed, "Existing trigger resolution and Pir's replacement do not create new consent");
                assertTrue(frozen.containsAll(ids(next.state())));
                // Exercise the actual private provider boundary on every owned
                // priority frame, including the large hand and stack.
                var observation = table.automatedSeat(actor).automatedObservation();
                assertNotNull(observation);
                driver.answer(next, Map.of("action", "passPriority", "stackInterruptionVersion", armed)); passes++;
                next = ready(driver, input -> input.playerId() == actor && priority(input));
                for (var entry : next.state().getAsJsonArray("activity")) {
                    var fact = entry.getAsJsonObject();
                    if (text(fact, "kind").equals("resolved") && frozen.contains(text(fact, "stackId"))) settled.add(text(fact, "stackId"));
                }
            }
            assertEquals(passes, 70, "Every separate trigger resolves through ordinary priority, with no merging or elision");
            assertEquals(settled, frozen, "Every actual settlement retains its exact entry ID even when stack snapshots advance");
            assertEquals(counters(next.state(), "Chasm Skulker"), skulkerBefore + 70);
            assertEquals(counters(next.state(), "Toothy, Imaginary Friend"), toothyBefore + 70);
            assertEquals(version(next), armed);
            var empty = next;
            assertThrows(IllegalArgumentException.class, () -> empty.session().action(request(empty,
                    Map.of("action", "passPriority", "stackInterruptionVersion", armed))));
            Files.writeString(evidence.resolve("settled.json"), JSON.toJson(next.state()));
        }
    }

    private static Input queuedChoice(Driver driver, MatchTable table, int actor, Input input) throws Exception {
        var entered = new CountDownLatch(1); var release = new CountDownLatch(1);
        var worker = Executors.newSingleThreadExecutor();
        HeadlessPlatform.later(() -> {
            entered.countDown();
            try (var suspended = table.game.getAction().suspendState()) {
                if (!release.await(10, TimeUnit.SECONDS)) throw new AssertionError("Queue barrier expired");
            } catch (InterruptedException error) { Thread.currentThread().interrupt(); throw new AssertionError(error); }
        });
        assertTrue(entered.await(5, TimeUnit.SECONDS));
        long armed = version(input);
        try {
            input.session().action(request(input, Map.of("action", "passPriority", "stackInterruptionVersion", armed)));
            var answer = worker.submit(() -> table.seat(0).gui().showConfirmDialog("Required choice", "Rules choice", "Continue", "Cancel"));
            var choice = driver.until(next -> next.playerId() != actor && text(next.prompt(), "kind").equals("choice"));
            assertTrue(version(choice) > armed);
            driver.answer(choice, Map.of("choices", List.of(0))); assertTrue(answer.get(3, TimeUnit.SECONDS));
        } finally { release.countDown(); worker.shutdownNow(); }
        var restored = ready(driver, next -> next.playerId() == actor && priority(next));
        assertEquals(text(restored.prompt(), "id"), text(input.prompt(), "id"));
        assertEquals(ids(restored.state()), ids(input.state()));
        assertTrue(restored.state().getAsJsonArray("notices").toString().contains("Resolve these stopped"));
        return restored;
    }

    private static void castDraw(Driver driver, Input input, int actor, int amount) throws Exception {
        driver.use(input, card(input.state(), "Hand", "Blue Sun's Zenith"), "cast");
        var next = ready(driver, state -> state.playerId() == actor
                && (text(state.prompt(), "kind").equals("number") || text(state.prompt(), "inputType").startsWith("InputPayMana")));
        if (text(next.prompt(), "kind").equals("number")) {
            driver.answer(next, Map.of("value", String.valueOf(amount)));
            next = ready(driver, state -> state.playerId() == actor && text(state.prompt(), "inputType").startsWith("InputPayMana"));
        }
        int used = 0;
        while (text(next.prompt(), "inputType").startsWith("InputPayMana")) {
            driver.use(next, untappedIsland(next.state()), "activate", Map.of("U", 1)); used++;
            next = ready(driver, state -> state.playerId() == actor && (text(state.prompt(), "inputType").startsWith("InputPayMana") || priority(state)));
        }
        assertEquals(used, amount + 3);
    }

    private static Input ready(Driver driver, Predicate<Input> stop) throws Exception {
        while (true) {
            var input = driver.until(next -> stop.test(next) || text(next.prompt(), "inputType").equals("InputSelectTargets")
                    || next.prompt().has("ordered") && next.prompt().get("ordered").getAsBoolean()
                    || next.prompt().has("librarySearch"));
            if (stop.test(input)) return input;
            if (text(input.prompt(), "inputType").equals("InputSelectTargets"))
                driver.answer(input, Map.of("action", "player", "playerId", input.playerId()));
            else if (input.prompt().has("librarySearch")) driver.answer(input, Map.of("action", "cancel"));
            else driver.answer(input, Map.of("choices", input.prompt().getAsJsonArray("choices").asList().stream()
                    .map(value -> value.getAsJsonObject().get("index").getAsInt()).toList()));
        }
    }

    private static Deck deck(String name, String main) {
        var deck = new Deck(name); deck.getMain().add(StaticData.instance().getCommonCards().getCard(main), 99);
        deck.getOrCreate(DeckSection.Commander).add(StaticData.instance().getCommonCards().getCard("Progenitus")); return deck;
    }
    private static boolean priority(Input input) { return text(input.prompt(), "inputType").equals("InputPassPriority"); }
    private static long version(Input input) { return input.state().get("stackInterruptionVersion").getAsLong(); }
    private static Set<String> ids(JsonObject state) {
        var ids = new LinkedHashSet<String>();
        for (var item : state.getAsJsonArray("stack")) assertTrue(ids.add(text(item.getAsJsonObject(), "id")), "Stack occurrences must be distinct");
        return ids;
    }
    private static JsonObject card(JsonObject state, String zoneName, String name) {
        return zone(viewer(state), zoneName).getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "name").equals(name)).findFirst().orElseThrow();
    }
    private static JsonObject untappedIsland(JsonObject state) {
        return zone(viewer(state), "Battlefield").getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "name").equals("Island") && !value.get("tapped").getAsBoolean()).findFirst().orElseThrow();
    }
    private static int counters(JsonObject state, String name) {
        var counters = card(state, "Battlefield", name).getAsJsonObject("counters");
        return counters.has("+1/+1") ? counters.get("+1/+1").getAsInt() : 0;
    }
    private static JsonObject request(Input input, Map<String, ?> answer) {
        var request = JSON.toJsonTree(answer).getAsJsonObject();
        request.addProperty("sessionId", text(input.state(), "id")); request.addProperty("promptId", text(input.prompt(), "id")); return request;
    }
}
