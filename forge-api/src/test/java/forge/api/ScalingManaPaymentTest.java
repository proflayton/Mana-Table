package forge.api;

import com.google.common.eventbus.Subscribe;
import forge.StaticData;
import forge.ai.ComputerUtilMana;
import forge.card.MagicColor;
import forge.deck.Deck;
import forge.game.ability.AbilityFactory;
import forge.game.card.Card;
import forge.game.card.CardCollection;
import forge.game.card.CounterEnumType;
import forge.game.event.GameEventCardTapped;
import forge.game.mana.ManaCostBeingPaid;
import forge.game.mana.ManaSourceFacts;
import forge.game.player.Player;
import forge.game.spellability.SpellAbility;
import forge.game.zone.ZoneType;
import forge.gamemodes.match.input.InputPayMana;
import forge.player.PlayerControllerHuman;
import forge.util.MyRandom;
import org.testng.annotations.DataProvider;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicReference;
import java.util.concurrent.atomic.AtomicInteger;

import static org.testng.Assert.*;

/** Real producers and the human payment input, rather than a sort-only policy fixture. */
public class ScalingManaPaymentTest {
    @Test(timeOut = 30000)
    public void sharedSourceFactsNeverEvaluateRandomAmountsOrMutateTheResource() throws Exception {
        try (var fixture = new Fixture()) {
            var elevator = fixture.elevator(32);
            fixture.inState(() -> {
                var scaled = elevator.getManaAbilities().stream().filter(ability -> ability.getManaPart().isAnyMana()).findFirst().orElseThrow();
                var random = AbilityFactory.getAbility("AB$ Mana | Cost$ T | Produced$ Any | Amount$ X", elevator);
                random.setActivatingPlayer(fixture.actor()); random.setSVar("X", "Count$Random.1.40");
                var fixed = AbilityFactory.getAbility("AB$ Mana | Cost$ T | Produced$ R R | Amount$ 2 | Each$ 99", elevator);
                fixed.setActivatingPlayer(fixture.actor());
                boolean undoable = scaled.isUndoable();
                var rngCalls = new AtomicInteger();
                Runnable previous = MyRandom.exchangeUseObserver(rngCalls::incrementAndGet);
                try {
                    assertNull(ManaSourceFacts.currentAmount(random), "Unknown amounts are not executed to guess a ranking");
                    assertNull(ManaSourceFacts.comparableTapOutput(random));
                    assertEquals(ManaSourceFacts.currentAmount(scaled), Integer.valueOf(32));
                    assertEquals(ManaSourceFacts.comparableTapOutput(scaled), Integer.valueOf(32));
                    assertEquals(ManaSourceFacts.comparableTapOutput(fixed), Integer.valueOf(4),
                            "Each applies to Combo in the resolver, not to fixed output");
                    elevator.setCounters(CounterEnumType.CHARGE, 33);
                    assertEquals(ManaSourceFacts.currentAmount(scaled), Integer.valueOf(33), "A later read uses current counters");
                    elevator.setCounters(CounterEnumType.CHARGE, 32);
                } finally { MyRandom.exchangeUseObserver(previous); }
                assertEquals(rngCalls.get(), 0, "Quantity reads do not advance game randomness");
                assertEquals(scaled.isUndoable(), undoable, "A read cannot change the ability's execution properties");
                assertEquals(elevator.getCounters(CounterEnumType.CHARGE), 32);
                assertFalse(elevator.isTapped()); assertEquals(fixture.events.taps(elevator), 0);
                assertEquals(fixture.actor().getManaPool().totalMana(), 0);
                return null;
            });
        }
    }

    @DataProvider public Object[][] elevatorBills() {
        return new Object[][] {
                {32, "3", 32, -1},
                {32, "8", 32, -1},
                {19, "3", 3, (int) MagicColor.COLORLESS},
                {20, "3", 20, -1},
                {32, "2 U", 32, (int) MagicColor.BLUE},
                {32, "2 C", 3, (int) MagicColor.COLORLESS}
        };
    }

    @Test(dataProvider = "elevatorBills", timeOut = 30000)
    public void ordinaryAutoUsesTheUnlockedYieldButPreservesExplicitColorlessRequirements(
            int counters, String bill, int expectedProduced, int expectedColor) throws Exception {
        try (var fixture = new Fixture()) {
            var elevator = fixture.elevator(counters);
            var payment = fixture.payment(bill);
            fixture.preview(payment, elevator);
            fixture.auto(payment);
            fixture.assertPaid(payment, elevator, counters, expectedProduced, expectedColor);
        }
    }

