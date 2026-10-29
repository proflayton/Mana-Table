package forge.api;

import com.google.common.eventbus.Subscribe;
import forge.StaticData;
import forge.deck.Deck;
import forge.game.card.Card;
import forge.game.combat.BlockRemovalRepair;
import forge.game.combat.Combat;
import forge.game.combat.CombatInputState;
import forge.game.combat.CombatUtil;
import forge.game.event.GameEvent;
import forge.game.player.Player;
import forge.game.zone.ZoneType;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

public class BlockRemovalRepairTest {
    @Test public void oneBuildBlockDomainMatchesLiveRulesAcrossConstraintsAndFreshMutations() throws Exception {
        try (var fixture = new Fixture()) {
            var ordinary = fixture.attacker("Grizzly Bears", 1);
            var protection = fixture.attacker("White Knight", 1);
            var flying = fixture.attacker("Wind Drake", 1);
            var menace = fixture.attacker("Ripscale Predator", 1);
            var companion = fixture.add("Grizzly Bears", 0);
            fixture.combat.addAttacker(companion, fixture.player(1), fixture.combat.getBandOfAttacker(menace));
            var foreignAttack = fixture.attacker("Grizzly Bears", 2);
            fixture.blocker("Grizzly Bears", 2, foreignAttack);
            var ground = fixture.add("Grizzly Bears", 1);
            var black = fixture.add("Black Knight", 1);
            var air = fixture.add("Wind Drake", 1);
            fixture.add("Mogg Flunkies", 1);
            fixture.add("Qal Sisma Behemoth", 1);
            var tapped = fixture.add("Grizzly Bears", 1); tapped.setTapped(true);
            var additional = fixture.blocker("Grizzly Bears", 1, ordinary);
            additional.addCanBlockAdditional(1, 700L);
            var shared = fixture.blocker("Grizzly Bears", 1, menace);

            var domain = assertLiveBlockDomain(fixture);
            assertFalse(domain.blockOptions().get(flying.getId()).contains(ground.getId()));
            assertFalse(domain.blockOptions().get(protection.getId()).contains(black.getId()));
            assertFalse(domain.blockOptions().get(ordinary.getId()).contains(tapped.getId()));
            assertTrue(domain.blockOptions().get(flying.getId()).contains(air.getId()));
            assertTrue(domain.blockOptions().get(protection.getId()).contains(additional.getId()));
            assertTrue(domain.blockOptions().get(companion.getId()).contains(shared.getId()), "Existing band pairs remain removable");
            assertTrue(domain.blockOptions().get(foreignAttack.getId()).isEmpty(), "An owner cannot block an attack on a different seat");

            // Every subsequent builder gets fresh facts: none of its invariant
            // reads may survive tap, assignment-capacity, or rules changes.
            tapped.setTapped(false); ground.setTapped(true);
            additional.removeCanBlockAdditional(700L);
            domain = assertLiveBlockDomain(fixture);
            assertTrue(domain.blockOptions().get(ordinary.getId()).contains(tapped.getId()));
            assertFalse(domain.blockOptions().get(protection.getId()).contains(additional.getId()));
            assertFalse(domain.blockOptions().get(ordinary.getId()).contains(ground.getId()));

            var lure = fixture.attacker("Taunting Elf", 1);
            air.addMustBlockCard(701L, ordinary);
            domain = assertLiveBlockDomain(fixture);
            assertTrue(domain.blockOptions().get(lure.getId()).contains(tapped.getId()));
            assertFalse(domain.blockOptions().get(ordinary.getId()).contains(tapped.getId()));
            var tax = fixture.attacker("Archangel of Tithes", 1);
            assertNotNull(CombatUtil.getBlockCost(fixture.table.game, tapped, lure));
            domain = assertLiveBlockDomain(fixture);
            assertTrue(domain.blockOptions().get(ordinary.getId()).contains(tapped.getId()), "An optional global block tax does not compel paying for lure");
            tax.setTapped(true);
            assertLiveBlockDomain(fixture);
        }
    }

