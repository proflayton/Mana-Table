package forge.api;

import org.testng.annotations.DataProvider;
import org.testng.annotations.Test;

import java.util.Map;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

public class OpeningMulliganTest {
    @DataProvider public Object[][] tables() { return new Object[][] {{2}, {3}, {4}}; }

    @Test(dataProvider = "tables", timeOut = 45000)
    public void multiplayerFirstRedrawIsFreeAndLaterRedrawsReturnTheExactCount(int seats) throws Exception {
        try (var game = new RulesScenario("opening-mulligans-" + seats, seats)) {
            var input = game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").equals("InputConfirmMulligan"));
            assertEquals(count(viewer(input.state()), "Hand"), 7);
            assertOpeningOffer(input, 7, seats > 2 ? 7 : 6, seats > 2 ? 0 : 1, seats > 2);
            for (int redraw = 1; redraw <= 3; redraw++) {
                assertTrue(game.answer(input, Map.of("action", "cancel")));
                input = game.until(next -> next.seat() == 0);
                int bottom = redraw - (seats > 2 ? 1 : 0);
                if (bottom > 0) {
                    assertEquals(text(input.prompt(), "inputType"), "InputLondonMulligan");
                    assertEquals(input.prompt().get("min").getAsInt(), bottom);
                    assertEquals(input.prompt().get("max").getAsInt(), bottom);
                    assertEquals(count(viewer(input.state()), "Hand"), 7, "London redraws seven before bottoming");
                    for (int index = 0; index < bottom; index++) {
                        var card = zone(viewer(input.state()), "Hand").getAsJsonArray("cards").asList().stream()
                                .map(item -> item.getAsJsonObject()).filter(item -> !item.get("highlighted").getAsBoolean()).findFirst().orElseThrow();
                        assertTrue(game.answer(input, Map.of("action", "card", "key", text(card, "key"))));
                        input = game.until(next -> next.seat() == 0);
                    }
                    assertTrue(input.prompt().get("okEnabled").getAsBoolean());
                    assertTrue(game.answer(input, Map.of("action", "ok")));
                    input = game.until(next -> next.seat() == 0);
                }
                assertEquals(text(input.prompt(), "inputType"), "InputConfirmMulligan",
                        "A free redraw must not ask the player to return zero cards");
                assertEquals(count(viewer(input.state()), "Hand"), 7 - bottom);
                assertOpeningOffer(input, 7 - bottom, 6 - bottom, bottom + 1, false);
            }
            assertTrue(game.answer(input, Map.of("action", "ok")));
            var main = game.until(next -> text(next.state(), "phaseKey").equals("MAIN1"));
            assertEquals(count(player(main.state(), 0), "Hand"), seats > 2 ? 6 : 4,
                    "Multiplayer starting player draws on turn one; the two-player starting player skips that draw");
        }
    }

    private static void assertOpeningOffer(RulesScenario.Input input, int handSize, int nextHandSize, int bottom, boolean free) {
        var offer = input.prompt().getAsJsonObject("openingHand");
        assertNotNull(offer, "The active policy supplies the next redraw's consequences");
        assertEquals(offer.get("handSize").getAsInt(), handSize);
        assertEquals(offer.get("nextHandSize").getAsInt(), nextHandSize);
        assertEquals(offer.get("cardsToReturn").getAsInt(), bottom);
        assertEquals(offer.get("freeMulligan").getAsBoolean(), free);
    }
}
