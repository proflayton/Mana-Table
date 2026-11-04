package forge.api;

import forge.StaticData;
import forge.deck.Deck;
import forge.game.ExecutionTracker;
import forge.game.card.Card;
import forge.game.player.Player;
import forge.game.player.PlayerView;
import forge.game.spellability.SpellAbility;
import forge.game.zone.ZoneType;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;

import static forge.game.ExecutionTracker.Outcome.RESOLVED;
import static forge.game.ExecutionTracker.Step.STACK;
import static org.testng.Assert.*;

/** Actual tracker emission and audience-retention fixtures, not a gameplay
 * simulator. The native polled workload supplies the real cast/draw journey. */
public class ExecutionAncestryRetentionTest {
    @Test(timeOut = 45000)
    public void liveIndependentTriggerRootsKeepTheirAdmittedCallerAcrossHistoryPruningAndReleaseWithTheirEvidence() throws Exception {
        try (var table = table()) {
            var rules = table.game.getExecutions(); var owner = table.game.getPlayers().get(0); var other = table.game.getPlayers().get(1);
            var spell = source(table, owner, "Shamanic Revelation", ZoneType.Stack);
            var toothy = source(table, owner, "Toothy, Imaginary Friend", ZoneType.Battlefield);
            var eye = source(table, other, "Mind's Eye", ZoneType.Battlefield);
            var projections = List.of(projection(table, owner.getView()), projection(table, other.getView()), projection(table, new PlayerView(-1, null)));
            var parent = spell.getFirstSpellAbility(); parent.setActivatingPlayer(owner);
            String parentId = rules.prepare(parent); rules.step(parent, STACK);
            var children = new ArrayList<SpellAbility>();
            for (int count = 0; count < 110; count++) {
                var child = trigger(count % 2 == 0 ? toothy : eye, count % 2 == 0 ? owner : other);
                rules.prepare(child, parentId); rules.step(child, STACK); children.add(child);
                assertEquals(rules.occurrence(child).budgetRootId(), child.getExecutionId(), "A trigger's allowance is independent of its caller");
            }
            rules.complete(parent, RESOLVED);
            var childIds = children.stream().map(SpellAbility::getExecutionId).toList();
            projections.get(0).finiteGrant("captured", childIds); projections.get(1).pinStack(childIds);
            var before = projections.stream().map(MatchExecutions::frame).toList();
            prune(rules, toothy, owner);
            assertNull(rules.occurrence(parentId), "The completed caller really left the core history; independent trigger roots do not keep it");
            for (int audience = 0; audience < projections.size(); audience++) {
                var frame = projections.get(audience).frame();
                for (String childId : childIds) {
                    assertEquals(occurrence(frame, childId), occurrence(before.get(audience), childId),
                            "Pruning unrelated history must not rewrite already-admitted trigger ancestry or source/actor metadata");
                    assertEquals(occurrence(frame, childId).parentId(), parentId);
                    assertEquals(occurrence(frame, childId).budgetRootId(), childId);
                }
                assertEquals(occurrence(frame, parentId), occurrence(before.get(audience), parentId));
                assertFalse(projections.get(audience).finiteOverflow());
            }
            children.forEach(child -> rules.complete(child, RESOLVED)); prune(rules, toothy, owner);
            assertNull(projections.get(2).reference(parentId), "The audience without pins releases completed dependencies after ordinary history retires");
            assertNotNull(projections.get(0).reference(parentId)); assertNotNull(projections.get(1).reference(parentId));
            projections.get(0).finiteGrant("released", List.of()); projections.get(1).pinStack(List.of());
            for (var projection : projections) {
                assertNull(projection.reference(parentId)); assertTrue(projection.frame().occurrences().size() <= 256);
            }
            assertEquals(occurrence(before.get(0), childIds.get(0)).step(), "stack", "Old immutable frames retain their own event-time values");
        }
    }

    @Test(timeOut = 45000)
    public void retainingAnOwnedPrivateCallerNeverAdmitsItToTheOtherAudience() throws Exception {
        try (var table = table()) {
            var owner = table.game.getPlayers().get(0); var other = table.game.getPlayers().get(1); var rules = table.game.getExecutions();
            var hidden = source(table, owner, "Shamanic Revelation", ZoneType.Hand);
            var visible = source(table, other, "Mind's Eye", ZoneType.Battlefield);
            var own = projection(table, owner.getView()); var observer = projection(table, other.getView());
            var parent = hidden.getFirstSpellAbility(); parent.setActivatingPlayer(owner); String parentId = rules.prepare(parent);
            var child = trigger(visible, other); String childId = rules.prepare(child, parentId); rules.step(child, STACK); rules.complete(parent, RESOLVED);
            assertEquals(occurrence(own.frame(), childId).parentId(), parentId);
            assertNull(occurrence(observer.frame(), childId).parentId()); assertNull(observer.reference(parentId));
            prune(rules, visible, other);
            assertEquals(occurrence(own.frame(), childId).parentId(), parentId, "Already-admitted private evidence belongs only to its original owner");
            assertNull(occurrence(observer.frame(), childId).parentId()); assertNull(observer.reference(parentId));
            assertEquals(occurrence(observer.frame(), childId).budgetRootId(), childId);
            assertFalse(observer.frame().toString().contains("Shamanic Revelation")); assertFalse(observer.frame().toString().contains(parentId));
            rules.complete(child, RESOLVED); prune(rules, visible, other);
            assertNull(own.reference(parentId)); assertNull(observer.reference(childId));
        }
    }