    private static CombatInputState assertLiveBlockDomain(Fixture fixture) {
        var events = new EventCounter(); fixture.table.game.subscribeToEvents(events);
        try {
            var view = fixture.table.game.getView().getCombat();
            for (int repeat = 0; repeat < 2; repeat++) {
                var expected = new LinkedHashMap<Integer, List<Integer>>();
                for (Card attacker : fixture.combat.getAttackers()) {
                    var blockers = new ArrayList<Integer>();
                    for (Card blocker : fixture.player(1).getCreaturesInPlay()) {
                        if (fixture.combat.isBlocking(blocker, attacker) || CombatUtil.canBlock(attacker, blocker, fixture.combat)) {
                            blockers.add(blocker.getId());
                        }
                    }
                    expected.put(attacker.getId(), List.copyOf(blockers));
                }
                var domain = CombatInputState.blockers(fixture.combat, fixture.player(1));
                assertEquals(domain.blockOptions(), expected, "The live predicate is the domain oracle, including assigned-pair removals");
                assertEquals(domain.blockerCandidates(), fixture.player(1).getCreaturesInPlay().stream().map(Card::getId).toList());
                assertEquals(domain.blockProblem(), CombatUtil.validateBlocks(fixture.combat, fixture.player(1)));
                assertSame(fixture.table.game.getView().getCombat(), view);
            }
            assertEquals(events.count, 0, "A domain query must not mutate cards/combat or publish rules events");
            return CombatInputState.blockers(fixture.combat, fixture.player(1));
        } finally { fixture.table.game.unsubscribeFromEvents(events); }
    }

    @Test public void outstandingLureStillRespectsOptionalBlockingCostsAndSatisfiedRequirements() throws Exception {
        try (var fixture = new Fixture()) {
            var lure = fixture.attacker("Taunting Elf", 1);
            var ordinary = fixture.attacker("Grizzly Bears", 1);
            var free = fixture.add("Grizzly Bears", 1);
            var taxed = fixture.add("Qal Sisma Behemoth", 1);
            assertTrue(CombatUtil.mustBlockAnAttacker(free, fixture.combat, null));
            assertFalse(CombatUtil.canBlock(ordinary, free, fixture.combat));
            assertNotNull(CombatUtil.getBlockCost(fixture.table.game, taxed, lure));
            assertFalse(CombatUtil.mustBlockAnAttacker(taxed, fixture.combat, null),
                    "A player is not compelled to pay a blocking cost to satisfy lure");
            assertTrue(CombatUtil.canBlock(ordinary, taxed, fixture.combat),
                    "An outstanding lure does not remove an optional taxed alternative");
        }
        try (var fixture = new Fixture()) {
            var requirement = fixture.attacker("Grizzly Bears", 1);
            requirement.addIntrinsicKeyword("CARDNAME must be blocked if able.");
            var ordinary = fixture.attacker("Grizzly Bears", 1);
            fixture.blocker("Grizzly Bears", 1, requirement);
            var free = fixture.add("Grizzly Bears", 1);
            assertFalse(CombatUtil.mustBlockAnAttacker(free, fixture.combat, null));
            assertTrue(CombatUtil.canBlock(ordinary, free, fixture.combat), "An already satisfied one-block requirement imposes no new lure assignment");
        }
    }
    @Test public void minimalWitnessPreservesValidOwnAndForeignBlocksWithoutChangingLiveState() throws Exception {
        try (var fixture = new Fixture()) {
            var menace = fixture.attacker("Ripscale Predator", 1);
            var ordinary = fixture.attacker("Grizzly Bears", 1);
            var foreignAttack = fixture.attacker("Grizzly Bears", 2);
            var first = fixture.blocker("Grizzly Bears", 1, menace);
            var second = fixture.blocker("Grizzly Bears", 1, ordinary);
            var foreign = fixture.blocker("Grizzly Bears", 2, foreignAttack);
            var view = fixture.table.game.getView().getCombat();
            var events = new EventCounter(); fixture.table.game.subscribeToEvents(events);
            String problem = CombatUtil.validateBlocks(fixture.combat, fixture.player(1));
            assertNotNull(problem);
            for (int repeat = 0; repeat < 4; repeat++) {
                var result = BlockRemovalRepair.find(fixture.combat, fixture.player(1), 16);
                assertEquals(result.assignments(), List.of(new BlockRemovalRepair.Assignment(ordinary, second)));
                assertEquals(result.candidatesTried(), 1);
                assertSame(fixture.table.game.getCombat(), fixture.combat);
                assertSame(fixture.table.game.getView().getCombat(), view, "Even a redundant live view refresh is forbidden");
                assertEquals(fixture.combat.getBlockers(menace), List.of(first));
                assertEquals(fixture.combat.getBlockers(ordinary), List.of(second));
                assertEquals(fixture.combat.getBlockers(foreignAttack), List.of(foreign));
                assertTrue(first.getView().isBlocking()); assertTrue(second.getView().isBlocking());
                assertTrue(foreign.getView().isBlocking());
                assertEquals(CombatUtil.validateBlocks(fixture.combat, fixture.player(1)), problem);
            }
            assertEquals(events.count, 0, "A repair projection must not publish rules events");
        }
    }

