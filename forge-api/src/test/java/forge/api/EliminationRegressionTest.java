package forge.api;

import com.google.common.eventbus.Subscribe;
import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import forge.StaticData;
import forge.ai.LobbyPlayerAi;
import forge.deck.Deck;
import forge.deck.DeckSection;
import forge.game.event.GameEventGameFinished;
import forge.game.player.RegisteredPlayer;
import forge.item.IPaperCard;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.nio.file.Path;
import java.time.Duration;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.function.Predicate;
import java.util.stream.IntStream;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Actual command-zone Phage casts, with fixture mana supplied before the game starts. */
public class EliminationRegressionTest {
    @Test(timeOut = 90000)
    public void losingTheOnlyHumanEndsTheGameAndAllowsAPlayableRematch() throws Exception {
        String previousId;
        try (var game = new EliminationGame("solo-phage-elimination", false)) {
            var observations = new DeckPlaytests(Files.createTempDirectory("mana-natural-loss-test"));
            var test = observations.begin("phage-deck", "phage-version", "Phage test", "Commander", List.of(),
                    "game", "Can the commander survive its own trigger?", List.of("green", "red", "green"));
            observations.attach(test.id(), game.table.seat(0));
            game.castPhage = true;
            var ended = game.until(states -> text(states.get(0), "status").equals("finished")).get(0);
            assertTrue(game.castAccepted, "The test must cast Phage through a real priority decision");
            assertTrue(viewer(ended).get("eliminated").getAsBoolean());
            assertEquals(text(ended, "result"), "Defeat");
            var completed = observations.get("phage-deck", test.id());
            assertEquals(completed.status(), "completed", "Natural elimination is a completed observation, not a concession");
            assertEquals(completed.result(), "Defeat");
            assertEquals(completed.observations().get("commanderCasts"), Integer.valueOf(1));
            assertTrue(ended.get("prompt").isJsonNull());
            assertTrue(ended.getAsJsonArray("activity").asList().stream().anyMatch(entry -> {
                var event = entry.getAsJsonObject();
                return text(event, "kind").equals("ability") && text(event, "cardName").equals("Phage the Untouchable");
            }), "The loss comes from Phage's real enters trigger");
            assertTrue(game.finished.await(5, TimeUnit.SECONDS), "The hidden priority input must release the game thread");
            game.assertSourceDeckUnchanged();
            previousId = text(ended, "id");
        }
        // Reuse the same initialized Forge/platform process, as the application does.
        try (var rematch = new EliminationGame("solo-phage-rematch", false)) {
            var playable = rematch.until(states -> mainFor(states.get(0), 0)).get(0);
            assertNotEquals(text(playable, "id"), previousId);
            assertFalse(viewer(playable).get("eliminated").getAsBoolean());
            assertEquals(text(playable.getAsJsonObject("prompt"), "inputType"), "InputPassPriority");
            rematch.assertSourceDeckUnchanged();
        }
    }

    @Test(timeOut = 90000)
    public void aNaturalHumanLossReleasesItsInputWhileTheOtherHumansContinue() throws Exception {
        try (var game = new EliminationGame("multihuman-phage-elimination", true)) {
            game.castPhage = true;
            var after = game.until(states -> !states.get(0).getAsJsonArray("players").isEmpty()
                    && viewer(states.get(0)).get("eliminated").getAsBoolean());
            assertTrue(game.castAccepted);
            var retained = after.get(0).deepCopy();
            assertTrue(retained.get("prompt").isJsonNull(), "The eliminated seat receives no hidden or visible priority decision");
            assertNotEquals(text(retained, "status"), "finished", "One loss does not end a table with surviving humans");
            assertTrue(retained.get("result").isJsonNull());
            var continued = game.until(states -> IntStream.range(1, 4).anyMatch(seat -> mainFor(states.get(seat), 2)));
            int departedId = retained.get("viewerId").getAsInt();
            for (var state : continued) {
                assertTrue(player(state, departedId).get("eliminated").getAsBoolean());
                assertNotEquals(text(state, "status"), "finished");
                assertEquals(state.getAsJsonArray("players").asList().stream()
                        .filter(player -> !player.getAsJsonObject().get("eliminated").getAsBoolean()).count(), 3L);
            }
            assertTrue(continued.get(0).get("prompt").isJsonNull());
            assertNotEquals(continued.get(1).get("activePlayerId").getAsInt(), departedId);
            assertEquals(retained, after.get(0), "Advancing survivors cannot mutate the retained loss snapshot");
            game.assertSourceDeckUnchanged();
        }
    }

    private static boolean mainFor(JsonObject state, int minimumTurn) {
        return state.get("turn").getAsInt() >= minimumTurn && text(state, "phaseKey").equals("MAIN1")
                && state.get("activePlayerId").equals(state.get("viewerId"))
                && state.has("prompt") && !state.get("prompt").isJsonNull();
    }

    private static final class EliminationGame implements AutoCloseable {
        private static final Gson JSON = new GsonBuilder().serializeNulls().setPrettyPrinting().create();
        private static final Path RESOURCES = Path.of("../forge-gui/res");
        final MatchTable table;
        final CountDownLatch finished = new CountDownLatch(1);
        private final Path evidence;
        private final Deck sourceDeck;
        private final int humans;
        private final Map<Integer, String> answered = new HashMap<>();
        private final List<Object> trace = new ArrayList<>();
        boolean castPhage, castAccepted;

