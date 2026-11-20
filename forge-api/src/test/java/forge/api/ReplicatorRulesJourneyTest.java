package forge.api;

import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import forge.game.zone.ZoneType;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.FutureTask;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicReference;

import static forge.api.RulesScenario.*;
import static forge.api.CardDataRules.*;
import static org.testng.Assert.*;

/** Actual casts, target restrictions, payment and token-copy resolution. */
public class ReplicatorRulesJourneyTest {
    @Test(timeOut = 90000)
    public void entersCreatesFoodAndOnlyControlledTokensCanBeCopiedTapped() throws Exception {
        // The inert seats must survive a normal first-turn draw/cleanup while
        // this journey waits for the actor's next own sorcery-speed window.
        try (var game = new RulesScenario("replicator-food-copy", Map.of(
                0, List.of("Wastes", "Wastes", "Spellbook"), 1, List.of("Wastes", "Wastes"),
                2, List.of("Spellbook"), 3, List.of("Spellbook")),
                "Replicator", "Rhys the Redeemed")) {
            var main = main(game, 0, 1);
            int actor = main.state().get("viewerId").getAsInt();
            int opponent = game.state(1).get("viewerId").getAsInt();
            assertTrue(food(main.state(), actor).isEmpty());
            main = fund(game, main, 2);
            invoke(game, main, cards(viewer(main.state()), "Hand", "Replicator").get(0));
            var entered = settle(game, 0);
            assertEquals(food(entered.state(), actor).size(), 1, "The actual Replicator ETB creates exactly one Food");
            var source = cards(viewer(entered.state()), "Battlefield", "Replicator").get(0);
            String sourceId = text(source, "visualId"), originalId = text(food(entered.state(), actor).get(0), "visualId");
            assertFalse(food(entered.state(), actor).get(0).get("tapped").getAsBoolean());
            save(game, "01-real-etb.json", entered.state());

            // Produce the foreign token through another real cast, rather than
            // injecting a token or editing a running battlefield for a negative.
            var other = fund(game, main(game, 1, 1), 2);
            invoke(game, other, cards(viewer(other.state()), "Hand", "Replicator").get(0));
            var otherEntered = settle(game, 1);
            assertEquals(food(otherEntered.state(), opponent).size(), 1);
            String foreignId = text(food(otherEntered.state(), opponent).get(0), "visualId");

            main = fund(game, main(game, 0, 2), 2);
            source = occurrence(main.state(), sourceId);
            assertFalse(source.get("tapped").getAsBoolean());
            assertEquals(viewer(main.state()).getAsJsonObject("mana").get("C").getAsInt(), 2);
            invoke(game, main, source);
            var targets = game.until(input -> input.seat() == 0 && text(input.prompt(), "inputType").equals("InputSelectTargets"));
            var original = occurrence(targets.state(), originalId);
            var foreign = occurrence(targets.state(), foreignId);
            var nontoken = cards(viewer(targets.state()), "Battlefield", "Spellbook").get(0);
            assertTrue(original.get("selectable").getAsBoolean());
            assertFalse(foreign.get("selectable").getAsBoolean(), "The opponent-controlled token is not a legal target");
            assertFalse(nontoken.get("selectable").getAsBoolean(), "An owned nontoken is not a legal target");
            var targetIds = List.of(originalId, foreignId, text(nontoken, "visualId"));
            targets = rejectAtFrontier(game, targets, actor, sourceId, foreignId, targetIds, true);
            targets = rejectAtFrontier(game, targets, actor, sourceId, text(nontoken, "visualId"), targetIds, false);
            original = occurrence(targets.state(), originalId);
            save(game, "02-current-targets.json", targets.state());
            assertTrue(game.answer(targets, Map.of("action", "card", "key", text(original, "key"))));
            var copied = settle(game, 0);
            var ownFood = food(copied.state(), actor);
            assertEquals(ownFood.size(), 2); assertEquals(food(copied.state(), opponent).size(), 1);
            var copy = ownFood.stream().filter(card -> !text(card, "visualId").equals(originalId)).findFirst().orElseThrow();
            var retained = occurrence(copied.state(), originalId);
            assertNotEquals(text(copy, "visualId"), originalId);
            assertEquals(text(copy, "gamePieceType"), "token");
            assertTrue(text(copy, "type").contains("Artifact") && text(copy, "type").contains("Food"));
            assertEquals(text(copy, "type"), text(retained, "type"));
            assertEquals(text(copy, "name"), text(retained, "name"));
            assertEquals(text(copy, "text"), text(retained, "text"));
            assertTrue(copy.get("tapped").getAsBoolean(), "The first settled copy publication is already tapped");
            assertFalse(retained.get("tapped").getAsBoolean(), "The original Food was not tapped to manufacture this result");
            assertTrue(occurrence(copied.state(), sourceId).get("tapped").getAsBoolean(), "Replicator paid its actual tap cost");
            assertEquals(viewer(copied.state()).getAsJsonObject("mana").get("C").getAsInt(), 0, "The actual activation spent two mana");
            assertEquals(text(copied.state(), "phaseKey"), "MAIN1");
            assertEquals(copied.state().get("turn"), main.state().get("turn"), "No later untap phase supplies the tapped-copy witness");
            assertTrue(game.table.game.getAction().readState(() -> game.table.game.getCardsIn(ZoneType.Battlefield).stream()
                    .filter(card -> card.isToken() && card.getType().hasSubtype("Food") && card.getController().getId() == actor)
                    .allMatch(card -> card.getOwner().getId() == actor)), "Both real controlled Food tokens retain the actor as owner");
            save(game, "03-resolved-copy.json", copied.state());
            var spectator = HeadlessPlatform.query(game.table, () -> new Gson().toJsonTree(SpectatorObservation.project(game.table,
                    ((Number)game.session(0).state().get("boardRevision")).longValue(), null)).getAsJsonObject());
            assertEquals(food(spectator, actor).size(), 2); assertEquals(food(spectator, opponent).size(), 1);
            assertTrue(occurrence(spectator, text(copy, "visualId")).get("tapped").getAsBoolean());
            save(game, "04-public-copy.json", spectator);
        }
    }

