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
import java.util.*;
import static forge.game.ExecutionTracker.Outcome.*;
import static org.testng.Assert.*;

/** Authored emission/eviction fixtures, not a replacement gameplay simulator. */
public class FiniteYieldRetentionTest {
    @Test public void acknowledgedDeliveryKeepsAdmittedCausalReceiptsPastTheGlobalRingWithoutExposingPrivateExecutions() throws Exception {
        try (var table = table()) {
            var owner = table.game.getPlayers().get(0); var other = table.game.getPlayers().get(1);
            var source = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard("Loch Mare"), owner);
            owner.getZone(ZoneType.Battlefield).add(source, null, null);
            var activity = new MatchActivity(owner.getView()); var rules = table.game.getExecutions();
            var projection = new MatchExecutions(rules, owner.getView(), activity);
            activity.executionFilter(projection::reference);
            table.game.subscribeToEvents(projection); table.game.subscribeToEvents(activity);
            activity.beginDelivery(); projection.beginDelivery();
            String first = null;
            for (int count = 0; count < 350; count++) {
                var ability = new SpellAbility.EmptySa(source, owner); String id = rules.prepare(ability);
                if (first == null) first = id;
                try (var execution = rules.enter(ability)) {
                    table.game.fireEvent(new GameEventCardCounters(source, CounterEnumType.P1P1, count, count + 1));
                }
                rules.complete(ability, RESOLVED);
            }
            String retained = first; assertNull(rules.occurrence(retained));
            var capture = MatchActivity.capture(activity::frame, projection::frame);
            assertEquals(capture.activity().entries().size(), 350);
            assertEquals(capture.activity().entries().get(0).executionId(), retained);
            assertTrue(capture.executions().occurrences().stream().anyMatch(value -> value.id().equals(retained) && "complete".equals(value.step())));
            assertTrue(capture.executions().transitions().stream().anyMatch(value -> value.executionId().equals(retained) && "complete".equals(value.step())));
            var hidden = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard("Island"), other);
            other.getZone(ZoneType.Hand).add(hidden, null, null);
            var secret = new SpellAbility.EmptySa(hidden, other); String secretId = rules.prepare(secret);
            try (var execution = rules.enter(secret)) { }
            rules.complete(secret, RESOLVED);
            assertTrue(projection.frame().occurrences().stream().noneMatch(value -> value.id().equals(secretId)));
            assertTrue(projection.frame().transitions().stream().noneMatch(value -> value.executionId().equals(secretId)));
            activity.acknowledgeDelivery(activity.sequence()); projection.acknowledgeDelivery(projection.frame().lastSequence());
            assertTrue(projection.frame().occurrences().stream().noneMatch(value -> value.id().equals(retained)));
            assertEquals(activity.snapshot().size(), 120);
        }
    }
    @Test public void oneAudienceRetainsExactOwnedHistoryAndParentsPastTheNormalRings() throws Exception {
        try (var table = table()) {
            var owner = table.game.getPlayers().get(0); var other = table.game.getPlayers().get(1);
            var source = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard("Loch Mare"), owner);
            owner.getZone(ZoneType.Battlefield).add(source, null, null);
            var activity = new MatchActivity(owner.getView()); var rules = table.game.getExecutions();
            var projection = new MatchExecutions(rules, owner.getView(), activity);
            activity.executionFilter(projection::reference); activity.finiteAncestry(projection::finiteDescendant);
            table.game.subscribeToEvents(projection); table.game.subscribeToEvents(activity);
            var observer = new MatchExecutions(rules, other.getView(), new MatchActivity(other.getView()));
            table.game.subscribeToEvents(observer);
            var ability = new SpellAbility.EmptySa(source, owner); String root = rules.prepare(ability);
            projection.finiteGrant("grant", List.of(root)); projection.pinStack(List.of(root)); activity.finiteGrant("grant", List.of(root));
            String child;
            try (var parent = rules.enter(ability)) {
                for (int count = 0; count < 130; count++) table.game.fireEvent(new GameEventCardCounters(source, CounterEnumType.P1P1, count, count + 1));
                var nested = new SpellAbility.EmptySa(source, owner); child = rules.prepare(nested);
                try (var execution = rules.enter(nested)) { table.game.fireEvent(new GameEventCardCounters(source, CounterEnumType.P1P1, 130, 131)); }
                rules.complete(nested, RESOLVED);
            }
            rules.complete(ability, RESOLVED);
            var originalEntries = activity.snapshot(); assertEquals(originalEntries.size(), 131);
            for (int count = 0; count < 600; count++) {
                var unrelated = new SpellAbility.EmptySa(source, owner); rules.prepare(unrelated);
                try (var execution = rules.enter(unrelated)) { table.game.fireEvent(new GameEventCardCounters(source, CounterEnumType.P1P1, count, count + 1)); }
                rules.complete(unrelated, RESOLVED);
                observer.pinStack(List.of()); // Another audience cannot release this audience's consent evidence.
            }
            assertNull(rules.occurrence(root)); assertTrue(projection.frame().firstSequence() > 1);
            assertTrue(projection.frame().occurrences().stream().anyMatch(value -> value.id().equals(root)));
            assertTrue(projection.frame().occurrences().stream().anyMatch(value -> value.id().equals(child)));
            assertTrue(observer.frame().occurrences().stream().noneMatch(value -> value.id().equals(root)));
            var captured = MatchActivity.capture(activity::frame, projection::frame);
            assertTrue(captured.activity().entries().containsAll(originalEntries), "Exact old event IDs and parent-owned facts survive fast projection delivery");
            assertEquals(captured.activity().entries().size(), 251, "Unrelated public history retains only its ordinary120-entry tail");
            assertFalse(activity.finiteOverflow()); assertFalse(projection.finiteOverflow());
            projection.finiteGrant("fresh", List.of()); activity.finiteGrant("fresh", List.of()); projection.pinStack(List.of());
            assertEquals(activity.snapshot().size(), 120); assertTrue(projection.frame().occurrences().stream().noneMatch(value -> value.id().equals(root)));
        }
    }

    @Test public void finiteRetentionHasAnExplicitCapacityAndCannotAdmitAPrivateOwner() throws Exception {
        try (var table = table()) {
            var owner = table.game.getPlayers().get(0); var other = table.game.getPlayers().get(1);
            var source = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard("Loch Mare"), owner);
            owner.getZone(ZoneType.Battlefield).add(source, null, null);
            var activity = new MatchActivity(owner.getView()); var rules = table.game.getExecutions();
            var projection = new MatchExecutions(rules, owner.getView(), activity);
            activity.executionFilter(projection::reference); activity.finiteAncestry(projection::finiteDescendant);
            table.game.subscribeToEvents(projection); table.game.subscribeToEvents(activity);
            var ability = new SpellAbility.EmptySa(source, owner); String root = rules.prepare(ability);
            projection.finiteGrant("grant", List.of(root)); activity.finiteGrant("grant", List.of(root));
            try (var execution = rules.enter(ability)) {
                for (int count = 0; count <= MatchActivity.MAXIMUM_FINITE_ENTRIES; count++)
                    table.game.fireEvent(new GameEventCardCounters(source, CounterEnumType.P1P1, count, count + 1));
                var hidden = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard("Island"), other);
                other.getZone(ZoneType.Hand).add(hidden, null, null);
                var secret = new SpellAbility.EmptySa(hidden, other); String hiddenId = rules.prepare(secret);
                try (var privateExecution = rules.enter(secret)) { table.game.fireEvent(new GameEventCardCounters(source, CounterEnumType.P1P1, 0, 1)); }
                rules.complete(secret, RESOLVED);
                assertFalse(projection.frame().toString().contains(hiddenId));
                assertTrue(activity.snapshot().stream().noneMatch(value -> hiddenId.equals(value.executionId())));
            }
            assertTrue(activity.finiteOverflow(), "A single oversized resolution reports a gap instead of retaining unbounded history");
            assertTrue(activity.snapshot().size() <= MatchActivity.MAXIMUM_FINITE_ENTRIES + 120);
        }
    }
    private static MatchTable table() throws Exception {
        var resources = Path.of("../forge-gui/res"); if (StaticData.instance() == null) EngineResources.load(resources);
        var table = new MatchTable("Constructed", List.of(MatchTable.human(new Deck(), "Constructed", "Owner"),
                MatchTable.human(new Deck(), "Constructed", "Observer")), resources, Files.createTempDirectory("finite-retention"));
        HeadlessPlatform.activate(table); return table;
    }
}
