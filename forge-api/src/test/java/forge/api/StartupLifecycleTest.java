package forge.api;

import com.google.common.eventbus.Subscribe;
import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import forge.StaticData;
import forge.card.GamePieceType;
import forge.deck.Deck;
import forge.deck.DeckSection;
import forge.game.event.GameEventGameStarted;
import forge.game.player.RegisteredPlayer;
import forge.game.zone.ZoneType;
import forge.player.PlayerControllerHuman;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.EnumSet;
import java.util.HashSet;
import java.util.List;
import java.util.Map;
import java.util.concurrent.BlockingQueue;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.LinkedBlockingQueue;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicReference;
import java.util.function.Predicate;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Real setup work must respect closure and the original registered identities. */
public class StartupLifecycleTest {
    private static final Gson JSON = new GsonBuilder().serializeNulls().create();
    private static final Path RESOURCES = Path.of("../forge-gui/res");
    private static final List<String> BASICS = List.of("Forest", "Island", "Mountain", "Plains");
    private static final List<String> COMMANDERS = List.of("Rhys the Redeemed", "Talrand, Sky Summoner",
            "Rograkh, Son of Rohgahh", "Isamaru, Hound of Konda");

    @Test(timeOut = 30000)
    public void closingAnAdmittedButStateBlockedStartCannotPrepareOrReopenTheTable() throws Exception {
        Path evidence = evidence("queued-close-");
        try (var table = new MatchTable("Commander", registrations(true), RESOURCES, evidence)) {
            var started = new AtomicInteger();
            var observer = new Object() {
                @Subscribe public void onStarted(GameEventGameStarted event) { started.incrementAndGet(); }
            };
            table.game.subscribeToEvents(observer);
            var completion = new AtomicReference<CompletableFuture<Void>>();
            try {
                // The actual worker cannot enter preparation until this thread
                // releases state. Close therefore always wins before setup.
                table.game.getAction().withState(() -> {
                    completion.set(table.start());
                    assertFalse(completion.get().isDone());
                    table.close();
                    assertTrue(table.game.isGameOver());
                    for (var player : table.game.getRegisteredPlayers()) assertEmptyZones(player);
                });
                completion.get().get(5, TimeUnit.SECONDS);
                table.game.getAction().readState(() -> {
                    assertEquals(started.get(), 0, "A revoked start cannot announce a newly started game");
                    for (var player : table.game.getRegisteredPlayers()) assertEmptyZones(player);
                    for (var state : table.testViews()) {
                        assertEquals(state.get("status"), "finished");
                        assertNull(state.get("error"));
                        assertNull(state.get("prompt"));
                    }
                    return null;
                });
            } finally {
                table.game.unsubscribeFromEvents(observer);
                table.close();
                if (completion.get() != null) completion.get().get(5, TimeUnit.SECONDS);
                Files.writeString(evidence.resolve("private-after-worker.json"), JSON.toJson(table.testViews()));
            }
        }
    }

    @Test(timeOut = 30000)
    public void aDepartureBeforePreparationKeepsEverySurvivorsRegisteredDeck() throws Exception {
        Path evidence = evidence("queued-departure-");
        try (var table = new MatchTable("Commander", registrations(true), RESOURCES, evidence)) {
            var publications = publications(table);
            var completion = new AtomicReference<CompletableFuture<Void>>();
            var departed = table.game.getRegisteredPlayers().get(1);
            try {
                table.game.getAction().withState(() -> {
                    completion.set(table.start());
                    ((PlayerControllerHuman)departed.getController()).concede();
                    assertTrue(departed.hasLost());
                    assertFalse(table.game.getPlayers().contains(departed));
                    assertEquals(table.game.getPlayers().size(), 3);
                    assertEquals(table.game.getRegisteredPlayers().size(), 4);
                    assertFalse(table.game.isGameOver());
                    for (var player : table.game.getRegisteredPlayers()) assertEmptyZones(player);
                });
                var main = continueToMain(table, publications, 1);
                assertEquals(text(main.state(), "phaseKey"), "MAIN1");
                assertFalse(completion.get().isDone(), "Survivors still own a live rules continuation");
                assertPreparedIdentities(table, 1, true);
                Files.writeString(evidence.resolve("private-survivor-priority.json"), JSON.toJson(table.testViews()));
            } finally {
                table.close();
                if (completion.get() != null) completion.get().get(5, TimeUnit.SECONDS);
                Files.writeString(evidence.resolve("private-after-worker.json"), JSON.toJson(table.testViews()));
            }
        }
    }

