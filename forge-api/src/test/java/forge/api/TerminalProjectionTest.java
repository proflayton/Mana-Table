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
        commanderCombat(false, false);
    }

    @Test(timeOut = 90000)
    public void aRealLosingBlockPreventsAnOtherwiseFatalCommanderHit() throws Exception {
        commanderCombat(true, false);
    }

    @Test(timeOut = 90000)
    public void theLastLossRevealCannotReauthorizeEliminatedBattlefieldPortraits() throws Exception {
        commanderCombat(false, true);
    }

    private static void commanderCombat(boolean blocks, boolean withManifest) throws Exception {
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
        Path evidence = ApiTestWorkspace.evidence(blocks ? "blocked-commander-damage"
                : withManifest ? "terminal-loss-reveal" : "terminal-commander-damage");
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
            String commanderId = text(commander, "commanderId");
            assertFalse(commanderId.isEmpty());
            assertEquals(attack.state().getAsJsonObject("commanderCombat").get("damageLimit").getAsInt(), 21);
            var attackStage = new CombatStage("InputAttack", text(attack.state().getAsJsonObject("combat"), "declarationId"),
                    text(commander, "combatId"), 1, "");
            assertFalse(attackStage.matches(attack.state()), "An offered attacker is not an assigned attack");
            attack = stage(table, attack, Map.of("action", "attack", "attackerKey", text(commander, "key"), "defenderPlayerId", 1),
                    attackStage, evidence, "attack");
            assertFalse(new CombatStage(attackStage.inputType(), attackStage.declarationId(), attackStage.attackerId(), 0, "")
                    .matches(attack.state()), "A staged attack cannot certify a different defender");
            answer(table, answered, attack, Map.of("action", "ok"));
            var block = until(table, answered, input -> input.seat() == 1
                    && text(input.prompt(), "inputType").equals("InputBlock"));
            if (blocks) {
                var currentAttacker = zone(player(block.state(), 0), "Battlefield").getAsJsonArray("cards").asList().stream()
                        .map(value -> value.getAsJsonObject()).filter(face -> text(face, "commanderId").equals(commanderId)).findFirst().orElseThrow();
                var blocker = card(block.state(), "Battlefield", "Grizzly Bears");
                assertFalse(text(blocker, "combatId").isEmpty(), "The exact current blocker has a public physical identity");
                var blockStage = new CombatStage("InputBlock", text(block.state().getAsJsonObject("combat"), "declarationId"),
                        text(currentAttacker, "combatId"), 1, text(blocker, "combatId"));
                assertFalse(blockStage.matches(block.state()), "An eligible blocker without an assigned pair is incomplete");
                block = stage(table, block, Map.of("action", "block", "attackerKey", text(currentAttacker, "key"), "blockerKey", text(blocker, "key")),
                        blockStage, evidence, "block");
                assertFalse(new CombatStage(blockStage.inputType(), blockStage.declarationId(), blockStage.blockerId(), 1, blockStage.attackerId())
                        .matches(block.state()), "The opposite attacker/blocker pair is not the chosen assignment");
                assertFalse(new CombatStage(blockStage.inputType(), blockStage.declarationId(), blockStage.attackerId(), 1,
                        text(card(block.state(), "Battlefield", "Plains"), "combatId")).matches(block.state()),
                        "The correct attacker with a different physical card cannot certify the block");
                assertFalse(new CombatStage(blockStage.inputType(), attackStage.declarationId(), blockStage.attackerId(), 1, blockStage.blockerId())
                        .matches(block.state()), "A prior attack declaration cannot certify the current block input");
                var repeated = staged(table, block, blockStage, evidence, "already-staged-block");
                assertEquals(text(repeated.prompt(), "id"), text(block.prompt(), "id"),
                        "Reading the actual already-staged pair again accepts its same current input without another toggle command");
                block = repeated;
            }
            answer(table, answered, block, Map.of("action", "ok"));
            if (blocks) {
                var next = until(table, answered, input -> input.seat() == 1 && input.state().get("turn").getAsInt() >= 2
                        && text(input.prompt(), "inputType").equals("InputPassPriority") && text(input.state(), "phaseKey").equals("MAIN1"));
                var defender = table.game.getRegisteredPlayers().get(1);
                assertFalse(defender.hasLost()); assertEquals(defender.getLife(), 40);
                assertTrue(defender.getCardsIn(ZoneType.Graveyard).stream().anyMatch(card -> card.getName().equals("Grizzly Bears")),
                        "The actual legal block sacrifices its body rather than faking prevention");
                var damage = player(next.state(), 1).getAsJsonArray("commanderDamage").asList().stream().map(value -> value.getAsJsonObject())
                        .filter(row -> text(row, "commanderId").equals(commanderId)).findFirst().orElseThrow();
                assertEquals(damage.get("damage").getAsInt(), 0);
                assertFalse(table.game.isGameOver(), "The otherwise fatal22 commander damage was prevented by the actual block");
                return;
            }
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
            assertTrue(table.commanderCombat.damage(loser).stream().anyMatch(row -> commanderId.equals(row.get("commanderId"))
                    && Integer.valueOf(22).equals(row.get("damage"))), "The actual fatal hit retains its exact commander lineage and22 damage");
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

    private record CombatStage(String inputType, String declarationId, String attackerId, int defenderId, String blockerId) {
        boolean matches(JsonObject state) {
            if (declarationId.isEmpty() || attackerId.isEmpty() || !state.has("combat") || !state.get("combat").isJsonObject()) return false;
            var combat = state.getAsJsonObject("combat");
            if (!declarationId.equals(text(combat, "declarationId"))) return false;
            return combat.getAsJsonArray("attackers").asList().stream().map(value -> value.getAsJsonObject()).anyMatch(attacker -> {
                if (!attackerId.equals(text(attacker, "cardId")) || !attacker.has("defender") || !attacker.get("defender").isJsonObject()) return false;
                var defender = attacker.getAsJsonObject("defender");
                return text(defender, "kind").equals("player") && defender.get("id").getAsInt() == defenderId
                        && (blockerId.isEmpty() || attacker.getAsJsonArray("blockerIds").asList().stream().anyMatch(id -> id.getAsString().equals(blockerId)));
            });
        }
    }

    private static RulesScenario.Input stage(MatchTable table, RulesScenario.Input input, Map<String, ?> action,
            CombatStage expected, Path evidence, String name) throws Exception {
        Files.writeString(evidence.resolve("private-" + name + "-before.json"), JSON.toJson(Map.of("state", input.state(), "action", action, "expected", expected)));
        // Staging is not input completion. Submit this toggle once, then witness
        // its actual pair; an ACK alone cannot authorize another toggle or OK.
        var accepted = table.automatedSeat(input.seat()).action(request(input, action));
        Files.writeString(evidence.resolve("private-" + name + "-ack.json"), JSON.toJson(accepted));
        return staged(table, input, expected, evidence, name);
    }

    private static RulesScenario.Input staged(MatchTable table, RulesScenario.Input input, CombatStage expected, Path evidence, String name) throws Exception {
        long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(15);
        List<Map<String, Object>> last = List.of();
        try {
            // Observe after admitted owner work without publishing, answering
            // another actor or requiring a replacement prompt identity.
            HeadlessPlatform.query(table, () -> null);
            while (System.nanoTime() < deadline) {
                last = table.automatedSeats().stream().map(MatchSession::state).toList();
                for (var row : last) {
                    var state = JSON.toJsonTree(row).getAsJsonObject();
                    assertNotEquals(text(state, "status"), "error");
                    if (!text(state, "id").equals(text(input.state(), "id")) || state.get("viewerId").getAsInt() != input.seat()
                            || !state.has("prompt") || !state.get("prompt").isJsonObject()) continue;
                    var prompt = state.getAsJsonObject("prompt");
                    if (text(prompt, "inputType").equals(expected.inputType()) && prompt.get("okEnabled").getAsBoolean() && expected.matches(state)) {
                        Files.writeString(evidence.resolve("private-" + name + "-readback.json"), state.toString());
                        return new RulesScenario.Input(input.seat(), state, prompt);
                    }
                }
                Thread.sleep(3);
            }
            Files.writeString(evidence.resolve("private-" + name + "-timeout.json"), JSON.toJson(Map.of("expected", expected, "views", last)));
            throw new AssertionError("The single admitted combat stage did not publish its exact assignment: " + expected);
        } finally {
            // One bounded last cohort, not an unbounded poll-by-poll history.
            Files.writeString(evidence.resolve("private-" + name + "-last.json"), JSON.toJson(last));
        }
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
        if (action.get("action").equals("attack") || action.get("action").equals("block"))
            throw new IllegalArgumentException("Staged combat needs assignment readback, not a consumed input ID");
        table.automatedSeat(input.seat()).action(request(input, action)); answered.put(input.seat(), text(input.prompt(), "id"));
    }
    private static JsonObject request(RulesScenario.Input input, Map<String, ?> action) {
        var request = JSON.toJsonTree(action).getAsJsonObject();
        request.addProperty("sessionId", text(input.state(), "id")); request.addProperty("promptId", text(input.prompt(), "id")); return request;
    }
}
