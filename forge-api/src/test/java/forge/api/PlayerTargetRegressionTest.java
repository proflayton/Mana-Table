package forge.api;

import org.testng.annotations.DataProvider;
import org.testng.annotations.Test;
import java.util.*;
import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

public class PlayerTargetRegressionTest {
    @DataProvider public Object[][] targets() {
        return new Object[][] {{0, false, false}, {1, false, false}, {2, false, false}, {3, false, false}, {0, true, false}, {2, true, false}, {1, false, true}};
    }

    @Test(dataProvider = "targets", timeOut = 90000)
    public void bojukaBogOffersLegalPlayersAndExilesOnlyTheChosenGraveyard(int targetSeat, boolean hexproof, boolean emptyGraveyard) throws Exception {
        var battlefield = hexproof ? Map.of(0, List.of("Leyline of Sanctity"), 3, List.of("Leyline of Sanctity")) : Map.<Integer, List<String>>of();
        try (var game = new RulesScenario("bojuka-bog-" + targetSeat + "-" + hexproof + "-" + emptyGraveyard, battlefield, "Bojuka Bog", "Ayara, First of Locthwain")) {
            // Fill graveyards through actual cleanup discards, or play on turn one
            // to verify that an empty graveyard still permits the required target.
            int playTurn = emptyGraveyard ? 1 : 5, graveyardSize = emptyGraveyard ? 0 : 1;
            RulesScenario.Input main;
            while (true) {
                main = game.until(i -> text(i.state(), "phaseKey").equals("CLEANUP")
                        || i.state().get("turn").getAsInt() == playTurn && i.seat() == 0 && text(i.state(), "phaseKey").equals("MAIN1"));
                if (main.state().get("turn").getAsInt() == playTurn) break;
                var discard = zone(viewer(main.state()), "Hand").getAsJsonArray("cards").get(0).getAsJsonObject();
                assertTrue(game.answer(main, Map.of("action", "card", "key", text(discard, "key"))));
            }
            var bog = zone(viewer(main.state()), "Hand").getAsJsonArray("cards").get(0).getAsJsonObject();
            assertEquals(text(bog, "name"), "Bojuka Bog");
            assertTrue(game.answer(main, Map.of("action", "card", "key", text(bog, "key"))));
            var targeting = game.until(i -> text(i.prompt(), "inputType").equals("InputSelectTargets"));
            assertEquals(targeting.seat(), 0);
            assertTrue(text(targeting.prompt(), "message").contains("Bojuka Bog"));
            Set<Integer> expected = new HashSet<>();
            for (int seat = 0; seat < 4; seat++) if (!hexproof || seat != 3) expected.add(game.state(seat).get("viewerId").getAsInt());
            Set<Integer> actual = new HashSet<>();
            targeting.prompt().getAsJsonArray("playerChoices").forEach(p -> actual.add(p.getAsInt()));
            assertEquals(actual, expected, "Every legal player must be selectable; your own hexproof does not stop your ability");
            assertFalse(targeting.prompt().get("canAutoPass").getAsBoolean());
            assertFalse(targeting.prompt().get("okEnabled").getAsBoolean());
            game.rejected(targeting, "passIfNoResponse", "This response window needs your decision");
            if (hexproof) game.rejected(targeting, Map.of("action", "player", "playerId", game.state(3).get("viewerId").getAsInt()),
                    "That player is not selectable in this decision");
            var field = zone(viewer(targeting.state()), "Battlefield").getAsJsonArray("cards");
            assertTrue(field.asList().stream().map(c -> c.getAsJsonObject()).anyMatch(c -> text(c, "name").equals("Bojuka Bog") && c.get("tapped").getAsBoolean()));
            int targetId = game.state(targetSeat).get("viewerId").getAsInt();
            assertTrue(game.answer(targeting, Map.of("action", "player", "playerId", targetId)));
            var resolved = game.until(i -> i.state().getAsJsonArray("stack").isEmpty()
                    && count(player(i.state(), targetId), "Exile") == graveyardSize);
            for (var entry : resolved.state().getAsJsonArray("players")) {
                var player = entry.getAsJsonObject();
                boolean chosen = player.get("id").getAsInt() == targetId;
                assertEquals(count(player, "Graveyard"), chosen ? 0 : graveyardSize);
                assertEquals(count(player, "Exile"), chosen ? graveyardSize : 0);
            }
        }
    }
}
