package forge.api;

import com.google.gson.Gson;
import com.google.gson.JsonObject;
import forge.card.MagicColor;
import forge.game.mana.Mana;
import forge.game.zone.ZoneType;
import forge.player.PlayerControllerHuman;
import org.testng.annotations.DataProvider;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.util.List;
import java.util.Map;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Human source reads must belong to the same committed decision as the board. */
public class SourceQueryPublicationTest {
    private static final Gson JSON = new Gson();

    @DataProvider public Object[][] catalogStates() { return new Object[][] {{false}, {true}}; }

    @Test(dataProvider = "catalogStates", timeOut = 90000)
    public void aSourceEventBeforePublicationCannotMintHumanOffersAgainstTheOldBoard(boolean warmCatalog) throws Exception {
        try (var game = new RulesScenario("source-query-unpublished-event-" + warmCatalog,
                Map.of(0, List.of("Forest", "Forest", "Forest")), "Manalith", "Rhys the Redeemed")) {
            game.until(input -> input.seat() == 0 && priority(input)
                    && text(input.state(), "phaseKey").equals("MAIN1"));
            HeadlessPlatform.query(game.table, () -> null);
            var session = game.session(0);
            var retained = HeadlessPlatform.query(game.table, () -> {
                var before = game.state(0);
                var prompt = before.getAsJsonObject("prompt");
                var spell = card(before, "Hand", "Manalith");
                var request = request(before, prompt, Map.of("key", text(spell, "key"),
                        "expectedRevision", before.get("revision").getAsLong()));
                JsonObject warmed = warmCatalog ? JSON.toJsonTree(session.sourceActions(request)).getAsJsonObject() : null;
                var actor = game.table.game.getRegisteredPlayers().get(0);
                var controller = (PlayerControllerHuman)actor.getController();
                var input = controller.getInputQueue().getInput();
                var forest = actor.getCardsIn(ZoneType.Battlefield).get(0);
                actor.getManaPool().addMana(new Mana(MagicColor.GREEN, forest,
                        forest.getManaAbilities().get(0).getManaPart(), actor));
                assertEquals(actor.getManaPool().totalMana(), 1, "A real rules event changes source eligibility before publication");
                assertSame(controller.getInputQueue().getInput(), input);
                assertSame(controller.getInputProxy().getInput(), input);
                assertEquals(game.state(0), before, "The retained board, revision and input are still the old committed value");
                Files.writeString(game.evidence.resolve("unpublished-event.json"), JSON.toJson(Map.of(
                        "warmCatalog", warmCatalog, "published", before, "livePoolCount", actor.getManaPool().totalMana())));
                assertThrows(IllegalArgumentException.class, () -> session.sourceActions(request));
                assertEquals(game.state(0), before, "Rejecting a mixed read must not publish, pass or replace the decision");
                if (warmed != null) {
                    var action = warmed.getAsJsonArray("actions").get(0).getAsJsonObject();
                    assertThrows(IllegalArgumentException.class, () -> session.action(request(before, prompt, Map.of(
                            "action", "useSourceAction", "key", text(warmed, "sourceCardKey"), "actionId", text(action, "id")))));
                }
                game.table.publish();
                var published = game.state(0);
                assertEquals(text(published.getAsJsonObject("prompt"), "id"), text(prompt, "id"),
                        "The same actual input can acquire a fresh coherent publication");
                assertTrue(published.get("revision").getAsLong() > before.get("revision").getAsLong());
                assertEquals(viewer(published).getAsJsonObject("mana").get("G").getAsInt(), 1);
                var currentSpell = card(published, "Hand", "Manalith");
                var freshRequest = request(published, published.getAsJsonObject("prompt"), Map.of(
                        "key", text(currentSpell, "key"), "expectedRevision", published.get("revision").getAsLong()));
                var fresh = JSON.toJsonTree(session.sourceActions(freshRequest)).getAsJsonObject();
                assertEquals(fresh.get("revision"), published.get("revision"));
                assertEquals(fresh.getAsJsonArray("actions").get(0).getAsJsonObject().getAsJsonObject("costPreview")
                        .getAsJsonObject("funding").getAsJsonObject("pool").get("G").getAsInt(), 1);
                assertEquals(JSON.toJsonTree(session.sourceActions(freshRequest)), fresh,
                        "Unchanged current reads retain their handles and facts");
                var currentForest = card(published, "Battlefield", "Forest");
                var mana = JSON.toJsonTree(session.sourceActions(request(published, published.getAsJsonObject("prompt"),
                        Map.of("key", text(currentForest, "key"))))).getAsJsonObject();
                Files.writeString(game.evidence.resolve("coherent-republication.json"), JSON.toJson(Map.of(
                        "before", before, "published", published, "freshCatalog", fresh)));
                return new Continuation(published, mana);
            });
            var input = new Input(0, retained.state(), retained.state().getAsJsonObject("prompt"));
            var action = retained.mana().getAsJsonArray("actions").get(0).getAsJsonObject();
            var command = Map.of("action", "useSourceAction", "key", text(retained.mana(), "sourceCardKey"),
                    "actionId", text(action, "id"));
            assertTrue(game.answer(input, command), "A fresh coherent offer still executes the actual engine ability");
            var paid = game.until(next -> next.seat() == 0 && priority(next)
                    && viewer(next.state()).getAsJsonObject("mana").get("G").getAsInt() == 2);
            assertEquals(zone(viewer(paid.state()), "Battlefield").getAsJsonArray("cards").asList().stream()
                    .map(value -> value.getAsJsonObject()).filter(value -> value.get("tapped").getAsBoolean()).count(), 1L);
            assertThrows(IllegalArgumentException.class, () -> session.action(request(input.state(), input.prompt(), command)));
            assertNull(session.state().get("error"));
        }
    }

    private record Continuation(JsonObject state, JsonObject mana) { }
    private static boolean priority(Input input) { return text(input.prompt(), "inputType").equals("InputPassPriority"); }
    private static JsonObject card(JsonObject state, String zone, String name) {
        return zone(viewer(state), zone).getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "name").equals(name)).findFirst().orElseThrow();
    }
    private static JsonObject request(JsonObject state, JsonObject prompt, Map<String, ?> body) {
        return RulesScenario.request(new Input(0, state, prompt), body);
    }
}
