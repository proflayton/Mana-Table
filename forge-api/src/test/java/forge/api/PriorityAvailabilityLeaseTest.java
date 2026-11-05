package forge.api;

import com.google.common.eventbus.Subscribe;
import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import forge.ai.AvailableActions;
import forge.card.MagicColor;
import forge.game.card.CardCollectionView;
import forge.game.event.GameEvent;
import forge.game.mana.Mana;
import forge.game.player.Player;
import forge.game.zone.PlayerZone;
import forge.game.zone.ZoneType;
import forge.gamemodes.match.input.InputPassPriority;
import forge.player.PlayerControllerHuman;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Real current inputs and controlled rules-event timing. No renderer-derived
 * permission or replacement rules simulator supplies the no-response decision. */
public class PriorityAvailabilityLeaseTest {
    private static final Gson JSON = new GsonBuilder().serializeNulls().setPrettyPrinting().create();

    @Test(timeOut = 90000)
    public void aVerifiedUnchangedNoResponseWindowStillCommitsItsOneOrdinaryPass() throws Exception {
        try (var game = scenario("priority-lease-current", "Manalith", 2)) {
            var main = main(game); var input = actual(game);
            assertTrue(main.prompt().get("canAutoPass").getAsBoolean());
            assertTrue(input.canAutoPass());
            game.session(0).action(request(main, Map.of("action", "passIfNoResponse")));
            HeadlessPlatform.query(game.table, () -> null);
            assertFalse(input.isPriorityActive(), "An unchanged certified lease must pass rather than fail after its own markBusy");
            assertNull(game.session(0).state().get("error"));
        }
    }

    @Test(timeOut = 90000)
    public void aRealResourceChangeAfterAdmissionCannotConsumeTheSameUnansweredInput() throws Exception {
        try (var game = scenario("priority-lease-queued-resource", "Manalith", 2)) {
            var main = main(game); var input = actual(game);
            assertTrue(main.prompt().get("canAutoPass").getAsBoolean());
            try (var barrier = new InputBarrier(game)) {
                game.session(0).action(request(main, Map.of("action", "passIfNoResponse")));
                game.table.game.getAction().withState(() -> {
                    assertTrue(input.isPriorityActive()); assertSame(controller(game).getInputQueue().getInput(), input);
                    addGreen(game);
                    assertEquals(actor(game).getManaPool().totalMana(), 1);
                });
                barrier.release();
                HeadlessPlatform.query(game.table, () -> null);
                assertTrue(input.isPriorityActive(), "Queued no-response permission must not survive a real same-input resource change");
                assertSame(controller(game).getInputQueue().getInput(), input);
                assertSame(controller(game).getInputProxy().getInput(), input);
            }
            var restored = current(game);
            assertEquals(text(restored.prompt(), "id"), text(main.prompt(), "id"));
            assertEquals(restored.state().get("turn"), main.state().get("turn"));
            assertEquals(text(restored.state(), "phaseKey"), text(main.state(), "phaseKey"));
            assertFalse(restored.prompt().get("canAutoPass").getAsBoolean());
            game.rejected(restored, "passIfNoResponse", "This response window needs your decision");
            assertTrue(game.answer(restored, Map.of("action", "card", "key", text(card(restored.state(), "Hand", "Manalith"), "key"))));
            var payment = game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").startsWith("InputPayMana"));
            assertTrue(payment.prompt().get("okEnabled").getAsBoolean(), "The added real mana now funds Manalith with the two untapped Forests");
            assertTrue(game.answer(payment, Map.of("action", "ok")));
            var resolved = game.until(next -> next.seat() == 0 && priority(next) && next.state().getAsJsonArray("stack").isEmpty()
                    && zone(viewer(next.state()), "Battlefield").getAsJsonArray("cards").asList().stream()
                        .anyMatch(value -> text(value.getAsJsonObject(), "name").equals("Manalith")));
            assertEquals(actor(game).getManaPool().totalMana(), 0);
            assertEquals(zone(viewer(resolved.state()), "Battlefield").getAsJsonArray("cards").asList().stream()
                    .map(value -> value.getAsJsonObject()).filter(value -> text(value, "name").equals("Forest") && value.get("tapped").getAsBoolean()).count(), 2L);
            Files.writeString(game.evidence.resolve("restored-and-real-cast.json"), JSON.toJson(List.of(main.state(), restored.state(), resolved.state())));
        }
    }

