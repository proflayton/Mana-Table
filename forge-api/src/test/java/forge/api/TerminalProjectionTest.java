package forge.api;

import com.google.gson.Gson;
import com.google.gson.JsonObject;
import forge.StaticData;
import forge.deck.Deck;
import forge.deck.DeckSection;
import forge.game.zone.ZoneType;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.TimeUnit;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** A terminal registered player's real final board is readable, never actionable. */
public class TerminalProjectionTest {
    private static final Gson JSON = new Gson();

    @Test(timeOut = 90000)
    public void theLastCommanderDamageLossRetainsOnlyExactRegisteredFinalCardViews() throws Exception {
        lastCommanderDamageLoss(false);
    }

    @Test(timeOut = 90000)
    public void theLastLossRevealCannotReauthorizeEliminatedBattlefieldPortraits() throws Exception {
        lastCommanderDamageLoss(true);
    }

    private static void lastCommanderDamageLoss(boolean withManifest) throws Exception {
        var resources = Path.of("../forge-gui/res");
        if (StaticData.instance() == null) EngineResources.load(resources);
        var deck = new Deck("Terminal commander damage");
        deck.getMain().add(StaticData.instance().getCommonCards().getCard("Forest"), 99);
        deck.getOrCreate(DeckSection.Commander).add(StaticData.instance().getCommonCards().getCard("Rhys the Redeemed"));
        var players = java.util.stream.IntStream.range(0, 2)
                .mapToObj(index -> MatchTable.automated(deck, "Commander", "Policy " + index)).toList();
        players.get(0).addExtraCardsOnBattlefield(List.of("Raised by Giants", "Glorious Anthem", "True Conviction",
                "Concordant Crossroads", "Forest").stream().<forge.item.IPaperCard>map(name -> StaticData.instance().getCommonCards().getCard(name)).toList());
        players.get(1).addExtraCardsOnBattlefield(List.of("Grizzly Bears", "Plains").stream()
                .<forge.item.IPaperCard>map(name -> StaticData.instance().getCommonCards().getCard(name)).toList());
        Path evidence = ApiTestWorkspace.evidence(withManifest ? "terminal-loss-reveal" : "terminal-commander-damage");
        try (var table = new MatchTable("Commander", players, resources, evidence, true)) {
            table.start(); var answered = new HashMap<Integer, String>();
            var main = until(table, answered, input -> input.seat() == 0
                    && text(input.prompt(), "inputType").equals("InputPassPriority") && text(input.state(), "phaseKey").equals("MAIN1"));
            if (withManifest) {
                HeadlessPlatform.query(table, () -> {
                    // Controlled setup uses Reality Shift's real manifest operation;
                    // the loss, cleanup, historical disclosure and acknowledgement
                    // below all run through ordinary commander damage and the API.
                    var owner = table.game.getRegisteredPlayers().get(1);
                    var cause = forge.game.ability.AbilityFactory.getAbility("DB$ Manifest | DefinedPlayer$ You", owner.getCommanders().get(0));
                    cause.setActivatingPlayer(owner);
                    var manifested = owner.getCardsIn(ZoneType.Library).get(0).manifest(owner, cause, forge.game.ability.AbilityKey.newMap());
                    assertTrue(manifested.isManifested()); assertTrue(manifested.isFaceDown());
                    table.publish(); return null;
                });
                main = until(table, answered, input -> input.seat() == 0
                        && text(input.prompt(), "inputType").equals("InputPassPriority") && text(input.state(), "phaseKey").equals("MAIN1"));
            }
            use(table, answered, main, card(main.state(), "Command", "Rhys the Redeemed"));
            var payment = until(table, answered, input -> input.seat() == 0
                    && text(input.prompt(), "inputType").startsWith("InputPayMana"));
            use(table, answered, payment, card(payment.state(), "Battlefield", "Forest"));
            var attack = until(table, answered, input -> input.seat() == 0
                    && text(input.prompt(), "inputType").equals("InputAttack"));
            var commander = card(attack.state(), "Battlefield", "Rhys the Redeemed");
            assertEquals(commander.get("power").getAsInt(), 11);
            answer(table, answered, attack, Map.of("action", "attack", "attackerKey", text(commander, "key"), "defenderPlayerId", 1));
            attack = until(table, answered, input -> input.seat() == 0
                    && text(input.prompt(), "inputType").equals("InputAttack"));
            answer(table, answered, attack, Map.of("action", "ok"));
            var block = until(table, answered, input -> input.seat() == 1
                    && text(input.prompt(), "inputType").equals("InputBlock"));
            answer(table, answered, block, Map.of("action", "ok"));
            boolean sawLossReveal = false;
            long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(15);
            while (System.nanoTime() < deadline && !table.game.isGameOver()) {
                for (var seat : table.automatedSeats()) {
                    var state = JSON.toJsonTree(seat.state()).getAsJsonObject();
                    if (!state.has("prompt") || !state.get("prompt").isJsonObject()) continue;
                    var prompt = state.getAsJsonObject("prompt"); int owner = state.get("viewerId").getAsInt();
                    if (text(prompt, "id").equals(answered.get(owner))) continue;
                    if (text(prompt, "kind").equals("reveal")) {
                        assertTrue(withManifest); assertEquals(owner, 0);
                        assertFalse(table.game.isGameOver(), "The last-loss disclosure precedes the final outcome");
                        assertTrue(player(state, 1).get("eliminated").getAsBoolean());
                        assertFalse(table.game.getPlayers().contains(table.game.getRegisteredPlayers().get(1)));
                        Files.writeString(evidence.resolve("private-loss-reveal-first.json"), state.toString());
                        var historical = zone(player(state, 1), "Battlefield").getAsJsonArray("cards");
                        assertEquals(historical.size(), 3, "Registered historical portraits remain readable during the disclosure");
                        for (var value : historical) {
                            var face = value.getAsJsonObject();
                            assertEquals(text(face, "key"), "", "An eliminated historical portrait cannot acquire a current prompt handle");
                            assertFalse(face.get("selectable").getAsBoolean()); assertFalse(face.get("highlighted").getAsBoolean());
                            assertEquals(text(face, "playAffordability"), "Unknown");
                            assertEquals(text(face, "visualId"), ""); assertEquals(text(face, "combatId"), "");
                        }
                        assertTrue(prompt.getAsJsonArray("choices").asList().stream().anyMatch(value -> {
                            var face = value.getAsJsonObject().getAsJsonObject("card");
                            return face != null && text(face, "name").equals("Forest") && text(face, "key").isEmpty();
                        }), "The rules-required original face is still readable without an action handle");
                        var living = zone(player(state, 0), "Battlefield").getAsJsonArray("cards");
                        assertTrue(living.asList().stream().anyMatch(value -> !text(value.getAsJsonObject(), "key").isEmpty()),
                                "The unrelated current battlefield keeps its own current handles");
                        sawLossReveal = true;
                        answer(table, answered, new RulesScenario.Input(owner, state, prompt), Map.of("action", "ok"));
                        continue;
                    }
                    assertEquals(text(prompt, "inputType"), "InputPassPriority", "Only normal combat priority remains");
                    answer(table, answered, new RulesScenario.Input(owner, state, prompt), Map.of("action", "ok"));
                }
                Thread.sleep(3);
            }
            assertTrue(table.game.isGameOver(), "Actual commander damage must finish the two-player game");
            assertEquals(sawLossReveal, withManifest, "The regression must inspect the real pending loss-disclosure window");
            HeadlessPlatform.query(table, () -> null);
            table.publish();
            var loser = table.game.getRegisteredPlayers().get(1);
            assertTrue(loser.hasLost()); assertEquals(loser.getLife(), 18, "This is commander damage, not a life-zero loss");
            assertFalse(table.game.getPlayers().contains(loser), "The last loser has left the active rules-player set");
            assertEquals(loser.getCardsIn(ZoneType.Battlefield).size(), withManifest ? 3 : 2,
                    "The last two-player loss deliberately retains the registered player's actual board");
            var batch = table.automatedSeats().stream().map(MatchSession::automatedObservation).toList();
            var spectator = JSON.toJsonTree(table.spectatorState()).getAsJsonObject();
            var completion = spectator.getAsJsonObject("outcome");
            assertEquals(text(completion, "gameId"), table.id);
            assertEquals(text(completion, "kind"), "win");
            assertEquals(completion.getAsJsonArray("winningPlayerIds"), JSON.toJsonTree(List.of(0)));
            assertEquals(completion.getAsJsonArray("winningTeamIds"), JSON.toJsonTree(List.of(table.game.getRegisteredPlayers().get(0).getTeam())));
            Files.writeString(evidence.resolve("private-final-observations.json"), JSON.toJson(batch));
            Files.writeString(evidence.resolve("public-final-observation.json"), JSON.toJson(spectator));
            for (var row : batch) {
                var observation = JSON.toJsonTree(row).getAsJsonObject(); var state = observation.getAsJsonObject("state");
                assertEquals(text(state, "status"), "finished"); assertTrue(!state.has("prompt") || state.get("prompt").isJsonNull());
                assertEquals(state.get("outcome"), completion, "FFA seats and the independent public audience share one actual rules result");
                assertEquals(text(state, "result"), state.get("viewerId").getAsInt() == 0 ? "Victory" : "Defeat");
                for (var entry : state.getAsJsonArray("players")) {
                    var player = entry.getAsJsonObject();
                    for (var area : player.getAsJsonArray("zones")) for (var value : area.getAsJsonObject().getAsJsonArray("cards")) {
                        var face = value.getAsJsonObject();
                        assertEquals(text(face, "key"), ""); assertFalse(face.get("selectable").getAsBoolean());
                        assertFalse(face.get("highlighted").getAsBoolean());
                        if (text(area.getAsJsonObject(), "name").equals("Battlefield")) {
                            var publicFace = zone(player(spectator, player.get("id").getAsInt()), "Battlefield").getAsJsonArray("cards").asList().stream()
                                    .map(card -> card.getAsJsonObject()).filter(card -> text(card, "name").equals(text(face, "name"))).findFirst().orElseThrow();
                            assertFalse(text(face, "visualId").isEmpty());
                            assertEquals(text(face, "visualId"), text(publicFace, "visualId"));
                            assertEquals(text(face, "combatId"), text(publicFace, "combatId"));
                        }
                    }
                }
                var finalCards = zone(player(state, 1), "Battlefield").getAsJsonArray("cards");
                assertEquals(finalCards.size(), withManifest ? 3 : 2);
                for (var value : finalCards) {
                    var face = value.getAsJsonObject();
                    assertFalse(text(face, "visualId").isEmpty(), "An exact retained final-zone card remains identifiable for display");
                    assertEquals(text(face, "combatId"), text(face, "visualId"));
                    assertEquals(text(face, "key"), ""); assertFalse(face.get("selectable").getAsBoolean());
                    assertFalse(face.get("highlighted").getAsBoolean());
                }
                assertEquals(observation.getAsJsonObject("domain").getAsJsonArray("sources").size(), 0);
            }
            assertEquals(JSON.toJsonTree(table.automatedSeats().stream().map(MatchSession::automatedObservation).toList()), JSON.toJsonTree(batch),
                    "Another terminal poll cannot mint, rotate or remove final display identities");
            var finalState = table.automatedSeat(0).state();
            var oldAttack = attack;
            assertThrows(IllegalArgumentException.class, () -> table.automatedSeat(0).action(request(oldAttack, Map.of("action", "ok"))));
            assertEquals(table.automatedSeat(0).state(), finalState, "Retained final display cards cannot reopen an expired rules input");
        }
    }

