package forge.api;

import com.google.gson.Gson;
import com.google.gson.JsonObject;
import forge.card.MagicColor;
import forge.game.Game;
import forge.game.card.Card;
import forge.game.card.CardCollection;
import forge.game.card.CardCollectionView;
import forge.game.event.GameEventGameFinished;
import forge.game.player.Player;
import forge.game.zone.PlayerZone;
import forge.game.zone.ZoneType;
import com.google.common.eventbus.Subscribe;
import org.testng.annotations.DataProvider;
import org.testng.annotations.Test;

import java.lang.reflect.Field;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

public class CardQueryConcessionTest {
    @DataProvider(name = "queries")
    public Object[][] queries() {
        return new Object[][]{{"static-zones"}, {"single-zone"}, {"include-phasing"}, {"owned-zones"},
                {"colored"}, {"visitor"}, {"visitor-sideboard"}, {"copy-last-state"}, {"copy-zone"},
                {"players-single"}, {"players-multiple"}, {"players-creatures"}};
    }

    @Test(dataProvider = "queries", timeOut = 90000)
    public void aConcessionDuringACardQueryKeepsItsOriginalParticipantTraversal(String query) throws Exception {
        try (var scenario = new RulesScenario("card-query-concession-" + query, Map.of(
                0, List.of("Grizzly Bears"), 1, List.of("Llanowar Elves"),
                2, List.of("Elvish Mystic"), 3, List.of("Runeclaw Bear")))) {
            var main = scenario.until(input -> input.seat() == 0
                    && text(input.state(), "phaseKey").equals("MAIN1")
                    && text(input.prompt(), "inputType").equals("InputPassPriority"));
            Game game = scenario.table.game;
            var participants = new ArrayList<>(game.getPlayers());
            int departedId = participants.get(2).getId();
            String sessionId = text(main.state(), "id");
            List<Integer> expected = ids(read(query, game, participants.get(0))).stream()
                    .filter(id -> game.findById(id).getOwner().getId() != departedId).toList();
            var finished = new CountDownLatch(1);
            var listener = new Object() {
                @Subscribe public void gameFinished(GameEventGameFinished event) { finished.countDown(); }
            };
            game.subscribeToEvents(listener);
            try {
                List<Read> observed;
                try (var hook = new ZoneReads(participants, zones(query), () -> concede(scenario, 2, sessionId))) {
                    assertEquals(ids(read(query, game, participants.get(0))), expected,
                            "The query keeps its original ordering and returns the surviving cards after cleanup");
                    assertTrue(hook.conceded, "The query must execute the real one-shot concession callback");
                    observed = List.copyOf(hook.reads);
                }
                assertEquals(observed, expectedReads(query, participants),
                        "Every original participant is read once per requested zone, including the now-empty departed player");
                assertEquals(game.getPlayers().size(), 3);
                assertFalse(game.getPlayers().contains(participants.get(2)));
                assertSame(game.getCardsIn(ZoneType.Stack), game.getStackZone().getCards(),
                        "The single Stack query retains its existing direct view");
                assertSame(game.getCardsIncludePhasingIn(ZoneType.Stack), game.getStackZone().getCards());

                var ready = scenario.until(input -> input.seat() == 0
                        && text(input.state(), "phaseKey").equals("MAIN1")
                        && text(input.prompt(), "inputType").equals("InputPassPriority"));
                assertTrue(player(ready.state(), departedId).get("eliminated").getAsBoolean());
                assertTrue(scenario.state(2).get("prompt").isJsonNull());
                var forest = zone(viewer(ready.state()), "Hand").getAsJsonArray("cards").asList().stream()
                        .map(card -> card.getAsJsonObject()).filter(card -> text(card, "name").equals("Forest")
                                && card.get("selectable").getAsBoolean()).findFirst().orElseThrow();
                assertTrue(scenario.answer(ready, Map.of("action", "card", "key", text(forest, "key"))));
                var afterLand = scenario.until(input -> input.seat() == 0
                        && text(input.state(), "phaseKey").equals("MAIN1")
                        && text(input.prompt(), "inputType").equals("InputPassPriority")
                        && zone(viewer(input.state()), "Battlefield").getAsJsonArray("cards").asList().stream()
                        .map(card -> card.getAsJsonObject()).anyMatch(card -> text(card, "name").equals("Forest")));
                assertNotEquals(text(afterLand.prompt(), "id"), text(ready.prompt(), "id"));
                for (int seat = 0; seat < 4; seat++) assertPrivateZones(scenario.state(seat));

                // Visitors still stop immediately when the caller requests it.
                var visited = new ArrayList<Integer>();
                game.forEachCardInGame(card -> { visited.add(card.getId()); return false; }, true);
                assertEquals(visited.size(), 1);

                concede(scenario, 1, sessionId);
                concede(scenario, 3, sessionId);
                assertTrue(finished.await(5, TimeUnit.SECONDS), "The surviving game must release its game thread");
                for (int seat = 0; seat < 4; seat++) {
                    var snapshot = scenario.table.seat(seat).state();
                    assertNull(snapshot.get("error"));
                    assertNull(snapshot.get("prompt"));
                    var state = new Gson().toJsonTree(snapshot).getAsJsonObject();
                    assertEquals(text(state, "status"), "finished");
                    assertEquals(text(state, "result"), seat == 0 ? "Victory" : "Defeat");
                }
            } finally {
                game.unsubscribeFromEvents(listener);
            }
        }
    }

