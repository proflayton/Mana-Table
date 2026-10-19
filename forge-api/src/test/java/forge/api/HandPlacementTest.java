package forge.api;

import com.google.gson.JsonElement;
import com.google.gson.JsonObject;
import org.testng.annotations.DataProvider;
import org.testng.annotations.Test;

import java.util.*;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Real four-seat rules, exact prompt scopes and private hand routing. */
public class HandPlacementTest {
    @DataProvider public Object[][] landOrders() { return new Object[][] {{false}, {true}}; }

    @Test(dataProvider = "landOrders", timeOut = 90000)
    public void activateTheExactForestAbilityFromHandBeforeOrAfterANormalLandPlay(boolean landFirst) throws Exception {
        try (var game = new RulesScenario("hand-placement-" + (landFirst ? "land-first" : "ability-first"), Map.of(0, List.of("Gaea's Touch")))) {
            var input = main(game);
            assertEquals(viewer(input.state()).get("landPlaysUsed").getAsInt(), 0);
            assertEquals(viewer(input.state()).get("landPlayLimit").getAsInt(), 1);
            if (landFirst) {
                assertTrue(game.answer(input, Map.of("action", "card", "key", text(hand(input.state()).get(0), "key"))));
                input = game.until(i -> i.seat() == 0 && priority(i) && forests(i.state()) == 1);
                assertEquals(viewer(input.state()).get("landPlaysUsed").getAsInt(), 1);
                assertTrue(hand(input.state()).stream().noneMatch(c -> c.get("selectable").getAsBoolean()), "The ordinary second land is not playable");
            }
            var ability = placement(input);
            var eligible = ability.getAsJsonArray("eligibleHandKeys").asList().stream().map(JsonElement::getAsString).toList();
            assertEquals(new HashSet<>(eligible), hand(input.state()).stream().map(c -> text(c,"key")).collect(java.util.stream.Collectors.toSet()));
            assertEquals(ability.get("handPlacementLimit").getAsInt(), 1);
            assertNotNull(card(input.state(), "Battlefield", "Gaea's Touch"));
            assertFalse(ability.has("eligibleHandCardIds"), "Internal engine IDs never cross the protocol");
            var initial = input;
            String abilityId = text(ability,"id"), handKey = eligible.get(0);
            String firstForestId = text(hand(input.state()).get(0),"visualId");
            int handBefore = count(viewer(input.state()), "Hand");
            rejected(game, input, Map.of("action","useHandPlacement","abilityId","unknown","handKey",handKey));
            rejected(game, input, Map.of("action","useHandPlacement","abilityId",abilityId,"handKey",text(ability,"sourceCardKey")));
            JsonObject foreign = request(input, Map.of("action","useHandPlacement","abilityId",abilityId,"handKey",handKey));
            assertThrows(IllegalArgumentException.class, () -> game.table.seat(1).action(foreign));
            assertEquals(text(game.state(0).getAsJsonObject("prompt"),"id"),text(input.prompt(),"id"));
            assertTrue(game.answer(input, Map.of("action","useHandPlacement","abilityId",abilityId,"handKey",handKey)));
            input = game.until(i -> i.seat() == 0 && priority(i) && !i.state().getAsJsonArray("stack").isEmpty());
            assertEquals(count(viewer(input.state()), "Hand"),handBefore,"Activation does not move a hand card before resolution");
            assertEquals(forests(input.state()),landFirst ? 1L : 0L);
            assertNotNull(card(input.state(),"Battlefield","Gaea's Touch"),"Routing must never substitute the sacrifice mana ability");
            assertEquals(viewer(input.state()).getAsJsonObject("mana").get("G").getAsInt(),0);
            assertTrue(input.state().getAsJsonArray("stack").get(0).getAsJsonObject().get("ability").getAsBoolean());
            assertTrue(game.answer(input,Map.of("action","ok")));
            Set<Integer> responders = new HashSet<>();
            input = game.until(i -> {
                if (i.seat() != 0 && priority(i) && !i.state().getAsJsonArray("stack").isEmpty()) responders.add(i.seat());
                return i.seat() == 0 && text(i.prompt(),"inputType").equals("InputSelectEntitiesFromList");
            });
            assertEquals(responders,Set.of(1,2,3),"Every opponent receives its normal response opportunity");
            assertEquals(text(input.prompt().getAsJsonObject("sourceCard"),"name"),"Gaea's Touch");
            assertTrue(hand(input.state()).stream().allMatch(c -> c.get("selectable").getAsBoolean()));
            rejected(game,input,Map.of("action","useHandPlacement","abilityId",abilityId,"handKey",text(hand(input.state()).get(0),"key")));
            for (int seat=1;seat<4;seat++) {
                var other=game.state(seat);
                int actor=viewer(input.state()).get("id").getAsInt();
                assertTrue(zone(player(other,actor),"Hand").getAsJsonArray("cards").isEmpty());
                assertFalse(other.toString().contains(handKey),"Another viewer cannot receive the actor's scoped eligible hand keys");
            }
            var chosenForest = hand(input.state()).stream().filter(c -> !text(c,"visualId").equals(firstForestId)).findFirst().orElseThrow();
            String chosenId=text(chosenForest,"visualId");
            assertTrue(game.answer(input,Map.of("action","card","key",text(chosenForest,"key"))));
            input=game.until(i -> i.seat()==0 && priority(i) && i.state().getAsJsonArray("stack").isEmpty()
                    && forests(i.state())==(landFirst?2:1));
            assertTrue(zone(viewer(input.state()),"Battlefield").getAsJsonArray("cards").asList().stream()
                    .map(JsonElement::getAsJsonObject).anyMatch(c -> text(c,"visualId").equals(chosenId)),"Resolution uses the player's later Forest choice");
            assertNotNull(card(input.state(),"Hand","Forest"));
            assertEquals(viewer(input.state()).get("landPlaysUsed").getAsInt(),landFirst?1:0,"Putting a Forest is not a land play");
            assertEquals(viewer(input.state()).get("landPlayLimit").getAsInt(),1);
            assertTrue(placements(input).isEmpty(),"The once-per-turn route disappears after activation");
            JsonObject stale=request(initial,Map.of("action","useHandPlacement","abilityId",abilityId,"handKey",handKey));
            assertThrows(IllegalArgumentException.class,()->game.table.seat(0).action(stale));
            rejected(game,input,Map.of("action","useHandPlacement","abilityId",abilityId,"handKey",text(hand(input.state()).get(0),"key")));
            assertNotNull(card(game.state(0),"Battlefield","Gaea's Touch"),"A reused/invalid route never falls back to sacrifice");
            if (!landFirst) {
                var normal=hand(input.state()).stream().filter(c -> c.get("selectable").getAsBoolean()).findFirst().orElseThrow();
                assertTrue(game.answer(input,Map.of("action","card","key",text(normal,"key"))));
                input=game.until(i -> i.seat()==0 && priority(i) && forests(i.state())==2);
                assertEquals(viewer(input.state()).get("landPlaysUsed").getAsInt(),1);
            }
            assertTrue(game.answer(input,Map.of("action","card","key",text(card(input.state(),"Battlefield","Gaea's Touch"),"key"))));
            input=game.until(i -> i.seat()==0 && priority(i) && card(i.state(),"Battlefield","Gaea's Touch")==null);
            assertEquals(viewer(input.state()).getAsJsonObject("mana").get("G").getAsInt(),2,"The separate sacrifice ability still produces GG");
            assertTrue(input.state().getAsJsonArray("stack").isEmpty(),"The separate mana ability does not use the stack");
        }
    }