    private static JsonObject card(JsonObject state, String zoneName, String name) {
        return zone(viewer(state), zoneName).getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "name").equals(name)).findFirst().orElseThrow();
    }
    private static RulesScenario.Input until(MatchTable table, Map<Integer, String> answered,
            java.util.function.Predicate<RulesScenario.Input> stop) throws Exception {
        long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(15);
        while (System.nanoTime() < deadline) {
            for (var seat : table.automatedSeats()) {
                var state = JSON.toJsonTree(seat.state()).getAsJsonObject();
                assertNotEquals(text(state, "status"), "error");
                if (!state.has("prompt") || !state.get("prompt").isJsonObject()) continue;
                var prompt = state.getAsJsonObject("prompt"); int owner = state.get("viewerId").getAsInt();
                if (text(prompt, "id").equals(answered.get(owner))) continue;
                var input = new RulesScenario.Input(owner, state, prompt);
                if (stop.test(input)) return input;
                String type = text(prompt, "inputType");
                if (state.get("turn").getAsInt() == 0 && type.equals("InputConfirm"))
                    answer(table, answered, input, Map.of("action", owner == 0 ? "ok" : "cancel"));
                else if (type.equals("InputPassPriority") || type.contains("Mulligan"))
                    answer(table, answered, input, Map.of("action", "ok"));
                else throw new AssertionError("Unexpected fixture input: " + prompt);
            }
            Thread.sleep(3);
        }
        throw new AssertionError("Terminal fixture did not reach its next input");
    }
    private static void use(MatchTable table, Map<Integer, String> answered, RulesScenario.Input input, JsonObject source) {
        var catalog = JSON.toJsonTree(table.automatedSeat(input.seat()).sourceActions(request(input, Map.of("key", text(source, "key"))))).getAsJsonObject();
        var action = catalog.getAsJsonArray("actions").get(0).getAsJsonObject();
        answer(table, answered, input, Map.of("action", "useSourceAction", "key", text(source, "key"), "actionId", text(action, "id")));
    }
    private static void answer(MatchTable table, Map<Integer, String> answered, RulesScenario.Input input, Map<String, ?> action) {
        table.automatedSeat(input.seat()).action(request(input, action)); answered.put(input.seat(), text(input.prompt(), "id"));
    }
    private static JsonObject request(RulesScenario.Input input, Map<String, ?> action) {
        var request = JSON.toJsonTree(action).getAsJsonObject();
        request.addProperty("sessionId", text(input.state(), "id")); request.addProperty("promptId", text(input.prompt(), "id")); return request;
    }
}
