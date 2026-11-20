package forge.api;

import forge.game.zone.ZoneType;
import org.testng.annotations.Test;

import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

import static forge.api.CardDataRules.*;
import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Real landfall and current multi-player target input, including zero targets. */
public class HomerLandfallJourneyTest {
    @Test(timeOut = 90000)
    public void twoChosenPlayersMillFourAndOtherPlayersStayUnchanged() throws Exception { play(false); }

    @Test(timeOut = 90000)
    public void choosingZeroPlayersLegallyResolvesWithoutMill() throws Exception { play(true); }

    private static void play(boolean zero) throws Exception {
        try (var game = new RulesScenario("homer-landfall-" + (zero ? "zero" : "two"), Map.of(
                0, List.of("Homer, the Hermit", "Changeling Outcast", "Grizzly Bears")), "Forest", "Rhys the Redeemed")) {
            var before = main(game, 0, 1);
            int actor = before.state().get("viewerId").getAsInt();
            int first = game.state(1).get("viewerId").getAsInt(), second = game.state(2).get("viewerId").getAsInt();
            assertTrue(game.table.game.getAction().readState(() -> game.table.game.getRegisteredPlayers().get(0)
                    .getCardsIn(ZoneType.Battlefield).stream().anyMatch(card -> card.getName().equals("Changeling Outcast") && card.hasKeyword("Changeling"))),
                    "The actual multi-type creature has changeling; one object must not be counted once per matching creature type");
            var baselines = new LinkedHashMap<Integer, List<Integer>>();
            for (var entry : before.state().getAsJsonArray("players")) {
                var player = entry.getAsJsonObject();
                baselines.put(player.get("id").getAsInt(), List.of(count(player, "Library"), count(player, "Graveyard")));
            }
            save(game, "00-before-land.json", before.state());
            var land = cards(viewer(before.state()), "Hand", "Forest").get(0);
            invoke(game, before, land);
            var targets = game.until(input -> input.seat() == 0 && text(input.prompt(), "inputType").equals("InputSelectTargets"));
            assertEquals(text(targets.prompt().getAsJsonObject("sourceCard"), "name"), "Homer, the Hermit");
            assertTrue(targets.prompt().get("okEnabled").getAsBoolean(), "The actual any-number choice permits zero targets");
            save(game, "01-current-targets.json", targets.state());
            if (!zero) {
                for (int selected : new int[] {first, second}) {
                    assertTrue(targets.prompt().getAsJsonArray("playerChoices").asList().stream()
                            .anyMatch(value -> value.getAsInt() == selected), "Choose only an actual current player offer");
                    assertTrue(game.answer(targets, Map.of("action", "player", "playerId", selected)));
                    targets = game.until(input -> input.seat() == 0 && text(input.prompt(), "inputType").equals("InputSelectTargets"));
                }
            }
            assertTrue(targets.prompt().get("okEnabled").getAsBoolean());
            assertTrue(game.answer(targets, Map.of("action", "ok")));
            var settled = settle(game, 0);
            assertEquals(settled.state().get("turn"), before.state().get("turn"), "No intervening draw/turn supplies a mill-count change");
            assertEquals(settled.state().get("activePlayerId").getAsInt(), actor);
            assertEquals(cards(viewer(settled.state()), "Battlefield", "Forest").size(), 1, "The trigger followed the real land play");
            for (var entry : settled.state().getAsJsonArray("players")) {
                var player = entry.getAsJsonObject(); int id = player.get("id").getAsInt();
                int milled = !zero && (id == first || id == second) ? 4 : 0;
                assertEquals(count(player, "Library"), baselines.get(id).get(0) - milled,
                        "Only chosen players mill twice the two qualifying creature occurrences");
                assertEquals(count(player, "Graveyard"), baselines.get(id).get(1) + milled);
            }
            save(game, "02-resolved-landfall.json", settled.state());
        }
    }
}
