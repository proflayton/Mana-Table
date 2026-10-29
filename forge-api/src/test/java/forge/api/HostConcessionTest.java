package forge.api;

import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import forge.StaticData;
import forge.deck.Deck;
import forge.deck.DeckSection;
import forge.game.ability.ApiType;
import forge.game.card.Card;
import forge.game.zone.ZoneType;
import forge.gamemodes.match.input.InputSelectTargets;
import forge.player.PlayerControllerHuman;
import org.testng.annotations.DataProvider;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.nio.file.Path;
import java.time.Duration;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.Executors;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.Future;
import java.util.concurrent.TimeUnit;
import java.util.function.Predicate;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Host transport ownership must outlive that player's ordinary rules concession. */
public class HostConcessionTest {
    private static final Gson JSON = new GsonBuilder().serializeNulls().setPrettyPrinting().create();
    private static final Path RESOURCES = Path.of("../forge-gui/res");

    @DataProvider public Object[][] foreignOwners() { return new Object[][]{{"input"}, {"choice"}}; }

    @Test(timeOut = 90000)
    public void admissionFromTheRulesExecutorIsStillDeferredAndExplicitStopWins() throws Exception {
        if (StaticData.instance() == null) EngineResources.load(RESOURCES);
        try (var table = new MatchTable("Constructed", List.of(
                MatchTable.human(new Deck(), "Constructed", "Host"),
                MatchTable.human(new Deck(), "Constructed", "Guest")), RESOURCES,
                Files.createTempDirectory("host-concession-closed"))) {
            var result = new CompletableFuture<Void>();
            forge.util.ThreadUtil.invokeInGameThread(() -> {
                try {
                    table.game.getAction().withState(() -> {
                        assertTrue(forge.util.ThreadUtil.isGameThread());
                        var request = object(Map.of("sessionId", table.id));
                        var admitted = table.seat(0).concede(request);
                        assertEquals(admitted.get("status"), "resolving");
                        assertNull(admitted.get("prompt")); assertNull(admitted.get("result"));
                        assertFalse(table.game.getRegisteredPlayers().get(0).conceded(),
                                "Admission never runs inline even when its caller already owns rules state");
                        assertEquals(table.seat(0).concede(request), admitted, "Repeated admission is idempotent");
                        table.close();
                    });
                    result.complete(null);
                } catch (Throwable failure) { result.completeExceptionally(failure); }
            });
            result.get(3, TimeUnit.SECONDS);
            assertTrue(table.game.isGameOver());
            assertFalse(table.game.getRegisteredPlayers().stream().anyMatch(player -> player.conceded()),
                    "An explicitly stopped table cannot turn queued consent into a later rules concession");
        }
    }

