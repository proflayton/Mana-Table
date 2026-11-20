package forge.api;

import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import forge.game.zone.ZoneType;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.Map;
import java.util.Set;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

public class StackTargetProjectionTest {
    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void aRealPaidSpellKeepsItsAnonymousManifestTargetForEveryAudience() throws Exception {
        try (var scenario = new RulesScenario("public-anonymous-manifest-target",
                Map.of(0, List.of("Plains", "Reliquary Tower")), "Path to Exile", "Rhys the Redeemed",
                Map.of(1, Collections.nCopies(99, "Willbender")))) {
            var opening = scenario.until(input -> text(input.prompt(), "inputType").equals("InputSelectEntitiesFromList")
                    && input.prompt().getAsJsonArray("playerChoices").size() == 4);
            int owner = scenario.table.game.getRegisteredPlayers().get(1).getId();
            int caster = scenario.table.game.getRegisteredPlayers().get(0).getId();
            HeadlessPlatform.query(scenario.table, () -> {
                assertNull(scenario.table.game.getPhaseHandler().getPlayerTurn());
                var registered = scenario.table.game.getRegisteredPlayers().get(1).getRegisteredPlayer();
                // Actual rules effects author the initial occurrences; no live card or face is injected.
                registered.addStartGameScript(List.of("DB$ Manifest | Amount$ 1"));
                registered.addStartGameScript(List.of("DB$ Dig | Defined$ You | DigNum$ 1 | ChangeNum$ All"
                        + " | DestinationZone$ Exile | ExileFaceDown$ True"));
                return null;
            });
            assertTrue(scenario.answer(opening, Map.of("action", "player", "playerId", caster)));
            var main = main(scenario);
            var recipient = card(main.state(), owner, "Face-down card");
            String recipientId = text(recipient, "combatId");
            assertFalse(recipientId.isBlank(), "The existing public battlefield occurrence already has an opaque identity");
            HeadlessPlatform.query(scenario.table, () -> {
                var game = scenario.table.game;
                var targetOwner = game.getRegisteredPlayers().get(1);
                var manifest = targetOwner.getCardsIn(ZoneType.Battlefield).get(0);
                assertTrue(manifest.isManifested(), "The normal startup Manifest effect produced the actual permanent");
                assertTrue(manifest.isFaceDown());
                var hiddenExile = targetOwner.getCardsIn(ZoneType.Exile);
                assertEquals(hiddenExile.size(), 1);
                assertTrue(hiddenExile.get(0).isFaceDown(), "The separate normal Dig effect exiled a real card face down");
                var observer = game.getRegisteredPlayers().get(0).getView();
                var activity = new MatchActivity(observer, scenario.table.identities,
                        view -> CurrentCardViews.isCurrent(game, view));
                var identities = new CombatCardIds(observer, activity, scenario.table.identities);
                assertEquals(StackTargetProjection.card(game, manifest.getView(), identities::id).id(), recipientId,
                        "Anonymous target admission uses the same existing public battlefield identity");
                for (var hidden : List.of(targetOwner.getCardsIn(ZoneType.Hand).get(0),
                        targetOwner.getCardsIn(ZoneType.Library).get(0), hiddenExile.get(0))) {
                    assertTrue(CurrentCardViews.isCurrent(game, hidden.getView()), "The negative is a real current occurrence");
                    assertFalse(hidden.getView().canBeShownTo(observer));
                    assertNull(StackTargetProjection.card(game, hidden.getView(), identities::id),
                            "An anonymous battlefield target must not disclose hidden hand, library or face-down exile membership");
                }
                return null;
            });
            var spell = zone(viewer(main.state()), "Hand").getAsJsonArray("cards").asList().stream()
                    .map(value -> value.getAsJsonObject()).filter(value -> text(value, "name").equals("Path to Exile"))
                    .findFirst().orElseThrow();
            assertTrue(scenario.answer(main, Map.of("action", "card", "key", text(spell, "key"))));
            var targeting = scenario.until(input -> text(input.prompt(), "inputType").equals("InputSelectTargets"));
            recipient = card(targeting.state(), owner, "Face-down card");
            assertEquals(text(recipient, "combatId"), recipientId);
            assertTrue(recipient.get("selectable").getAsBoolean(), "The actual spell offers this face-down creature as a legal target");
            assertTrue(scenario.answer(targeting, Map.of("action", "card", "key", text(recipient, "key"))));
            var stacked = payAndStack(scenario);
            assertTrue(card(stacked.state(), caster, "Plains").get("tapped").getAsBoolean(),
                    "The actual white source paid for Path before its committed target was projected");
            String pathExecutionId = HeadlessPlatform.query(scenario.table, () -> {
                var json = new GsonBuilder().serializeNulls().setPrettyPrinting().create();
                var states = new ArrayList<JsonObject>();
                for (int seat = 0; seat < 4; seat++) states.add(json.toJsonTree(scenario.table.seat(seat).state()).getAsJsonObject());
                states.add(json.toJsonTree(SpectatorObservation.project(scenario.table,
                        states.get(0).get("boardRevision").getAsLong(), null)).getAsJsonObject());
                for (var state : states) {
                    var stack = state.getAsJsonArray("stack").asList().stream().map(value -> value.getAsJsonObject())
                            .filter(item -> text(item, "name").equals("Path to Exile")).findFirst().orElseThrow();
                    var targets = stack.getAsJsonArray("targets");
                    assertEquals(targets.size(), 1, "Every audience retains the real committed anonymous target");
                    var target = targets.get(0).getAsJsonObject();
                    assertEquals(target.keySet(), Set.of("kind", "id", "name", "playerId"),
                            "A committed display reference carries no response key, action or printed face");
                    assertEquals(text(target, "kind"), "card");
                    assertEquals(text(target, "id"), recipientId);
                    assertEquals(text(target, "name"), "Face-down card");
                    assertEquals(target.get("playerId").getAsInt(), owner);
                    var portrait = card(state, owner, "Face-down card");
                    assertEquals(text(portrait, "combatId"), recipientId);
                    assertTrue(portrait.get("faceDown").getAsBoolean());
                    assertEquals(text(portrait, "manaCost"), ""); assertEquals(text(portrait, "text"), "");
                    assertEquals(portrait.get("power").getAsInt(), 2); assertEquals(portrait.get("toughness").getAsInt(), 2);
                    for (String privateField : List.of("faces", "artName", "artFace", "artSet", "edition", "collectorNumber"))
                        assertFalse(portrait.has(privateField), "The canonical face-down portrait cannot identify its printing: " + privateField);
                    assertFalse(json.toJson(portrait).contains("Willbender"));
                    assertTrue(zone(player(state, owner), "Exile").getAsJsonArray("cards").isEmpty(),
                            "The separate face-down exile occurrence remains undisclosed");
                    if (state.get("viewerId").getAsInt() == -1) {
                        assertFalse(json.toJson(state).contains("Willbender"), "The spectator never learns the hidden printed card");
                        for (var member : state.getAsJsonArray("players")) {
                            assertTrue(zone(member.getAsJsonObject(), "Hand").getAsJsonArray("cards").isEmpty());
                            assertTrue(zone(member.getAsJsonObject(), "Library").getAsJsonArray("cards").isEmpty());
                        }
                    }
                    Files.writeString(scenario.evidence.resolve("anonymous-target-seat-" + state.get("viewerId").getAsInt() + ".json"), json.toJson(state));
                }
                String execution = text(states.get(0).getAsJsonArray("stack").get(0).getAsJsonObject(), "executionId");
                assertFalse(execution.isBlank(), "The real committed Path has a retained execution identity");
                return execution;
            });
            Input search = null;
            int revealAcknowledgments = 0;
            for (int continuation = 0; continuation < 8 && search == null; continuation++) {
                var next = scenario.until(input -> text(input.prompt(), "kind").equals("reveal")
                        || input.seat() == 1 && text(input.prompt(), "inputType").equals("InputConfirm"));
                if (!text(next.prompt(), "kind").equals("reveal")) {
                    search = next;
                    break;
                }
                var choices = next.prompt().getAsJsonArray("choices");
                assertEquals(choices.size(), 1, "The real face-down departure reveals only its one moved card");
                var face = choices.get(0).getAsJsonObject().getAsJsonObject("card");
                assertEquals(text(face, "name"), "Willbender");
                assertFalse(face.get("faceDown").getAsBoolean());
                assertFalse(face.has("key"), "The rules reveal is reading material, not card response authority");
                assertEquals(text(next.prompt(), "executionId"), pathExecutionId);
                assertEquals(text(next.prompt(), "executionStep"), "resolution");
                assertTrue(zone(player(next.state(), owner), "Battlefield").getAsJsonArray("cards").isEmpty());
                var moved = zone(player(next.state(), owner), "Exile").getAsJsonArray("cards");
                assertEquals(moved.size(), 1, "Only the departed target is now face up; the other exile stays hidden");
                assertEquals(text(moved.get(0).getAsJsonObject(), "name"), "Willbender");
                assertFalse(moved.get(0).getAsJsonObject().get("faceDown").getAsBoolean());
                assertNotEquals(text(moved.get(0).getAsJsonObject(), "combatId"), recipientId);
                assertTrue(scenario.answer(next, Map.of("action", "ok")), "Acknowledge the exact current rules reveal");
                revealAcknowledgments++;
            }
            assertTrue(revealAcknowledgments > 0, "The real face-down departure requires a reveal before Path's search");
            assertNotNull(search, "The actual optional search must follow within the bounded reveal continuations");
            assertTrue(scenario.answer(search, Map.of("action", "cancel")), "The real target controller declines Path's optional search");
            var settled = scenario.until(input -> text(input.prompt(), "inputType").equals("InputPassPriority")
                    && input.state().getAsJsonArray("stack").isEmpty());
            var exiled = zone(player(settled.state(), owner), "Exile");
            assertEquals(exiled.get("count").getAsInt(), 2);
            assertEquals(exiled.getAsJsonArray("cards").size(), 1, "Only Path's now face-up exile occurrence is disclosed");
            var revealed = exiled.getAsJsonArray("cards").get(0).getAsJsonObject();
            assertEquals(text(revealed, "name"), "Willbender", "The actual spell resolved onto the manifested card");
            assertNotEquals(text(revealed, "combatId"), recipientId, "The newly revealed exile occurrence cannot reuse the concealed battlefield identity");
        }
    }

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void aMovedPathTargetCannotRebindToItsNewExileOccurrenceWhileTheRealSearchIsPending() throws Exception {
        try (var scenario = RulesScenario.allied("public-in-flight-target-movement", Map.of(
                0, List.of("Plains", "Reliquary Tower"), 1, List.of("Birds of Paradise", "Reliquary Tower")), "Path to Exile")) {
            var main = main(scenario);
            int owner = scenario.table.game.getRegisteredPlayers().get(1).getId();
            var spell = zone(viewer(main.state()), "Hand").getAsJsonArray("cards").asList().stream()
                    .map(value -> value.getAsJsonObject()).filter(value -> text(value, "name").equals("Path to Exile")).findFirst().orElseThrow();
            assertTrue(scenario.answer(main, Map.of("action", "card", "key", text(spell, "key"))));
            var targeting = scenario.until(input -> text(input.prompt(), "inputType").equals("InputSelectTargets"));
            var recipient = card(targeting.state(), owner, "Birds of Paradise");
            assertTrue(scenario.answer(targeting, Map.of("action", "card", "key", text(recipient, "key"))));
            payAndStack(scenario);
            assertTargets(scenario, "Path to Exile", text(recipient, "combatId"), owner);
            var oldTargetView = HeadlessPlatform.query(scenario.table, () -> scenario.table.game.getRegisteredPlayers().get(1)
                    .getCardsIn(forge.game.zone.ZoneType.Battlefield).stream().filter(value -> value.getName().equals("Birds of Paradise"))
                    .findFirst().orElseThrow().getView());
            var json = new GsonBuilder().serializeNulls().create();
            Object retained = HeadlessPlatform.query(scenario.table, () -> {
                var state = SpectatorObservation.project(scenario.table,
                        ((Number)scenario.session(0).state().get("boardRevision")).longValue(), null);
                return ((Map<?, ?>)((List<?>)state.get("stack")).get(0)).get("targets");
            });
            var before = json.toJsonTree(retained);
            var search = scenario.until(input -> input.seat() == 1 && text(input.prompt(), "inputType").equals("InputConfirm"));
            assertEquals(search.prompt().getAsJsonObject("librarySearch").get("libraryOwnerId").getAsInt(), owner);
            assertEquals(zone(player(search.state(), owner), "Exile").getAsJsonArray("cards").size(), 1);
            HeadlessPlatform.query(scenario.table, () -> {
                var states = new ArrayList<JsonObject>();
                for (int seat = 0; seat < 4; seat++) states.add(json.toJsonTree(scenario.table.seat(seat).state()).getAsJsonObject());
                states.add(json.toJsonTree(SpectatorObservation.project(scenario.table,
                        states.get(0).get("boardRevision").getAsLong(), null)).getAsJsonObject());
                var identityCalls = new java.util.concurrent.atomic.AtomicInteger();
                assertNull(StackTargetProjection.card(scenario.table.game, oldTargetView, ignored -> {
                    identityCalls.incrementAndGet(); return "retired-reference-must-not-allocate";
                }));
                assertEquals(identityCalls.get(), 0, "Retired target admission fails before any audience identity lookup or allocation");
                var exileView = scenario.table.game.getRegisteredPlayers().get(1).getCardsIn(forge.game.zone.ZoneType.Exile).get(0).getView();
                assertNotSame(exileView, oldTargetView); assertEquals(exileView.getId(), oldTargetView.getId());
                assertNotNull(StackTargetProjection.card(scenario.table.game, exileView, current -> {
                    identityCalls.incrementAndGet(); return scenario.table.identities.id(current);
                }), "A genuinely current public exile view still supports a newly admitted reference");
                assertEquals(identityCalls.get(), 1);
                for (var state : states) {
                    var item = state.getAsJsonArray("stack").get(0).getAsJsonObject();
                    Files.writeString(scenario.evidence.resolve("after-movement-seat-" + state.get("viewerId").getAsInt() + ".json"), json.toJson(state));
                    assertEquals(text(item, "name"), "Path to Exile");
                    assertTrue(item.getAsJsonArray("targets").isEmpty(),
                            "The old battlefield target is historical, not the new public exile occurrence with the same physical numeric ID");
                }
                return null;
            });
            assertEquals(json.toJsonTree(retained), before, "Earlier target descriptions remain detached historical values");
            assertTrue(scenario.answer(search, Map.of("action", "cancel")), "The actual controller declines its optional library search");
            var settled = scenario.until(input -> text(input.prompt(), "inputType").equals("InputPassPriority")
                    && input.state().getAsJsonArray("stack").isEmpty());
            assertEquals(zone(player(settled.state(), owner), "Exile").getAsJsonArray("cards").size(), 1);
            assertTrue(zone(viewer(settled.state()), "Graveyard").getAsJsonArray("cards").asList().stream()
                    .anyMatch(value -> text(value.getAsJsonObject(), "name").equals("Path to Exile")));
        }
    }

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void anActualDragonBloodActivationKeepsItsAllyTargetControllerForEveryAudience() throws Exception {
        try (var scenario = RulesScenario.allied("public-ally-stack-target", Map.of(
                0, List.of("Dragon Blood", "Forest", "Forest", "Forest", "Reliquary Tower"),
                1, List.of("Grizzly Bears", "Reliquary Tower")), "Forest")) {
            var main = main(scenario);
            int owner = scenario.table.game.getRegisteredPlayers().get(1).getId();
            assertFalse(scenario.table.game.getRegisteredPlayers().get(0).isOpponentOf(scenario.table.game.getRegisteredPlayers().get(1)),
                    "The target is an actual allied player, not a renamed opponent");
            var source = card(main.state(), 0, "Dragon Blood");
            assertTrue(scenario.answer(main, Map.of("action", "card", "key", text(source, "key"))));
            var targeting = scenario.until(input -> text(input.prompt(), "inputType").equals("InputSelectTargets"));
            var recipient = card(targeting.state(), owner, "Grizzly Bears");
            assertTrue(scenario.answer(targeting, Map.of("action", "card", "key", text(recipient, "key"))));
            var stacked = payAndStack(scenario);
            assertTargets(scenario, "Dragon Blood", text(recipient, "combatId"), owner);
            assertTrue(card(stacked.state(), 0, "Dragon Blood").get("tapped").getAsBoolean());
            var resolved = scenario.until(input -> card(input.state(), owner, "Grizzly Bears").get("power").getAsInt() == 3);
            assertEquals(card(resolved.state(), owner, "Grizzly Bears").get("toughness").getAsInt(), 3,
                    "The real activation resolves its counter onto the ally instead of merely projecting a target");
        }
    }

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void repeatedTargetedSpellPartsDisplayOneReferenceWhileAllThreeEffectsStillResolve() throws Exception {
        try (var scenario = RulesScenario.allied("public-repeated-stack-target", Map.of(
                0, List.of("Forest", "Plains", "Reliquary Tower"), 1, List.of("Grizzly Bears", "Reliquary Tower")), "Seeds of Strength")) {
            var main = main(scenario);
            int owner = scenario.table.game.getRegisteredPlayers().get(1).getId();
            var spell = zone(viewer(main.state()), "Hand").getAsJsonArray("cards").asList().stream()
                    .map(value -> value.getAsJsonObject()).filter(value -> text(value, "name").equals("Seeds of Strength")).findFirst().orElseThrow();
            assertTrue(scenario.answer(main, Map.of("action", "card", "key", text(spell, "key"))));
            String recipientId = "";
            for (int part = 0; part < 3; part++) {
                var targeting = scenario.until(input -> text(input.prompt(), "inputType").equals("InputSelectTargets"));
                var recipient = card(targeting.state(), owner, "Grizzly Bears"); recipientId = text(recipient, "combatId");
                assertTrue(scenario.answer(targeting, Map.of("action", "card", "key", text(recipient, "key"))));
            }
            payAndStack(scenario);
            assertTargets(scenario, "Seeds of Strength", recipientId, owner);
            var resolved = scenario.until(input -> card(input.state(), owner, "Grizzly Bears").get("power").getAsInt() == 5);
            assertEquals(card(resolved.state(), owner, "Grizzly Bears").get("toughness").getAsInt(), 5,
                    "Deduplicating display references cannot collapse the three actual targeted pump effects");
        }
    }

