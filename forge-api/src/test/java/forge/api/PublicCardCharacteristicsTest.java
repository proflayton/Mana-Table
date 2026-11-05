package forge.api;

import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import forge.card.GamePieceType;
import forge.game.ability.AbilityFactory;
import forge.game.ability.AbilityUtils;
import forge.game.card.CardFactory;
import org.testng.annotations.DataProvider;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.Set;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

public class PublicCardCharacteristicsTest {
    private static final Set<String> FIELDS = Set.of("name", "type", "gamePieceType", "manaCost", "commanderTax", "power", "toughness",
            "text", "tapped", "phasedOut", "sick", "damage", "attacking", "blocking", "counters", "defenderId", "defender", "faceDown",
            "combatKeywords", "blockingCapacity", "states");
    @DataProvider public Object[][] pieces() { return new Object[][] {{"speed"}, {"emblem"}, {"copy"}}; }

    @Test(dataProvider = "pieces", groups = "canonical-interaction", timeOut = 90000)
    public void actualRulesCreatedPiecesKeepTheirCategoryAndVisibleTypeForEveryAudience(String kind) throws Exception {
        try (var scenario = new RulesScenario("public-characteristics-" + kind, Map.of(0, List.of("Reliquary Tower")))) {
            scenario.until(input -> input.seat() == 0 && text(input.state(), "phaseKey").equals("MAIN1")
                    && text(input.prompt(), "inputType").equals("InputPassPriority"));
            HeadlessPlatform.query(scenario.table, () -> {
                var actor = scenario.table.game.getRegisteredPlayers().get(0);
                var source = actor.getCommanders().get(0);
                // Controlled production factory operations exercise the actual
                // pieces created by speed rules, effect resolution and copies.
                // This is a projection test, not a cast/equip gesture claim.
                if (kind.equals("speed")) actor.increaseSpeed();
                else if (kind.equals("emblem")) {
                    var effect = AbilityFactory.getAbility("DB$ Effect | Name$ Emblem - projection | Duration$ Permanent", source);
                    effect.setActivatingPlayer(actor); AbilityUtils.resolve(effect);
                } else {
                    var original = source.getFirstSpellAbility(); original.setActivatingPlayer(actor);
                    var copy = CardFactory.copySpellAbilityAndPossiblyHost(original, original, actor);
                    assertTrue(copy.isCopied()); assertEquals(copy.getHostCard().getGamePieceType(), GamePieceType.COPIED_SPELL);
                    scenario.table.game.getStack().add(copy);
                }
                scenario.table.publish();
                var physical = kind.equals("copy") ? scenario.table.game.getStack().peekAbility().getHostCard()
                        : actor.getCardsIn(forge.game.zone.ZoneType.Command).stream().filter(card -> card.getName().equals(
                                kind.equals("speed") ? "Start Your Engines!" : "Emblem - projection")).findFirst().orElseThrow();
                var facts = PublicCardCharacteristics.capture(physical.getView(), actor.getView(), scenario.table.game.getView().getCombat(), false, true);
                assertEquals(facts.keySet(), FIELDS, "The complete scalar schema carries no audience identities, private reading or action authority");
                assertEquals(PublicCardCharacteristics.projected(facts), facts, "Every captured characteristic belongs to the shared public selector");
                assertFrozen(facts);
                var json = new GsonBuilder().serializeNulls().setPrettyPrinting().create();
                var states = new ArrayList<JsonObject>();
                for (int seat = 0; seat < 4; seat++) states.add(json.toJsonTree(scenario.table.seat(seat).state()).getAsJsonObject());
                states.add(json.toJsonTree(SpectatorObservation.project(scenario.table,
                        states.get(0).get("boardRevision").getAsLong(), null)).getAsJsonObject());
                for (var state : states) {
                    JsonObject piece = kind.equals("copy") ? state.getAsJsonArray("stack").get(0).getAsJsonObject().getAsJsonObject("card")
                            : zone(player(state, actor.getId()), "Command").getAsJsonArray("cards").asList().stream()
                                .map(value -> value.getAsJsonObject()).filter(card -> text(card, "name").equals(
                                        kind.equals("speed") ? "Start Your Engines!" : "Emblem - projection")).findFirst().orElseThrow();
                    Files.writeString(scenario.evidence.resolve("actual-piece-seat-" + state.get("viewerId").getAsInt() + ".json"), json.toJson(state));
                    assertEquals(text(piece, "gamePieceType"), kind.equals("copy") ? "copied_spell" : "effect",
                            "Piece classification follows the actual engine category rather than emblem/token display flags");
                    assertEquals(text(piece, "type"), kind.equals("copy") ? "Legendary Creature - Elf Warrior" : "Effect",
                            "A real blank-type rule effect has the same visible Effect label for every audience");
                    for (String field : FIELDS) assertEquals(piece.get(field), json.toJsonTree(facts.get(field)),
                            "Both real projection consumers include the complete captured characteristic: " + field);
                }
                return null;
            });
        }
    }

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void aRealManifestKeepsOnlyPublicPhysicalFactsAndDetachedCounterContainers() throws Exception {
        try (var scenario = new RulesScenario("public-characteristics-concealed", Map.of(), "Merfolk Looter", "Rhys the Redeemed")) {
            scenario.until(input -> input.seat() == 0 && text(input.state(), "phaseKey").equals("MAIN1")
                    && text(input.prompt(), "inputType").equals("InputPassPriority"));
            HeadlessPlatform.query(scenario.table, () -> {
                var actor = scenario.table.game.getRegisteredPlayers().get(0);
                var cause = AbilityFactory.getAbility("DB$ Manifest | DefinedPlayer$ You", actor.getCommanders().get(0));
                cause.setActivatingPlayer(actor);
                var source = actor.getCardsIn(forge.game.zone.ZoneType.Library).get(0).manifest(actor, cause, forge.game.ability.AbilityKey.newMap());
                var initialCounters = new forge.game.GameEntityCounterTable();
                source.addCounter(forge.game.card.CounterEnumType.P1P1, 2, actor, initialCounters);
                initialCounters.replaceCounterEffect(scenario.table.game, cause);
                assertEquals(source.getCounters(forge.game.card.CounterEnumType.P1P1), 2);
                scenario.table.game.getAction().checkStaticAbilities(false); source.updatePTforView();
                scenario.table.publish();
                var facts = PublicCardCharacteristics.capture(source.getView(), actor.getView(), scenario.table.game.getView().getCombat(), true, true);
                assertEquals(facts.keySet(), FIELDS); assertFrozen(facts);
                assertEquals(facts.get("name"), "Face-down card"); assertEquals(facts.get("type"), "Creature");
                assertEquals(facts.get("power"), 4); assertEquals(facts.get("toughness"), 4);
                String counterName = forge.game.card.CounterEnumType.P1P1.getName();
                assertEquals(facts.get("counters"), Map.of(counterName, 2));
                assertEquals(facts.get("manaCost"), ""); assertEquals(facts.get("text"), "");
                assertEquals(facts.get("gamePieceType"), "card"); assertEquals(facts.get("commanderTax"), 0);
                assertFalse(facts.toString().contains("Merfolk Looter"));
                var before = new GsonBuilder().serializeNulls().create().toJsonTree(facts);
                // Controlled real counter placement and tap mutate the live
                // view, never the old captured containers, characteristics or states.
                var nextCounters = new forge.game.GameEntityCounterTable();
                source.addCounter(forge.game.card.CounterEnumType.P1P1, 3, actor, nextCounters);
                source.addCounter(forge.game.card.CounterKeywordType.get("Flying"), 1, actor, nextCounters);
                nextCounters.replaceCounterEffect(scenario.table.game, cause);
                scenario.table.game.getAction().checkStaticAbilities(false); source.updatePTforView();
                source.setTapped(true); assertTrue(source.getView().isTapped());
                var fresh = PublicCardCharacteristics.capture(source.getView(), actor.getView(), scenario.table.game.getView().getCombat(), true, true);
                assertEquals(fresh.get("power"), 7); assertEquals(fresh.get("toughness"), 7);
                assertEquals(((Map<?, ?>)fresh.get("counters")).get(counterName), 5);
                assertNotEquals(fresh.get("states"), facts.get("states"), "A real flying counter changes the new effective public state");
                assertEquals(new GsonBuilder().serializeNulls().create().toJsonTree(facts), before);
                assertEquals(facts.get("tapped"), false);
                assertEquals(facts.get("counters"), Map.of(counterName, 2));
                assertEquals(facts.get("power"), 4); assertEquals(facts.get("toughness"), 4);
                return null;
            });
        }
    }

    @SuppressWarnings("unchecked")
    private static void assertFrozen(Object value) {
        if (value instanceof Map<?, ?> map) {
            assertThrows(UnsupportedOperationException.class, () -> ((Map<Object, Object>)map).put("__test", null));
            for (Object item : map.values()) assertFrozen(item);
        } else if (value instanceof List<?> list) {
            assertThrows(UnsupportedOperationException.class, () -> ((List<Object>)list).add(null));
            for (Object item : list) assertFrozen(item);
        }
    }
}