    private static CardCollectionView read(String query, Game game, Player owner) {
        return switch (query) {
            // This exact production constant is used by StaticAbilityAlternativeCost in the CI failure.
            case "static-zones" -> game.getCardsIn(ZoneType.STATIC_ABILITIES_SOURCE_ZONES);
            case "single-zone" -> game.getCardsIn(ZoneType.Battlefield);
            case "include-phasing" -> game.getCardsIncludePhasingIn(ZoneType.Battlefield);
            case "owned-zones" -> game.getCardsInOwnedBy(List.of(ZoneType.Battlefield, ZoneType.Graveyard, ZoneType.Stack), owner);
            case "colored" -> game.getColoredCardsInPlay(MagicColor.GREEN);
            case "visitor", "visitor-sideboard" -> {
                var cards = new CardCollection();
                game.forEachCardInGame(card -> { cards.add(card); return true; }, query.equals("visitor-sideboard"));
                yield cards;
            }
            case "copy-last-state" -> { game.copyLastState(); yield game.getLastStateBattlefield(); }
            case "copy-zone" -> game.copyLastState(ZoneType.Battlefield);
            case "players-single" -> game.getPlayers().getCardsIn(ZoneType.Battlefield);
            case "players-multiple" -> game.getPlayers().getCardsIn(List.of(ZoneType.Battlefield, ZoneType.Graveyard));
            case "players-creatures" -> game.getPlayers().getCreaturesInPlay();
            default -> throw new AssertionError(query);
        };
    }

    private static List<Integer> ids(CardCollectionView cards) {
        return cards.stream().map(Card::getId).toList();
    }

    private static List<ZoneType> zones(String query) {
        return switch (query) {
            case "static-zones" -> List.of(ZoneType.Graveyard, ZoneType.Battlefield, ZoneType.Exile, ZoneType.Command);
            case "owned-zones", "players-multiple", "copy-last-state" -> List.of(ZoneType.Battlefield, ZoneType.Graveyard);
            case "visitor", "visitor-sideboard" -> List.of(ZoneType.Graveyard, ZoneType.Hand, ZoneType.Library, ZoneType.Battlefield, ZoneType.Exile, ZoneType.Command, ZoneType.Sideboard);
            default -> List.of(ZoneType.Battlefield);
        };
    }

