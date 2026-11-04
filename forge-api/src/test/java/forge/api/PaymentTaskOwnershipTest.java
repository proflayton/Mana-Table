package forge.api;

import com.google.gson.Gson;
import forge.game.zone.ZoneType;
import forge.gamemodes.match.input.InputPayMana;
import forge.gamemodes.match.input.InputPayManaOfCostPayment;
import forge.gui.interfaces.IGuiGame;
import forge.player.PlayerControllerHuman;
import forge.util.ThreadUtil;
import org.testng.annotations.DataProvider;
import org.testng.annotations.Test;

import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Proxy;
import java.nio.file.Files;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.TimeUnit;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Failure releases task ownership without treating a partial payment as paid. */
public class PaymentTaskOwnershipTest {
    @DataProvider public Object[][] tasks() { return new Object[][] {{false}, {true}}; }

    @Test(dataProvider = "tasks", timeOut = 90000)
    public void aProducerOrAutoExceptionReleasesOnlyItsOwnPaymentTask(boolean automatic) throws Exception {
        try (var game = new RulesScenario("payment-task-failure-" + automatic,
                Map.of(0, List.of("Manalith", "Island")), "Island", "Balmor, Battlemage Captain")) {
            var main = game.until(input -> input.seat() == 0 && text(input.prompt(), "inputType").equals("InputPassPriority")
                    && text(input.state(), "phaseKey").equals("MAIN1"));
            var commander = zone(viewer(main.state()), "Command").getAsJsonArray("cards").get(0).getAsJsonObject();
            var catalog = new Gson().toJsonTree(game.session(0).sourceActions(request(main, Map.of("key", text(commander, "key"))))).getAsJsonObject();
            assertTrue(game.answer(main, Map.of("action", "useSourceAction", "key", text(catalog, "sourceCardKey"),
                    "actionId", text(catalog.getAsJsonArray("actions").get(0).getAsJsonObject(), "id"))));
            game.until(input -> input.seat() == 0 && text(input.prompt(), "inputType").startsWith("InputPayMana"));
            var done = new CompletableFuture<Boolean>();
            ThreadUtil.invokeInGameThread(() -> {
                try {
                    game.table.game.getAction().withState(() -> {
                        var actor = game.table.game.getRegisteredPlayers().get(0);
                        var controller = (PlayerControllerHuman)actor.getController();
                        var parent = (InputPayMana)controller.getInputQueue().getInput();
                        var originalBill = parent.getRemainingManaCost().toString();
                        var fault = new IllegalStateException("CONTROLLED_PAYMENT_TASK_FAULT");
                        if (automatic) {
                            var copy = parent.getPaidForAbility().copy(actor);
                            var nested = new InputPayManaOfCostPayment(controller, parent.getRemainingManaCost(), copy, actor, null, false) {
                                @Override protected void runAsAi(Runnable task) { throw fault; }
                            };
                            controller.getInputQueue().setInput(nested);
                            try {
                                assertSame(expectThrows(IllegalStateException.class, nested::selectButtonOK), fault);
                                assertFalse(nested.isActivatingManaAbility(), "A failed Auto task releases its own lock in finally");
                                assertEquals(nested.getRemainingManaCost().toString(), originalBill);
                            } finally { nested.stop(); }
                        } else {
                            var originalGui = controller.getGui();
                            controller.setGui((IGuiGame)Proxy.newProxyInstance(IGuiGame.class.getClassLoader(), new Class<?>[] {IGuiGame.class},
                                    (proxy, method, args) -> {
                                        if (method.getName().equals("chooseColor")) throw fault;
                                        try { return method.invoke(originalGui, args); }
                                        catch (InvocationTargetException failure) { throw failure.getCause(); }
                                    }));
                            try {
                                var source = actor.getCardsIn(ZoneType.Battlefield).stream()
                                        .filter(value -> value.getName().equals("Manalith")).findFirst().orElseThrow();
                                assertSame(expectThrows(IllegalStateException.class,
                                        () -> parent.selectAbility(source.getManaAbilities().get(0))), fault);
                                assertTrue(source.isTapped(), "The test fails after this real producer paid its source tap");
                            } finally { controller.setGui(originalGui); }
                        }
                        assertSame(controller.getInputQueue().getInput(), parent);
                        assertFalse(parent.isActivatingManaAbility(), "A failed producer cannot retain parent task ownership");
                        assertEquals(parent.getRemainingManaCost().toString(), originalBill);
                        assertEquals(actor.getManaPool().totalMana(), 0, "A failed color chooser produced and spent no mana");
                        try {
                            Files.writeString(game.evidence.resolve("task-exception-outcome.json"), new Gson().toJson(Map.of(
                                    "automatic", automatic, "bill", originalBill, "locked", parent.isActivatingManaAbility(),
                                    "state", game.state(0))));
                        } catch (java.io.IOException failure) { throw new java.io.UncheckedIOException(failure); }
                        controller.selectButtonCancel();
                    });
                    done.complete(true);
                } catch (Throwable failure) { done.completeExceptionally(failure); }
            });
            assertTrue(done.get(5, TimeUnit.SECONDS));
            var restored = awaitPriority(game);
            assertTrue(restored.getAsJsonArray("stack").isEmpty());
            assertEquals(count(viewer(restored), "Command"), 1);
        }
    }

    private static com.google.gson.JsonObject awaitPriority(RulesScenario game) throws InterruptedException {
        long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(5);
        do {
            var state = game.state(0);
            var value = state.get("prompt");
            if (value != null && !value.isJsonNull() && text(value.getAsJsonObject(), "inputType").equals("InputPassPriority")) return state;
            // The direct owner lifecycle can finish before its queued GUI
            // publication. Observing it must not answer the retired payment.
            Thread.sleep(5);
        } while (System.nanoTime() < deadline);
        throw new AssertionError("The cancelled real parent did not return priority: " + game.state(0));
    }
}
