package forge.api;

import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import forge.StaticData;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.List;
import java.util.Map;
import java.util.Objects;
import java.util.UUID;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Real casts, current target offers, mana payment and rules attachment outcomes.
 * Manual target choice verifies provider facts; policy choice has separate tests. */
public class AuraAttachmentJourneyTest {
    private static final Gson JSON = new GsonBuilder().serializeNulls().setPrettyPrinting().create();

    @Test(timeOut = 90000)
    public void rancorCastsOntoFriendlyCreatureWithBothSidesLegallyOffered() throws Exception {
        play("attachment-rancor", "Rancor", "Forest", "Known", false, 4, 2, "trample");
    }

    @Test(timeOut = 90000)
    public void renamedStructuralAuraCastsAndResolvesWithoutNameOrAiHintAuthority() throws Exception {
        if (StaticData.instance() == null) EngineResources.load(Path.of("../forge-gui/res"));
        String name = "Fixture renamed attachment " + UUID.randomUUID();
        var paper = AttachmentCapabilitiesTest.paper(name, "Mode$ Continuous | AffectedDefined$ Enchanted | Affected$ Creature"
                + " | AddPower$ 2 | AddKeyword$ Trample | Description$ Unrelated hostile wording.", "Curse");
        StaticData.instance().getCommonCards().addCard(paper);
        play("attachment-renamed-positive", name, "Forest", "Known", false, 4, 2, "trample");
    }

    @Test(timeOut = 90000)
    public void deadWeightPreservesNegativeModifiersAndOpponentOwnershipThroughResolution() throws Exception {
        play("attachment-dead-weight", "Dead Weight", "Swamp", "Known", true, 1, 1, "");
    }

    @Test(timeOut = 90000)
    public void conditionalModifierRemainsUnknownEvenWhileActualBaseEffectResolves() throws Exception {
        play("attachment-conditional", "Zealot's Conviction", "Plains", "Unknown", false, 3, 3, "");
    }

    @Test(timeOut = 90000)
    public void dynamicMixedModifierRemainsUnknownWhileTheActualRulesDetermineItsStats() throws Exception {
        play("attachment-mixed", "Ethereal Armor", "Plains", "Unknown", false, 3, 3, "first strike");
    }