    private static List<Read> expectedReads(String query, List<Player> participants) {
        var expected = new ArrayList<Read>();
        boolean zoneMajor = query.equals("static-zones") || query.equals("owned-zones");
        if (zoneMajor) {
            for (ZoneType zone : zones(query)) for (Player player : participants) expected.add(expectedRead(query, player, zone));
        } else {
            for (Player player : participants) for (ZoneType zone : zones(query)) {
                if (!query.equals("visitor") || zone != ZoneType.Sideboard) expected.add(expectedRead(query, player, zone));
            }
        }
        return expected;
    }

    private static Read expectedRead(String query, Player player, ZoneType zone) {
        boolean filter = !query.equals("include-phasing") && !query.equals("owned-zones")
                && !((query.equals("visitor") || query.equals("visitor-sideboard")) && zone == ZoneType.Battlefield);
        return new Read(player.getId(), zone, filter);
    }

    private record Read(int playerId, ZoneType zone, boolean filter) { }

    /** One-shot observation only. Original zones are restored before ordinary concession
     * cleanup can use them. Nested reads from that cleanup delegate without triggering
     * or counting another hook. This controls membership timing, not atomic zone state. */
    private static final class ZoneReads implements AutoCloseable {
        private final List<Slot> slots = new ArrayList<>();
        private final List<Read> reads = new ArrayList<>();
        private boolean duringConcession;
        private boolean conceded;

        @SuppressWarnings("unchecked")
        ZoneReads(List<Player> participants, List<ZoneType> zones, Runnable concede) throws Exception {
            Field field = Player.class.getDeclaredField("zones");
            field.setAccessible(true);
            for (Player player : participants) {
                var map = (Map<ZoneType, PlayerZone>) field.get(player);
                for (ZoneType zone : zones) {
                    PlayerZone original = map.get(zone);
                    assertNotNull(original);
                    slots.add(new Slot(player, map, zone, original, concede));
                }
            }
            slots.forEach(Slot::install);
        }

        private final class Slot {
            private final Map<ZoneType, PlayerZone> map;
            private final ZoneType zone;
            private final PlayerZone original;
            private final PlayerZone wrapper;
            private boolean consumed;

            Slot(Player player, Map<ZoneType, PlayerZone> map, ZoneType zone, PlayerZone original, Runnable concede) {
                this.map = map;
                this.zone = zone;
                this.original = original;
                wrapper = new PlayerZone(zone, player) {
                    @Override public CardCollectionView getCards(boolean filter) {
                        if (duringConcession) return original.getCards(filter);
                        consumed = true;
                        restore();
                        reads.add(new Read(player.getId(), zone, filter));
                        if (!conceded) {
                            conceded = true;
                            // All cleanup uses the actual zones, including contains,
                            // remove, controller correction and zone-change events.
                            slots.forEach(Slot::restore);
                            duringConcession = true;
                            try { concede.run(); }
                            finally {
                                duringConcession = false;
                                slots.stream().filter(slot -> !slot.consumed).forEach(Slot::install);
                            }
                        }
                        return original.getCards(filter);
                    }
                };
            }

            void install() { map.put(zone, wrapper); }
            void restore() { map.put(zone, original); }
        }

        @Override public void close() { slots.forEach(Slot::restore); }
    }

    private static void concede(RulesScenario scenario, int seat, String sessionId) {
        var request = new JsonObject();
        request.addProperty("sessionId", sessionId);
        scenario.table.seat(seat).concede(request);
    }

    private static void assertPrivateZones(JsonObject state) {
        int viewerId = state.get("viewerId").getAsInt();
        for (var entry : state.getAsJsonArray("players")) {
            var player = entry.getAsJsonObject();
            assertTrue(zone(player, "Library").getAsJsonArray("cards").isEmpty());
            if (player.get("id").getAsInt() != viewerId) assertTrue(zone(player, "Hand").getAsJsonArray("cards").isEmpty());
        }
    }
}