    @Test(timeOut = 45000)
    public void additionalAncestryHasAnExplicitCapacityAndAFreshReleasedLifetimeCanRecover() throws Exception {
        try (var table = table()) {
            var owner = table.game.getPlayers().get(0); var rules = table.game.getExecutions();
            var source = source(table, owner, "Toothy, Imaginary Friend", ZoneType.Battlefield);
            var projection = projection(table, owner.getView()); String previous = null;
            for (int count = 0; count < MatchActivity.MAXIMUM_FINITE_ENTRIES + 270; count++) {
                var child = trigger(source, owner); previous = rules.prepare(child, previous); rules.complete(child, RESOLVED);
            }
            assertTrue(projection.finiteOverflow(), "An oversized ancestry closure reports a gap instead of retaining unbounded historical chains");
            assertTrue(projection.frame().occurrences().size() <= rules.occurrences().size() + MatchActivity.MAXIMUM_FINITE_ENTRIES);
            projection.finiteGrant("still-oversized", List.of(previous));
            assertTrue(projection.finiteOverflow(), "Resetting the grant cannot hide a still-required over-capacity dependency");
            projection.finiteGrant("released", List.of()); prune(rules, source, owner);
            projection.finiteGrant("fresh", List.of());
            assertFalse(projection.finiteOverflow(), "A new bounded lifetime can recover after its old dependencies actually retire");
            assertTrue(projection.frame().occurrences().size() <= 256);
        }
    }

    @Test(timeOut = 45000)
    public void malformedCyclicProducerReferencesCannotMakeAudienceRetentionRecurseForever() throws Exception {
        try (var table = table()) {
            var owner = table.game.getPlayers().get(0); var rules = table.game.getExecutions();
            var source = source(table, owner, "Toothy, Imaginary Friend", ZoneType.Battlefield); var projection = projection(table, owner.getView());
            var left = trigger(source, owner); var right = trigger(source, owner);
            String a = rules.prepare(left); rules.complete(left, RESOLVED); String b = rules.prepare(right); rules.complete(right, RESOLVED);
            projection.pinStack(List.of(a));
            // Defensive malformed-producer input only. A real tracker forms a
            // causal DAG; this is not a valid client ledger or game journey.
            for (var pair : List.of(List.of(a, b), List.of(b, a))) projection.changed(new ExecutionTracker.Transition(rules.lastSequence(),
                    new ExecutionTracker.Occurrence(pair.get(0), pair.get(1), pair.get(0), "trigger", owner.getId(), source.getView(),
                            source.getName(), "complete", "resolved")));
            prune(rules, source, owner);
            assertNotNull(projection.reference(a)); assertNotNull(projection.reference(b));
            assertTrue(projection.frame().occurrences().size() <= 258); assertFalse(projection.finiteOverflow());
            projection.pinStack(List.of()); assertNull(projection.reference(a)); assertNull(projection.reference(b));
        }
    }

    private static MatchExecutions.Occurrence occurrence(MatchExecutions.Frame frame, String id) {
        return frame.occurrences().stream().filter(value -> value.id().equals(id)).findFirst().orElseThrow();
    }
    private static SpellAbility trigger(Card source, Player actor) {
        var ability = new SpellAbility.EmptySa(source, actor); ability.setTrigger(source.getTriggers().get(0)); return ability;
    }
    private static Card source(MatchTable table, Player owner, String name, ZoneType zone) {
        var card = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard(name), owner);
        if (zone == ZoneType.Stack) table.game.getStackZone().add(card, null, null);
        else owner.getZone(zone).add(card, null, null);
        return card;
    }
    private static MatchExecutions projection(MatchTable table, PlayerView viewer) {
        var projection = new MatchExecutions(table.game.getExecutions(), viewer, new MatchActivity(viewer, table.identities));
        table.game.subscribeToEvents(projection); return projection;
    }
    private static void prune(ExecutionTracker rules, Card source, Player owner) {
        for (int count = 0; count < 300; count++) {
            var unrelated = new SpellAbility.EmptySa(source, owner); rules.prepare(unrelated); rules.complete(unrelated, RESOLVED);
        }
    }
    private static MatchTable table() throws Exception {
        var resources = Path.of("../forge-gui/res"); if (StaticData.instance() == null) EngineResources.load(resources);
        var table = new MatchTable("Constructed", List.of(MatchTable.human(new Deck(), "Constructed", "Owner"),
                MatchTable.human(new Deck(), "Constructed", "Observer")), resources, Files.createTempDirectory("execution-ancestry"));
        HeadlessPlatform.activate(table); return table;
    }
}
