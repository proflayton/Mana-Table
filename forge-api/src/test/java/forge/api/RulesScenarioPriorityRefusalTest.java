package forge.api;

import com.google.gson.JsonObject;
import forge.card.MagicColor;
import forge.game.mana.Mana;
import forge.game.player.Player;
import forge.game.zone.ZoneType;
import forge.gamemodes.match.input.InputPayMana;
import forge.gamemodes.match.input.InputPassPriority;
import forge.player.PlayerControllerHuman;
import org.testng.annotations.Test;

import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicReference;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** The real API's admission acknowledgment is not proof that its queued input
 * was consumed. Controlled resource timing exercises the driver, not new rules. */
public class RulesScenarioPriorityRefusalTest {
    @Test(timeOut = 90000)
    public void aNonconsumingFiniteGrantPublicationWaitsForTheActualNextSeat() throws Exception {
        try (var game = new RulesScenario("scenario-finite-nonconsuming-publication",
                Map.of(0, List.of("Toothy, Imaginary Friend", "Reliquary Tower")))) {
            var initial = game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").equals("InputPassPriority")
                    && !next.state().getAsJsonArray("stack").isEmpty());
            var thread = new AtomicReference<Thread>();
            var driver = Executors.newSingleThreadExecutor(task -> { var value = new Thread(task, "scenario-next-seat"); thread.set(value); return value; });
            try (var barrier = new InputBarrier(game)) {
                var members = initial.state().getAsJsonArray("stack").asList().stream()
                        .map(value -> text(value.getAsJsonObject(), "executionId")).toList();
                assertTrue(game.answer(initial, Map.of("action", "grantFiniteStackYield", "grantId", "one-real-seat",
                        "executionIds", members, "stackActionVersion", initial.state().get("stackActionVersion").getAsLong(),
                        "resolutionChoiceVersion", initial.state().get("resolutionChoiceVersion").getAsLong())));
                game.table.game.getAction().withState(game.table::publish);
                var published = current(game);
                assertEquals(text(published.prompt(), "id"), text(initial.prompt(), "id"));
                assertTrue(published.state().get("boardRevision").getAsLong() > initial.state().get("boardRevision").getAsLong());
                assertEquals(published.state().getAsJsonObject("finiteStackYield").get("automaticPasses").getAsInt(), 0);
                var next = driver.submit(() -> game.until(input -> text(input.prompt(), "inputType").equals("InputPassPriority")));
                assertDriverWaits(next, thread, "A nonconsuming grant publication must not masquerade as the next seat");
                barrier.release();
                var actualNext = next.get(5, TimeUnit.SECONDS);
                assertNotEquals(actualNext.seat(), 0);
                assertNotEquals(text(actualNext.prompt(), "id"), text(initial.prompt(), "id"));
                assertEquals(game.state(0).getAsJsonObject("finiteStackYield").get("automaticPasses").getAsInt(), 1);
            } finally { driver.shutdownNow(); assertTrue(driver.awaitTermination(5, TimeUnit.SECONDS)); }
        }
    }

    @Test(timeOut = 90000)
    public void aSameIdentityPositiveAutoPublicationIsNotAWithdrawnPermissionWitness() throws Exception {
        try (var game = new RulesScenario("scenario-auto-positive-republication",
                Map.of(0, List.of("Forest", "Forest", "Spellbook"), 1, List.of("Spellbook"),
                        2, List.of("Spellbook"), 3, List.of("Spellbook")), "Lightning Bolt", "Progenitus")) {
            var old = main(game); var input = actual(game);
            assertTrue(old.prompt().get("canAutoPass").getAsBoolean());
            try (var barrier = new InputBarrier(game)) {
                assertTrue(game.answer(old, Map.of("action", "passIfNoResponse")));
                game.table.game.getAction().withState(() -> addGreen(game));
                barrier.release(); HeadlessPlatform.query(game.table, () -> null);
                assertTrue(input.isPriorityActive()); assertFalse(current(game).prompt().get("canAutoPass").getAsBoolean());
            }
            HeadlessPlatform.query(game.table, () -> { assertTrue(controller(game).refreshRestoredPriority(input)); game.table.publish(); return null; });
            var positive = current(game);
            assertEquals(text(positive.prompt(), "id"), text(old.prompt(), "id"));
            assertTrue(positive.prompt().get("canAutoPass").getAsBoolean());
            assertTrue(positive.state().get("boardRevision").getAsLong() > old.state().get("boardRevision").getAsLong());
            var thread = new AtomicReference<Thread>();
            var driver = Executors.newSingleThreadExecutor(task -> { var value = new Thread(task, "scenario-positive-republication"); thread.set(value); return value; });
            try (var barrier = new InputBarrier(game)) {
                var next = driver.submit(() -> game.until(value -> text(value.prompt(), "inputType").equals("InputPassPriority")));
                assertDriverWaits(next, thread, "A positive same-ID refresh cannot be inferred to be a refused submitted pass");
                assertTrue(game.answer(positive, Map.of("action", "passPriority")), "Only this explicit manual command resumes the held input");
                barrier.release(); assertNotEquals(next.get(5, TimeUnit.SECONDS).seat(), 0);
            } finally { driver.shutdownNow(); assertTrue(driver.awaitTermination(5, TimeUnit.SECONDS)); }
        }
    }

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
            var before = paymentSnapshot(game);
            assertEquals(before.pool().size(), 1); assertEquals(before.tappedForests(), 0L);
            assertTrue(before.payingMana().isEmpty());
            assertFalse(game.answer(old, Map.of("action", "passIfNoResponse")));
            var waiting = current(game);
            assertEquals(text(waiting.prompt(), "id"), text(payment.prompt(), "id"));
            assertTrue(text(waiting.prompt(), "inputType").startsWith("InputPayMana"));
            assertEquals(paymentSnapshot(game), before, "The retired reply must not alter the replacement payment or its resources");
            assertTrue(game.answer(payment, Map.of("action", "cancel")));
        }
    }

    @Test(timeOut = 90000)
    public void aPaymentSnapshotWaitsForAnOwnedPreviewToRefundItsTemporaryBorrow() throws Exception {
        try (var game = scenario("scenario-payment-preview-snapshot")) {
            main(game);
            HeadlessPlatform.query(game.table, () -> { addGreen(game); game.table.publish(); return null; });
            var current = current(game);
            assertTrue(game.answer(current, Map.of("action", "card", "key", text(card(current.state(), "Hand", "Manalith"), "key"))));
            var payment = game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").startsWith("InputPayMana"));
            var before = paymentSnapshot(game);
            assertEquals(before.pool().size(), 1); assertEquals(before.tappedForests(), 0L);
            var borrowed = new CountDownLatch(1); var release = new CountDownLatch(1);
            var reading = new AtomicReference<Thread>();
            var workers = Executors.newFixedThreadPool(2);
            try {
                var preview = workers.submit(() -> HeadlessPlatform.query(game.table, () -> {
                    var pool = actor(game).getManaPool(); var spent = new ArrayList<Mana>();
                    try {
                        assertFalse(pool.payManaCostFromPool(before.owner().getRemainingManaCost(),
                                before.owner().getPaidForAbility(), true, spent));
                        assertEquals(spent.size(), 1); borrowed.countDown();
                        if (!release.await(5, TimeUnit.SECONDS)) throw new AssertionError("Held preview did not release");
                    } finally { pool.refundMana(spent); }
                    return null;
                }));
                assertTrue(borrowed.await(5, TimeUnit.SECONDS));
                // The old outside-owner assertion can observe this legitimate intermediate state.
                assertEquals(actor(game).getManaPool().totalMana(), 0);
                var snapshot = workers.submit(() -> { reading.set(Thread.currentThread()); return paymentSnapshot(game); });
                assertPaymentSnapshotWaits(snapshot, reading);
                release.countDown(); preview.get(5, TimeUnit.SECONDS);
                assertEquals(snapshot.get(5, TimeUnit.SECONDS), before);
                assertTrue(game.answer(payment, Map.of("action", "cancel")));
            } finally { release.countDown(); workers.shutdownNow(); assertTrue(workers.awaitTermination(5, TimeUnit.SECONDS)); }
        }
    }

    private record PaymentSnapshot(InputPayMana owner, List<Mana> pool, List<Mana> payingMana,
                                   String originalBill, String remainingBill, long tappedForests) { }
    private static PaymentSnapshot paymentSnapshot(RulesScenario game) {
        return HeadlessPlatform.query(game.table, () -> capturePayment(game));
    }
    private static PaymentSnapshot capturePayment(RulesScenario game) {
        var input = (InputPayMana) controller(game).getInputQueue().getInput();
        assertTrue(input.isPaymentActive());
        var pool = new ArrayList<Mana>(); actor(game).getManaPool().forEach(pool::add);
        return new PaymentSnapshot(input, List.copyOf(pool), List.copyOf(input.getPaidForAbility().getPayingMana()),
                input.getOriginalManaCost().toString(), input.getRemainingManaCost().toString(),
                actor(game).getCardsIn(ZoneType.Battlefield).stream().filter(c -> c.getName().equals("Forest") && c.isTapped()).count());
    }
    private static void assertPaymentSnapshotWaits(Future<PaymentSnapshot> snapshot, AtomicReference<Thread> reader) throws Exception {
        long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(5);
        while (System.nanoTime() < deadline) {
            assertFalse(snapshot.isDone(), "A coherent snapshot must wait for the preview owner to refund its borrow");
            var thread = reader.get();
            if (thread != null && thread.getState() == Thread.State.TIMED_WAITING
                    && java.util.Arrays.stream(thread.getStackTrace()).anyMatch(frame ->
                        frame.getClassName().equals(HeadlessPlatform.class.getName()) && frame.getMethodName().equals("query"))) return;
            Thread.sleep(1);
        }
        fail("The payment snapshot did not enter its actual owner query wait");
    }

    private static RulesScenario scenario(String name) throws Exception {
        return new RulesScenario(name, Map.of(0, List.of("Forest", "Forest", "Spellbook"),
                1, List.of("Spellbook"), 2, List.of("Spellbook"), 3, List.of("Spellbook")), "Manalith", "Progenitus");
    }
    private static void assertDriverWaits(Future<Input> next, AtomicReference<Thread> thread, String message) throws Exception {
        long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(5);
        while (System.nanoTime() < deadline) {
            assertFalse(next.isDone(), message);
            var current = thread.get();
            if (current != null && current.getState() == Thread.State.TIMED_WAITING
                    && java.util.Arrays.stream(current.getStackTrace()).anyMatch(frame ->
                        frame.getClassName().equals(RulesScenario.class.getName()) && frame.getMethodName().equals("until"))) return;
            Thread.sleep(1);
        }
        fail("The held driver did not reach its actual observation loop");
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
