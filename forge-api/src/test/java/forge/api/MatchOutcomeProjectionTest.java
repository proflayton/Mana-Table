package forge.api;

import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import forge.StaticData;
import forge.deck.Deck;
import forge.deck.DeckSection;
import forge.game.GameEndReason;
import forge.game.player.Player;
import org.testng.annotations.Test;

import java.nio.file.Path;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.TimeUnit;
import java.util.function.Predicate;
import java.util.stream.IntStream;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Real rules outcomes and public identities; no client invents team victories. */
public class MatchOutcomeProjectionTest {
    private static final Gson JSON = new GsonBuilder().serializeNulls().create();

    @Test(timeOut = 90000)
    public void everyActualAlliedWinnerReceivesTheSamePublicCompletion() throws Exception {
        try (var fixture = new Fixture("team-winners", List.of(7, 7, 9, 9), 40)) {
            fixture.main();
            fixture.assertPublic(null);
            fixture.concede(2);
            assertFalse(fixture.table.game.isGameOver());
            fixture.assertPublic(null);
            fixture.concede(3);
            var outcome = fixture.finished();
            assertEquals(outcome.kind(), "win");
            assertEquals(outcome.ruleReason(), GameEndReason.AllOpposingTeamsLost.name());
            assertEquals(outcome.winningPlayerIds(), List.of(fixture.player(0).getId(), fixture.player(1).getId()));
            assertEquals(outcome.winningTeamIds(), List.of(7));
            fixture.assertPublic(outcome);
            assertEquals(fixture.state(0).get("result").getAsString(), "Victory");
            assertEquals(fixture.state(1).get("result").getAsString(), "Victory",
                    "A team has multiple actual winners, not one arbitrary winning lobby player");
            assertEquals(fixture.publicState().get("result").getAsString(), "Seat 0, Seat 1 win");
        }
    }

    @Test(timeOut = 90000)
    public void anEliminatedAllyRemainsAnIndividualLoserWhenItsTeamWins() throws Exception {
        try (var fixture = new Fixture("eliminated-ally", List.of(7, 7, 9, 9), 40)) {
            fixture.main();
            fixture.concede(1);
            fixture.concede(2);
            fixture.concede(3);
            var outcome = fixture.finished();
            assertEquals(outcome.winningPlayerIds(), List.of(fixture.player(0).getId()));
            assertEquals(outcome.winningTeamIds(), List.of(7));
            assertFalse(fixture.table.game.getOutcome().isWinner(fixture.player(1).getRegisteredPlayer()));
            fixture.assertPublic(outcome);
            assertEquals(fixture.state(1).get("result").getAsString(), "Defeat");
        }
    }

    @Test(timeOut = 90000)
    public void aRulesDrawHasNoWinningPlayersOrTeams() throws Exception {
        try (var fixture = new Fixture("rules-draw", List.of(7, 7, 9, 9), 0)) {
            fixture.advance(state -> text(state, "status").equals("finished"));
            var outcome = fixture.finished();
            assertEquals(outcome.kind(), "draw");
            assertEquals(outcome.ruleReason(), GameEndReason.Draw.name());
            assertEquals(outcome.winningPlayerIds(), List.of());
            assertEquals(outcome.winningTeamIds(), List.of());
            fixture.assertPublic(outcome);
            for (int seat = 0; seat < 4; seat++) assertEquals(fixture.state(seat).get("result").getAsString(), "Draw");
        }
    }

    @Test(timeOut = 90000)
    public void freeForAllTeamsAreActualGameAssignmentsAndFailureCannotBecomeACompletion() throws Exception {
        try (var fixture = new Fixture("ffa-failure", List.of(-1, -1, -1, -1), 40)) {
            fixture.main();
            assertEquals(fixture.table.game.getRegisteredPlayers().stream().map(Player::getTeam).distinct().count(), 4L);
            assertTrue(fixture.table.game.getRegisteredPlayers().stream().allMatch(player -> player.getTeam() >= 0));
            fixture.assertPublic(null);
            fixture.table.fail(new IllegalStateException("Controlled outcome fixture failure"));
            fixture.table.publish();
            fixture.assertPublic(null);
            for (int seat = 0; seat < 4; seat++) {
                assertEquals(text(fixture.state(seat), "status"), "error");
                assertTrue(fixture.state(seat).get("result").isJsonNull());
            }
            assertEquals(text(fixture.publicState(), "status"), "error");
            assertTrue(fixture.publicState().get("result").isJsonNull());
            assertNull(MatchOutcomeProjection.of(fixture.table, null), "Host cleanup is not a rules result even without its error text");
        }
    }