    @Test(dataProvider = "foreignOwners", timeOut = 90000)
    public void aHostConcessionReturnsBeforeSurvivorsAcknowledgeItsFaceDownCards(String foreignOwner) throws Exception {
        if (StaticData.instance() == null) EngineResources.load(RESOURCES);
        var players = List.of(MatchTable.human(new Deck(), "Constructed", "Host"),
                MatchTable.human(new Deck(), "Constructed", "Guest"),
                MatchTable.automated(new Deck(), "Constructed", "Policy 2"),
                MatchTable.automated(new Deck(), "Constructed", "Policy 3"));
        var workers = Executors.newCachedThreadPool(task -> {
            var worker = new Thread(task, "host-concession-reveal"); worker.setDaemon(true); return worker;
        });
        var table = new MatchTable("Constructed", players, RESOURCES, Files.createTempDirectory("host-concession-reveal"));
        Future<?> waiting = null, conceding = null;
        Throwable firstFailure = null;
        try {
            HeadlessPlatform.activate(table);
            var owner = table.game.getRegisteredPlayers().get(0);
            var actor = table.game.getRegisteredPlayers().get(1);
            var ownedCards = new ArrayList<Card>();
            for (var zone : List.of(ZoneType.Battlefield, ZoneType.Exile)) {
                var concealed = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard("Grizzly Bears"), owner);
                owner.getZone(zone).add(concealed); assertTrue(concealed.turnFaceDown(true));
                ownedCards.add(concealed);
            }
            var target = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard("Llanowar Elves"), actor);
            actor.getZone(ZoneType.Battlefield).add(target);
            var source = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard("Flame Slash"), actor);
            actor.getZone(ZoneType.Hand).add(source);
            var ability = source.getSpellAbilities().stream().filter(part -> part.getApi() == ApiType.DealDamage).findFirst().orElseThrow();
            ability.setActivatingPlayer(actor);
            var input = new InputSelectTargets((PlayerControllerHuman)actor.getController(), List.of(target, ownedCards.get(0)), ability,
                    false, null, null, candidate -> true, false);
            waiting = workers.submit(() -> table.game.getAction().readState(() -> {
                if (foreignOwner.equals("input")) { input.showAndWait(); return null; }
                return actor.getController().chooseNumber(ability, "Original required choice", 0, 1);
            }));
            var guest = table.seat(1);
            long openingDeadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(3);
            JsonObject before = object(guest.state());
            while (before.get("prompt").isJsonNull() && System.nanoTime() < openingDeadline) {
                Thread.sleep(5); before = object(guest.state());
            }
            assertEquals(text(before.getAsJsonObject("prompt"), "kind"), foreignOwner);
            assertFalse(before.toString().contains("Grizzly Bears"), "The original face is private before ordinary loss revelation");
            var oldPrompt = before.getAsJsonObject("prompt");
            var command = object(Map.of("sessionId", table.id));
            conceding = workers.submit(() -> table.seat(0).concede(command));
            conceding.get(1, TimeUnit.SECONDS);
            // Idempotence also applies while actual disclosure is still waiting.
            table.seat(0).concede(command);
            long cleanupDeadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(3);
            while (table.game.getAction().readState(() -> table.game.getPlayers().contains(owner)) && System.nanoTime() < cleanupDeadline)
                Thread.sleep(5);
            assertEquals(table.game.getAction().readState(() -> table.game.getPlayers().size()), Integer.valueOf(3));
            assertTrue(table.game.getAction().readState(() -> ownedCards.stream().allMatch(card -> card.isInZone(ZoneType.None))),
                    "Owned cards and membership finish cleanup before any captured revelation acknowledgement");
            if (foreignOwner.equals("choice")) {
                var retained = object(guest.state()).getAsJsonObject("prompt");
                assertEquals(text(retained, "id"), text(oldPrompt, "id"), "A waiting foreign dialog retains its actual response owner");
                assertFalse(waiting.isDone());
                guest.action(object(Map.of("sessionId", table.id, "promptId", text(retained, "id"), "choices", List.of(1))));
                assertEquals(waiting.get(1, TimeUnit.SECONDS), Integer.valueOf(1), "The original required choice completes exactly as answered");
            }
            var survivors = new ArrayList<MatchEndpoint>(List.of(guest)); survivors.addAll(table.automatedSeats());
            var acknowledged = new HashMap<Integer, Integer>();
            var answered = new HashMap<Integer, String>();
            long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(5);
            while (System.nanoTime() < deadline && acknowledged.values().stream().mapToInt(Integer::intValue).sum() < 6) {
                for (int seat = 0; seat < survivors.size(); seat++) {
                    var state = object(survivors.get(seat).state());
                    assertNotEquals(text(state, "status"), "error", state.toString());
                    if (state.get("prompt").isJsonNull()) continue;
                    var prompt = state.getAsJsonObject("prompt");
                    if (!text(prompt, "kind").equals("reveal") || text(prompt, "id").equals(answered.get(seat))) continue;
                    assertTrue(prompt.toString().contains("Grizzly Bears"), "The actual rules reveal discloses the owned original face");
                    for (var row : prompt.getAsJsonArray("choices")) {
                        var item = row.getAsJsonObject(); var face = item.getAsJsonObject("card");
                        assertFalse(face.has("key") || face.has("visualId") || face.has("combatId"),
                                "A copied ceased-card face never gains current action or occurrence handles");
                        assertTrue(!item.has("cardId") || item.get("cardId").isJsonNull() || text(item, "cardId").isEmpty());
                    }
                    var reply = object(Map.of("sessionId", table.id, "promptId", text(prompt, "id")));
                    survivors.get(seat).action(reply); answered.put(seat, text(prompt, "id"));
                    acknowledged.merge(seat, 1, Integer::sum);
                }
                Thread.sleep(5);
            }
            assertEquals(acknowledged, Map.of(0, 2, 1, 2, 2, 2), "Every survivor sees each actual battlefield/exile revelation once");
            long settledDeadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(3);
            while (table.game.getAction().readState(() -> table.game.getPlayers().contains(owner)) && System.nanoTime() < settledDeadline)
                Thread.sleep(5);
            assertEquals(table.game.getAction().readState(() -> table.game.getPlayers().size()), Integer.valueOf(3));
            assertFalse(table.game.isGameOver());
            assertTrue(owner.hasLost());
            assertNull(table.seat(0).state().get("prompt"));
            if (foreignOwner.equals("input")) {
                long restoredDeadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(3);
                var restored = object(guest.state());
                while (restored.get("prompt").isJsonNull() && System.nanoTime() < restoredDeadline) {
                    Thread.sleep(5); restored = object(guest.state());
                }
                assertEquals(text(restored.getAsJsonObject("prompt"), "inputType"), "InputSelectTargets");
                assertNotEquals(text(restored.getAsJsonObject("prompt"), "id"), text(oldPrompt, "id"));
                var offered = zone(viewer(restored), "Battlefield").getAsJsonArray("cards").asList().stream()
                        .map(row -> row.getAsJsonObject()).filter(row -> row.get("selectable").getAsBoolean()).toList();
                assertEquals(offered.stream().map(row -> text(row, "name")).toList(), List.of("Llanowar Elves"));
                assertFalse(player(restored, owner.getId()).getAsJsonArray("zones").asList().stream()
                        .flatMap(zone -> zone.getAsJsonObject().getAsJsonArray("cards").asList().stream())
                        .anyMatch(card -> card.getAsJsonObject().get("selectable").getAsBoolean()), "Ceased owned cards are never offered again");
                guest.action(object(Map.of("sessionId", table.id,
                        "promptId", text(restored.getAsJsonObject("prompt"), "id"), "action", "card", "key", text(offered.get(0), "key"))));
                waiting.get(1, TimeUnit.SECONDS);
                assertTrue(ability.getTargets().getTargetCards().contains(target));
                assertTrue(ability.getTargets().getTargetCards().stream().noneMatch(ownedCards::contains));
            }
            var stale = object(Map.of("sessionId", table.id, "promptId", text(oldPrompt, "id"), "action", "ok"));
            assertThrows(IllegalArgumentException.class, () -> guest.action(stale));
        } catch (Exception | AssertionError failure) {
            firstFailure = failure;
            throw failure;
        } finally {
            if (conceding != null && !conceding.isDone()) conceding.cancel(true);
            try { workers.submit(table::close).get(2, TimeUnit.SECONDS); }
            catch (Exception cleanup) {
                if (waiting != null) waiting.cancel(true);
                if (firstFailure != null) firstFailure.addSuppressed(cleanup);
                else throw cleanup;
            } finally { workers.shutdownNow(); }
        }
    }

    @Test(timeOut = 90000)
    public void concedingTheHostSeatKeepsItsGuestAndTwoPolicySeatsPlaying() throws Exception {
        if (StaticData.instance() == null) EngineResources.load(RESOURCES);
        Path evidenceRoot = ApiTestWorkspace.evidence("host-concession-network");
        Path evidence = Files.createTempDirectory(evidenceRoot, "host-concession-network-");
        var cards = CardCatalog.fromDatabases(StaticData.instance().getAvailableDatabases().values());
        var documents = new DeckDocumentSession(cards, evidence.resolve("saved"), new DeckPlaytests(evidence.resolve("saved")));
        documents.importDeck("Concession fixture", "Deck\n99 Forest\nCommander\n1 Rhys the Redeemed", "Commander");
        var opponents = new AiDeckCatalog(cards, documents);
        String opponent = opponents.choices("Commander").stream().filter(row -> row.get("id").startsWith("deck:"))
                .findFirst().orElseThrow().get("id");
        var host = new SyncLobby(RESOURCES, evidence, true, opponents);
        var guest = new SyncLobby(RESOURCES, evidence, true);
        var trace = new ArrayList<Object>();
        try {
            var lobby = object(host.host("Commander", 4, false, "mixed"));
            String address = lobby.getAsJsonArray("addresses").asList().stream().map(row -> row.getAsJsonObject())
                    .filter(row -> text(row, "label").equals("This computer")).findFirst().orElseThrow().get("url").getAsString();
            guest.join(address);
            var deck = new Deck("Concession fixture");
            deck.getMain().add(StaticData.instance().getCommonCards().getCard("Forest"), 99);
            deck.getOrCreate(DeckSection.Commander).add(StaticData.instance().getCommonCards().getCard("Rhys the Redeemed"));
            host.selectDeck(deck, "Commander"); guest.selectDeck(deck, "Commander");
            for (int seat = 2; seat < 4; seat++) host.setSeat(object(Map.of("tableId", text(lobby, "tableId"),
                    "seat", seat, "type", "AI", "opponentId", opponent)));
            host.ready(true); guest.ready(true); host.start();
            var endpoints = new ArrayList<MatchEndpoint>(List.of(host.match(), guest.match()));
            endpoints.addAll(host.automatedSeats());
            assertEquals(endpoints.size(), 4, "The actual lobby owns two human and two private policy controllers");
            var answered = new HashMap<Integer, String>();
            var before = until(endpoints, answered, trace, states -> main(states.get(0)));
            var hostPrompt = before.get(0).getAsJsonObject("prompt");
            int departedId = before.get(0).get("viewerId").getAsInt();
            var concession = object(Map.of("sessionId", text(before.get(0), "id")));
            var receipt = object(endpoints.get(0).concede(concession));
            trace.add(Map.of("hostConcession", receipt));
            assertTrue(receipt.get("prompt").isJsonNull(), "The departed host no longer owns an input");
            assertNotEquals(text(receipt, "status"), "finished", "Admission does not invent a global rules outcome");
            assertTrue(receipt.get("result").isJsonNull());
            assertFalse(endpoints.get(0).finished(), "Concession is not process or shared-table shutdown");
            var stale = object(Map.of("sessionId", text(before.get(0), "id"), "promptId", text(hostPrompt, "id"), "action", "ok"));
            assertThrows(IllegalArgumentException.class, () -> endpoints.get(0).action(stale));
            answered.clear();
            var after = until(endpoints, answered, trace, states -> main(states.get(1)));
            for (var state : after) {
                assertEquals(text(state, "id"), text(before.get(0), "id"));
                assertEquals(text(state, "status"), "playing");
                assertTrue(state.get("error").isJsonNull());
                assertTrue(player(state, departedId).get("eliminated").getAsBoolean());
                assertEquals(state.getAsJsonArray("players").asList().stream()
                        .filter(row -> !row.getAsJsonObject().get("eliminated").getAsBoolean()).count(), 3L);
            }
            assertTrue(after.get(0).get("prompt").isJsonNull());
            for (int seat = 1; seat < 4; seat++) assertTrue(answered.containsKey(seat),
                    "Every surviving human/policy controller answers a fresh real decision after the host's concession");
            assertEquals(text(object(host.state()), "mode"), "hosting");
            assertEquals(text(object(guest.state()), "mode"), "joined");
            assertTrue(object(guest.state()).get("matchActive").getAsBoolean());
            assertTrue(object(guest.reconnect()).get("matchActive").getAsBoolean(), "The network seat resumes while its conceded host keeps serving");
            assertEquals(object(guest.match().state()).get("viewerId"), after.get(1).get("viewerId"));
            assertThrows(IllegalArgumentException.class, guest::returnToLobby);
        } finally {
            Files.writeString(evidence.resolve("trace.json"), JSON.toJson(trace));
            guest.close(); host.close();
        }
    }

    private static boolean main(JsonObject state) {
        return text(state, "phaseKey").equals("MAIN1") && state.get("activePlayerId").equals(state.get("viewerId"))
                && !state.get("prompt").isJsonNull() && text(state.getAsJsonObject("prompt"), "inputType").equals("InputPassPriority");
    }

    private static JsonObject object(Object value) { return JSON.toJsonTree(value).getAsJsonObject(); }

    private static List<JsonObject> until(List<MatchEndpoint> endpoints, Map<Integer, String> answered,
                                          List<Object> trace, Predicate<List<JsonObject>> stop) throws Exception {
        long deadline = System.nanoTime() + Duration.ofSeconds(30).toNanos();
        while (System.nanoTime() < deadline) {
            var states = endpoints.stream().map(endpoint -> object(endpoint.state())).toList();
            for (var state : states) assertNotEquals(text(state, "status"), "error", state.toString());
            if (stop.test(states)) return states;
            for (int seat = 0; seat < endpoints.size(); seat++) {
                var state = states.get(seat);
                if (state.get("prompt").isJsonNull()) continue;
                var prompt = state.getAsJsonObject("prompt");
                if (text(prompt, "id").equals(answered.get(seat))) continue;
                Map<String, ?> reply;
                if (state.get("turn").getAsInt() == 0 && prompt.has("playerChoices") && !prompt.getAsJsonArray("playerChoices").isEmpty())
                    reply = Map.of("action", "player", "playerId", states.get(0).get("viewerId").getAsInt());
                else if (prompt.has("okEnabled") && prompt.get("okEnabled").getAsBoolean()) reply = Map.of("action", "ok");
                else throw new AssertionError("Unhandled concession fixture decision: " + prompt);
                var request = object(reply);
                request.addProperty("sessionId", text(state, "id")); request.addProperty("promptId", text(prompt, "id"));
                try {
                    endpoints.get(seat).action(request);
                    answered.put(seat, text(prompt, "id"));
                    trace.add(Map.of("seat", seat, "state", state, "reply", request));
                    assertTrue(trace.size() < 256, "The concession journey stays bounded");
                } catch (IllegalArgumentException stale) {
                    if (!"That choice has changed. Use the current prompt.".equals(stale.getMessage())) throw stale;
                }
            }
            Thread.sleep(5);
        }
        throw new AssertionError("Host concession did not reach the next real turn: "
                + endpoints.stream().map(MatchEndpoint::state).toList());
    }
}