    private static Input rejectAtFrontier(RulesScenario game, Input input, int actor, String sourceId,
                                         String rejectedId, List<String> targetIds, boolean controlled) throws Exception {
        var captured = new AtomicReference<Input>(input);
        var proof = new AtomicReference<Map<String, Object>>();
        var producerFailure = new AtomicReference<Throwable>();
        var entered = new CountDownLatch(1);
        var release = new CountDownLatch(1);
        var bodyExited = new CountDownLatch(1);
        var started = new CountDownLatch(1);
        var call = new FutureTask<Input>(() -> {
            started.countDown(); // Does not claim the caller is physically parked.
            return rejectUnchanged(game, captured.get(), actor, sourceId, rejectedId, targetIds, controlled, proof);
        });
        var caller = new Thread(call, "Replicator rejection caller");
        caller.setDaemon(true);
        Throwable failure = null;
        boolean enqueued = false, launched = false;
        try {
            if (controlled) {
                // Identify the real owner while enqueueing, then release its
                // state lock before waiting for the ordinary UI callback.
                game.table.inState(() -> HeadlessPlatform.later(() -> {
                    try {
                        var state = game.state(input.seat());
                        captured.set(sameTargets(input, state, targetIds));
                        entered.countDown();
                        assertTrue(release.await(10, TimeUnit.SECONDS), "Coordinator releases the ordinary callback");
                    } catch (Throwable error) {
                        producerFailure.set(error);
                        if (error instanceof InterruptedException) Thread.currentThread().interrupt();
                    } finally {
                        entered.countDown();
                        bodyExited.countDown(); // BODY exit, not trailing publication completion.
                    }
                }));
                enqueued = true;
                assertTrue(entered.await(10, TimeUnit.SECONDS), "The ordinary callback captured the current target input");
                if (producerFailure.get() != null) throwFailure(producerFailure.get());
            }
            caller.start(); launched = true;
            assertTrue(started.await(10, TimeUnit.SECONDS), "The rejection caller started");
            release.countDown();
            // The callback's normal trailing publishInput still owns state,
            // so the caller's readState can enter only after that publication.
            var after = call.get(10, TimeUnit.SECONDS);
            if (producerFailure.get() != null) throwFailure(producerFailure.get());
            return after;
        } catch (Throwable error) {
            failure = error instanceof ExecutionException && error.getCause() != null ? error.getCause() : error;
            throwFailure(failure);
            throw new AssertionError("unreachable");
        } finally {
            release.countDown();
            try {
                if (launched) {
                    caller.join(TimeUnit.SECONDS.toMillis(10));
                    assertFalse(caller.isAlive(), "The rejection caller terminates before fixture teardown");
                }
                if (enqueued) assertTrue(bodyExited.await(10, TimeUnit.SECONDS), "The producer callback body terminates");
            } catch (Throwable cleanup) {
                if (failure != null) failure.addSuppressed(cleanup);
                else { failure = cleanup; throwFailure(cleanup); }
            } finally {
                var record = new LinkedHashMap<String, Object>();
                record.put("controlled", controlled);
                record.put("capturedBefore", captured.get().state());
                record.put("transaction", proof.get());
                record.put("producerFailure", producerFailure.get() == null ? null : producerFailure.get().toString());
                record.put("failure", failure == null ? null : failure.toString());
                record.put("progress", Map.of("entered", entered.getCount() == 0, "started", started.getCount() == 0,
                        "bodyExited", bodyExited.getCount() == 0));
                record.put("latchScope", "Progress only: started does not mean physically parked; bodyExited does not mean trailing publication completed");
                record.put("scope", "Controlled legal publication schedule; not attribution of the historical missing170 observation");
                try { save(game, controlled ? "02a-controlled-rejection.json" : "02b-nontoken-rejection.json", record); }
                catch (Throwable diagnostic) {
                    if (failure != null) failure.addSuppressed(diagnostic);
                    else throwFailure(diagnostic);
                }
            }
        }
    }

