package forge.api;

import com.google.gson.*;
import org.testng.annotations.Test;
import java.util.Map;
import java.util.List;
import java.util.concurrent.atomic.AtomicLong;
import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

public class DecisionTempoIntegrationTest {
    private static final Gson JSON = new Gson();
    private static JsonObject clock(RulesScenario.Input input) {
        int player = input.state().get("viewerId").getAsInt();
        return input.state().getAsJsonObject("tempo").getAsJsonArray("clocks").asList().stream().map(JsonElement::getAsJsonObject)
                .filter(row -> row.get("playerId").getAsInt() == player).findFirst().orElseThrow();
    }
    private static JsonObject acknowledgement(RulesScenario.Input input) {
        var request = new JsonObject(); request.addProperty("sessionId", text(input.state(), "id"));
        request.addProperty("clockId", text(clock(input), "id")); return request;
    }
    @Test(timeOut = 45000) public void realPriorityTimeoutUsesTheScopedPassAndDoesNotChangeTheBoardOnClockReads() throws Exception {
        var time = new AtomicLong();
        try (var game = new RulesScenario("tempo-priority", DecisionTempo.Settings.preset("fast", "pass"), time::get)) {
            var input = game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").equals("InputPassPriority"));
            var seat = game.table.seat(0); var ack = acknowledgement(input);
            assertEquals(game.table.seat(1).tempoPresented(ack).get("accepted"), false);
            assertEquals(seat.tempoPresented(ack).get("accepted"), true);
            long revision = input.state().get("boardRevision").getAsLong();
            time.set(29_999); game.table.tickTempo();
            var reading = JSON.toJsonTree(seat.state()).getAsJsonObject();
            assertEquals(reading.get("boardRevision").getAsLong(), revision);
            assertEquals(text(reading.getAsJsonObject("prompt"), "id"), text(input.prompt(), "id"));
            time.set(30_000); game.table.tickTempo();
            assertThrows(IllegalArgumentException.class, () -> seat.action(request(input, Map.of("action", "passPriority"))));
            var next = game.until(choice -> choice.seat() == 0);
            assertNotEquals(text(next.prompt(), "id"), text(input.prompt(), "id"));
            assertNotEquals(text(clock(next), "id"), text(clock(input), "id"));
            assertEquals(seat.tempoPresented(ack).get("accepted"), false, "Reconnecting cannot restart an earlier decision");
            assertEquals(clock(next).get("strikes").getAsInt(), 1);
        }
    }
    @Test(timeOut = 45000) public void requiredChoiceReceivesGraceThenNormalEngineEliminationWithoutAnInventedAnswer() throws Exception {
        var time = new AtomicLong();
        try (var game = new RulesScenario("tempo-required-choice", DecisionTempo.Settings.preset("fast", "pass"), time::get)) {
            var input = game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").equals("InputConfirmMulligan"));
            var seat = game.table.seat(0); seat.tempoPresented(acknowledgement(input));
            time.set(30_000); game.table.tickTempo();
            var reading = JSON.toJsonTree(seat.state()).getAsJsonObject();
            assertEquals(text(reading.getAsJsonObject("prompt"), "id"), text(input.prompt(), "id"));
            assertFalse(seat.eliminated());
            time.set(90_000); game.table.tickTempo(); assertTrue(seat.eliminated());
            var shared = JSON.toJsonTree(game.table.seat(1).state()).getAsJsonObject().getAsJsonObject("tempo");
            assertEquals(shared.getAsJsonArray("timedOutPlayers").toString(), "[0]", "The settled reason is public to the table");
            assertThrows(IllegalArgumentException.class, () -> seat.action(request(input, Map.of("action", "ok"))));
        }
    }
    @Test(timeOut = 45000) public void nestedAbilityChoiceRunsWhileTheInputWorkerWaitsAndCancellationCannotRefundTheParent() throws Exception {
        var time = new AtomicLong();
        try (var game = new RulesScenario("tempo-nested-ability", Map.of(0, List.of("Llanowar Wastes")),
                DecisionTempo.Settings.preset("fast", "pass"), time::get)) {
            var input = game.until(next -> next.seat() == 0 && text(next.state(), "phaseKey").equals("MAIN1")
                    && text(next.prompt(), "inputType").equals("InputPassPriority"));
            var seat = game.table.seat(0); seat.tempoPresented(acknowledgement(input));
            var source = zone(viewer(input.state()), "Battlefield").getAsJsonArray("cards").asList().stream().map(JsonElement::getAsJsonObject)
                    .filter(card -> text(card, "name").equals("Llanowar Wastes")).findFirst().orElseThrow();
            time.set(20_000); assertTrue(game.answer(input, Map.of("action", "card", "key", text(source, "key"))));
            var menu = game.until(next -> next.seat() == 0 && text(next.prompt(), "kind").equals("choice"));
            assertEquals(text(clock(menu), "id"), text(clock(input), "id"));
            assertEquals(clock(menu).get("remainingMs").getAsLong(), 10_000L);
            time.set(24_000);
            assertEquals(clock(new Input(0, game.state(0), menu.prompt())).get("remainingMs").getAsLong(), 6_000L,
                    "A required dialog must keep timing while the input worker waits for its answer");
            assertEquals(menu.prompt().get("min").getAsInt(), 0, "The real ability chooser supports cancelling without playing an ability");
            time.set(25_000); assertTrue(game.answer(menu, Map.of("action", "choose", "choices", List.of())));
            var restored = game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").equals("InputPassPriority"));
            assertEquals(text(clock(restored), "id"), text(clock(input), "id"));
            assertEquals(clock(restored).get("remainingMs").getAsLong(), 5_000L);
        }
    }
}