    @Test public void subsetSearchAndClearFallbackRespectTheExactCandidateBudget() throws Exception {
        try (var fixture = new Fixture()) {
            var first = fixture.attacker("Ripscale Predator", 1);
            var second = fixture.attacker("Ripscale Predator", 1);
            var ordinary = fixture.attacker("Grizzly Bears", 1);
            fixture.blocker("Grizzly Bears", 1, first);
            fixture.blocker("Grizzly Bears", 1, second);
            var retained = fixture.blocker("Grizzly Bears", 1, ordinary);
            var exhausted = BlockRemovalRepair.find(fixture.combat, fixture.player(1), 0);
            assertNull(exhausted.assignments()); assertEquals(exhausted.candidatesTried(), 0);
            for (int cap = 1; cap <= 3; cap++) {
                var bounded = BlockRemovalRepair.find(fixture.combat, fixture.player(1), cap);
                assertEquals(bounded.assignments(), List.of(), "The reserved clear trial is a certified fallback, not an inference");
                assertEquals(bounded.candidatesTried(), cap);
            }
            var full = BlockRemovalRepair.find(fixture.combat, fixture.player(1), 16);
            assertEquals(full.assignments(), List.of(new BlockRemovalRepair.Assignment(ordinary, retained)));
            assertEquals(full.candidatesTried(), 4, "Two removals repair both joint restrictions while preserving the ordinary block");
            expectThrows(IllegalArgumentException.class, () -> BlockRemovalRepair.find(fixture.combat, fixture.player(1), -1));
        }
    }

    @Test public void blockerAloneAndBandSharedAssignmentsUseTheRulesValidator() throws Exception {
        try (var fixture = new Fixture()) {
            var attacker = fixture.attacker("Grizzly Bears", 1);
            fixture.add("Grizzly Bears", 1);
            fixture.blocker("Mogg Flunkies", 1, attacker);
            assertNotNull(CombatUtil.validateBlocks(fixture.combat, fixture.player(1)));
            assertEquals(BlockRemovalRepair.find(fixture.combat, fixture.player(1), 8).assignments(), List.of());
        }
        try (var fixture = new Fixture()) {
            var menace = fixture.attacker("Ripscale Predator", 1);
            var companion = fixture.add("Grizzly Bears", 0);
            fixture.combat.addAttacker(companion, fixture.player(1), fixture.combat.getBandOfAttacker(menace));
            var blocker = fixture.blocker("Grizzly Bears", 1, menace);
            assertEquals(fixture.combat.getBlockers(companion), List.of(blocker));
            var result = BlockRemovalRepair.find(fixture.combat, fixture.player(1), 8);
            assertEquals(result.assignments(), List.of(), "One band toggle removes both projected pair representations");
            assertEquals(result.candidatesTried(), 1);
            assertEquals(fixture.combat.getBlockers(companion), List.of(blocker));
        }
    }

    @Test public void forcedBlocksAndForeignInvalidAssignmentsCannotBeSilentlyCleared() throws Exception {
        try (var fixture = new Fixture()) {
            var lure = fixture.attacker("Taunting Elf", 1);
            fixture.blocker("Mogg Flunkies", 1, lure);
            fixture.add("Grizzly Bears", 1);
            assertNotNull(CombatUtil.validateBlocks(fixture.combat, fixture.player(1)));
            assertNull(BlockRemovalRepair.find(fixture.combat, fixture.player(1), 8).assignments(),
                    "Removing own blocks violates the actual compulsory-block requirement");
        }
        try (var fixture = new Fixture()) {
            var ownAttack = fixture.attacker("Ripscale Predator", 1);
            var foreignAttack = fixture.attacker("Ripscale Predator", 2);
            fixture.blocker("Grizzly Bears", 1, ownAttack);
            var foreign = fixture.blocker("Grizzly Bears", 2, foreignAttack);
            assertNull(BlockRemovalRepair.find(fixture.combat, fixture.player(1), 8).assignments(),
                    "The witness cannot repair another seat's invalid assignment by deleting it");
            assertEquals(fixture.combat.getBlockers(foreignAttack), List.of(foreign));
        }
        try (var fixture = new Fixture()) {
            var ordinary = fixture.attacker("Grizzly Bears", 1);
            fixture.blocker("Grizzly Bears", 1, ordinary);
            var valid = BlockRemovalRepair.find(fixture.combat, fixture.player(1), 8);
            assertNull(valid.assignments()); assertEquals(valid.candidatesTried(), 0);
        }
    }