    @Test(timeOut = 30000)
    public void aSingleColorBurstPaysItsColorAndGenericWhileAnotherSourcePaysTheOtherColor() throws Exception {
        try (var fixture = new Fixture()) {
            var elevator = fixture.elevator(32);
            var swamp = fixture.add("Swamp", ZoneType.Battlefield);
            var payment = fixture.payment("5 U B");
            fixture.preview(payment, elevator);
            fixture.auto(payment);
            fixture.inState(() -> {
                assertTrue(payment.getRemainingManaCost().isPaid());
                assertTrue(elevator.isTapped()); assertTrue(swamp.isTapped());
                assertEquals(fixture.actor().getManaPool().totalMana(), 26);
                assertEquals(fixture.actor().getManaPool().getAmountOfColor(MagicColor.BLUE), 26,
                        "The burst is one chosen color, not invented mixed-color output");
                assertEquals(elevator.getCounters(CounterEnumType.CHARGE), 32);
                assertEquals(fixture.events.taps(elevator), 1); assertEquals(fixture.events.taps(swamp), 1);
                return null;
            });
        }
    }

    @Test(timeOut = 30000)
    public void anotherCounterProducerDoesNotDependOnElevatorNamesOrTheVariableBeingCalledX() throws Exception {
        try (var fixture = new Fixture()) {
            var cornucopia = fixture.add("Astral Cornucopia", ZoneType.Battlefield);
            fixture.inState(() -> {
                cornucopia.setCounters(CounterEnumType.CHARGE, 12);
                // A controlled granted alternative exposes the same physical
                // source conflict on a real, different counter-based producer.
                cornucopia.addSpellAbility(AbilityFactory.getAbility(
                        "AB$ Mana | Cost$ T | Produced$ C | Amount$ 1", cornucopia));
                return null;
            });
            var payment = fixture.payment("1");
            fixture.preview(payment, cornucopia);
            fixture.auto(payment);
            fixture.assertPaid(payment, cornucopia, 12, 12, -1);
        }
    }

    @Test(timeOut = 30000)
    public void moreOutputDoesNotMakeSacrificingTheProducerAnEquivalentTapCost() throws Exception {
        try (var fixture = new Fixture()) {
            var elevator = fixture.elevator(32);
            fixture.inState(() -> {
                elevator.addSpellAbility(AbilityFactory.getAbility(
                        "AB$ Mana | Cost$ T Sac<1/CARDNAME> | Produced$ Any | Amount$ 64", elevator));
                return null;
            });
            var payment = fixture.payment("3");
            fixture.preview(payment, elevator);
            fixture.auto(payment);
            fixture.assertPaid(payment, elevator, 32, 32, -1);
            assertTrue(elevator.isInZone(ZoneType.Battlefield),
                    "A higher yield cannot silently opt into a different destructive commitment");
        }
    }

    @Test(timeOut = 30000)
    public void sourceScopedOptionalAutoUsesTheSameBetterSmallBillRoute() throws Exception {
        try (var fixture = new Fixture()) {
            var elevator = fixture.elevator(32);
            var payment = fixture.payment("3");
            fixture.inState(() -> {
                var witness = OptionalManaPayment.witness(payment);
                assertNotNull(witness, "The exact current optional bill has a complete funding witness");
                payment.arm();
                assertTrue(witness.apply(payment));
                return null;
            });
            fixture.await(payment);
            fixture.assertPaid(payment, elevator, 32, 32, -1);
        }
    }

    @Test(timeOut = 30000)
    public void anExplicitManualThreeColorlessChoiceRemainsThePlayersChoice() throws Exception {
        try (var fixture = new Fixture()) {
            var elevator = fixture.elevator(32);
            var payment = fixture.payment("3");
            fixture.inState(() -> {
                var colorless = elevator.getManaAbilities().stream()
                        .filter(ability -> ability.getManaPart().getOrigProduced().equals("C"))
                        .findFirst().orElseThrow();
                colorless.setActivatingPlayer(fixture.actor());
                payment.arm();
                assertTrue(payment.selectAbility(colorless, Map.of()));
                return null;
            });
            fixture.await(payment);
            fixture.assertPaid(payment, elevator, 32, 3, (int) MagicColor.COLORLESS);
        }
    }

    public static final class TapEvents {
        private final Map<Integer, Integer> counts = new HashMap<>();
        @Subscribe public void event(GameEventCardTapped event) {
            if (event.tapped()) counts.merge(event.card().getId(), 1, Integer::sum);
        }
        int taps(Card card) { return counts.getOrDefault(card.getId(), 0); }
    }

    private static final class Fixture implements AutoCloseable {
        final MatchTable table;
        final PlayerControllerHuman controller;
        final TapEvents events = new TapEvents();