    private static Input rejectUnchanged(RulesScenario game, Input captured, int actor, String sourceId,
                                         String rejectedId, List<String> targetIds, boolean controlled,
                                         AtomicReference<Map<String, Object>> proof) {
        return game.table.game.getAction().readState(() -> {
            var record = new LinkedHashMap<String, Object>(); proof.set(record);
            var current = sameTargets(captured, game.state(captured.seat()), targetIds);
            record.put("currentBefore", current.state());
            if (controlled) assertTrue(current.state().get("boardRevision").getAsLong() > captured.state().get("boardRevision").getAsLong(),
                    "The normal callback tail publishes before rejection validation owns the state boundary");
            assertEquals(player(current.state(), actor).get("mana"), player(captured.state(), actor).get("mana"),
                    "Publication before rejection cannot change the captured mana pool");
            assertEquals(occurrence(current.state(), sourceId).get("tapped"), occurrence(captured.state(), sourceId).get("tapped"),
                    "Publication before rejection cannot change the captured source tap state");
            var rejected = occurrence(current.state(), rejectedId);
            assertFalse(text(rejected, "key").isEmpty(), "Use only an actually published current card key for the negative");
            assertFalse(rejected.get("selectable").getAsBoolean());
            var request = request(current, Map.of("action", "card", "key", text(rejected, "key")));
            record.put("request", request);
            IllegalArgumentException error = null;
            JsonObject after;
            try {
                error = expectThrows(IllegalArgumentException.class, () -> game.session(current.seat()).action(request));
            } finally {
                after = new GsonBuilder().serializeNulls().create().toJsonTree(game.session(current.seat()).state()).getAsJsonObject();
                record.put("error", error == null ? null : error.toString());
                record.put("after", after);
            }
            assertEquals(error.getMessage(), "That card cannot be selected for this choice");
            var next = sameTargets(current, after, targetIds);
            assertEquals(footprint(after, actor, sourceId), footprint(current.state(), actor, sourceId),
                    "The actual rejected transaction preserves board, mana and source tap state");
            // Keep the baseline explicit: the witness records both the earlier
            // captured input and the transaction's actual current-before.
            var before = footprint(current.state(), actor, sourceId);
            assertEquals(footprint(after, actor, sourceId), before, "A rejected target cannot pay/tap/change the board");
            return next;
        });
    }

    private static Input sameTargets(Input expected, JsonObject state, List<String> targetIds) {
        assertEquals(text(state, "status"), "playing");
        assertEquals(state.get("id"), expected.state().get("id"));
        assertEquals(state.get("viewerId"), expected.state().get("viewerId"));
        assertTrue(state.has("prompt") && !state.get("prompt").isJsonNull(), "A rejected action cannot consume the prompt asynchronously");
        var prompt = state.getAsJsonObject("prompt");
        assertEquals(text(prompt, "id"), text(expected.prompt(), "id"));
        assertEquals(text(prompt, "inputType"), "InputSelectTargets");
        for (String id : targetIds) {
            var actual = occurrence(state, id); var prior = occurrence(expected.state(), id);
            assertEquals(text(actual, "key"), text(prior, "key"));
            assertEquals(actual.get("selectable"), prior.get("selectable"));
        }
        return new Input(expected.seat(), state, prompt);
    }

