package forge.api;

import com.google.gson.Gson;
import forge.game.spellability.SpellAbility;
import forge.game.zone.ZoneType;
import forge.gamemodes.match.input.InputPassPriority;
import forge.gamemodes.match.input.InputPayMana;
import forge.player.PlayerControllerHuman;
import org.testng.annotations.DataProvider;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.util.ArrayList;
import java.util.Collections;
import java.util.IdentityHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Query graphs stay untracked; only accepted invocation materialization changes purpose. */
public class SourceQueryPurposeTest {
    private static final Gson JSON = new Gson();
    @DataProvider public Object[][] sources() { return new Object[][] {
            {"Snuff Out", false}, {"Cryptic Command", false}, {"Manalith", true}, {"Hill Giant", false} }; }

    @Test(dataProvider = "sources", timeOut = 90000)
    public void realAlternativeModeAndPaymentGraphsKeepTheirQueryPurpose(String name, boolean payment) throws Exception {
        try (var game = new RulesScenario("source-query-purpose-" + name.replace(' ', '-'),
                Map.of(0, name.equals("Hill Giant")
                        ? List.of("Henzie \"Toolbox\" Torre", "Mountain", "Island", "Island", "Island", "Island")
                        : List.of("Swamp", "Island", "Island", "Island", "Island"), 1, List.of("Grizzly Bears")),
                name, "Rhys the Redeemed")) {
            var input = game.until(next -> next.seat() == 0 && priority(next) && text(next.state(), "phaseKey").equals("MAIN1"));
            if (payment) {
                var source = zone(viewer(input.state()), "Hand").getAsJsonArray("cards").get(0).getAsJsonObject();
                var catalog = JSON.toJsonTree(game.session(0).sourceActions(request(input, Map.of("key", text(source, "key"))))).getAsJsonObject();
                assertTrue(game.answer(input, Map.of("action", "useSourceAction", "key", text(catalog, "sourceCardKey"),
                        "actionId", text(catalog.getAsJsonArray("actions").get(0).getAsJsonObject(), "id"))));
                game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").startsWith("InputPayMana"));
            }
            HeadlessPlatform.query(game.table, () -> {
                var actor = game.table.game.getRegisteredPlayers().get(0);
                var other = game.table.game.getRegisteredPlayers().get(1);
                var controller = (PlayerControllerHuman)actor.getController();
                var current = controller.getInputQueue().getInput();
                var source = actor.getCardsIn(payment ? ZoneType.Battlefield : ZoneType.Hand).get(0);
                var originals = source.getSpellAbilities().stream().flatMap(ability -> graph(ability).stream()).distinct().toList();
                var originalActors = new IdentityHashMap<SpellAbility, Object>();
                originals.forEach(ability -> originalActors.put(ability, ability.getActivatingPlayer()));
                var before = game.state(0);
                var counter = SpellAbility.class.getDeclaredField("maxId"); counter.setAccessible(true);
                int originalCounter = counter.getInt(null);
                long queryStarted = System.nanoTime();
                var catalog = SourceActions.describe(current, source, "query-purpose-probe", 1, value -> true);
                assertFalse(catalog.actions().isEmpty());
                if (name.equals("Snuff Out")) assertTrue(catalog.actions().size() >= 2, "The real static alternative must be included");
                if (name.equals("Hill Giant")) assertTrue(catalog.actions().stream().anyMatch(action -> action.ability().isBlitz()),
                        "The real stack-only static must generate and preserve Henzie's Blitz route");
                int queryVisits = 0;
                for (var action : catalog.actions()) for (var ability : graph(action.ability())) {
                    queryVisits++;
                    assertTrue(ability.isQueryCopy());
                    assertNull(ability.getView().getTracker(), "Query modes and alternatives cannot register live views");
                }
                if (name.equals("Cryptic Command")) assertTrue(queryVisits >= 5, "The actual additional choice graph is exercised");
                for (var action : catalog.actions()) {
                    assertNotNull(action.ability().getOriginalAbility());
                    assertFalse(action.ability().getOriginalAbility().isQueryCopy(), "Lineage must reach the actual rules original");
                    boolean selected = current instanceof InputPassPriority priority
                            ? priority.selectAbility(action.ability()) : ((InputPayMana)current).selectAbility(action.ability());
                    assertFalse(selected, "An exposed query graph cannot be passed directly to an input's execution API");
                    assertThrows(IllegalArgumentException.class, () -> action.invocation(other));
                }
                assertEquals(counter.getInt(null), originalCounter, "A complete query, including derived alternatives, allocates no live ability IDs");
                long queryNanos = System.nanoTime() - queryStarted;
                assertEquals(game.state(0), before);
                long invocationStarted = System.nanoTime();
                int invocationVisits = 0;
                for (var action : catalog.actions()) {
                    var invocation = action.invocation(actor).ability();
                    assertNotSame(invocation, action.ability());
                    assertNotEquals(invocation.getId(), action.ability().getId());
                    assertSame(invocation.getOriginalAbility(), action.ability().getOriginalAbility());
                    assertEquals(invocation.getMapParams(), action.ability().getMapParams(),
                            "Invocation must retain the chosen route's current cost, announcement and restriction parameters");
                    for (var ability : graph(invocation)) {
                        invocationVisits++;
                        assertFalse(ability.isQueryCopy(), "No child or additional mode can retain query purpose in an invocation");
                        assertSame(ability.getView().getTracker(), actor.getGame().getTracker());
                        assertSame(ability.getActivatingPlayer(), actor);
                    }
                    assertTrue(action.ability().isQueryCopy(), "Materialization cannot change its retained query graph");
                    var derived = action.ability().copyWithNoManaCost(actor);
                    assertTrue(derived.isQueryCopy());
                    assertTrue(derived.hasParam("WithoutManaCost"));
                    assertTrue(derived.copyForInvocation(actor).hasParam("WithoutManaCost"),
                            "A derived route's current WithoutManaCost parameter must survive invocation materialization");
                }
                long invocationNanos = System.nanoTime() - invocationStarted;
                originals.forEach(ability -> assertSame(ability.getActivatingPlayer(), originalActors.get(ability),
                        "Neither query nor invocation preparation can rebind live original/subability context"));
                assertEquals(game.state(0), before);
                Files.writeString(game.evidence.resolve("query-invocation-work.json"), JSON.toJson(Map.of(
                        "source", name, "payment", payment, "queryRoutes", catalog.actions().size(),
                        "queryGraphVisits", queryVisits, "invocationGraphVisits", invocationVisits,
                        "queryNanos", queryNanos, "invocationNanos", invocationNanos,
                        "timingScope", "Local fixture measurements, no latency gate or AI-speed claim")));
                return null;
            });
        }
    }

    private static boolean priority(Input input) { return text(input.prompt(), "inputType").equals("InputPassPriority"); }
    private static List<SpellAbility> graph(SpellAbility root) {
        var result = new ArrayList<SpellAbility>();
        Set<SpellAbility> seen = Collections.newSetFromMap(new IdentityHashMap<>());
        visit(root, result, seen); return result;
    }
    private static void visit(SpellAbility ability, List<SpellAbility> result, Set<SpellAbility> seen) {
        if (!seen.add(ability)) return;
        result.add(ability);
        if (ability.getSubAbility() != null) visit(ability.getSubAbility(), result, seen);
        for (var child : ability.getAdditionalAbilities().values()) visit(child, result, seen);
        for (var children : ability.getAdditionalAbilityLists().values())
            for (var child : children) visit(child, result, seen);
    }
}