    private static void play(String label, String aura, String land, String knowledge, boolean hostile,
                             int power, int toughness, String keyword) throws Exception {
        try (var game = RulesScenario.automated(label, Map.of(0, List.of(land, "Grizzly Bears"), 1, List.of("Hill Giant")), aura, "Rhys the Redeemed")) {
            var main = game.until(input -> input.seat() == 0 && priority(input) && text(input.state(), "phaseKey").equals("MAIN1"));
            var hand = zone(viewer(main.state()), "Hand").getAsJsonArray("cards").get(0).getAsJsonObject();
            assertEquals(text(hand, "name"), aura);
            var catalog = JSON.toJsonTree(game.session(0).sourceActions(request(main, Map.of("key", text(hand, "key"))))).getAsJsonObject();
            var action = catalog.getAsJsonArray("actions").get(0).getAsJsonObject();
            Files.writeString(game.evidence.resolve("00-source.json"), JSON.toJson(catalog));
            assertTrue(game.answer(main, Map.of("action", "useSourceAction", "key", text(catalog, "sourceCardKey"), "actionId", text(action, "id"))));
            var selection = game.until(input -> input.seat() == 0 && text(input.prompt(), "inputType").equals("InputSelectTargets"));
            Files.writeString(game.evidence.resolve("01-selection.json"), JSON.toJson(selection.state()));
            var friendly = card(selection.state(), 0, "Battlefield", "Grizzly Bears");
            var opponent = card(selection.state(), 1, "Battlefield", "Hill Giant");
            assertTrue(friendly.get("selectable").getAsBoolean(), "The actual friendly creature is a legal target");
            assertTrue(opponent.get("selectable").getAsBoolean(), "The actual opposing creature is also a legal target; policy cannot rely on target filtering");
            var owned = game.session(0).automatedObservation();
            Files.writeString(game.evidence.resolve("02-owned-observation.json"), JSON.toJson(owned));
            var graph = AttachmentCapabilitiesTest.map(AttachmentCapabilitiesTest.map(owned.get("semantics")).get("operationGraph"));
            Files.writeString(game.evidence.resolve("02-operation.json"), JSON.toJson(graph));
            var node = AttachmentCapabilitiesTest.maps(graph.get("operations")).get(0);
            assertEquals(node.get("effect"), "Unknown", "Ordinary stack and private semantic wires retain legacy enum compatibility");
            assertEquals(node.get("knowledge"), "Unknown"); assertEquals(graph.get("knowledge"), "Unknown");
            var modifier = AttachmentCapabilitiesTest.map(node.get("attachment"));
            assertEquals(modifier.get("knowledge"), knowledge);
            if (knowledge.equals("Unknown")) {
                assertNull(modifier.get("powerDelta")); assertNull(modifier.get("toughnessDelta"));
                assertEquals(modifier.get("grantedKeywords"), List.of(), "An unsupported sibling cannot leak a partial positive modifier");
            } else {
                assertEquals(modifier.get("powerDelta"), hostile ? -2 : 2);
                assertEquals(modifier.get("toughnessDelta"), hostile ? -2 : 0);
            }
            var targets = AttachmentCapabilitiesTest.map(node.get("targets"));
            assertEquals(targets.get("knowledge"), "Known"); assertEquals(targets.get("min"), 1); assertEquals(targets.get("max"), 1);
            var source = AttachmentCapabilitiesTest.map(node.get("sources"));
            assertEquals(source.get("knowledge"), "Unknown"); assertEquals(source.get("binding"), "Unknown");
            var sources = AttachmentCapabilitiesTest.maps(source.get("entities"));
            assertTrue(sources.isEmpty(), "An announced casting copy must not become a current zone occurrence");
            var portrait = selection.prompt().getAsJsonObject("sourceCard");
            String portraitId = text(portrait, "visualId");
            assertFalse(portraitId.isEmpty()); assertEquals(node.get("attachmentSourceCardId"), portraitId);
            assertEquals(text(portrait, "key"), ""); assertFalse(portrait.get("selectable").getAsBoolean());
            assertTrue(selection.state().getAsJsonArray("players").asList().stream().map(value -> value.getAsJsonObject())
                    .flatMap(value -> value.getAsJsonArray("zones").asList().stream()).map(value -> value.getAsJsonObject())
                    .flatMap(value -> value.getAsJsonArray("cards").asList().stream()).map(value -> value.getAsJsonObject())
                    .noneMatch(value -> text(value, "combatId").equals(portraitId)), "The portrait is not injected into any zone");
            assertEquals(AttachmentCapabilitiesTest.map(node.get("recipients")).get("targetOperationId"), node.get("id"));
            var chosen = hostile ? opponent : friendly;
            String chosenId = text(chosen, "combatId");
            int chosenOwner = selection.state().getAsJsonArray("players").get(hostile ? 1 : 0).getAsJsonObject().get("id").getAsInt();
            assertTrue(game.answer(selection, Map.of("action", "card", "key", text(chosen, "key"))));
            var payment = game.until(input -> input.seat() == 0);
            if (text(payment.prompt(), "inputType").equals("InputSelectTargets")) {
                assertTrue(payment.prompt().get("okEnabled").getAsBoolean());
                assertTrue(game.answer(payment, Map.of("action", "ok"))); payment = game.until(input -> input.seat() == 0);
            }
            assertTrue(text(payment.prompt(), "inputType").startsWith("InputPayMana"));
            Files.writeString(game.evidence.resolve("03-payment.json"), JSON.toJson(payment.state()));
            String bill = text(payment.prompt(), "executionId");
            var manaSource = card(payment.state(), 0, "Battlefield", land);
            String landId = text(manaSource, "visualId");
            var manaActions = JSON.toJsonTree(game.session(0).sourceActions(request(payment,
                    Map.of("key", text(manaSource, "key"))))).getAsJsonObject();
            var manaAction = manaActions.getAsJsonArray("actions").get(0).getAsJsonObject();
            assertTrue(game.answer(payment, Map.of("action", "useSourceAction", "key", text(manaSource, "key"),
                    "actionId", text(manaAction, "id"))));
            payment = awaitManaProgress(game, bill, landId);
            if (text(payment.prompt(), "inputType").startsWith("InputPayMana")) {
                assertTrue(payment.prompt().get("okEnabled").getAsBoolean(), "The real mana child funded its owning aura bill");
                assertTrue(game.answer(payment, Map.of("action", "ok")));
            }
            var stacked = game.until(input -> input.seat() == 0 && priority(input) && input.state().getAsJsonArray("stack").size() == 1);
            var wireGraph = stacked.state().getAsJsonArray("stack").get(0).getAsJsonObject().getAsJsonObject("operationGraph");
            assertEquals(text(wireGraph.getAsJsonArray("operations").get(0).getAsJsonObject(), "effect"), "Unknown");
            assertFalse(wireGraph.getAsJsonArray("operations").get(0).getAsJsonObject().has("attachmentSourceCardId"),
                    "An ordinary stack graph does not inherit active-decision portrait provenance");
            Files.writeString(game.evidence.resolve("04-stack.json"), JSON.toJson(stacked.state()));
            var resolved = game.until(input -> input.seat() == 0 && priority(input) && input.state().getAsJsonArray("stack").isEmpty()
                    && hasCard(input.state(), 0, "Battlefield", aura));
            Files.writeString(game.evidence.resolve("05-resolved.json"), JSON.toJson(resolved.state()));
            var attached = card(resolved.state(), 0, "Battlefield", aura);
            assertEquals(text(attached.getAsJsonObject("attachedTo"), "id"), chosenId);
            var result = card(resolved.state(), hostile ? 1 : 0, "Battlefield", hostile ? "Hill Giant" : "Grizzly Bears");
            assertEquals(result.get("power").getAsInt(), power); assertEquals(result.get("toughness").getAsInt(), toughness);
            assertEquals(text(result, "combatId"), chosenId);
            if (!keyword.isEmpty()) assertTrue(result.getAsJsonArray("combatKeywords").asList().stream()
                    .anyMatch(value -> value.getAsString().equals(keyword)));
            var unchanged = card(resolved.state(), hostile ? 0 : 1, "Battlefield", hostile ? "Grizzly Bears" : "Hill Giant");
            assertEquals(unchanged.get("power").getAsInt(), hostile ? 2 : 3);
            assertEquals(unchanged.get("toughness").getAsInt(), hostile ? 2 : 3);
            assertTrue(AttachmentCapabilitiesTest.maps(targets.get("candidates")).stream().anyMatch(value ->
                    Objects.equals(value.get("cardId"), chosenId) && Objects.equals(value.get("ownerId"), chosenOwner)));
        }
    }