    @Test(timeOut = 30000)
    public void anUnexpectedPreparationFaultRemainsExceptionalAndPublishesATerminalError() throws Exception {
        Path evidence = evidence("setup-fault-");
        var registered = registrations(true);
        var fault = new IllegalStateException("Controlled startup preparation failure");
        registered.get(1).addExtraCardsOnBattlefield(() -> { throw fault; });
        try (var table = new MatchTable("Commander", registered, RESOURCES, evidence)) {
            var failure = expectThrows(ExecutionException.class, () -> table.start().get(5, TimeUnit.SECONDS));
            assertSame(failure.getCause(), fault, "Only a revoked or departed setup may cancel normally");
            table.game.getAction().readState(() -> {
                assertTrue(table.game.isGameOver());
                for (var state : table.testViews()) {
                    assertEquals(state.get("status"), "error");
                    assertNotNull(state.get("error"));
                    assertNull(state.get("result"), "A failed setup cannot invent a rules defeat");
                    assertNull(state.get("prompt"));
                }
                return null;
            });
            Files.writeString(evidence.resolve("private-after-worker.json"), JSON.toJson(table.testViews()));
        }
    }

    @Test(timeOut = 30000)
    public void aDepartureDuringTheRealCompanionChoiceCannotRecreateTheDepartedPlayersZones() throws Exception {
        Path evidence = evidence("companion-departure-");
        var registered = registrations(false);
        registered.get(0).getDeck().getOrCreate(DeckSection.Sideboard)
                .add(StaticData.instance().getCommonCards().getCard("Lutri, the Spellchaser"));
        try (var table = new MatchTable("Constructed", registered, RESOURCES, evidence)) {
            var publications = publications(table);
            var completion = table.start();
            try {
                var choosing = next(table, publications, input -> input.seat() == 0 && hasPrompt(input.state())
                        && text(input.state().getAsJsonObject("prompt"), "message").toLowerCase(java.util.Locale.ROOT).contains("companion"),
                        System.nanoTime() + TimeUnit.SECONDS.toNanos(10));
                var departed = table.game.getRegisteredPlayers().get(0);
                var companion = table.game.getAction().readState(() -> {
                    assertEquals(departed.getCardsIn(ZoneType.Library).size(), 60);
                    assertEquals(departed.getCardsIn(ZoneType.Sideboard).size(), 1);
                    var actual = departed.getCardsIn(ZoneType.Sideboard).get(0);
                    assertEquals(actual.getName(), "Lutri, the Spellchaser");
                    return actual;
                });
                Files.writeString(evidence.resolve("private-companion-choice.json"), JSON.toJson(choosing.state()));
                var request = new JsonObject(); request.addProperty("sessionId", table.id);
                var receipt = table.seat(0).concede(request);
                assertNull(receipt.get("prompt"), "Departure admission revokes the old companion answer");
                continueToMain(table, publications, 0);
                assertFalse(completion.isDone());
                assertPreparedIdentities(table, 0, false);
                assertTrue(companion.isInZone(ZoneType.Sideboard), "An unchosen companion remains outside the game after departure");
                assertSame(companion.getOwner(), departed);
                assertEquals(departed.getCardsIn(ZoneType.Sideboard).size(), 1);
                assertSame(departed.getCardsIn(ZoneType.Sideboard).get(0), companion,
                        "Departure must retain the original outside-game card, not create a replacement companion");
                assertThrows(IllegalArgumentException.class, () -> table.seat(0).action(request(
                        new Input(0, choosing.state(), choosing.state().getAsJsonObject("prompt")), Map.of("choices", List.of(0)))));
                Files.writeString(evidence.resolve("private-survivor-priority.json"), JSON.toJson(table.testViews()));
            } finally {
                table.close();
                completion.get(5, TimeUnit.SECONDS);
                Files.writeString(evidence.resolve("private-after-worker.json"), JSON.toJson(table.testViews()));
            }
        }
    }

