package forge.api;

import java.util.HashSet;
import java.util.List;
import java.util.Map;
import org.testng.annotations.Test;
import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

public class PhasingSnapshotTest {
    @Test(timeOut = 90000)
    public void phasedOutStateSurvivesOpponentsTurnsAndClearsWhenTheIsleReturns() throws Exception {
        var battlefield = Map.of(0, List.of("Teferi's Isle", "Reliquary Tower"),
                1, List.of("Reliquary Tower"), 2, List.of("Reliquary Tower"), 3, List.of("Reliquary Tower"));
        try (var game = new RulesScenario("phasing-snapshots", battlefield, "Darksteel Colossus", "Rhys the Redeemed")) {
            var turns = new HashSet<Integer>();
            var viewers = new HashSet<Integer>();
            var identities = new HashSet<String>();
            var returned = game.until(input -> {
                int turn = input.state().get("turn").getAsInt();
                if (turn < 1 || !text(input.state(), "phaseKey").equals("MAIN1")
                        || !text(input.prompt(), "inputType").equals("InputPassPriority")) return false;
                int owner = game.table.game.getPlayers().get(0).getId();
                var isle = zone(player(input.state(), owner), "Battlefield").getAsJsonArray("cards").asList().stream()
                        .map(c -> c.getAsJsonObject()).filter(c -> text(c, "name").equals("Teferi's Isle")).findFirst().orElseThrow();
                assertEquals(isle.get("phasedOut").getAsBoolean(), turn < 5,
                        "Every seat receives the engine's phasing status, including during opponents' turns");
                assertFalse(text(isle, "visualId").isEmpty());
                identities.add(text(isle, "visualId"));
                turns.add(turn);
                viewers.add(input.seat());
                if (turn < 5) assertFalse(isle.get("selectable").getAsBoolean(), "A phased-out land cannot be activated");
                else assertFalse(isle.get("tapped").getAsBoolean(), "The returning Isle untaps in the same untap step");
                return turn == 5 && input.seat() == 0;
            });
            assertEquals(returned.state().get("turn").getAsInt(), 5);
            assertEquals(turns, java.util.Set.of(1, 2, 3, 4, 5));
            assertEquals(viewers, java.util.Set.of(0, 1, 2, 3));
            assertEquals(identities.size(), 1, "Phasing preserves the same public card occurrence");
        }
    }
}