    @Test(timeOut = 90000) public void actualBlockInputPublishesAndExecutesOnlyCurrentRemovalAuthority() throws Exception {
        try (var scenario = new RulesScenario("block-removal-repair", Map.of(
                0, List.of("Ripscale Predator", "Reliquary Tower"), 1, List.of("Grizzly Bears", "Reliquary Tower"),
                2, List.of("Reliquary Tower"), 3, List.of("Reliquary Tower")))) {
            var attacking = scenario.until(input -> input.seat() == 0 && text(input.prompt(), "inputType").equals("InputAttack"));
            var attacker = zone(viewer(attacking.state()), "Battlefield").getAsJsonArray("cards").get(0).getAsJsonObject();
            int defender = scenario.state(1).get("viewerId").getAsInt();
            assertTrue(scenario.answer(attacking, Map.of("action", "attack", "attackerKey", text(attacker, "key"), "defenderPlayerId", defender)));
            var blocking = scenario.until(input -> input.seat() == 1 && text(input.prompt(), "inputType").equals("InputBlock"));
            assertTrue(blocking.state().getAsJsonObject("combat").get("blockRepair").isJsonNull());
            var blocker = zone(viewer(blocking.state()), "Battlefield").getAsJsonArray("cards").get(0).getAsJsonObject();
            var currentAttacker = zone(player(blocking.state(), attacking.state().get("viewerId").getAsInt()), "Battlefield")
                    .getAsJsonArray("cards").get(0).getAsJsonObject();
            assertTrue(scenario.answer(blocking, Map.of("action", "block", "attackerKey", text(currentAttacker, "key"), "blockerKey", text(blocker, "key"))));
            var invalid = scenario.until(input -> input.seat() == 1 && text(input.prompt(), "inputType").equals("InputBlock")
                    && !text(input.state().getAsJsonObject("combat"), "blockProblem").isEmpty());
            var combat = invalid.state().getAsJsonObject("combat");
            assertTrue(combat.getAsJsonArray("blockRepair").isEmpty());
            var offered = combat.getAsJsonArray("attackers").get(0).getAsJsonObject();
            assertEquals(offered.getAsJsonArray("eligibleBlockerIds"), offered.getAsJsonArray("blockerIds"),
                    "The already-assigned pair remains explicitly offered for removal");
            var invalidAttacker = zone(player(invalid.state(), attacking.state().get("viewerId").getAsInt()), "Battlefield")
                    .getAsJsonArray("cards").get(0).getAsJsonObject();
            var invalidBlocker = zone(viewer(invalid.state()), "Battlefield").getAsJsonArray("cards").get(0).getAsJsonObject();
            assertTrue(scenario.answer(invalid, Map.of("action", "block", "attackerKey", text(invalidAttacker, "key"), "blockerKey", text(invalidBlocker, "key"))));
            var repaired = scenario.until(input -> input.seat() == 1 && text(input.prompt(), "inputType").equals("InputBlock"));
            assertTrue(repaired.state().getAsJsonObject("combat").get("blockProblem").isJsonNull());
            assertTrue(repaired.state().getAsJsonObject("combat").get("blockRepair").isJsonNull());
            assertTrue(scenario.answer(repaired, Map.of("action", "ok")));
            scenario.until(input -> text(input.state(), "phaseKey").equals("MAIN2"));
        }
    }

    public static final class EventCounter {
        int count;
        @Subscribe public void event(GameEvent event) { count++; }
    }

    private static final class Fixture implements AutoCloseable {
        final MatchTable table;
        final Combat combat;
        Fixture() throws Exception {
            var resources = Path.of("../forge-gui/res");
            if (StaticData.instance() == null) EngineResources.load(resources);
            table = new MatchTable("Constructed", List.of(MatchTable.human(new Deck(), "Constructed", "Attacker"),
                    MatchTable.human(new Deck(), "Constructed", "Defender"), MatchTable.human(new Deck(), "Constructed", "Other defender")),
                    resources, Files.createTempDirectory("block-removal-repair"));
            HeadlessPlatform.activate(table);
            combat = new Combat(player(0));
            table.game.getPhaseHandler().setCombat(combat);
        }
        Player player(int seat) { return table.game.getRegisteredPlayers().get(seat); }
        Card add(String name, int seat) {
            Card card = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard(name), player(seat));
            card.getOwner().getZone(ZoneType.Battlefield).add(card); card.setSickness(false);
            return card;
        }
        Card attacker(String name, int defender) {
            Card card = add(name, 0); combat.addAttacker(card, player(defender)); return card;
        }
        Card blocker(String name, int seat, Card attacker) {
            Card card = add(name, seat); combat.addBlocker(attacker, card); return card;
        }
        @Override public void close() { table.close(); }
    }
}
