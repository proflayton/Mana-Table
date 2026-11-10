package forge.api;

import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import forge.StaticData;
import forge.deck.Deck;
import forge.game.ability.AbilityFactory;
import forge.game.ability.ApiType;
import forge.game.card.Card;
import forge.game.card.CardView;
import forge.game.keyword.Keyword;
import forge.game.player.Player;
import forge.game.spellability.SpellAbility;
import forge.game.zone.ZoneType;
import forge.gamemodes.match.input.InputSelectEntitiesFromList;
import forge.gamemodes.match.input.InputSelectTargets;
import forge.player.PlayerControllerHuman;
import forge.util.collect.FCollection;
import org.testng.annotations.Test;

import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Negative target feedback comes from the same live Forge eligibility as selection. */
public class TargetRestrictionProjectionTest {
    private static final Gson JSON = new GsonBuilder().serializeNulls().create();

    @Test(timeOut = 30000)
    public void staffReportsTheActualGreavesGrantAndRefreshesWhenItMovesOrLeaves() throws Exception {
        try (var f = new Fixture("staff-greaves")) {
            HeadlessPlatform.query(f.table, () -> {
                var greaves = f.add("Lightning Greaves", f.actor());
                greaves.attachToEntity(f.first, null);
                f.refresh();
                assertTrue(f.first.hasKeyword(Keyword.SHROUD));
                assertFalse(f.first.canBeTargetedBy(f.ability));
                var input = f.install();
                var problem = input.cardSelectionProblem(f.first);
                assertEquals(problem.reason(), InputSelectTargets.CardTargetReason.SHROUD);
                assertSame(problem.grantingSource(), greaves, "The grant origin is the equipment, not the recipient's copied static");
                var before = f.observation();
                var row = restriction(before, f.id(f.first));
                assertEquals(text(row, "reason"), "shroud");
                assertEquals(text(row, "sourceCardId"), f.id(greaves));
                assertFalse(text(row, "detail").isBlank());
                assertFalse(card(before, f.id(f.first)).get("selectable").getAsBoolean());
                assertTrue(card(before, f.id(f.second)).get("selectable").getAsBoolean());
                var published = (List<?>)((Map<?, ?>)((Map<?, ?>)f.session().automatedObservation().get("state")).get("prompt")).get("targetRestrictions");
                assertThrows(UnsupportedOperationException.class, published::clear);
                assertThrows(UnsupportedOperationException.class, ((Map<?, ?>)published.get(0))::clear);
                for (var other : f.table.automatedSeats()) if (other != f.session()) {
                    var otherState = JSON.toJsonTree(other.automatedObservation().get("state")).getAsJsonObject();
                    assertTrue(otherState.get("prompt") == null || otherState.get("prompt").isJsonNull()
                            || !otherState.getAsJsonObject("prompt").has("targetRestrictions"));
                }
                var spectator = JSON.toJsonTree(f.table.spectatorState()).getAsJsonObject();
                assertTrue(spectator.get("prompt") == null || spectator.get("prompt").isJsonNull());
                assertFalse(input.selectCard(f.first, null, null));
                assertEquals(f.ability.getTargets().size(), 0, "An explanation must not dispatch an illegal target");

                greaves.attachToEntity(f.second, null); f.refresh(); f.republish(input);
                var moved = f.observation();
                assertEquals(prompt(moved).get("id"), prompt(before).get("id"), "The same current input refreshes its blocked facts");
                assertFalse(hasRestriction(moved, f.id(f.first)));
                assertEquals(text(restriction(moved, f.id(f.second)), "sourceCardId"), f.id(greaves));
                assertEquals(text(restriction(before, f.id(f.first)), "sourceCardId"), f.id(greaves), "Earlier facts stay detached");
                greaves.unattachFromEntity(f.second);
                f.actor().getZone(ZoneType.Battlefield).remove(greaves);
                f.actor().getZone(ZoneType.Graveyard).add(greaves);
                f.refresh(); f.republish(input);
                assertFalse(hasRestriction(f.observation(), f.id(f.second)));
                assertTrue(f.first.canBeTargetedBy(f.ability));
                assertTrue(f.second.canBeTargetedBy(f.ability));
                return null;
            });
        }
    }

