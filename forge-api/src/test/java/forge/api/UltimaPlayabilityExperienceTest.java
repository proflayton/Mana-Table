package forge.api;

import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import forge.StaticData;
import forge.card.mana.ManaAtom;
import forge.game.card.Card;
import forge.game.mana.Mana;
import forge.game.zone.ZoneType;
import forge.gamemodes.match.input.InputPassPriority;
import forge.player.PlayerControllerHuman;
import org.testng.annotations.DataProvider;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.util.Collections;
import java.util.List;
import java.util.Map;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Compare the ordinary hand cue with the same exact card's actual payment.
 * A source catalog is an offer to announce a cast, not proof it is funded. */
public class UltimaPlayabilityExperienceTest {
    private static final Gson JSON = new GsonBuilder().serializeNulls().setPrettyPrinting().create();
    private static final String ULTIMA = "Ultima, Origin of Oblivion";

    @DataProvider public Object[][] funding() {
        return new Object[][] {{"Wastes", 4, false}, {"Wastes", 5, true}, {"Forest", 5, true}};
    }

    @Test(dataProvider = "funding", groups = "canonical-interaction", timeOut = 90000)
    public void theExactUltimaHandCueAgreesWithItsRealGenericFivePayment(String land, int count, boolean payable) throws Exception {
        try (var game = new RulesScenario("ultima-origin-payment-" + land + "-" + count,
                Map.of(0, Collections.nCopies(count, land)), ULTIMA, "Progenitus")) {
            game.until(next -> next.seat() == 0 && priority(next) && text(next.state(), "phaseKey").equals("MAIN1"));
            var before = current(game);
            var spell = card(before.state(), "Hand", ULTIMA);
            assertNotNull(spell, "This regression uses Origin of Oblivion, not the distinct white sorcery named Ultima");
            assertEquals(tapped(before.state(), land), 0L);
            assertEquals(pool(before.state()), 0);
            var foreign = HeadlessPlatform.query(game.table, () -> List.of(game.state(1), game.state(2), game.state(3)));
            Files.writeString(game.evidence.resolve("foreign-observations.json"), JSON.toJson(foreign));
            for (var state : foreign) for (var player : state.getAsJsonArray("players")) {
                for (var zone : player.getAsJsonObject().getAsJsonArray("zones")) for (var value : zone.getAsJsonObject().getAsJsonArray("cards"))
                    assertNotEquals(affordability(value.getAsJsonObject()), "Payable", "A non-owner publication cannot borrow another seat's current funding witness");
            }

            var catalog = JSON.toJsonTree(game.session(0).sourceActions(request(before,
                    Map.of("key", text(spell, "key"), "expectedRevision", before.state().get("revision"))))).getAsJsonObject();
            var after = current(game);
            Files.writeString(game.evidence.resolve("ordinary-before-query.json"), JSON.toJson(before.state()));
            Files.writeString(game.evidence.resolve("ordinary-source-catalog.json"), JSON.toJson(catalog));
            Files.writeString(game.evidence.resolve("ordinary-after-query.json"), JSON.toJson(after.state()));
            assertEquals(after.state(), before.state(), "Reading a timing-legal source cannot tap, fund, select, publish, or answer priority");
            assertEquals(catalog.get("revision"), before.state().get("revision"));
            assertEquals(text(catalog, "decisionId"), text(before.prompt(), "id"));
            assertEquals(text(catalog, "sourceCardKey"), text(spell, "key"));
            assertEquals(text(catalog, "sourceCardId"), text(spell, "visualId"));
            var casts = catalog.getAsJsonArray("actions").asList().stream().map(value -> value.getAsJsonObject())
                    .filter(action -> text(action, "kind").equals("cast")).toList();
            assertEquals(casts.size(), 1, "The unfunded case still permits announcing the exact timing-legal cast");
            var cast = casts.get(0);
            var preview = cast.getAsJsonObject("costPreview");
            assertNotNull(preview);
            assertEquals(text(preview, "manaCostKnowledge"), "Unknown", "The nominal preview does not certify an adjusted payment");
            var costs = preview.getAsJsonArray("costs").asList().stream().map(value -> value.getAsJsonObject())
                    .filter(cost -> text(cost, "kind").equals("Mana")).toList();
            assertEquals(costs.size(), 1);
            assertEquals(costs.get(0).get("amount").getAsInt(), 5);
            assertTrue(costs.get(0).getAsJsonArray("manaSymbols").isEmpty(), "Origin costs five generic mana, with no explicit colorless shard");
            assertEquals(text(preview.getAsJsonObject("funding"), "knowledge"), "Unknown");
            assertEquals(preview.getAsJsonObject("funding").getAsJsonArray("sources").size(), count);

            int handBefore = zone(viewer(before.state()), "Hand").getAsJsonArray("cards").size();
            assertTrue(game.answer(after, Map.of("action", "useSourceAction", "expectedTask", "priority",
                    "key", text(catalog, "sourceCardKey"), "actionId", text(cast, "id"))));
            var payment = game.until(next -> next.seat() == 0 && (text(next.prompt(), "inputType").startsWith("InputPayMana")
                    || priority(next) && stacked(next.state())));
            payment = current(game);
            Files.writeString(game.evidence.resolve("actual-payment.json"), JSON.toJson(payment.state()));
            if (payable) {
                if (!stacked(payment.state())) {
                    assertTrue(payment.prompt().get("okEnabled").getAsBoolean(), "The actual Auto payment must recognize all five current resources");
                    assertTrue(game.answer(payment, Map.of("action", "ok")));
                    payment = game.until(next -> next.seat() == 0 && priority(next) && stacked(next.state()));
                    payment = current(game);
                }
                assertEquals(tapped(payment.state(), land), 5L, "The real payment taps each of the five physical sources once");
                assertEquals(pool(payment.state()), 0);
                assertEquals(zone(viewer(payment.state()), "Hand").getAsJsonArray("cards").size(), handBefore - 1);
                assertEquals(payment.state().getAsJsonArray("stack").asList().stream()
                        .filter(value -> text(value.getAsJsonObject(), "name").equals(ULTIMA)).count(), 1L);
            } else {
                assertTrue(text(payment.prompt(), "inputType").startsWith("InputPayMana"));
                assertFalse(payment.prompt().get("okEnabled").getAsBoolean(), "Four one-mana sources cannot complete the five-generic bill");
                assertEquals(payment.prompt().getAsJsonArray("unpaidManaParts"), JSON.toJsonTree(List.of(Map.of("symbol", "{5}", "count", 1))));
                assertFalse(stacked(payment.state()));
                var unpaid = current(game);
                game.rejected(unpaid, "ok", "Continue is not available");
                assertEquals(current(game).state(), unpaid.state(), "A disabled Auto payment cannot spend or cast");
                assertTrue(game.answer(unpaid, Map.of("action", "cancel", "expectedTask", "mana")));
                payment = game.until(next -> next.seat() == 0 && priority(next)
                        && !text(next.prompt(), "id").equals(text(before.prompt(), "id")));
                payment = current(game);
                assertFalse(stacked(payment.state()));
                assertEquals(tapped(payment.state(), land), 0L);
                assertEquals(pool(payment.state()), 0);
                assertEquals(zone(viewer(payment.state()), "Hand").getAsJsonArray("cards").size(), handBefore);
            }
            Files.writeString(game.evidence.resolve("actual-result.json"), JSON.toJson(Map.of("payable", payable,
                    "initialSelectable", spell.get("selectable"), "initialCanAutoPass", before.prompt().get("canAutoPass"),
                    "result", payment.state())));
            assertEquals(affordability(spell), payable ? "Payable" : "Unknown",
                    "Only a current positive funding witness may brighten this exact ordinary hand card");
        }
    }

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void aFundedCardKeepsItsPositiveWitnessWhenAnUnrelatedManualRouteMakesTheScanIncomplete() throws Exception {
        try (var game = new RulesScenario("ultima-origin-positive-in-incomplete-scan",
                Map.of(0, List.of("Forest", "Forest", "Forest", "Forest", "Forest", "Victory Chimes")), ULTIMA, "Progenitus")) {
            game.until(next -> next.seat() == 0 && priority(next) && text(next.state(), "phaseKey").equals("MAIN1"));
            var observed = HeadlessPlatform.query(game.table, () -> {
                var actor = game.table.game.getRegisteredPlayers().get(0);
                var controller = (PlayerControllerHuman)actor.getController();
                var input = (InputPassPriority)controller.getInputQueue().getInput();
                // A genuine second script supplies a controlled, more expensive
                // hand spell without depending on a shuffled opening hand.
                var giant = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard("Giant Adephage"), actor);
                actor.getZone(ZoneType.Hand).add(giant);
                assertTrue(controller.refreshRestoredPriority(input));
                game.table.publish();
                assertTrue(input.isPriorityActive());
                assertSame(controller.getInputQueue().getInput(), input);
                return Map.entry(game.state(0), controller.isPriorityAvailabilityCurrent(input));
            });
            var state = observed.getKey();
            Files.writeString(game.evidence.resolve("mixed-funding-scan.json"), JSON.toJson(state));
            assertEquals(affordability(card(state, "Hand", ULTIMA)), "Payable", "A positive funding witness does not require proving every other card absent");
            assertFalse(observed.getValue(), "This controlled current scan is incomplete; false Auto alone would not establish that distinction");
            assertEquals(affordability(card(state, "Hand", "Giant Adephage")), "Unknown", "A relevant manual route is not a complete seven-mana funding witness");
            assertFalse(state.getAsJsonObject("prompt").get("canAutoPass").getAsBoolean());
            assertEquals(tapped(state, "Forest"), 0L);
            assertEquals(tapped(state, "Victory Chimes"), 0L);
            assertEquals(pool(state), 0);
            assertTrue(state.getAsJsonArray("stack").isEmpty());
        }
    }

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void anOldFundingWitnessCannotSurviveARealRulesChangeOrAnUnverifiedInput() throws Exception {
        try (var game = new RulesScenario("ultima-origin-funding-lifetime",
                Map.of(0, Collections.nCopies(5, "Wastes")), ULTIMA, "Progenitus")) {
            game.until(next -> next.seat() == 0 && priority(next) && text(next.state(), "phaseKey").equals("MAIN1"));
            var before = current(game);
            assertEquals(affordability(card(before.state(), "Hand", ULTIMA)), "Payable");
            var actor = game.table.game.getRegisteredPlayers().get(0);
            var controller = (PlayerControllerHuman)actor.getController();
            var states = HeadlessPlatform.query(game.table, () -> {
                var input = (InputPassPriority)controller.getInputQueue().getInput();
                var source = actor.getCardsIn(ZoneType.Battlefield).stream().filter(value -> value.getName().equals("Wastes")).findFirst().orElseThrow();
                actor.getManaPool().addMana(new Mana((byte)ManaAtom.COLORLESS, source, source.getManaAbilities().get(0).getManaPart(), actor));
                game.table.publish();
                var stale = game.state(0);
                assertEquals(text(stale.getAsJsonObject("prompt"), "id"), text(before.prompt(), "id"));
                assertEquals(affordability(card(stale, "Hand", ULTIMA)), "Unknown", "Republishing the same input cannot remint its stale scan");
                assertTrue(controller.refreshRestoredPriority(input));
                game.table.publish();
                var refreshed = game.state(0);
                assertEquals(affordability(card(refreshed, "Hand", ULTIMA)), "Payable", "A later completed clean scan may establish fresh funding");
                input.setAvailabilityVerified(false);
                game.table.publish();
                var unverified = game.state(0);
                assertEquals(affordability(card(unverified, "Hand", ULTIMA)), "Unknown", "An unverified input carries no positive funding certificate");
                return List.of(stale, refreshed, unverified);
            });
            Files.writeString(game.evidence.resolve("funding-lifetime.json"), JSON.toJson(states));
            assertEquals(pool(states.get(2)), 1);
            assertEquals(tapped(states.get(2), "Wastes"), 0L);
            assertFalse(stacked(states.get(2)));
        }
    }

    private static String affordability(JsonObject card) {
        return card.has("playAffordability") ? text(card, "playAffordability") : "Unknown";
    }

    private static Input current(RulesScenario game) {
        var state = HeadlessPlatform.query(game.table, () -> game.state(0));
        return new Input(0, state, state.getAsJsonObject("prompt"));
    }

    private static boolean priority(Input input) { return text(input.prompt(), "inputType").equals("InputPassPriority"); }

    private static JsonObject card(JsonObject state, String zoneName, String name) {
        return zone(viewer(state), zoneName).getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "name").equals(name)).findFirst().orElse(null);
    }

    private static long tapped(JsonObject state, String land) {
        return zone(viewer(state), "Battlefield").getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "name").equals(land) && value.get("tapped").getAsBoolean()).count();
    }

    private static int pool(JsonObject state) {
        return viewer(state).getAsJsonObject("mana").entrySet().stream().mapToInt(entry -> entry.getValue().getAsInt()).sum();
    }

    private static boolean stacked(JsonObject state) {
        return state.getAsJsonArray("stack").asList().stream().map(value -> value.getAsJsonObject())
                .anyMatch(value -> text(value, "name").equals(ULTIMA));
    }
}