    private static RulesScenario.Input main(RulesScenario scenario) throws Exception {
        return scenario.until(input -> input.seat() == 0 && text(input.state(), "phaseKey").equals("MAIN1")
                && text(input.prompt(), "inputType").equals("InputPassPriority"));
    }

    private static RulesScenario.Input payAndStack(RulesScenario scenario) throws Exception {
        var payment = scenario.until(input -> text(input.prompt(), "inputType").startsWith("InputPayMana"));
        assertTrue(payment.prompt().get("okEnabled").getAsBoolean());
        assertTrue(scenario.answer(payment, Map.of("action", "ok")), "Explicitly use the real human Auto payment control");
        return scenario.until(input -> text(input.prompt(), "inputType").equals("InputPassPriority")
                && !input.state().getAsJsonArray("stack").isEmpty());
    }

    private static JsonObject card(JsonObject state, int owner, String name) {
        return zone(player(state, owner), "Battlefield").getAsJsonArray("cards").asList().stream()
                .map(value -> value.getAsJsonObject()).filter(value -> text(value, "name").equals(name)).findFirst().orElseThrow();
    }

    private static void assertTargets(RulesScenario scenario, String source, String recipientId, int owner) throws Exception {
        HeadlessPlatform.query(scenario.table, () -> {
            var json = new GsonBuilder().serializeNulls().setPrettyPrinting().create();
            var states = new ArrayList<JsonObject>();
            for (int seat = 0; seat < 4; seat++) states.add(json.toJsonTree(scenario.table.seat(seat).state()).getAsJsonObject());
            states.add(json.toJsonTree(SpectatorObservation.project(scenario.table,
                    states.get(0).get("boardRevision").getAsLong(), null)).getAsJsonObject());
            for (var state : states) {
                var stack = state.getAsJsonArray("stack").asList().stream().map(value -> value.getAsJsonObject())
                        .filter(item -> text(item, "name").equals(source)).findFirst().orElseThrow();
                Files.writeString(scenario.evidence.resolve("target-seat-" + state.get("viewerId").getAsInt() + ".json"), json.toJson(state));
                var targets = stack.getAsJsonArray("targets");
                assertEquals(targets.size(), 1, "Committed display targets form one ordered set across all targeted spell parts");
                var target = targets.get(0).getAsJsonObject();
                assertEquals(text(target, "kind"), "card"); assertEquals(text(target, "id"), recipientId);
                assertTrue(target.has("playerId") && !target.get("playerId").isJsonNull(), "A disclosed card target includes its current controller");
                assertEquals(target.get("playerId").getAsInt(), owner);
            }
            return null;
        });
    }
}