    private static final class Fixture implements AutoCloseable {
        final MatchTable table;
        private final Map<Integer, String> answered = new HashMap<>();

        Fixture(String name, List<Integer> teams, int startingLife) throws Exception {
            var resources = Path.of("../forge-gui/res");
            if (StaticData.instance() == null) EngineResources.load(resources);
            var deck = new Deck(name);
            deck.getMain().add(StaticData.instance().getCommonCards().getCard("Forest"), 99);
            deck.getOrCreate(DeckSection.Commander).add(StaticData.instance().getCommonCards().getCard("Rhys the Redeemed"));
            var players = IntStream.range(0, teams.size()).mapToObj(seat -> {
                var player = MatchTable.automated(deck, "Commander", "Seat " + seat);
                player.setTeamNumber(teams.get(seat));
                player.setStartingLife(startingLife);
                return player;
            }).toList();
            table = new MatchTable("Commander", players, resources, ApiTestWorkspace.evidence(name), true);
            table.start();
        }

        Player player(int seat) { return table.game.getRegisteredPlayers().get(seat); }
        MatchSession session(int seat) { return table.automatedSeat(player(seat).getId()); }
        JsonObject state(int seat) { return JSON.toJsonTree(session(seat).state()).getAsJsonObject(); }
        JsonObject publicState() { return JSON.toJsonTree(table.spectatorState()).getAsJsonObject(); }
        void main() throws Exception {
            advance(state -> text(state, "phaseKey").equals("MAIN1") && state.get("prompt").isJsonObject()
                    && text(state.getAsJsonObject("prompt"), "inputType").equals("InputPassPriority"));
        }

        void concede(int seat) throws Exception {
            var command = new JsonObject(); command.addProperty("sessionId", table.id);
            session(seat).concede(command);
            awaitConcession(table, session(seat));
        }

        MatchOutcomeProjection.Outcome finished() throws Exception {
            long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(5);
            do {
                var outcome = table.game.getAction().readState(() -> MatchOutcomeProjection.of(table, null));
                if (outcome != null) { table.publish(); return outcome; }
                Thread.sleep(3);
            } while (System.nanoTime() < deadline);
            throw new AssertionError("The rules game did not finish: " + publicState());
        }

        void assertPublic(MatchOutcomeProjection.Outcome outcome) {
            var expected = JSON.toJsonTree(outcome);
            var publicState = publicState();
            assertEquals(publicState.get("outcome"), expected);
            for (int seat = 0; seat < 4; seat++) {
                var state = state(seat);
                assertEquals(state.get("outcome"), expected);
                for (int member = 0; member < 4; member++) {
                    int id = player(member).getId(), team = player(member).getTeam();
                    assertEquals(RulesScenario.player(state, id).get("teamId").getAsInt(), team);
                    assertEquals(RulesScenario.player(publicState, id).get("teamId").getAsInt(), team);
                }
            }
        }

        void advance(Predicate<JsonObject> stop) throws Exception {
            long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(15);
            do {
                for (int seat = 0; seat < 4; seat++) {
                    var state = state(seat);
                    assertNotEquals(text(state, "status"), "error");
                    if (stop.test(state)) return;
                    if (!state.get("prompt").isJsonObject()) continue;
                    var prompt = state.getAsJsonObject("prompt");
                    if (text(prompt, "id").equals(answered.get(seat))) continue;
                    var command = new JsonObject(); command.addProperty("sessionId", table.id);
                    command.addProperty("promptId", text(prompt, "id"));
                    if (state.get("turn").getAsInt() == 0 && prompt.has("playerChoices")
                            && !prompt.getAsJsonArray("playerChoices").isEmpty()) {
                        command.addProperty("action", "player"); command.addProperty("playerId", player(0).getId());
                    } else {
                        String type = text(prompt, "inputType");
                        assertTrue(type.equals("InputConfirm") || type.contains("Mulligan") || type.equals("InputPassPriority"), prompt.toString());
                        assertTrue(prompt.get("okEnabled").getAsBoolean());
                        command.addProperty("action", "ok");
                    }
                    session(seat).action(command); answered.put(seat, text(prompt, "id"));
                }
                Thread.sleep(3);
            } while (System.nanoTime() < deadline);
            throw new AssertionError("Fixture did not reach its input: " + publicState());
        }

        @Override public void close() { table.close(); }
    }
}