    @Test(timeOut = 90000)
    public void republishingTheSameInputCannotCertifyItsOldScanAfterARealEligibilityChange() throws Exception {
        try (var game = scenario("priority-lease-republished-resource", "Manalith", 2)) {
            var main = main(game); var input = actual(game);
            assertTrue(input.canAutoPass());
            var refreshed = HeadlessPlatform.query(game.table, () -> {
                addGreen(game); assertSame(controller(game).getInputQueue().getInput(), input);
                assertTrue(input.isPriorityActive()); game.table.publish(); return game.state(0);
            });
            assertEquals(text(refreshed.getAsJsonObject("prompt"), "id"), text(main.prompt(), "id"));
            assertFalse(refreshed.getAsJsonObject("prompt").get("canAutoPass").getAsBoolean(),
                    "A fresh publication is not a fresh availability scan for the same input");
            game.rejected(new Input(0, refreshed, refreshed.getAsJsonObject("prompt")), "passIfNoResponse", "This response window needs your decision");
            assertTrue(input.isPriorityActive()); assertEquals(actor(game).getManaPool().totalMana(), 1);
            Files.writeString(game.evidence.resolve("same-input-republished.json"), JSON.toJson(List.of(main.state(), refreshed)));
        }
    }

    @Test(timeOut = 90000)
    public void anUnknownOwnerAvailabilityFlagAfterAdmissionHoldsWithoutInventingAResponse() throws Exception {
        try (var game = scenario("priority-lease-queued-unknown", "Manalith", 2)) {
            var main = main(game); var input = actual(game);
            try (var barrier = new InputBarrier(game)) {
                game.session(0).action(request(main, Map.of("action", "passIfNoResponse")));
                game.table.game.getAction().withState(() -> input.setAvailabilityVerified(false));
                barrier.release(); HeadlessPlatform.query(game.table, () -> null);
                assertTrue(input.isPriorityActive(), "A still-owned input with unknown availability cannot be auto-passed");
            }
            var restored = current(game); assertFalse(restored.prompt().get("canAutoPass").getAsBoolean());
            assertEquals(actor(game).getManaPool().totalMana(), 0);
            assertTrue(game.answer(restored, Map.of("action", "passPriority")), "Explicit manual passing is distinct from a no-response certificate");
        }
    }

    @Test(timeOut = 90000)
    public void anUnpayableColoredInstantStillProducesAUsableNoResponseCertificate() throws Exception {
        try (var game = scenario("priority-lease-unpayable-color", "Lightning Bolt", 2)) {
            var main = main(game); var input = actual(game);
            assertTrue(main.prompt().get("canAutoPass").getAsBoolean()); assertTrue(input.canAutoPass());
            assertTrue(game.answer(main, Map.of("action", "passIfNoResponse")));
            HeadlessPlatform.query(game.table, () -> null); assertFalse(input.isPriorityActive());
        }
    }

    @Test(timeOut = 90000)
    public void ordinaryFloatingManaPaymentPreviewPreservesExactUnitsAndCanCertifyItsCleanScan() throws Exception {
        try (var game = scenario("priority-lease-floating-preview", "Cultivate", 1)) {
            main(game); var input = actual(game);
            HeadlessPlatform.query(game.table, () -> {
                addGreen(game); var before = units(actor(game)); var events = new RuleEvents();
                game.table.game.subscribeToEvents(events);
                try {
                    assertTrue(controller(game).refreshRestoredPriority(input));
                    var after = units(actor(game)); assertEquals(after.size(), before.size());
                    for (int index = 0; index < before.size(); index++) assertSame(after.get(index), before.get(index));
                    assertTrue(events.values.isEmpty(), "A speculative partial mana payment/refund must not publish rules changes");
                    assertTrue(input.canAutoPass(), "Known unpayable fixed costs still permit a clean no-response scan");
                    game.table.publish(); assertTrue(game.state(0).getAsJsonObject("prompt").get("canAutoPass").getAsBoolean());
                } finally { game.table.game.unsubscribeFromEvents(events); }
                return null;
            });
        }
    }

    @Test(timeOut = 90000)
    public void aRealMutationInsideTheActualAvailabilityScanLeavesUnknownUntilACleanRescan() throws Exception {
        try (var game = scenario("priority-lease-mutating-scan", "Lightning Bolt", 2)) {
            main(game); var input = actual(game);
            HeadlessPlatform.query(game.table, () -> {
                try (var hook = new DuringAvailabilityScan(actor(game), () -> addGreen(game))) {
                    assertTrue(controller(game).refreshRestoredPriority(input)); assertTrue(hook.fired);
                    assertEquals(actor(game).getManaPool().totalMana(), 1);
                    assertFalse(input.canAutoPass(), "Even an apparently empty result cannot certify a scan whose real resources changed while it ran");
                }
                var before = units(actor(game));
                assertTrue(controller(game).refreshRestoredPriority(input));
                assertTrue(input.canAutoPass(), "A later clean real scan can certify the same unanswered input without a permanent disable");
                assertSame(units(actor(game)).get(0), before.get(0)); return null;
            });
        }
    }