    private static void assertPreparedIdentities(MatchTable table, int departedSeat, boolean commander) {
        table.game.getAction().readState(() -> {
            assertFalse(table.game.isGameOver());
            assertEquals(table.game.getPlayers().size(), 3);
            for (int seat = 0; seat < 4; seat++) {
                var player = table.game.getRegisteredPlayers().get(seat);
                assertEquals(player.hasLost(), seat == departedSeat);
                if (seat == departedSeat) { assertEmptyInGameZones(player); continue; }
                var cards = player.getCardsIn(ZoneType.Library, ZoneType.Hand);
                assertEquals(cards.size(), commander ? 99 : 60);
                for (var card : cards) {
                    assertEquals(card.getName(), BASICS.get(seat), "Each survivor keeps its own registered library");
                    assertSame(card.getOwner(), player);
                }
                assertTrue(player.getCardsIn(ZoneType.Hand).size() >= 7);
                var command = player.getCardsIn(ZoneType.Command);
                var physical = command.stream().filter(card -> card.getGamePieceType() == GamePieceType.CARD).toList();
                var effects = command.stream().filter(card -> card.getGamePieceType() == GamePieceType.EFFECT).toList();
                assertEquals(physical.size(), commander ? 1 : 0);
                assertEquals(effects.size(), commander ? 1 : 0);
                assertEquals(command.size(), physical.size() + effects.size());
                if (commander) {
                    assertEquals(physical.get(0).getName(), COMMANDERS.get(seat));
                    assertTrue(physical.get(0).isRealCommander());
                    assertEquals(effects.get(0).getName(), "Commander Effect");
                    assertSame(physical.get(0).getOwner(), player);
                    assertSame(effects.get(0).getOwner(), player);
                }
                assertTrue(player.getCardsIn(ZoneType.Battlefield).isEmpty());
            }
            var views = table.testViews();
            for (var state : views) {
                assertEquals(state.get("status"), "playing");
                assertNull(state.get("error"));
                assertEquals(state.get("boardRevision"), views.get(0).get("boardRevision"));
            }
            assertNull(views.get(departedSeat).get("prompt"));
            return null;
        });
    }

    private static void assertEmptyZones(forge.game.player.Player player) {
        for (var zone : ZoneType.values()) {
            var physical = player.getZone(zone);
            if (physical != null)
                assertTrue(physical.getCards(false).isEmpty(), "An unprepared player must have no cards in " + zone);
        }
    }

    private static void assertEmptyInGameZones(forge.game.player.Player player) {
        var zones = EnumSet.of(ZoneType.Hand, ZoneType.Library, ZoneType.Graveyard, ZoneType.Battlefield,
                ZoneType.Exile, ZoneType.Command, ZoneType.Stack);
        zones.addAll(ZoneType.PART_OF_COMMAND_ZONE);
        // Sideboard cards are outside the game. Genuine loss removes the
        // player's in-game objects, including every companion/rules effect.
        for (var zone : zones)
            assertTrue(player.getCardsIn(zone, false).isEmpty(), "A departed player cannot recreate an in-game " + zone);
    }

