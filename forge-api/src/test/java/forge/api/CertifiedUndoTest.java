package forge.api;

import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import forge.game.card.Card;
import forge.game.mana.Mana;
import forge.game.zone.ZoneType;
import org.testng.annotations.DataProvider;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.concurrent.atomic.AtomicLong;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Normal production actions, independent live-ledger checks and all audience
 * copies. No fixture mutates an already running game to create the positive path. */
public class CertifiedUndoTest {
    private static final Gson JSON = new Gson();

    @DataProvider public Object[][] sources() {
        return new Object[][]{{"Forest", "G", 1}, {"Sol Ring", "C", 2}};
    }

    @Test(dataProvider = "sources", groups = "canonical-interaction", timeOut = 90000)
    public void oneCompleteActivationRestoresItsPoolAndInternalLedgers(String name, String color, int amount) throws Exception {
        try (var game = new RulesScenario("certified-undo-" + color, Map.of(0, List.of(name)))) {
            var initial = main(game);
            var rulesSource = source(game, name);
            var before = footprint(game, rulesSource);
            var active = activate(game, initial, name);
            assertEquals(text(active.state(), "phaseKey"), text(initial.state(), "phaseKey"));
            assertEquals(active.state().get("priorityPlayerId"), initial.state().get("priorityPlayerId"));
            assertFalse(active.prompt().get("canAutoPass").getAsBoolean(),
                    "The voluntary manual correction window cannot silently pass its retained priority");
            assertEquals(mana(active.state(), color), amount);
            assertTrue(card(active.state(), name).get("tapped").getAsBoolean());
            var offer = offer(active);
            assertEquals(text(offer, "sourceCardId"), text(card(active.state(), name), "visualId"));
            assertEquals(text(offer, "sourceName"), name);
            assertNotEquals(footprint(game, rulesSource), before, "A real mana activation changed its rules ledgers");

            HeadlessPlatform.query(game.table, () -> null);
            var read = JSON.toJsonTree(game.session(0).sourceActions(request(active,
                    Map.of("key", text(card(active.state(), name), "key"))))).getAsJsonObject();
            assertTrue(read.getAsJsonArray("actions").isEmpty(), "Reading a tapped source does not activate it again");
            assertEquals(game.state(0).getAsJsonObject("undo").getAsJsonObject("offer"), offer,
                    "Read-only source discovery does not consume or replace Undo");
            var request = undo(offer);
            assertTrue(game.answer(active, request));
            var restored = freshPriority(game, active);
            // An ordinary publication can supersede a captured restoration frame
            // without another rules action. Compare audiences from one cohort.
            game.table.publish();
            assertEquals(footprint(game, rulesSource), before,
                    "Undo restores internal activation/tap history and exact mana, not only the tapped flag");
            assertFalse(card(restored.state(), name).get("tapped").getAsBoolean());
            assertEquals(mana(restored.state(), color), 0);
            assertTrue(restored.state().get("boardRevision").getAsLong() > active.state().get("boardRevision").getAsLong());
            assertEquals(restored.state().getAsJsonObject("undo").get("restorationVersion").getAsLong(), 1);
            assertTrue(restored.state().getAsJsonObject("undo").get("offer").isJsonNull(), "One Undo exposes no older checkpoint");
            var available = JSON.toJsonTree(game.session(0).sourceActions(request(restored,
                    Map.of("key", text(card(restored.state(), name), "key"))))).getAsJsonObject();
            assertFalse(available.getAsJsonArray("actions").isEmpty(), "Fresh source actions see the actual restored untapped source");
            assertEquals(text(restored.state().getAsJsonObject("undo").getAsJsonObject("lastRestoration"), "actionId"), text(offer, "actionId"));
            assertTrue(restored.state().getAsJsonArray("activity").asList().stream()
                    .anyMatch(row -> text(row.getAsJsonObject(), "kind").equals("restoration")));
            var audienceJson = new GsonBuilder().serializeNulls().create();
            var cohort = game.table.testViews().stream()
                    .map(view -> audienceJson.toJsonTree(view).getAsJsonObject()).toList();
            Files.writeString(game.evidence.resolve("restored-cohort.json"), JSON.toJson(cohort));
            assertEquals(cohort.size(), 4);
            var currentActor = cohort.get(0);
            assertTrue(currentActor.get("boardRevision").getAsLong() > restored.state().get("boardRevision").getAsLong(),
                    "The regression deliberately advances publication after capturing the actor's frame");
            assertEquals(text(currentActor.getAsJsonObject("prompt"), "id"), text(restored.prompt(), "id"),
                    "Publication alone retains the same rules decision");
            assertEquals(currentActor.getAsJsonObject("undo").get("lastRestoration"),
                    restored.state().getAsJsonObject("undo").get("lastRestoration"),
                    "Later publications retain the original restoration marker");
            for (var other : cohort) {
                assertEquals(other.get("boardRevision"), currentActor.get("boardRevision"),
                        "All audiences in one publication cohort must agree exactly");
                assertEquals(other.getAsJsonObject("undo").get("restorationVersion").getAsLong(), 1);
                assertEquals(other.getAsJsonObject("undo").get("lastRestoration"),
                        currentActor.getAsJsonObject("undo").get("lastRestoration"));
                assertTrue(other.getAsJsonObject("undo").get("offer").isJsonNull(), "An observer never receives the actor's capability");
                var actor = player(other, restored.state().get("viewerId").getAsInt());
                assertEquals(actor.getAsJsonObject("mana").get(color).getAsInt(), 0);
                var source = zone(actor, "Battlefield").getAsJsonArray("cards").asList().stream()
                        .map(value -> value.getAsJsonObject()).filter(value -> text(value, "name").equals(name)).findFirst().orElseThrow();
                assertFalse(source.get("tapped").getAsBoolean(), "Every audience sees the restored untapped source");
            }
            rejectedUnchanged(game, () -> game.session(0).action(request(restored, request)),
                    "A consumed Undo does not mutate or expose another action");
            var again = activate(game, restored, name);
            assertEquals(mana(again.state(), color), amount, "Reactivation produces exactly one ordinary activation's mana");
            assertEquals(footprint(game, rulesSource).activationsThisTurn(), before.activationsThisTurn() + 1);
            assertEquals(footprint(game, rulesSource).activationsThisGame(), before.activationsThisGame() + 1);
            assertEquals(footprint(game, rulesSource).resolvedThisTurn(), before.resolvedThisTurn() + 1);
        }
    }

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void newerActionsReplaceAuthorityWithoutCreatingAnUndoChain() throws Exception {
        try (var game = new RulesScenario("certified-undo-latest-only", Map.of(0, List.of("Forest", "Sol Ring")))) {
            var first = activate(game, main(game), "Forest");
            var old = offer(first);
            var beforeSecond = footprint(game, source(game, "Sol Ring"));
            var second = activate(game, first, "Sol Ring");
            var current = offer(second);
            assertNotEquals(text(current, "id"), text(old, "id"));
            rejectedUnchanged(game, () -> game.session(0).action(request(second, undo(old))),
                    "A replaced Undo offer cannot change the committed state");
            assertTrue(game.answer(second, undo(current)));
            var restored = freshPriority(game, second);
            assertEquals(mana(restored.state(), "G"), 1, "The first activation remains committed");
            assertTrue(card(restored.state(), "Forest").get("tapped").getAsBoolean());
            assertEquals(mana(restored.state(), "C"), 0);
            assertFalse(card(restored.state(), "Sol Ring").get("tapped").getAsBoolean());
            assertEquals(footprint(game, source(game, "Sol Ring")), beforeSecond,
                    "Reversing the latest action preserves the previous activation's exact mana identities and histories");
            assertTrue(restored.state().getAsJsonObject("undo").get("offer").isJsonNull());
        }
    }

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void malformedForeignAndChangedOffersCannotApplyAnyInverse() throws Exception {
        try (var game = new RulesScenario("certified-undo-rejected-offers", Map.of(0, List.of("Sol Ring")))) {
            var active = activate(game, main(game), "Sol Ring");
            var offer = offer(active);
            for (var invalid : List.of(Map.of("offerId", "foreign"), Map.of("actionId", "foreign"),
                    Map.of("undoBarrierVersion", offer.get("barrierVersion").getAsLong() + 1), Map.of("undoBarrierVersion", 0.5))) {
                var body = new java.util.LinkedHashMap<String, Object>(undo(offer)); body.putAll(invalid);
                rejectedUnchanged(game, () -> game.session(0).action(request(active, body)),
                        "Rejected authority cannot untap a source or leave/duplicate mana");
            }
            var foreign = request(active, undo(offer));
            rejectedUnchanged(game, () -> game.session(1).action(foreign),
                    "Another seat cannot change the committed state with a foreign Undo");
            assertTrue(game.answer(active, Map.of("action", "passPriority", "expectedTask", "priority")));
            HeadlessPlatform.query(game.table, () -> null);
            assertNull(game.table.game.getReversibleManaAction().current(), "Accepted priority passing revokes before later rules events");
            assertTrue(card(game.state(0), "Sol Ring").get("tapped").getAsBoolean());
            assertEquals(mana(game.state(0), "C"), 2, "Passing cannot silently perform a reversal");
        }
    }

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void advancedControllerChoicesCannotEnterOrReopenTheCertifiedRoute() throws Exception {
        var flag = forge.game.player.PlayerController.FullControlFlag.ChooseManaPoolShard;
        try (var game = new RulesScenario("certified-undo-controller-choice-disabled", Map.of(0, List.of("Sol Ring")))) {
            var initial = main(game);
            var controller = game.table.game.getRegisteredPlayers().get(0).getController();
            game.table.game.getAction().withState(() -> controller.getFullControl().add(flag));
            invokeMana(game, initial, "Sol Ring");
            var completed = freshPriority(game, initial);
            assertEquals(mana(completed.state(), "C"), 2, "The ordinary manual activation remains playable");
            assertTrue(completed.state().getAsJsonObject("undo").get("offer").isJsonNull(),
                    "An advanced controller may open choices during scanning, so this route is not certified");
        }
        try (var game = new RulesScenario("certified-undo-controller-choice-changed", Map.of(0, List.of("Sol Ring")))) {
            var active = activate(game, main(game), "Sol Ring");
            var command = undo(offer(active));
            var controller = game.table.game.getRegisteredPlayers().get(0).getController();
            game.table.game.getAction().withState(() -> controller.getFullControl().add(flag));
            game.table.publish();
            assertTrue(game.state(0).getAsJsonObject("undo").get("offer").isJsonNull());
            game.table.game.getAction().withState(() -> controller.getFullControl().remove(flag));
            assertThrows(IllegalArgumentException.class, () -> game.session(0).action(request(active, command)));
            assertEquals(mana(game.state(0), "C"), 2);
            assertTrue(card(game.state(0), "Sol Ring").get("tapped").getAsBoolean());
            assertEquals(game.table.game.getReversibleManaAction().restorationVersion(), 0L,
                    "Returning a controller preference to its former value cannot resurrect an expired offer");
        }
    }

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void aQueuedTapTriggerPreventsAnOfferEvenBeforeResolution() throws Exception {
        try (var game = new RulesScenario("certified-undo-tap-trigger", Map.of(0, List.of("Forest", "Manabarbs")))) {
            var initial = main(game);
            invokeMana(game, initial, "Forest");
            var response = freshPriority(game, initial);
            assertEquals(mana(response.state(), "G"), 1);
            assertFalse(response.state().getAsJsonArray("stack").isEmpty(), "The normal tap trigger still enters the stack");
            assertTrue(response.state().getAsJsonObject("undo").get("offer").isJsonNull());
            assertEquals(response.state().getAsJsonObject("undo").get("restorationVersion").getAsLong(), 0);
        }
    }

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void undoKeepsTheLogicalDecisionClockAndItsElapsedBudget() throws Exception {
        var now = new AtomicLong(100_000);
        try (var game = new RulesScenario("certified-undo-tempo", Map.of(0, List.of("Sol Ring")),
                DecisionTempo.Settings.preset("standard", "pass"), now::get)) {
            var active = activate(game, main(game), "Sol Ring");
            var clock = clock(game.state(0));
            game.table.tempo.presented(active.state().get("viewerId").getAsInt(), text(clock, "id"));
            now.addAndGet(15_000);
            var elapsed = clock(game.state(0));
            assertTrue(game.answer(active, undo(offer(active))));
            var restored = freshPriority(game, active);
            var after = clock(restored.state());
            assertEquals(text(after, "id"), text(elapsed, "id"), "Fresh action handles preserve the existing logical clock");
            assertTrue(after.get("remainingMs").getAsLong() <= elapsed.get("remainingMs").getAsLong());
            assertEquals(text(after, "phase"), "thinking", "Undo cannot regain a presentation allowance");
        }
    }