    @Test(timeOut=90000) public void distinguishBasicForestsFromOtherHandsAndRespectSorceryTiming() throws Exception {
        try(var game=new RulesScenario("hand-placement-nonbasic",Map.of(0,List.of("Gaea's Touch")),"Taiga","Rhys the Redeemed")) {
            var input=main(game);
            assertTrue(placements(input).isEmpty(),"A nonbasic Forest is not a basic Forest candidate");
            assertNotNull(card(input.state(),"Battlefield","Gaea's Touch"));
        }
        try(var game=new RulesScenario("hand-placement-next-turn",Map.of(0,List.of("Gaea's Touch")))) {
            var input=main(game); var ability=placement(input);
            assertEquals(ability.getAsJsonArray("eligibleHandKeys").size(),hand(input.state()).size());
            input=game.until(i -> i.seat()==0 && priority(i) && !text(i.state(),"phaseKey").equals("MAIN1"));
            assertTrue(placements(input).isEmpty(),"The sorcery-only route is absent outside a main phase");
        }
    }

    @Test(timeOut=90000) public void passiveExtraLandEffectsPublishTheirAuthoritativeAllowance() throws Exception {
        // Registered setup extras do not receive distinct entry timestamps. Test
        // these two independent permissions separately, as actual engine effects.
        for(var permission:List.of(Map.entry("Exploration",2),Map.entry("Azusa, Lost but Seeking",3))) {
        try(var game=new RulesScenario("hand-placement-allowance-"+permission.getValue(),Map.of(0,List.of(permission.getKey())))) {
            var input=main(game);
            assertEquals(viewer(input.state()).get("landPlaysUsed").getAsInt(),0);
            assertEquals(viewer(input.state()).get("landPlayLimit").getAsInt(),permission.getValue().intValue());
            assertTrue(placements(input).isEmpty(),"Passive extra land plays are not invented as activated routes");
            for(int played=1;played<=permission.getValue();played++) {
                var forest=hand(input.state()).stream().filter(c -> c.get("selectable").getAsBoolean()).findFirst().orElseThrow();
                assertTrue(game.answer(input,Map.of("action","card","key",text(forest,"key"))));
                int expected=played;
                input=game.until(i -> i.seat()==0 && priority(i) && forests(i.state())==expected);
                assertEquals(viewer(input.state()).get("landPlaysUsed").getAsInt(),played);
                assertEquals(viewer(input.state()).get("landPlayLimit").getAsInt(),permission.getValue().intValue());
            }
            assertTrue(hand(input.state()).stream().noneMatch(c -> c.get("selectable").getAsBoolean()));
        }
        }
        try(var game=new RulesScenario("hand-placement-unlimited",Map.of(0,List.of("Fastbond")))) {
            var input=main(game);
            assertEquals(viewer(input.state()).get("landPlaysUsed").getAsInt(),0);
            assertTrue(viewer(input.state()).get("landPlayLimit").isJsonNull(),"Unlimited land permission has no artificial numeric cap");
            assertTrue(placements(input).isEmpty());
        }
    }