        EliminationGame(String name, boolean allHuman) throws Exception {
            if (StaticData.instance() == null) EngineResources.load(RESOURCES);
            evidence = ApiTestWorkspace.evidence(name);
            sourceDeck = new Deck("Phage elimination source deck");
            sourceDeck.getMain().add(StaticData.instance().getCommonCards().getCard("Swamp"), 99);
            sourceDeck.getOrCreate(DeckSection.Commander).add(StaticData.instance().getCommonCards().getCard("Phage the Untouchable"));
            humans = allHuman ? 4 : 1;
            var players = new ArrayList<RegisteredPlayer>();
            for (int seat = 0; seat < 4; seat++) {
                RegisteredPlayer registered;
                if (seat < humans) registered = MatchTable.human(sourceDeck, "Commander", "Seat " + seat);
                else {
                    var ai = new LobbyPlayerAi("AI " + seat, Set.of());
                    ai.setAiProfile("Default");
                    registered = RegisteredPlayer.forCommander(new Deck(sourceDeck)).setPlayer(ai);
                }
                List<IPaperCard> extras = new ArrayList<>();
                extras.add(StaticData.instance().getCommonCards().getCard("Spellbook"));
                if (seat == 0) for (int land = 0; land < 7; land++) extras.add(StaticData.instance().getCommonCards().getCard("Swamp"));
                registered.addExtraCardsOnBattlefield(extras);
                players.add(registered);
            }
            table = new MatchTable("Commander", players, RESOURCES, evidence);
            table.game.subscribeToEvents(this);
            table.start();
        }

        @Subscribe public void finished(GameEventGameFinished event) { finished.countDown(); }

        List<JsonObject> until(Predicate<List<JsonObject>> stop) throws Exception {
            long deadline = System.nanoTime() + Duration.ofSeconds(30).toNanos();
            while (System.nanoTime() < deadline) {
                var states = new ArrayList<JsonObject>();
                for (int seat = 0; seat < humans; seat++) {
                    var state = JSON.toJsonTree(table.seat(seat).state()).getAsJsonObject();
                    assertNotEquals(text(state, "status"), "error", state.toString());
                    if (!state.getAsJsonArray("players").isEmpty() && viewer(state).get("eliminated").getAsBoolean())
                        assertTrue(state.get("prompt").isJsonNull(), "An eliminated player cannot retain a decision");
                    states.add(state);
                }
                if (stop.test(states)) return states;
                for (int seat = 0; seat < humans; seat++) {
                    var state = states.get(seat);
                    if (!state.has("prompt") || state.get("prompt").isJsonNull()) continue;
                    var prompt = state.getAsJsonObject("prompt");
                    if (text(prompt, "id").equals(answered.get(seat))) continue;
                    Map<String, ?> answer = defaultAnswer(state, prompt);
                    boolean casting = false;
                    if (seat == 0 && castPhage && !castAccepted && text(prompt, "inputType").equals("InputPassPriority")
                            && mainFor(state, 1)) {
                        var commander = zone(viewer(state), "Command").getAsJsonArray("cards").asList().stream()
                                .map(card -> card.getAsJsonObject()).filter(card -> card.get("selectable").getAsBoolean()).findFirst();
                        if (commander.isPresent()) { answer = Map.of("action", "card", "key", text(commander.get(), "key")); casting = true; }
                    }
                    var request = JSON.toJsonTree(answer).getAsJsonObject();
                    request.addProperty("sessionId", text(state, "id"));
                    request.addProperty("promptId", text(prompt, "id"));
                    try {
                        table.seat(seat).action(request);
                        if (casting) castAccepted = true;
                        answered.put(seat, text(prompt, "id"));
                        trace.add(Map.of("seat", seat, "state", state, "answer", request));
                    } catch (IllegalArgumentException stale) {
                        if (!"That choice has changed. Use the current prompt.".equals(stale.getMessage())) throw stale;
                    }
                }
                Thread.sleep(5);
            }
            throw new AssertionError("Elimination scenario timed out: " + table.testViews());
        }

        private Map<String, ?> defaultAnswer(JsonObject state, JsonObject prompt) {
            String kind = text(prompt, "kind");
            if (kind.equals("choice")) return Map.of("choices", IntStream.range(0, prompt.get("min").getAsInt()).boxed().toList());
            if (kind.equals("reveal")) return Map.of();
            if (kind.equals("number")) return Map.of("value", prompt.get("min").getAsInt());
            if (state.get("turn").getAsInt() == 0 && prompt.has("playerChoices") && !prompt.getAsJsonArray("playerChoices").isEmpty())
                return Map.of("action", "player", "playerId", table.game.getRegisteredPlayers().get(0).getId());
            if (prompt.has("okEnabled") && prompt.get("okEnabled").getAsBoolean()) return Map.of("action", "ok");
            for (var zone : viewer(state).getAsJsonArray("zones")) for (var entry : zone.getAsJsonObject().getAsJsonArray("cards")) {
                var card = entry.getAsJsonObject();
                if (card.get("selectable").getAsBoolean() && !card.get("highlighted").getAsBoolean())
                    return Map.of("action", "card", "key", text(card, "key"));
            }
            throw new AssertionError("Unhandled elimination decision: " + prompt);
        }

        void assertSourceDeckUnchanged() {
            assertEquals(sourceDeck.getMain().countAll(), 99);
            assertEquals(sourceDeck.getMain().count(StaticData.instance().getCommonCards().getCard("Swamp")), 99);
            assertEquals(sourceDeck.get(DeckSection.Commander).countAll(), 1);
            assertEquals(sourceDeck.get(DeckSection.Commander).iterator().next().getKey().getName(), "Phage the Untouchable");
        }

        @Override public void close() throws Exception {
            try { Files.writeString(evidence.resolve("trace.json"), JSON.toJson(Map.of("actions", trace, "lastViews", table.testViews()))); }
            finally { table.close(); table.game.unsubscribeFromEvents(this); }
        }
    }
}