    private static BlockingQueue<Published> publications(MatchTable table) {
        var queue = new LinkedBlockingQueue<Published>();
        for (int seat = 0; seat < 4; seat++) {
            int index = seat;
            var session = table.seat(seat);
            session.observePlaytest(frame -> queue.add(new Published(index, JSON.toJsonTree(session.state()).getAsJsonObject())));
        }
        return queue;
    }

    private static Published continueToMain(MatchTable table, BlockingQueue<Published> queue, int departedSeat) throws Exception {
        long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(10);
        var answered = new HashSet<String>();
        while (true) {
            var published = next(table, queue, input -> hasPrompt(input.state()), deadline);
            var state = JSON.toJsonTree(table.seat(published.seat()).state()).getAsJsonObject();
            if (!hasPrompt(state)) continue;
            var prompt = state.getAsJsonObject("prompt");
            if (!text(prompt, "id").equals(text(published.state().getAsJsonObject("prompt"), "id"))) continue;
            assertNotEquals(published.seat(), departedSeat, "A departed player cannot receive a new decision");
            String scope = published.seat() + ":" + text(prompt, "id");
            if (answered.contains(scope)) continue;
            String type = text(prompt, "inputType");
            if (type.equals("InputPassPriority") && text(state, "phaseKey").equals("MAIN1"))
                return new Published(published.seat(), state);
            Map<String, ?> answer;
            if (state.get("turn").getAsInt() == 0 && !prompt.getAsJsonArray("playerChoices").isEmpty()) {
                int first = table.game.getAction().readState(() -> table.game.getPlayers().get(0).getId());
                answer = Map.of("action", "player", "playerId", first);
            } else if (type.contains("Mulligan") || type.equals("InputConfirm") || type.equals("InputPassPriority")) {
                if (!prompt.get("okEnabled").getAsBoolean()) continue;
                answer = Map.of("action", type.equals("InputPassPriority") ? "passPriority" : "ok");
            } else throw new AssertionError("Unexpected startup decision: " + prompt);
            answered.add(scope);
            table.seat(published.seat()).action(request(new Input(published.seat(), state, prompt), answer));
        }
    }

    private static Published next(MatchTable table, BlockingQueue<Published> queue, Predicate<Published> accept, long deadline) throws Exception {
        while (true) {
            long remaining = deadline - System.nanoTime();
            assertTrue(remaining > 0, "Startup publication deadline expired: " + table.testViews());
            var published = queue.poll(remaining, TimeUnit.NANOSECONDS);
            assertNotNull(published, "Startup did not publish its next decision: " + table.testViews());
            assertNotEquals(text(published.state(), "status"), "error", published.state().toString());
            if (accept.test(published)) return published;
        }
    }

    private static boolean hasPrompt(JsonObject state) { return state.has("prompt") && !state.get("prompt").isJsonNull(); }
    private record Published(int seat, JsonObject state) { }

    private static List<RegisteredPlayer> registrations(boolean commander) {
        if (StaticData.instance() == null) EngineResources.load(RESOURCES);
        var players = new ArrayList<RegisteredPlayer>();
        for (int seat = 0; seat < 4; seat++) {
            var deck = new Deck("Startup seat " + seat);
            var land = StaticData.instance().getCommonCards().getCard(BASICS.get(seat)); assertNotNull(land);
            deck.getMain().add(land, commander ? 99 : 60);
            if (commander) {
                var leader = StaticData.instance().getCommonCards().getCard(COMMANDERS.get(seat)); assertNotNull(leader);
                deck.getOrCreate(DeckSection.Commander).add(leader);
            }
            players.add(MatchTable.human(deck, commander ? "Commander" : "Constructed", "Seat " + seat));
        }
        return players;
    }

    private static Path evidence(String prefix) throws Exception {
        Path root = Path.of("target/rules-regressions"); Files.createDirectories(root);
        return Files.createTempDirectory(root, "startup-" + prefix);
    }
}