    private static JsonObject request(RulesScenario.Input input,Map<String,?> values) {
        var result=new com.google.gson.Gson().toJsonTree(values).getAsJsonObject();
        result.addProperty("sessionId",text(input.state(),"id"));result.addProperty("promptId",text(input.prompt(),"id"));return result;
    }
    private static void rejected(RulesScenario game,RulesScenario.Input input,Map<String,?> values) {
        assertThrows(IllegalArgumentException.class,()->game.table.seat(input.seat()).action(request(input,values)));
        assertEquals(text(game.state(input.seat()).getAsJsonObject("prompt"),"id"),text(input.prompt(),"id"));
    }
    private static boolean priority(RulesScenario.Input input) { return text(input.prompt(),"inputType").equals("InputPassPriority"); }
    private static RulesScenario.Input main(RulesScenario game) throws Exception { return game.until(i -> i.seat()==0 && priority(i) && text(i.state(),"phaseKey").equals("MAIN1") && i.state().getAsJsonArray("stack").isEmpty()); }
    private static List<JsonObject> hand(JsonObject state) { return zone(viewer(state),"Hand").getAsJsonArray("cards").asList().stream().map(JsonElement::getAsJsonObject).toList(); }
    private static JsonObject card(JsonObject state,String zoneName,String name) { return zone(viewer(state),zoneName).getAsJsonArray("cards").asList().stream().map(JsonElement::getAsJsonObject).filter(c -> text(c,"name").equals(name)).findFirst().orElse(null); }
    private static long forests(JsonObject state) { return zone(viewer(state),"Battlefield").getAsJsonArray("cards").asList().stream().map(JsonElement::getAsJsonObject).filter(c -> text(c,"name").equals("Forest")).count(); }
    private static List<JsonObject> placements(RulesScenario.Input input) {
        return input.prompt().has("priorityAbilities")?input.prompt().getAsJsonArray("priorityAbilities").asList().stream().map(JsonElement::getAsJsonObject)
                .filter(a -> a.has("eligibleHandKeys")&&!a.getAsJsonArray("eligibleHandKeys").isEmpty()).toList():List.of();
    }
    private static JsonObject placement(RulesScenario.Input input) { return placements(input).stream().filter(a -> text(a,"sourceName").equals("Gaea's Touch")).findFirst().orElseThrow(()->new AssertionError(input.prompt())); }
}
