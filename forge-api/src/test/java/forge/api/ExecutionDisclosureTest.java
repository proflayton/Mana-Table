package forge.api;

import forge.StaticData;
import forge.deck.Deck;
import forge.game.card.Card;
import forge.game.card.CounterEnumType;
import forge.game.event.GameEventCardCounters;
import forge.game.spellability.SpellAbility;
import forge.game.zone.ZoneType;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.List;

import static org.testng.Assert.*;
import static forge.game.ExecutionTracker.Outcome.*;
import static forge.game.ExecutionTracker.Step.*;

/** Projection/retention fixtures deliberately do not claim to play a rules game;
 * ExecutionJourneyTest supplies the actual paid/stacked/resolved journeys. */
public class ExecutionDisclosureTest {
    @Test public void hiddenPreparationAndAncestryAreFilteredBeforePublication() throws Exception {
        try (var table = table()) {
            var owner = table.game.getPlayers().get(0);
            var observer = table.game.getPlayers().get(1);
            var source = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard("Loch Mare"), owner);
            owner.getZone(ZoneType.Hand).add(source, null, null);
            var ability = source.getFirstSpellAbility(); ability.setActivatingPlayer(owner);
            var activity = new MatchActivity(observer.getView());
            var projection = new MatchExecutions(table.game.getExecutions(), observer.getView(), activity);
            table.game.subscribeToEvents(projection);
            var rules = table.game.getExecutions();
            String hidden = rules.prepare(ability);
            rules.step(ability, CHOICE);
            assertTrue(projection.frame().occurrences().isEmpty());
            assertTrue(projection.frame().transitions().isEmpty());
            assertEquals(projection.frame().lastSequence(), 2L, "A global cursor can contain audience-filtered holes");
            var island = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard("Island"), owner);
            owner.getZone(ZoneType.Battlefield).add(island, null, null);
            var mana = island.getManaAbilities().get(0); mana.setActivatingPlayer(owner);
            String child;
            try (var execution = rules.enter(ability)) {
                child = rules.prepare(mana);
                rules.complete(mana, RESOLVED);
            }
            var frame = projection.frame();
            assertEquals(frame.occurrences().size(), 1);
            var visible = frame.occurrences().get(0);
            assertEquals(visible.id(), child);
            assertNull(visible.parentId());
            assertEquals(visible.budgetRootId(), child);
            assertEquals(visible.sourceName(), "Island");
            assertFalse(frame.toString().contains(hidden));
            assertFalse(frame.toString().contains("Loch Mare"));
            assertEquals(frame.transitions().get(0).sequence(), 3L);
        }
    }

    @Test public void retainedActivityDropsExpiredReferencesWhileLiveChildrenKeepTheirExactBudgetRoot() throws Exception {
        try (var table = table()) {
            var owner = table.game.getPlayers().get(0);
            var source = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard("Loch Mare"), owner);
            owner.getZone(ZoneType.Battlefield).add(source, null, null);
            var activity = new MatchActivity(owner.getView());
            var rules = table.game.getExecutions();
            var projection = new MatchExecutions(rules, owner.getView(), activity);
            activity.executionFilter(projection::reference);
            table.game.subscribeToEvents(projection); table.game.subscribeToEvents(activity);
            var old = new SpellAbility.EmptySa(source, owner);
            String expired = rules.prepare(old);
            try (var execution = rules.enter(old)) {
                table.game.fireEvent(new GameEventCardCounters(source, CounterEnumType.P1P1, 0, 1));
            }
            var copiedBeforeEviction = activity.frame();
            assertEquals(copiedBeforeEviction.entries().get(0).executionId(), expired);
            rules.complete(old, RESOLVED);
            var parent = new SpellAbility.EmptySa(source, owner);
            String root = rules.prepare(parent);
            var nested = new SpellAbility.EmptySa(source, owner);
            String child;
            try (var execution = rules.enter(parent)) {
                child = rules.prepare(nested);
                rules.step(nested, STACK);
            }
            rules.complete(parent, RESOLVED);
            for (int i = 0; i < 600; i++) {
                var later = new SpellAbility.EmptySa(source, owner);
                rules.prepare(later); rules.complete(later, RESOLVED);
            }
            var frame = projection.frame();
            assertEquals(frame.transitions().size(), 512);
            assertTrue(frame.firstSequence() > 1);
            assertTrue(frame.occurrences().stream().noneMatch(value -> value.id().equals(expired)));
            assertTrue(frame.occurrences().stream().anyMatch(value -> value.id().equals(root)));
            var live = frame.occurrences().stream().filter(value -> value.id().equals(child)).findFirst().orElseThrow();
            assertEquals(live.budgetRootId(), root);
            assertEquals(live.parentId(), root);
            assertNull(activity.snapshot().get(0).executionId(), "An expired occurrence is not rebound to the newest effect");
            assertNull(activity.snapshot().get(0).executionStep());
            // Deterministically reproduce the publication interleaving: IPC
            // already copied the event, then the rules stream evicted its ID.
            // Sanitize against that exact later frame rather than rereading
            // mutable admission or rebinding the event to a newer execution.
            var retained = frame.occurrences().stream().map(MatchExecutions.Occurrence::id)
                    .collect(java.util.stream.Collectors.toSet());
            var published = MatchActivity.retainedExecutions(copiedBeforeEviction.entries(), retained).get(0);
            assertNull(published.executionId()); assertNull(published.executionStep());
            assertEquals(published.change(), copiedBeforeEviction.entries().get(0).change());
            assertEquals(published.message(), copiedBeforeEviction.entries().get(0).message());
            assertEquals(activity.snapshot().get(0).change().before(), 0);
            assertEquals(activity.snapshot().get(0).change().after(), 1);
            assertTrue(frame.occurrences().size() <= 258, "512 transitions plus the live child and completed budget root remain bounded");
        }
    }

    @Test public void aHeldBoardPinsStackReferencesUntilItsNextRulesOwnedPublication() throws Exception {
        try (var table = table()) {
            var owner = table.game.getPlayers().get(0);
            var source = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard("Loch Mare"), owner);
            owner.getZone(ZoneType.Battlefield).add(source, null, null);
            var rules = table.game.getExecutions();
            var projection = new MatchExecutions(rules, owner.getView(), new MatchActivity(owner.getView()));
            table.game.subscribeToEvents(projection);
            var stacked = new SpellAbility.EmptySa(source, owner);
            String id = rules.prepare(stacked); rules.step(stacked, STACK);
            projection.pinStack(List.of(id));
            rules.complete(stacked, RESOLVED);
            for (int i = 0; i < 300; i++) {
                var later = new SpellAbility.EmptySa(source, owner);
                rules.prepare(later); rules.complete(later, RESOLVED);
            }
            assertNull(rules.occurrence(id), "The terminal rules occurrence has left the retained history");
            assertTrue(projection.frame().occurrences().stream().anyMatch(item -> item.id().equals(id)),
                    "The prior immutable stack board can still resolve its historical reference");
            projection.pinStack(List.of());
            assertFalse(projection.frame().occurrences().stream().anyMatch(item -> item.id().equals(id)),
                    "The next safe board releases the old stack pin");
        }
    }

    private static MatchTable table() throws Exception {
        var resources = Path.of("../forge-gui/res");
        if (StaticData.instance() == null) EngineResources.load(resources);
        var table = new MatchTable("Constructed", List.of(MatchTable.human(new Deck(), "Constructed", "Owner"),
                MatchTable.human(new Deck(), "Constructed", "Observer")), resources, Files.createTempDirectory("execution-disclosure"));
        HeadlessPlatform.activate(table); return table;
    }
}
