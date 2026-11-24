package forge.api;

import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import forge.StaticData;
import forge.deck.Deck;
import forge.game.GameEndReason;
import forge.game.card.Card;
import forge.game.combat.Combat;
import forge.game.player.GameLossReason;
import forge.game.zone.ZoneType;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;

import static org.testng.Assert.*;

/** Prepared authoritative combat with explicit rules termination. This is a
 * projection regression, not a natural-game or policy-strength result. */
public class TerminalCombatProjectionTest {
    private static final Gson JSON = new GsonBuilder().serializeNulls().setPrettyPrinting().create();

    @Test(timeOut = 90000)
    public void terminalWinDropsCurrentCombatForEverySeatAndSpectator() throws Exception {
        checkTerminal(false);
    }

    @Test(timeOut = 90000)
    public void terminalDrawDropsCurrentCombatForEverySeatAndSpectator() throws Exception {
        checkTerminal(true);
    }

    private static void checkTerminal(boolean draw) throws Exception {
        var resources = Path.of("../forge-gui/res");
        if (StaticData.instance() == null) EngineResources.load(resources);
        String label = draw ? "terminal-draw-combat" : "terminal-win-combat";
        var registrations = new ArrayList<forge.game.player.RegisteredPlayer>();
        for (int slot = 0; slot < 4; slot++) registrations.add(MatchTable.automated(new Deck(), "Constructed", "Fixture seat " + slot));
        try (var table = new MatchTable("Constructed", registrations, resources, ApiTestWorkspace.profile(label), true)) {
            HeadlessPlatform.activate(table);
            table.game.getAction().withState(() -> {
                var players = table.game.getRegisteredPlayers();
                var combat = new Combat(players.get(0));
                table.game.getPhaseHandler().setCombat(combat);
                var attacker = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard("Grizzly Bears"), players.get(0));
                players.get(0).getZone(ZoneType.Battlefield).add(attacker);
                attacker.setSickness(false);
                combat.addAttacker(attacker, players.get(1));
                table.game.updateCombatForView();
                table.publish();
                var before = table.spectatorState();
                assertNotNull(before.get("combat"), "An active public combat must remain available before termination");
                assertEquals(((List<?>) ((Map<?, ?>) before.get("combat")).get("attackers")).size(), 1);
                for (var seat : table.automatedSeats()) assertNotNull(seat.state().get("combat"));
                write(label, "before.json", Map.of("spectator", before,
                        "seats", table.automatedSeats().stream().map(MatchSession::state).toList()));

                if (draw) players.forEach(player -> player.intentionalDraw());
                else for (int slot = 1; slot < 4; slot++)
                    assertTrue(players.get(slot).loseConditionMet(GameLossReason.OpponentWon, "Terminal combat projection fixture"));
                table.game.setGameOver(draw ? GameEndReason.Draw : GameEndReason.AllOpponentsLost);
                assertTrue(table.game.isGameOver());
                assertSame(table.game.getCombat(), combat);
                assertEquals(combat.getAttackers().size(), 1,
                        "The regression deliberately retains engine combat after the terminal rules outcome");
                table.publish();

                var spectator = table.spectatorState();
                var seats = table.automatedSeats().stream().map(MatchSession::state).toList();
                write(label, "terminal.json", Map.of("spectator", spectator, "seats", seats));
                var expected = JSON.toJsonTree(MatchOutcomeProjection.of(table, null));
                assertFalse(expected.isJsonNull());
                assertEquals(expected.getAsJsonObject().get("kind").getAsString(), draw ? "draw" : "win");
                for (var seat : seats) {
                    assertEquals(seat.get("status"), "finished");
                    assertEquals(seat.get("boardRevision"), spectator.get("boardRevision"));
                    assertEquals(JSON.toJsonTree(seat.get("outcome")), expected);
                    assertNull(seat.get("combat"), "Owned terminal observations contain no current combat domain");
                }
                assertEquals(spectator.get("status"), "finished");
                assertEquals(JSON.toJsonTree(spectator.get("outcome")), expected);
                assertNull(spectator.get("combat"),
                        "The terminal spectator must match the owned current-combat lifecycle, even when the engine retains the combat object");
            });
        }
    }

    private static void write(String label, String file, Object value) {
        try { Files.writeString(ApiTestWorkspace.evidence(label).resolve(file), JSON.toJson(value)); }
        catch (java.io.IOException error) { throw new java.io.UncheckedIOException(error); }
    }
}