    private static JsonObject card(JsonObject state, int seat, String section, String name) {
        var player = state.getAsJsonArray("players").get(seat).getAsJsonObject();
        return zone(player, section).getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "name").equals(name)).findFirst().orElseThrow();
    }
    private static boolean hasCard(JsonObject state, int seat, String section, String name) {
        return zone(state.getAsJsonArray("players").get(seat).getAsJsonObject(), section).getAsJsonArray("cards").asList().stream()
                .map(value -> value.getAsJsonObject()).anyMatch(value -> text(value, "name").equals(name));
    }
    private static boolean priority(Input input) { return text(input.prompt(), "inputType").equals("InputPassPriority"); }

    private static Input awaitManaProgress(RulesScenario game, String owner, String sourceId) throws Exception {
        long deadline = System.nanoTime() + java.time.Duration.ofSeconds(5).toNanos();
        while (System.nanoTime() < deadline) {
            var state = game.state(0);
            if (state.get("prompt").isJsonObject()) {
                var input = new Input(0, state, state.getAsJsonObject("prompt"));
                boolean complete = state.getAsJsonObject("executions").getAsJsonArray("occurrences").asList().stream()
                        .map(value -> value.getAsJsonObject()).anyMatch(value -> text(value, "sourceCardId").equals(sourceId)
                                && text(value, "parentId").equals(owner) && text(value, "kind").equals("mana")
                                && text(value, "outcome").equals("resolved"));
                if (complete && (priority(input) || text(input.prompt(), "inputType").startsWith("InputPayMana")
                        && text(input.prompt(), "executionId").equals(owner))) return input;
            }
            Thread.sleep(2);
        }
        throw new AssertionError("The real mana child did not return to its owning payment: " + game.state(0));
    }
}