    @Test(timeOut = 30000)
    public void ownershipAndActualIgnorePermissionsOverrideKeywordPresence() throws Exception {
        try (var f = new Fixture("exceptions")) {
            HeadlessPlatform.query(f.table, () -> {
                var ownBoots = f.add("Swiftfoot Boots", f.actor());
                var enemyBoots = f.add("Swiftfoot Boots", f.player(1));
                ownBoots.attachToEntity(f.first, null); enemyBoots.attachToEntity(f.enemy, null); f.refresh();
                var input = f.install();
                assertNull(input.cardSelectionProblem(f.first), "Own hexproof permits the Staff's own controller");
                assertEquals(input.cardSelectionProblem(f.enemy).reason(), InputSelectTargets.CardTargetReason.HEXPROOF);
                assertSame(input.cardSelectionProblem(f.enemy).grantingSource(), enemyBoots);
                var permission = f.add("Forest", f.actor());
                permission.addStaticAbility("Mode$ IgnoreHexproof | Activator$ You | ValidEntity$ Creature");
                f.refresh();
                assertTrue(f.enemy.hasKeyword(Keyword.HEXPROOF));
                assertNull(f.install().cardSelectionProblem(f.enemy), "Actual ignore permission wins over the visible keyword");

                var greaves = f.add("Lightning Greaves", f.actor());
                greaves.attachToEntity(f.second, null); f.refresh();
                assertEquals(f.install().cardSelectionProblem(f.second).reason(), InputSelectTargets.CardTargetReason.SHROUD);
                permission.addStaticAbility("Mode$ IgnoreShroud | Activator$ You | ValidEntity$ Creature");
                f.refresh();
                assertTrue(f.second.hasKeyword(Keyword.SHROUD));
                assertNull(f.install().cardSelectionProblem(f.second));
                assertFalse(hasRestriction(f.observation(), f.id(f.enemy)));
                assertFalse(hasRestriction(f.observation(), f.id(f.second)));
                return null;
            });
        }
    }

    @Test(timeOut = 30000)
    public void sourceVisibilityAndCurrentMembershipNeverExposeHiddenFacesOrGrantActionHandles() throws Exception {
        try (var f = new Fixture("visibility")) {
            HeadlessPlatform.query(f.table, () -> {
                var hidden = f.add("Forest", f.player(1));
                hidden.turnFaceDown(true);
                hidden.addStaticAbility("Mode$ CantTarget | ValidTarget$ Creature");
                f.refresh();
                var input = f.install();
                var rows = TargetRestrictionProjection.describe(input, f.table.game.getCardsIn(ZoneType.Battlefield),
                        f.actor(), view -> false, f::id);
                var own = rows.stream().filter(row -> row.get("cardId").equals(f.id(f.first))).findFirst().orElseThrow();
                assertEquals(own.get("reason"), "restriction");
                assertFalse(own.containsKey("sourceCardId"), "A concealed grant origin is omitted");
                assertFalse(rows.stream().anyMatch(row -> row.get("cardId").equals(f.id(hidden))));
                assertTrue(rows.stream().allMatch(row -> !row.containsKey("key") && !row.containsKey("actionId")));
                assertTrue(TargetRestrictionProjection.describe(input, List.of(f.first), f.actor(), view -> true, f::id).isEmpty(),
                        "Published selectable cards never receive a contradictory rejection");
                f.actor().getZone(ZoneType.Battlefield).remove(f.first);
                f.actor().getZone(ZoneType.Hand).add(f.first);
                assertTrue(TargetRestrictionProjection.describe(input, List.of(f.first), f.actor(), view -> false, f::id).isEmpty());
                input.stop();
                assertTrue(TargetRestrictionProjection.describe(input, List.of(f.second), f.actor(), view -> false, f::id).isEmpty());
                return null;
            });
        }
    }

    @Test(timeOut = 30000)
    public void protectionAndGenericRestrictionsDoNotBecomeGuessedShroudOrChangeNullSourceSemantics() throws Exception {
        try (var f = new Fixture("other-restrictions")) {
            HeadlessPlatform.query(f.table, () -> {
                f.first.addChangedCardKeywords(List.of("Protection:Artifact"), List.of(), false, f.table.game.getNextTimestamp(), null);
                var input = f.install();
                assertEquals(input.cardSelectionProblem(f.first).reason(), InputSelectTargets.CardTargetReason.PROTECTION);
                assertNull(input.cardSelectionProblem(f.first).grantingSource(), "Intrinsic protection has no invented external source");
                var forest = f.add("Forest", f.actor());
                assertEquals(input.cardSelectionProblem(forest).reason(), InputSelectTargets.CardTargetReason.RESTRICTION);
                f.second.setPhasedOut(f.actor());
                assertFalse(f.second.canBeTargetedBy(f.ability));
                assertTrue(f.second.canBeTargetedBy(null), "The previous null-source compatibility gate remains intact");
                assertNull(f.second.targetingProblem(null));
                assertEquals(input.cardSelectionProblem(f.second).reason(), InputSelectTargets.CardTargetReason.RESTRICTION);
                return null;
            });
        }
    }