    private static void rejectedUnchanged(RulesScenario game, Runnable rejected, String message) {
        HeadlessPlatform.query(game.table, () -> {
            synchronized (game.table.gate) {
                var before = game.state(0);
                assertThrows(IllegalArgumentException.class, rejected::run);
                assertEquals(game.state(0), before, message);
                return null;
            }
        });
    }

    private record Footprint(boolean tapped, int tappedThisTurn, int activationsThisTurn,
                             int activationsThisGame, int resolvedThisTurn, int stackActivations,
                             boolean tappedLand, List<Mana> pool, List<Mana> produced) { }
    private static Footprint footprint(RulesScenario game, Card source) throws Exception {
        return game.table.game.getAction().callState(() -> {
            var ability = source.getManaAbilities().get(0);
            var field = Card.class.getDeclaredField("tappedThisTurn"); field.setAccessible(true);
            var pool = new ArrayList<Mana>(); source.getController().getManaPool().forEach(pool::add);
            return new Footprint(source.isTapped(), field.getInt(source), source.getAbilityActivatedThisTurn(ability),
                    source.getAbilityActivatedThisGame(ability), source.getAbilityResolvedThisTurn(ability),
                    game.table.game.getStack().getAbilityActivatedThisTurn().size(),
                    source.getController().hasTappedLandForManaThisTurn(), List.copyOf(pool), List.copyOf(ability.getManaPart().getLastManaProduced()));
        });
    }
    private static Card source(RulesScenario game, String name) {
        return game.table.game.getRegisteredPlayers().get(0).getCardsIn(ZoneType.Battlefield).stream()
                .filter(card -> card.getName().equals(name)).findFirst().orElseThrow();
    }
    private static JsonObject clock(JsonObject state) { return state.getAsJsonObject("tempo").getAsJsonArray("clocks").get(0).getAsJsonObject(); }
    private static JsonObject card(JsonObject state, String name) {
        return zone(viewer(state), "Battlefield").getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "name").equals(name)).findFirst().orElseThrow();
    }
    private static int mana(JsonObject state, String color) { return viewer(state).getAsJsonObject("mana").get(color).getAsInt(); }
    private static Input main(RulesScenario game) throws Exception {
        return game.until(input -> input.seat() == 0 && text(input.prompt(), "inputType").equals("InputPassPriority")
                && text(input.state(), "phaseKey").equals("MAIN1"));
    }
    private static Input freshPriority(RulesScenario game, Input previous) throws Exception {
        return game.until(input -> input.seat() == 0 && text(input.prompt(), "inputType").equals("InputPassPriority")
                && !text(input.prompt(), "id").equals(text(previous.prompt(), "id")));
    }
    private static Input activate(RulesScenario game, Input current, String name) throws Exception {
        invokeMana(game, current, name);
        var active = freshPriority(game, current);
        var reversal = game.table.game.getReversibleManaAction();
        assertNotNull(reversal.current(), "The normal completed activation needs an authoritative offer: " + reversal.lastInvalidationReason());
        offer(active); return active;
    }
    private static void invokeMana(RulesScenario game, Input current, String name) {
        var source = card(current.state(), name);
        var actions = JSON.toJsonTree(game.session(0).sourceActions(request(current, Map.of("key", text(source, "key"))))).getAsJsonObject();
        var ability = actions.getAsJsonArray("actions").get(0).getAsJsonObject();
        assertTrue(game.answer(current, Map.of("action", "useSourceAction", "expectedTask", "priority",
                "key", text(actions, "sourceCardKey"), "actionId", text(ability, "id"))));
    }
    private static JsonObject offer(Input input) {
        var undo = input.state().getAsJsonObject("undo");
        assertNotNull(undo); assertFalse(undo.get("offer").isJsonNull(), "The first settled priority must contain the certified offer: " + undo);
        return undo.getAsJsonObject("offer");
    }
    private static Map<String, Object> undo(JsonObject offer) {
        return Map.of("action", "undoCertifiedAction", "expectedTask", "priority", "offerId", text(offer, "id"),
                "actionId", text(offer, "actionId"), "undoBarrierVersion", offer.get("barrierVersion").getAsLong());
    }
}