    private static void throwFailure(Throwable failure) throws Exception {
        if (failure instanceof Error error) throw error;
        if (failure instanceof Exception error) throw error;
        throw new AssertionError(failure);
    }

    private static List<JsonObject> food(JsonObject state, int playerId) {
        return cards(player(state, playerId), "Battlefield", null).stream()
                .filter(card -> text(card, "gamePieceType").equals("token") && text(card, "type").contains("Food")).toList();
    }
    private static Object footprint(JsonObject state, int actor, String source) {
        return List.of(state.get("boardRevision"), player(state, actor).get("mana").deepCopy(), occurrence(state, source).get("tapped"));
    }
}

/** Shared current-input helpers for the bounded card-data rules journeys. */
final class CardDataRules {
    private static final Gson JSON = new GsonBuilder().serializeNulls().setPrettyPrinting().create();
    private CardDataRules() { }
    static boolean priority(Input input) { return text(input.prompt(), "inputType").equals("InputPassPriority"); }
    static Input main(RulesScenario game, int seat, int minimumTurn) throws Exception {
        return game.until(input -> input.seat() == seat && priority(input) && text(input.state(), "phaseKey").equals("MAIN1")
                && input.state().get("viewerId").equals(input.state().get("activePlayerId"))
                && input.state().get("turn").getAsInt() >= minimumTurn && input.state().getAsJsonArray("stack").isEmpty());
    }
    static List<JsonObject> cards(JsonObject player, String zone, String name) {
        return zone(player, zone).getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                .filter(card -> name == null || text(card, "name").equals(name)).toList();
    }
    static JsonObject occurrence(JsonObject state, String id) {
        return state.getAsJsonArray("players").asList().stream().map(value -> value.getAsJsonObject())
                .flatMap(player -> cards(player, "Battlefield", null).stream()).filter(card -> text(card, "visualId").equals(id)).findFirst().orElseThrow();
    }
    static void invoke(RulesScenario game, Input input, JsonObject source) {
        var actions = JSON.toJsonTree(game.session(input.seat()).sourceActions(request(input, Map.of("key", text(source, "key"))))).getAsJsonObject();
        assertEquals(actions.getAsJsonArray("actions").size(), 1, "The controlled source has one real current action");
        var action = actions.getAsJsonArray("actions").get(0).getAsJsonObject();
        assertTrue(game.answer(input, Map.of("action", "useSourceAction", "key", text(actions, "sourceCardKey"), "actionId", text(action, "id"))));
    }
    static Input fund(RulesScenario game, Input input, int amount) throws Exception {
        int seat = input.seat();
        for (int paid = 0; paid < amount; paid++) {
            var land = cards(viewer(input.state()), "Battlefield", "Wastes").stream().filter(card -> !card.get("tapped").getAsBoolean()).findFirst().orElseThrow();
            invoke(game, input, land);
            input = main(game, seat, input.state().get("turn").getAsInt());
        }
        assertEquals(viewer(input.state()).getAsJsonObject("mana").get("C").getAsInt(), amount);
        return input;
    }
    static Input settle(RulesScenario game, int seat) throws Exception {
        for (int count = 0; count < 8; count++) {
            var input = game.until(current -> current.seat() == seat && (text(current.prompt(), "inputType").startsWith("InputPayMana")
                    || text(current.prompt(), "inputType").equals("InputSelectTargets")
                    || priority(current) && current.state().getAsJsonArray("stack").isEmpty()));
            if (priority(input)) return input;
            assertTrue(input.prompt().get("okEnabled").getAsBoolean(), "Only a funded bill or completed real target choice may continue");
            assertTrue(game.answer(input, Map.of("action", "ok")));
        }
        throw new AssertionError("The actual card-data action did not settle within eight continuations");
    }
    static void save(RulesScenario game, String file, Object value) throws Exception {
        Files.writeString(game.evidence.resolve(file), JSON.toJson(value));
    }
}
