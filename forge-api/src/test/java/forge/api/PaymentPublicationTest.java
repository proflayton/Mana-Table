package forge.api;

import com.google.gson.Gson;
import forge.gamemodes.match.input.InputPayMana;
import forge.player.PlayerControllerHuman;
import org.testng.annotations.Test;

import java.util.List;
import java.util.Map;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Paying mana keeps its input stack entry while a separate rules task runs. */
public class PaymentPublicationTest {
    private static final Gson JSON = new Gson();

    @Test(timeOut = 90000)
    public void anInFlightPaymentCannotBeReopenedByGuiPublication() throws Exception {
        try (var game = new RulesScenario("payment-publication-in-flight", Map.of(0, List.of("Forest")),
                "Sol Ring", "Rhys the Redeemed")) {
            var priority = game.until(input -> input.seat() == 0
                    && text(input.prompt(), "inputType").equals("InputPassPriority")
                    && text(input.state(), "phaseKey").equals("MAIN1"));
            var spell = zone(viewer(priority.state()), "Hand").getAsJsonArray("cards").get(0).getAsJsonObject();
            var session = game.table.seat(0);
            var catalog = JSON.toJsonTree(session.sourceActions(request(priority,
                    Map.of("key", text(spell, "key"))))).getAsJsonObject();
            var cast = catalog.getAsJsonArray("actions").asList().stream().map(value -> value.getAsJsonObject())
                    .filter(action -> text(action, "kind").equals("cast")).findFirst().orElseThrow();
            assertTrue(game.answer(priority, Map.of("action", "useSourceAction", "key", text(spell, "key"),
                    "actionId", text(cast, "id"))));
            var payment = game.until(input -> input.seat() == 0
                    && text(input.prompt(), "inputType").startsWith("InputPayMana")
                    && input.prompt().get("okEnabled").getAsBoolean());

            HeadlessPlatform.query(game.table, () -> {
                var controller = (PlayerControllerHuman) game.table.game.getPlayers().get(0).getController();
                var current = (InputPayMana) controller.getInputQueue().getInput();
                assertTrue(current.isPaymentActive());
                // This is the same controller gesture as the queued API action.
                // The shared rules boundary holds its child task until these
                // real GUI publications have been observed, without sleeps.
                controller.selectButtonOk();
                assertFalse(current.isPaymentActive(), "Auto owns this payment until its queued rules task finishes");
                session.publishInput();
                var busy = session.state();
                assertNull(busy.get("prompt"), "A locked payment must not advertise another Auto action");
                assertEquals(busy.get("status"), "resolving");
                assertThrows(IllegalArgumentException.class, () -> session.action(request(payment, Map.of("action", "ok"))));
                session.publishInput();
                assertEquals(session.state().get("revision"), busy.get("revision"),
                        "Unrelated callbacks do not repeatedly change an already resolving decision");
                return null;
            });

            var stack = game.until(input -> input.seat() == 0
                    && text(input.prompt(), "inputType").equals("InputPassPriority")
                    && !input.state().getAsJsonArray("stack").isEmpty());
            assertEquals(stack.state().getAsJsonArray("stack").size(), 1, "The original payment casts exactly one spell");
            var resolved = game.until(input -> input.seat() == 0
                    && text(input.prompt(), "inputType").equals("InputPassPriority")
                    && input.state().getAsJsonArray("stack").isEmpty());
            var cards = zone(viewer(resolved.state()), "Battlefield").getAsJsonArray("cards").asList().stream()
                    .map(value -> value.getAsJsonObject()).toList();
            assertEquals(cards.stream().filter(card -> text(card, "name").equals("Sol Ring")).count(), 1L);
            assertTrue(cards.stream().filter(card -> text(card, "name").equals("Forest")).findFirst().orElseThrow()
                    .get("tapped").getAsBoolean(), "The actual mana source still pays normally");
        }
    }
}