        Fixture() throws Exception {
            var resources = Path.of("../forge-gui/res");
            if (StaticData.instance() == null) EngineResources.load(resources);
            table = new MatchTable("Constructed", List.of(MatchTable.human(new Deck(), "Constructed", "Payer"),
                    MatchTable.human(new Deck(), "Constructed", "Other")), resources, Files.createTempDirectory("scaling-mana"));
            HeadlessPlatform.activate(table);
            controller = (PlayerControllerHuman) actor().getController();
            table.game.subscribeToEvents(events);
        }
        Player actor() { return table.game.getRegisteredPlayers().get(0); }
        <T> T inState(java.util.concurrent.Callable<T> action) { return HeadlessPlatform.query(table, action); }
        Card add(String name, ZoneType zone) {
            return inState(() -> {
                var card = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard(name), actor());
                actor().getZone(zone).add(card); card.setSickness(false); return card;
            });
        }
        Card elevator(int counters) {
            var source = add("The Eternity Elevator", ZoneType.Battlefield);
            return inState(() -> {
                source.setCounters(CounterEnumType.CHARGE, counters);
                table.game.getAction().checkStaticAbilities(false);
                source.setSickness(false);
                for (var ability : source.getManaAbilities()) ability.setActivatingPlayer(actor());
                assertEquals(source.getManaAbilities().stream().anyMatch(ability -> ability.getManaPart().isAnyMana()), counters >= 20,
                        "The actual station static grants its second mana ability only after the threshold");
                return source;
            });
        }
        Probe payment(String cost) {
            var host = add("Spellbook", ZoneType.Hand);
            return inState(() -> {
                var ability = AbilityFactory.getAbility("AB$ Draw | Cost$ " + cost + " | NumCards$ 1", host);
                ability.setActivatingPlayer(actor());
                return new Probe(controller, ability, actor());
            });
        }
        void preview(Probe payment, Card source) {
            inState(() -> {
                var sources = payment.preview();
                assertNotNull(sources, "Auto preview must recognize the same legal funding that execution will use");
                assertTrue(sources.contains(source));
                assertFalse(source.isTapped(), "Preview cannot tap the real resource");
                assertEquals(actor().getManaPool().totalMana(), 0, "Preview cannot leave floating mana");
                assertEquals(events.taps(source), 0, "Preview cannot emit a tap event");
                return null;
            });
        }
        void auto(Probe payment) throws Exception {
            inState(() -> { payment.arm(); payment.selectButtonOK(); return null; });
            await(payment);
        }
        void await(Probe payment) throws Exception {
            assertTrue(payment.completed.await(5, TimeUnit.SECONDS), "The ordinary current payment gesture must finish its rules task");
            if (payment.failure.get() != null) throw new AssertionError("Payment task failed", payment.failure.get());
            inState(() -> { assertSame(actor().getController(), controller, "Auto restores the human controller"); return null; });
        }
        void assertPaid(Probe payment, Card source, int counters, int produced, int expectedColor) {
            inState(() -> {
                assertTrue(payment.getRemainingManaCost().isPaid(), "The actual input bill must be paid");
                int spent = payment.getOriginalManaCost().getConvertedManaCost();
                assertEquals(actor().getManaPool().totalMana(), produced - spent,
                        "One tap must leave the larger route's unused mana instead of discarding that route");
                if (expectedColor >= 0)
                    assertEquals(actor().getManaPool().getAmountOfColor((byte) expectedColor), produced - spent);
                else assertEquals(actor().getManaPool().getAmountOfColor(MagicColor.COLORLESS), 0,
                        "Any one color is colored mana, not invented colorless mana");
                assertTrue(source.isTapped());
                assertEquals(source.getCounters(CounterEnumType.CHARGE), counters, "Producing mana does not spend charge counters");
                assertEquals(events.taps(source), 1, "The source is tapped exactly once");
                assertEquals(payment.doneCalls, 1, "The current payment completes exactly once");
                return null;
            });
        }
        @Override public void close() {
            table.game.unsubscribeFromEvents(events); table.close();
        }
    }

    private static final class Probe extends InputPayMana {
        final CountDownLatch completed = new CountDownLatch(1);
        final AtomicReference<Throwable> failure = new AtomicReference<>();
        boolean armed;
        int doneCalls;
        Probe(PlayerControllerHuman controller, SpellAbility ability, Player player) {
            super(controller, ability, player, false);
            manaCost = new ManaCostBeingPaid(ability.getPayCosts().getCostMana().getMana());
            captureOriginalManaCost();
        }
        void arm() { armed = true; }
        CardCollection preview() {
            var result = new AtomicReference<CardCollection>();
            runAsAi(() -> result.set(ComputerUtilMana.getManaSourcesToPayCost(
                    getRemainingManaCost(), saPaidFor, player, effect)));
            return result.get();
        }
        @Override protected void onStateChanged() {
            try { super.onStateChanged(); }
            catch (RuntimeException | Error error) { failure.set(error); throw error; }
            finally { if (armed) completed.countDown(); }
        }
        @Override protected void done() { doneCalls++; }
        @Override protected String getMessage() { return ""; }
    }
}