    private static RulesScenario scenario(String name, String spell, int forests) throws Exception {
        var own = new ArrayList<String>(java.util.Collections.nCopies(forests, "Forest")); own.add("Spellbook");
        return new RulesScenario(name, Map.of(0, own, 1, List.of("Spellbook"), 2, List.of("Spellbook"), 3, List.of("Spellbook")), spell, "Progenitus");
    }
    private static boolean priority(Input input) { return text(input.prompt(), "inputType").equals("InputPassPriority"); }
    private static Input main(RulesScenario game) throws Exception { return game.until(input -> input.seat() == 0 && priority(input) && text(input.state(), "phaseKey").equals("MAIN1")); }
    private static Player actor(RulesScenario game) { return game.table.game.getRegisteredPlayers().get(0); }
    private static PlayerControllerHuman controller(RulesScenario game) { return (PlayerControllerHuman)actor(game).getController(); }
    private static InputPassPriority actual(RulesScenario game) throws Exception { return HeadlessPlatform.query(game.table, () -> (InputPassPriority)controller(game).getInputQueue().getInput()); }
    private static Input current(RulesScenario game) { var state = game.state(0); assertTrue(state.get("prompt").isJsonObject()); return new Input(0, state, state.getAsJsonObject("prompt")); }
    private static JsonObject card(JsonObject state, String zone, String name) { return zone(viewer(state), zone).getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject()).filter(value -> text(value, "name").equals(name)).findFirst().orElseThrow(); }
    private static void addGreen(RulesScenario game) { var source = actor(game).getCardsIn(ZoneType.Battlefield).stream().filter(card -> card.getName().equals("Forest")).findFirst().orElseThrow(); actor(game).getManaPool().addMana(new Mana(MagicColor.GREEN, source, source.getManaAbilities().get(0).getManaPart(), actor(game))); }
    private static List<Mana> units(Player player) { var result = new ArrayList<Mana>(); player.getManaPool().forEach(result::add); return result; }

    private static final class InputBarrier implements AutoCloseable {
        private final CountDownLatch release = new CountDownLatch(1);
        InputBarrier(RulesScenario game) throws InterruptedException {
            var entered = new CountDownLatch(1);
            HeadlessPlatform.later(() -> {
                entered.countDown();
                try (var suspended = game.table.game.getAction().suspendState()) {
                    if (!release.await(10, TimeUnit.SECONDS)) throw new AssertionError("Input queue barrier expired");
                } catch (InterruptedException failure) { Thread.currentThread().interrupt(); throw new AssertionError(failure); }
            });
            assertTrue(entered.await(5, TimeUnit.SECONDS));
        }
        void release() { release.countDown(); }
        @Override public void close() { release(); }
    }
    public static final class RuleEvents {
        final List<GameEvent> values = new ArrayList<>();
        @Subscribe public void event(GameEvent event) { values.add(event); }
    }
    private static final class DuringAvailabilityScan implements AutoCloseable {
        private final Map<ZoneType, PlayerZone> zones;
        private final PlayerZone original;
        boolean fired;
        @SuppressWarnings("unchecked")
        DuringAvailabilityScan(Player actor, Runnable mutation) throws Exception {
            var field = Player.class.getDeclaredField("zones"); field.setAccessible(true);
            zones = (Map<ZoneType, PlayerZone>) field.get(actor); original = zones.get(ZoneType.Battlefield);
            zones.put(ZoneType.Battlefield, new PlayerZone(ZoneType.Battlefield, actor) {
                @Override public CardCollectionView getCards(boolean filter) {
                    var cards = original.getCards(filter);
                    if (!fired && StackWalker.getInstance().walk(frames -> frames.anyMatch(frame ->
                            frame.getClassName().equals(AvailableActions.class.getName()) && frame.getMethodName().equals("scanAll")))) {
                        fired = true; close(); mutation.run();
                    }
                    return cards;
                }
            });
        }
        @Override public void close() { zones.put(ZoneType.Battlefield, original); }
    }
}
