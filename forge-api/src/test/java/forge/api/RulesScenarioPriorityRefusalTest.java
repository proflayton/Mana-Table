package forge.api;

import com.google.gson.JsonObject;
import forge.card.MagicColor;
import forge.game.mana.Mana;
import forge.game.player.Player;
import forge.game.zone.ZoneType;
import forge.gamemodes.match.input.InputPassPriority;
import forge.player.PlayerControllerHuman;
import org.testng.annotations.Test;

import java.util.List;
import java.util.Map;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** The real API's admission acknowledgment is not proof that its queued input
 * was consumed. Controlled resource timing exercises the driver, not new rules. */
public class RulesScenarioPriorityRefusalTest {
    @Test(timeOut = 90000)
    public void anOldPositiveAutoObservationCanYieldOnlyToAFreshOwnedPublication() throws Exception {
        try (var game = scenario("scenario-auto-admission-refusal")) {
            var old = main(game); var input = actual(game);
            assertTrue(old.prompt().get("canAutoPass").getAsBoolean());
            HeadlessPlatform.query(game.table, () -> { addGreen(game); game.table.publish(); return null; });
            var current = current(game);
            assertEquals(text(current.prompt(), "id"), text(old.prompt(), "id"));
            assertTrue(current.state().get("boardRevision").getAsLong() > old.state().get("boardRevision").getAsLong());
            assertFalse(current.prompt().get("canAutoPass").getAsBoolean());
            assertFalse(game.answer(old, Map.of("action", "passIfNoResponse")),
                    "A refused stale permission is not an admitted or consumed input");
            assertTrue(input.isPriorityActive());
            var revisited = game.until(next -> next.seat() == 0 && text(next.prompt(), "id").equals(text(old.prompt(), "id")));
            castAndResolve(game, revisited);
        }
    }

    @Test(timeOut = 90000)
    public void anAdmittedButRefusedQueuedPassDoesNotHideItsRestoredInput() throws Exception {
        try (var game = scenario("scenario-auto-queued-refusal")) {
            var old = main(game); var input = actual(game);
            assertTrue(old.prompt().get("canAutoPass").getAsBoolean());
            try (var barrier = new InputBarrier(game)) {
                assertTrue(game.answer(old, Map.of("action", "passIfNoResponse")), "This receipt admits queued work only");
                game.table.game.getAction().withState(() -> addGreen(game));
                barrier.release(); HeadlessPlatform.query(game.table, () -> null);
                assertTrue(input.isPriorityActive()); assertSame(controller(game).getInputQueue().getInput(), input);
            }
            var restored = current(game);
            assertEquals(text(restored.prompt(), "id"), text(old.prompt(), "id"));
            assertFalse(restored.prompt().get("canAutoPass").getAsBoolean());
            assertTrue(restored.state().get("boardRevision").getAsLong() > old.state().get("boardRevision").getAsLong());
            var revisited = game.until(next -> next.seat() == 0 && text(next.prompt(), "id").equals(text(old.prompt(), "id")));
            castAndResolve(game, revisited);
        }
    }

    @Test(timeOut = 90000)
    public void anUnchangedObservationCannotHideARefusalOrAnInvalidCommand() throws Exception {
        try (var game = scenario("scenario-auto-unwitnessed-refusal")) {
            var old = main(game); var input = actual(game);
            assertTrue(old.prompt().get("canAutoPass").getAsBoolean());
            HeadlessPlatform.query(game.table, () -> { input.setAvailabilityVerified(false); return null; });
            assertEquals(current(game).state().get("revision"), old.state().get("revision"));
            assertThrows(IllegalArgumentException.class, () -> game.answer(old, Map.of("action", "passIfNoResponse")));
            assertThrows(IllegalArgumentException.class, () -> game.answer(old, Map.of("action", "not-a-command")));
            assertTrue(input.isPriorityActive()); assertEquals(actor(game).getManaPool().totalMana(), 0);
        }
    }

    @Test(timeOut = 90000)
    public void aRetiredPriorityAnswerNeverAnswersItsReplacementRequiredPayment() throws Exception {
        try (var game = scenario("scenario-auto-required-owner")) {
            var old = main(game);
            HeadlessPlatform.query(game.table, () -> { addGreen(game); game.table.publish(); return null; });
            var current = current(game);
            assertTrue(game.answer(current, Map.of("action", "card", "key", text(card(current.state(), "Hand", "Manalith"), "key"))));
            var payment = game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").startsWith("InputPayMana"));
            assertFalse(game.answer(old, Map.of("action", "passIfNoResponse")));
            var waiting = current(game);
            assertEquals(text(waiting.prompt(), "id"), text(payment.prompt(), "id"));
            assertTrue(text(waiting.prompt(), "inputType").startsWith("InputPayMana"));
            assertEquals(actor(game).getManaPool().totalMana(), 1);
            assertEquals(actor(game).getCardsIn(ZoneType.Battlefield).stream().filter(c -> c.getName().equals("Forest") && c.isTapped()).count(), 0L);
            assertTrue(game.answer(payment, Map.of("action", "cancel")));
        }
    }

    private static RulesScenario scenario(String name) throws Exception {
        return new RulesScenario(name, Map.of(0, List.of("Forest", "Forest", "Spellbook"),
                1, List.of("Spellbook"), 2, List.of("Spellbook"), 3, List.of("Spellbook")), "Manalith", "Progenitus");
    }
    private static Input main(RulesScenario game) throws Exception {
        return game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").equals("InputPassPriority")
                && text(next.state(), "phaseKey").equals("MAIN1"));
    }
    private static Player actor(RulesScenario game) { return game.table.game.getRegisteredPlayers().get(0); }
    private static PlayerControllerHuman controller(RulesScenario game) { return (PlayerControllerHuman) actor(game).getController(); }
    private static InputPassPriority actual(RulesScenario game) throws Exception {
        return HeadlessPlatform.query(game.table, () -> (InputPassPriority) controller(game).getInputQueue().getInput());
    }
    private static Input current(RulesScenario game) { var state = game.state(0); return new Input(0, state, state.getAsJsonObject("prompt")); }
    private static JsonObject card(JsonObject state, String zone, String name) {
        return zone(viewer(state), zone).getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "name").equals(name)).findFirst().orElseThrow();
    }
    private static void addGreen(RulesScenario game) {
        var source = actor(game).getCardsIn(ZoneType.Battlefield).stream().filter(c -> c.getName().equals("Forest")).findFirst().orElseThrow();
        actor(game).getManaPool().addMana(new Mana(MagicColor.GREEN, source, source.getManaAbilities().get(0).getManaPart(), actor(game)));
    }
    private static void castAndResolve(RulesScenario game, Input current) throws Exception {
        assertTrue(game.answer(current, Map.of("action", "card", "key", text(card(current.state(), "Hand", "Manalith"), "key"))));
        var payment = game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").startsWith("InputPayMana"));
        assertTrue(payment.prompt().get("okEnabled").getAsBoolean()); assertTrue(game.answer(payment, Map.of("action", "ok")));
        var resolved = game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").equals("InputPassPriority")
                && next.state().getAsJsonArray("stack").isEmpty() && actor(game).getCardsIn(ZoneType.Battlefield).stream().anyMatch(c -> c.getName().equals("Manalith")));
        assertTrue(resolved.state().get("boardRevision").getAsLong() > current.state().get("boardRevision").getAsLong());
        assertEquals(actor(game).getManaPool().totalMana(), 0);
        assertEquals(actor(game).getCardsIn(ZoneType.Battlefield).stream().filter(c -> c.getName().equals("Forest") && c.isTapped()).count(), 2L);
    }
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
}
