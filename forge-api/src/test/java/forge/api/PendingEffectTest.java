package forge.api;

import com.google.common.collect.Multiset;
import com.google.common.collect.HashMultiset;
import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import forge.StaticData;
import forge.deck.Deck;
import forge.game.ability.AbilityFactory;
import forge.game.ability.AbilityKey;
import forge.game.ability.AbilityUtils;
import forge.game.card.Card;
import forge.game.card.CounterType;
import forge.game.card.CounterEnumType;
import forge.game.player.RegisteredPlayer;
import forge.game.zone.ZoneType;
import forge.gamemodes.match.input.InputSelectTargets;
import forge.player.PlayerControllerHuman;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.nio.file.Path;
import java.time.Duration;
import java.util.List;
import java.util.Map;
import java.util.ArrayList;
import java.util.function.Predicate;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Pending values come from the owning engine ability, never the new counter total. */
public class PendingEffectTest {
    private static final Gson JSON = new GsonBuilder().setPrettyPrinting().serializeNulls().create();
    private static final String PAYOFF = "All Will Be One";
    private static final String CHANDRA = "Chandra, the Firebrand";

    @Test
    public void unknownAmountsRemainUnknownAndConcealedSourcesPublishNoEffect() throws Exception {
        if (StaticData.instance() == null) EngineResources.load(Path.of("../forge-gui/res"));
        var players = new ArrayList<RegisteredPlayer>();
        for (int seat = 0; seat < 4; seat++) players.add(MatchTable.human(new Deck(), "Constructed", "Seat " + seat));
        // This unstarted projection fixture has no rules loop or fabricated
        // running-game mutation. The two journeys above establish real damage.
        try (var table = new MatchTable("Constructed", players, Path.of("../forge-gui/res"), Files.createTempDirectory("pending-effect-projection"))) {
            HeadlessPlatform.activate(table);
            var owner = table.game.getPlayers().get(0);
            var source = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard(PAYOFF), owner);
            owner.getZone(ZoneType.Battlefield).add(source, null, null);
            var zero = AbilityFactory.getAbility("DB$ DealDamage | NumDmg$ 0 | ValidTgts$ Any", source);
            var known = PendingEffects.project(zero, owner.getView(), table.identities, card -> null);
            assertNotNull(known);
            assertEquals(known.get("amount"), 0, "An exact zero must not become unknown");
            assertEquals(known.get("amountBasis"), "base");
            assertEquals(PendingEffects.project(zero, owner.getView(), table.identities, card -> null).get("id"), known.get("id"));
            for (String expression : List.of("Count$Targeted$Power", "X")) {
                var ability = AbilityFactory.getAbility("DB$ DealDamage | NumDmg$ " + expression + " | ValidTgts$ Any", source);
                var effect = PendingEffects.project(ability, owner.getView(), table.identities, card -> null);
                assertNotNull(effect);
                assertNull(effect.get("amount"), "A target dependency or unbound captured value must not be invented");
                assertEquals(effect.get("amountBasis"), "dynamic");
            }
            var variable = AbilityFactory.getAbility("DB$ DealDamage | NumDmg$ Count$xPaid | ValidTgts$ Any", source);
            assertNull(PendingEffects.project(variable, owner.getView(), table.identities, card -> null).get("amount"), "Unpaid X remains unknown");
            variable.setXManaCostPaid(0);
            assertEquals(PendingEffects.project(variable, owner.getView(), table.identities, card -> null).get("amount"), 0, "An explicitly paid zero is known");
            var divided = AbilityFactory.getAbility("DB$ DealDamage | NumDmg$ 3 | ValidTgts$ Any | DivideEvenly$ RoundedDown", source);
            var pool = PendingEffects.project(divided, owner.getView(), table.identities, card -> null);
            assertEquals(pool.get("amount"), 3);
            assertEquals(pool.get("distribution"), "divided", "A divided amount is the total pool, not damage per recipient");
            for (String name : List.of("Lightning Helix", "Electrolyze", "Volt Charge", "Searing Blood")) {
                var complex = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard(name), owner);
                table.game.getStackZone().add(complex, null, null);
                var ability = complex.getSpellAbilities().stream().filter(candidate -> candidate.isSpell()).findFirst().orElseThrow();
                assertNull(PendingEffects.project(ability, owner.getView(), table.identities, card -> null),
                        "A single numeric face cannot replace this card's complete chained effect: " + name);
            }
            var privateRecipient = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard("Llanowar Elves"), table.game.getPlayers().get(1));
            table.game.getPlayers().get(1).getZone(ZoneType.Hand).add(privateRecipient, null, null);
            var captured = AbilityFactory.getAbility("DB$ DealDamage | NumDmg$ TriggerCount$Amount | ValidTgts$ Opponent", source);
            captured.setTriggeringObject(AbilityKey.Amount, 1);
            var additions = HashMultiset.<CounterType>create();
            additions.add(CounterEnumType.P1P1);
            captured.setTriggeringObject(AbilityKey.CounterMap, additions);
            captured.setTriggeringObject(AbilityKey.Object, privateRecipient);
            captured.setTriggeringObject(AbilityKey.Source, owner);
            boolean[] rejected = {false};
            var privateContext = PendingEffects.project(captured, owner.getView(), table.identities, card -> { rejected[0] = true; return null; });
            assertTrue(rejected[0], "The recipient goes through its authorized public-card boundary");
            assertEquals(privateContext.get("amount"), 1, "An independently public damage amount remains available");
            assertFalse(privateContext.containsKey("counterChange"), "Rejected counter-card visibility suppresses the entire counter context");
            assertFalse(JSON.toJson(privateContext).contains("Llanowar Elves"));
            source.turnFaceDown(true);
            assertNull(PendingEffects.project(zero, owner.getView(), table.identities, card -> {
                throw new AssertionError("A concealed source must not reach the public card projector");
            }));
        }
    }

    @Test(timeOut = 90000)
    public void chandraLoyaltyThreeToFourCapturesOneDamageBeforeChoosingItsTarget() throws Exception {
        try (var game = new RulesScenario("pending-effect-chandra",
                Map.of(0, List.of(PAYOFF, "Mountain", "Mountain", "Mountain", "Mountain")), CHANDRA, "Progenitus")) {
            var main = main(game);
            int owner = main.state().get("viewerId").getAsInt();
            int victim = game.table.game.getPlayers().get(1).getId();
            int otherVictim = game.table.game.getPlayers().get(2).getId();
            var floating = floatMana(game, main, "Mountain", "R", 4);
            activate(game, floating, card(floating.state(), owner, "Hand", CHANDRA), action -> text(action, "kind").equals("cast"));

            // Casting the real planeswalker initializes loyalty through Forge's
            // ETB replacement and produces its separate three-counter trigger.
            var enters = target(game, PAYOFF);
            assertCounterTrigger(game, enters, CHANDRA, "Loyalty", 3, 3);
            save(game, "01-entering-loyalty-target", enters.state());
            assertTrue(game.answer(enters, Map.of("action", "player", "playerId", otherVictim)));
            var settled = game.until(input -> input.seat() == 0 && priority(input)
                    && input.state().getAsJsonArray("stack").isEmpty()
                    && card(input.state(), owner, "Battlefield", CHANDRA) != null);
            assertEquals(player(settled.state(), otherVictim).get("life").getAsInt(), 37);
            assertEquals(counter(card(settled.state(), owner, "Battlefield", CHANDRA), "Loyalty"), 3);

            activate(game, settled, card(settled.state(), owner, "Battlefield", CHANDRA),
                    action -> text(action, "text").contains("deals 1 damage"));
            var ownTarget = target(game, CHANDRA);
            assertEquals(ownTarget.prompt().getAsJsonObject("effect").get("amount").getAsInt(), 1);
            assertEquals(counter(card(ownTarget.state(), owner, "Battlefield", CHANDRA), "Loyalty"), 3,
                    "The original ability chooses its target before paying its loyalty cost");
            assertTrue(game.answer(ownTarget, Map.of("action", "player", "playerId", otherVictim)));

            var triggered = target(game, PAYOFF);
            assertCounterTrigger(game, triggered, CHANDRA, "Loyalty", 4, 1);
            assertCounterHistory(triggered.state(), CHANDRA, "Loyalty", 3, 4, CHANDRA);
            save(game, "02-added-loyalty-target", triggered.state());
            var stacked = chooseAndStack(game, triggered, victim);
            save(game, "03-added-loyalty-stack", stacked.state());
            var resolved = game.until(input -> input.seat() == 0 && priority(input)
                    && player(input.state(), victim).get("life").getAsInt() == 39);
            assertEquals(player(resolved.state(), victim).get("life").getAsInt(), 39,
                    "Adding one loyalty deals one damage, not the new total of four");
            assertEquals(player(resolved.state(), otherVictim).get("life").getAsInt(), 37,
                    "Chandra's original one-damage ability remains pending below the trigger");
            assertEquals(counter(card(resolved.state(), owner, "Battlefield", CHANDRA), "Loyalty"), 4);
            assertPlayerImpact(resolved.state(), victim, 1, 40, 39);
            save(game, "04-added-loyalty-result", resolved.state());
        }
    }

    @Test(timeOut = 90000)
    public void preventionKeepsPendingThreeSeparateFromActualTwoDamage() throws Exception {
        try (var game = new RulesScenario("pending-effect-prevention",
                Map.of(0, List.of(PAYOFF, "Mountain", "Mountain", "Mountain", "Mountain"),
                        1, List.of("Urza's Armor")), CHANDRA, "Progenitus")) {
            var main = main(game);
            int owner = main.state().get("viewerId").getAsInt();
            int victim = game.table.game.getPlayers().get(1).getId();
            var floating = floatMana(game, main, "Mountain", "R", 4);
            activate(game, floating, card(floating.state(), owner, "Hand", CHANDRA), action -> text(action, "kind").equals("cast"));
            var entering = target(game, PAYOFF);
            assertCounterTrigger(game, entering, CHANDRA, "Loyalty", 3, 3);
            save(game, "01-base-three-target", entering.state());
            var stacked = chooseAndStack(game, entering, victim);
            assertEquals(payoff(stacked.state()).getAsJsonObject("effect").get("amount").getAsInt(), 3);
            save(game, "02-base-three-stack", stacked.state());
            var resolved = game.until(input -> input.seat() == 0 && priority(input)
                    && input.state().getAsJsonArray("stack").isEmpty());
            assertEquals(player(resolved.state(), victim).get("life").getAsInt(), 38);
            assertPlayerImpact(resolved.state(), victim, 2, 40, 38);
            for (int seat = 0; seat < 4; seat++) assertPlayerImpact(game.state(seat), victim, 2, 40, 38);
            save(game, "03-actual-two-result", resolved.state());
        }
    }

    @Test(timeOut = 90000)
    public void proliferatingThreeExistingCreatureCountersCapturesOneInsteadOfFour() throws Exception {
        try (var game = new RulesScenario("pending-effect-nonloyalty",
                Map.of(0, List.of(PAYOFF, "Triskelion", "Karn's Bastion", "Forest", "Forest", "Forest", "Forest")),
                "Forest", "Progenitus", true)) {
            var main = main(game);
            int owner = main.state().get("viewerId").getAsInt();
            int victim = game.table.game.getPlayers().get(1).getId();
            var initial = card(main.state(), owner, "Battlefield", "Triskelion");
            assertEquals(counter(initial, "+1/+1"), 3);
            assertEquals(initial.get("power").getAsInt(), 4);
            assertEquals(initial.get("toughness").getAsInt(), 4);
            var floating = floatMana(game, main, "Forest", "G", 4);
            activate(game, floating, card(floating.state(), owner, "Battlefield", "Karn's Bastion"),
                    action -> text(action, "text").contains("Proliferate"));
            var choose = required(game, input -> text(input.prompt(), "inputType").equals("InputSelectEntitiesFromList"));
            var selected = card(choose.state(), owner, "Battlefield", "Triskelion");
            assertTrue(selected.get("selectable").getAsBoolean());
            assertTrue(game.answer(choose, Map.of("action", "card", "key", text(selected, "key"))));
            var triggered = target(game, PAYOFF);
            assertCounterTrigger(game, triggered, "Triskelion", "+1/+1", 4, 1);
            assertCounterHistory(triggered.state(), "Triskelion", "+1/+1", 3, 4, "Karn's Bastion");
            var current = card(triggered.state(), owner, "Battlefield", "Triskelion");
            assertEquals(current.get("power").getAsInt(), 5);
            assertEquals(current.get("toughness").getAsInt(), 5);
            var counterCard = triggered.prompt().getAsJsonObject("effect").getAsJsonObject("counterChange").getAsJsonObject("card");
            assertEquals(counterCard.get("power").getAsInt(), 5, "Pending context preserves authoritative current P/T");
            assertEquals(counterCard.get("toughness").getAsInt(), 5);
            save(game, "01-proliferate-target", triggered.state());
            var stacked = chooseAndStack(game, triggered, victim);
            save(game, "02-proliferate-stack", stacked.state());
            var resolved = game.until(input -> input.seat() == 0 && priority(input)
                    && input.state().getAsJsonArray("stack").isEmpty());
            assertEquals(player(resolved.state(), victim).get("life").getAsInt(), 39);
            assertEquals(counter(card(resolved.state(), owner, "Battlefield", "Triskelion"), "+1/+1"), 4);
            assertPlayerImpact(resolved.state(), victim, 1, 40, 39);
            save(game, "03-proliferate-result", resolved.state());
        }
    }

    private static void assertPlayerImpact(JsonObject state, int victim, int amount, int before, int after) {
        var impacts = state.getAsJsonArray("activity").asList().stream().map(value -> value.getAsJsonObject())
                .filter(entry -> entry.has("impact") && !entry.get("impact").isJsonNull())
                .map(entry -> entry.getAsJsonObject("impact")).toList();
        var damage = impacts.stream().filter(impact -> text(impact, "kind").equals("damage")
                && impact.get("playerId").getAsInt() == victim && text(impact, "sourceName").equals(PAYOFF))
                .findFirst().orElseThrow(() -> new AssertionError("Missing actual damage event: " + state));
        assertEquals(damage.get("amount").getAsLong(), (long)amount);
        assertTrue(damage.get("cardId").isJsonNull(), "The player recipient must not be replaced by its damage source");
        assertFalse(text(damage, "sourceCardId").isEmpty());
        assertTrue(damage.get("before").isJsonNull()); assertTrue(damage.get("after").isJsonNull());
        assertFalse(damage.get("combat").getAsBoolean()); assertFalse(damage.get("infect").getAsBoolean());
        var life = impacts.stream().filter(impact -> text(impact, "kind").equals("life")
                && impact.get("playerId").getAsInt() == victim).findFirst().orElseThrow();
        assertEquals(life.get("amount").getAsLong(), (long)after - before);
        assertEquals(life.get("before").getAsInt(), before); assertEquals(life.get("after").getAsInt(), after);
        assertTrue(life.get("sourceCardId").isJsonNull(), "A life event does not acquire an inferred damage cause");
    }

    private static void assertCounterTrigger(RulesScenario game, Input input, String recipient,
                                            String counterName, int total, int added) throws Exception {
        assertEquals(text(input.prompt().getAsJsonObject("sourceCard"), "name"), PAYOFF);
        var effect = input.prompt().getAsJsonObject("effect");
        assertNotNull(effect);
        assertEquals(text(effect, "kind"), "damage");
        assertEquals(text(effect, "amountBasis"), "trigger");
        assertEquals(effect.get("amount").getAsInt(), added);
        assertFalse(text(effect, "id").isBlank());
        int owner = input.state().get("viewerId").getAsInt();
        var current = card(input.state(), owner, "Battlefield", recipient);
        assertEquals(counter(current, counterName), total);
        var context = effect.getAsJsonObject("counterChange");
        assertEquals(context.get("actorPlayerId").getAsInt(), owner);
        assertEquals(text(context.getAsJsonObject("card"), "visualId"), text(current, "visualId"));
        assertTrue(text(context.getAsJsonObject("card"), "key").isEmpty(), "Context carries no actionable card key");
        assertFalse(context.getAsJsonObject("card").get("selectable").getAsBoolean(), "Counter context is read-only");
        assertFalse(context.getAsJsonObject("card").get("highlighted").getAsBoolean(), "Context does not inherit input selection highlights");
        var increment = context.getAsJsonArray("counters").get(0).getAsJsonObject();
        assertEquals(increment.get("added").getAsInt(), added);
        assertEquals(text(increment, "label").toLowerCase(java.util.Locale.ROOT), counterName.toLowerCase(java.util.Locale.ROOT));
        assertFalse(text(increment, "type").isEmpty());

        // Inspect the exact engine input while it is blocked at its own target
        // choice, independently checking the captured trigger and damage value.
        HeadlessPlatform.query(() -> {
            var controller = (PlayerControllerHuman)game.table.game.getPlayers().get(input.seat()).getController();
            var targeting = (InputSelectTargets)controller.getInputQueue().getInput();
            var ability = targeting.getSpellAbility();
            assertEquals(ability.getRootAbility().getTriggeringObject(AbilityKey.Amount), added);
            assertEquals(AbilityUtils.calculateAmount(ability.getHostCard(), ability.getParam("NumDmg"), ability), added);
            var object = (Card)ability.getRootAbility().getTriggeringObject(AbilityKey.Object);
            assertEquals(object.getName(), recipient);
            @SuppressWarnings("unchecked") var counters = (Multiset<CounterType>)ability.getRootAbility().getTriggeringObject(AbilityKey.CounterMap);
            assertEquals(counters.size(), added);
            return null;
        });
        synchronized (game.table.gate) { game.table.publish(); }
        var refreshed = game.state(input.seat()).getAsJsonObject("prompt");
        assertEquals(text(refreshed, "id"), text(input.prompt(), "id"));
        assertEquals(refreshed.getAsJsonObject("effect"), effect, "A harmless publication keeps the exact unanswered effect");
    }

    private static Input chooseAndStack(RulesScenario game, Input targeting, int victim) throws Exception {
        var effect = targeting.prompt().getAsJsonObject("effect");
        assertTrue(targeting.prompt().getAsJsonArray("playerChoices").asList().stream().anyMatch(value -> value.getAsInt() == victim));
        assertTrue(game.answer(targeting, Map.of("action", "player", "playerId", victim)));
        var priority = game.until(input -> input.seat() == 0 && priority(input)
                && input.state().getAsJsonArray("stack").asList().stream().anyMatch(value -> text(value.getAsJsonObject(), "name").equals(PAYOFF)));
        save(game, "actual-stack-before-validation", priority.state());
        var item = payoff(priority.state());
        assertEquals(item.getAsJsonObject("effect"), effect, "The same captured effect survives target selection and stack insertion");
        assertTrue(item.getAsJsonArray("targets").asList().stream().anyMatch(value -> value.getAsJsonObject().get("playerId").getAsInt() == victim));
        for (int seat = 0; seat < 4; seat++) {
            var state = game.state(seat);
            assertEquals(payoff(state).getAsJsonObject("effect"), effect, "Every seat receives the same public pending value and counter context");
            int viewerId = state.get("viewerId").getAsInt();
            for (var entry : state.getAsJsonArray("players")) {
                var player = entry.getAsJsonObject();
                if (player.get("id").getAsInt() != viewerId) assertTrue(zone(player, "Hand").getAsJsonArray("cards").isEmpty(), "Public effect context never broadens private hand visibility");
            }
        }
        return priority;
    }

    private static void assertCounterHistory(JsonObject state, String recipient, String counterName,
                                             int before, int after, String source) {
        assertTrue(state.getAsJsonArray("activity").asList().stream().map(value -> value.getAsJsonObject()).anyMatch(entry ->
                text(entry, "kind").equals("counters") && text(entry, "cardName").equals(recipient)
                        && entry.has("change") && !entry.get("change").isJsonNull()
                        && text(entry.getAsJsonObject("change"), "property").equalsIgnoreCase(counterName)
                        && entry.getAsJsonObject("change").get("before").getAsInt() == before
                        && entry.getAsJsonObject("change").get("after").getAsInt() == after
                        && entry.has("cause") && !entry.get("cause").isJsonNull()
                        && text(entry.getAsJsonObject("cause"), "name").equals(source)), "The actual counter mutation retains its exact source and before/after values");
    }

    private static Input floatMana(RulesScenario game, Input input, String landName, String color, int amount) throws Exception {
        for (int count = 1; count <= amount; count++) {
            var land = zone(viewer(input.state()), "Battlefield").getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                    .filter(card -> text(card, "name").equals(landName) && !card.get("tapped").getAsBoolean()).findFirst().orElseThrow();
            activate(game, input, land, action -> text(action, "text").contains("Add {" + color + "}"));
            int expected = count;
            input = current(game, state -> state.has("prompt") && !state.get("prompt").isJsonNull()
                    && text(state.getAsJsonObject("prompt"), "inputType").equals("InputPassPriority")
                    && viewer(state).getAsJsonObject("mana").get(color).getAsInt() == expected);
        }
        return input;
    }

    private static Input current(RulesScenario game, Predicate<JsonObject> ready) throws Exception {
        long deadline = System.nanoTime() + Duration.ofSeconds(5).toNanos();
        while (System.nanoTime() < deadline) {
            HeadlessPlatform.query(() -> null);
            var state = game.state(0);
            if (ready.test(state)) return new Input(0, state, state.getAsJsonObject("prompt"));
            Thread.sleep(5);
        }
        throw new AssertionError("The owned input did not settle: " + game.state(0));
    }

    private static void activate(RulesScenario game, Input input, JsonObject source, Predicate<JsonObject> matches) {
        var request = request(input, Map.of("key", text(source, "key")));
        var actions = JSON.toJsonTree(game.table.seat(input.seat()).sourceActions(request)).getAsJsonObject();
        var action = actions.getAsJsonArray("actions").asList().stream().map(value -> value.getAsJsonObject()).filter(matches).findFirst().orElseThrow();
        assertTrue(game.answer(input, Map.of("action", "useSourceAction", "key", text(source, "key"), "actionId", text(action, "id"))));
    }

    private static JsonObject request(Input input, Map<String, ?> body) {
        var request = JSON.toJsonTree(body).getAsJsonObject();
        request.addProperty("sessionId", text(input.state(), "id"));
        request.addProperty("promptId", text(input.prompt(), "id"));
        return request;
    }

    private static Input target(RulesScenario game, String source) throws Exception {
        var input = required(game, next -> text(next.prompt(), "inputType").equals("InputSelectTargets"));
        assertEquals(text(input.prompt().getAsJsonObject("sourceCard"), "name"), source);
        return input;
    }

    private static Input required(RulesScenario game, Predicate<Input> ready) throws Exception {
        while (true) {
            var input = game.until(next -> next.seat() == 0 && (text(next.prompt(), "inputType").startsWith("InputPayMana") || ready.test(next)));
            if (!text(input.prompt(), "inputType").startsWith("InputPayMana")) return input;
            assertTrue(input.prompt().get("okEnabled").getAsBoolean(), "The deliberately floated pool must pay the real cost");
            assertTrue(game.answer(input, Map.of("action", "ok")));
        }
    }

    private static Input main(RulesScenario game) throws Exception {
        return game.until(input -> input.seat() == 0 && priority(input)
                && text(input.state(), "phaseKey").equals("MAIN1")
                && input.state().get("activePlayerId").equals(input.state().get("viewerId")));
    }
    private static boolean priority(Input input) { return text(input.prompt(), "inputType").equals("InputPassPriority"); }
    private static JsonObject payoff(JsonObject state) {
        return state.getAsJsonArray("stack").asList().stream().map(value -> value.getAsJsonObject())
                .filter(item -> text(item, "name").equals(PAYOFF)).findFirst().orElseThrow();
    }
    private static JsonObject card(JsonObject state, int owner, String zoneName, String name) {
        return zone(player(state, owner), zoneName).getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "name").equals(name)).findFirst().orElse(null);
    }
    private static int counter(JsonObject card, String name) {
        return card.getAsJsonObject("counters").entrySet().stream().filter(entry -> entry.getKey().equalsIgnoreCase(name))
                .mapToInt(entry -> entry.getValue().getAsInt()).findFirst().orElse(0);
    }
    private static void save(RulesScenario game, String name, JsonObject state) throws Exception {
        Files.writeString(game.evidence.resolve(name + ".json"), JSON.toJson(state));
    }
}