    @Test(timeOut = 30000)
    public void selectedDeselectableCardsAndNonTargetCostSelectionsHaveNoRestrictionRows() throws Exception {
        try (var f = new Fixture("selection-boundaries")) {
            HeadlessPlatform.query(f.table, () -> {
                f.ability.getTargets().add(f.first);
                var greaves = f.add("Lightning Greaves", f.actor());
                greaves.attachToEntity(f.first, null); f.refresh();
                var input = f.install();
                assertFalse(f.first.canBeTargetedBy(f.ability));
                assertNull(input.cardSelectionProblem(f.first), "An already selected now-protected card can still be deselected");
                assertFalse(input.canAddTarget(f.first));
                assertFalse(hasRestriction(f.observation(), f.id(f.first)));
                input.selectCard(f.first, null, null);
                assertEquals(f.ability.getTargets().size(), 0);
                assertEquals(input.cardSelectionProblem(f.first).reason(), InputSelectTargets.CardTargetReason.SHROUD);
                var costAbility = AbilityFactory.getAbility("AB$ Draw | Cost$ Sac<1/Creature> | NumCards$ 1", f.staff);
                costAbility.setActivatingPlayer(f.actor());
                var cost = new InputSelectEntitiesFromList<>(f.controller(), 1, 1, new FCollection<>(List.of(f.first, f.second)), costAbility);
                f.controller().getInputQueue().setInput(cost);
                cost.showMessage();
                f.session().publishInput(); f.table.publish();
                var observation = f.observation();
                assertFalse(observation.getAsJsonObject("state").getAsJsonObject("prompt").has("targetRestrictions"));
                assertEquals(text(observation.getAsJsonObject("state").getAsJsonObject("prompt"), "inputType"), "InputSelectEntitiesFromList");
                assertNotNull(cost.getActivateAction(f.first), "Shroud does not prevent selecting a sacrifice cost");
                return null;
            });
        }
    }

    private static JsonObject prompt(JsonObject observation) { return observation.getAsJsonObject("state").getAsJsonObject("prompt"); }
    private static boolean hasRestriction(JsonObject observation, String id) {
        return prompt(observation).getAsJsonArray("targetRestrictions").asList().stream().anyMatch(value -> text(value.getAsJsonObject(), "cardId").equals(id));
    }
    private static JsonObject restriction(JsonObject observation, String id) {
        return prompt(observation).getAsJsonArray("targetRestrictions").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "cardId").equals(id)).findFirst().orElseThrow();
    }
    private static JsonObject card(JsonObject observation, String id) {
        return observation.getAsJsonObject("state").getAsJsonArray("players").asList().stream().map(value -> value.getAsJsonObject())
                .flatMap(player -> player.getAsJsonArray("zones").asList().stream()).map(value -> value.getAsJsonObject())
                .flatMap(zone -> zone.getAsJsonArray("cards").asList().stream()).map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "visualId").equals(id)).findFirst().orElseThrow();
    }

    private static final class Fixture implements AutoCloseable {
        final MatchTable table; final Card staff, first, second, enemy; final SpellAbility ability;
        Fixture(String name) throws Exception {
            var resources = Path.of("../forge-gui/res");
            if (StaticData.instance() == null) EngineResources.load(resources);
            var players = java.util.stream.IntStream.range(0, 4)
                    .mapToObj(seat -> MatchTable.automated(new Deck(), "Constructed", "Policy " + seat)).toList();
            table = new MatchTable("Constructed", players, resources, ApiTestWorkspace.evidence("target-restrictions-" + name), true);
            HeadlessPlatform.activate(table);
            staff = add("Staff of Domination", actor()); first = add("Omnath, Locus of the Void", actor());
            second = add("Grizzly Bears", actor()); enemy = add("Grizzly Bears", player(1));
            ability = staff.getSpellAbilities().stream().filter(sa -> sa.getApi() == ApiType.Untap && sa.usesTargeting()).findFirst().orElseThrow();
            ability.setActivatingPlayer(actor());
        }
        Player actor() { return player(0); }
        Player player(int seat) { return table.game.getRegisteredPlayers().get(seat); }
        PlayerControllerHuman controller() { return (PlayerControllerHuman) actor().getController(); }
        MatchSession session() { return table.automatedSeat(actor().getId()); }
        Card add(String name, Player owner) {
            var card = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard(name), owner);
            owner.getZone(ZoneType.Battlefield).add(card); return card;
        }
        void refresh() { table.game.getAction().checkStaticAbilities(false); }
        InputSelectTargets install() {
            var choices = new ArrayList<Card>();
            for (var card : table.game.getCardsIn(ZoneType.Battlefield)) if (card.isCreature()) choices.add(card);
            var input = new InputSelectTargets(controller(), choices, ability, false, null, null, null, false);
            controller().getInputQueue().setInput(input); republish(input); return input;
        }
        void republish(InputSelectTargets input) { input.showMessage(); session().publishInput(); table.publish(); }
        String id(Card card) { return id(card.getView()); }
        String id(CardView card) { return table.identities.id(card); }
        JsonObject observation() { return JSON.toJsonTree(session().automatedObservation()).getAsJsonObject(); }
        @Override public void close() { table.close(); }
    }
}
