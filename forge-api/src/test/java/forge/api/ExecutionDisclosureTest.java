package forge.api;

import forge.StaticData;
import forge.deck.Deck;
import forge.game.card.Card;
import forge.game.card.CounterEnumType;
import forge.game.event.GameEventCardCounters;
import forge.game.event.GameEventTurnBegan;
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
            rules.complete(null, RESOLVED);
            rules.recordOutcome(null, DECLINED);
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
            var stablePublication = MatchActivity.capture(activity::frame, projection::frame);
            assertNull(stablePublication.activity().entries().get(0).executionId(), "An expired occurrence is not rebound to the newest effect");
            assertNull(stablePublication.activity().entries().get(0).executionStep());
            assertEquals(activity.snapshot().get(0).executionId(), expired,
                    "The internal event-time copy never strips a reference using an unrelated live admission frame");
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

    @Test public void sourceLessStateBasedAdmissionRequiresAnActualPublicCommittedResult() throws Exception {
        try (var table = table()) {
            var owner = table.game.getPlayers().get(0); var observer = table.game.getPlayers().get(1);
            var rules = table.game.getExecutions(); var activity = new MatchActivity(observer.getView());
            var projection = new MatchExecutions(rules, observer.getView(), activity);
            activity.executionFilter(projection::reference);
            table.game.subscribeToEvents(projection); table.game.subscribeToEvents(activity);
            table.game.fireEvent(new GameEventTurnBegan(owner.getView(), 1));
            try (var empty = rules.enterStateBased()) { empty.complete(RESOLVED, null); }
            assertTrue(projection.frame().occurrences().isEmpty(), "An empty state check has no audience action");
            var hidden = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard("Loch Mare"), owner);
            owner.getZone(ZoneType.Hand).add(hidden, null, null);
            String privateId;
            try (var privateRule = rules.enterStateBased()) {
                privateId = privateRule.id();
                table.game.fireEvent(new GameEventCardCounters(hidden, CounterEnumType.P1P1, 0, 1));
                privateRule.complete(RESOLVED, null);
            }
            assertTrue(projection.frame().occurrences().isEmpty(), "A private-only mutation cannot reveal a source-less rules execution");
            assertFalse(activity.snapshot().toString().contains(privateId));
            var target = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard("Grizzly Bears"), owner);
            owner.getZone(ZoneType.Battlefield).add(target, null, null);
            String publicId;
            try (var publicRule = rules.enterStateBased()) {
                publicId = publicRule.id();
                table.game.fireEvent(new GameEventCardCounters(target, CounterEnumType.P1P1, 0, 1));
                var committed = activity.snapshot().get(activity.snapshot().size() - 1);
                assertEquals(committed.executionId(), publicId); assertEquals(committed.executionStep(), "resolution");
                publicRule.complete(RESOLVED, null);
            }
            var admitted = projection.frame().occurrences().stream().filter(item -> item.id().equals(publicId)).findFirst().orElseThrow();
            assertEquals(admitted.kind(), "rules"); assertEquals(admitted.ruleKind(), "stateBased");
            assertEquals(admitted.step(), "complete"); assertEquals(admitted.outcome(), "resolved");
            assertNull(admitted.actorPlayerId()); assertNull(admitted.sourceCardId()); assertNull(admitted.sourceName());
            assertNull(admitted.ruleResult());
            assertFalse(projection.frame().toString().contains(privateId));

            var ownActivity = new MatchActivity(owner.getView());
            var ownProjection = new MatchExecutions(rules, owner.getView(), ownActivity);
            ownActivity.executionFilter(ownProjection::reference);
            table.game.subscribeToEvents(ownProjection); table.game.subscribeToEvents(ownActivity);
            String choosingId;
            try (var choosing = rules.enterStateBased()) {
                choosingId = choosing.id();
                var actualOwner = rules.current();
                assertEquals(ownProjection.referenceDecision(actualOwner), actualOwner);
                assertTrue(ownProjection.frame().occurrences().stream().anyMatch(item -> item.id().equals(choosingId)));
                assertFalse(projection.frame().occurrences().stream().anyMatch(item -> item.id().equals(choosingId)),
                        "The owner of a required rules choice does not reveal that private input to another audience");
                choosing.complete(RESOLVED, null);
            }
            var hiddenParent = new SpellAbility.EmptySa(hidden, owner);
            String parentId = rules.prepare(hiddenParent), nestedId;
            try (var parent = rules.enter(hiddenParent); var nested = rules.enterStateBased()) {
                nestedId = nested.id();
                table.game.fireEvent(new GameEventCardCounters(target, CounterEnumType.P1P1, 1, 2));
                nested.complete(RESOLVED, null);
            }
            rules.complete(hiddenParent, RESOLVED);
            var ownNested = ownProjection.frame().occurrences().stream().filter(item -> item.id().equals(nestedId)).findFirst().orElseThrow();
            var otherNested = projection.frame().occurrences().stream().filter(item -> item.id().equals(nestedId)).findFirst().orElseThrow();
            assertEquals(ownNested.parentId(), parentId); assertEquals(ownNested.budgetRootId(), parentId);
            assertNull(otherNested.parentId()); assertEquals(otherNested.budgetRootId(), nestedId);
            assertFalse(projection.frame().toString().contains(parentId), "A public SBA result does not disclose its hidden synchronous parent");
        }
    }

    @Test public void replacementResultsAreCopiedWithoutDisclosingHiddenReplacementSources() throws Exception {
        try (var table = table()) {
            var owner = table.game.getPlayers().get(0); var observer = table.game.getPlayers().get(1);
            var rules = table.game.getExecutions(); var activity = new MatchActivity(observer.getView());
            var projection = new MatchExecutions(rules, observer.getView(), activity);
            activity.executionFilter(projection::reference);
            table.game.subscribeToEvents(projection); table.game.subscribeToEvents(activity);
            var hidden = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard("Loch Mare"), owner);
            owner.getZone(ZoneType.Hand).add(hidden, null, null);
            var target = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard("Grizzly Bears"), owner);
            owner.getZone(ZoneType.Battlefield).add(target, null, null);
            String hiddenId;
            try (var replacement = rules.enterReplacement(hidden, null)) {
                hiddenId = replacement.id(); replacement.step(RESOLUTION);
                table.game.fireEvent(new GameEventCardCounters(target, CounterEnumType.P1P1, 0, 1));
                replacement.complete(RESOLVED, forge.game.ExecutionTracker.RuleResult.UPDATED);
            }
            assertTrue(projection.frame().occurrences().isEmpty());
            assertNull(activity.snapshot().get(0).executionId(), "A visible result cannot grant disclosure of a private replacement source");
            assertFalse(projection.frame().toString().contains(hiddenId));
            String visibleId;
            try (var replacement = rules.enterReplacement(target, null)) {
                visibleId = replacement.id(); replacement.step(RESOLUTION);
                replacement.complete(RESOLVED, forge.game.ExecutionTracker.RuleResult.PREVENTED);
            }
            var admitted = projection.frame().occurrences().stream().filter(item -> item.id().equals(visibleId)).findFirst().orElseThrow();
            assertEquals(admitted.ruleKind(), "replacement"); assertEquals(admitted.ruleResult(), "prevented");
            assertEquals(admitted.outcome(), "resolved", "A successful replacement process is distinct from its prevented result");
            var terminal = projection.frame().transitions().get(projection.frame().transitions().size() - 1);
            assertEquals(terminal.ruleResult(), "prevented");
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
