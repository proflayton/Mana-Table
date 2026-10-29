package forge.game.mana;

import com.google.common.eventbus.Subscribe;
import forge.card.CardRarity;
import forge.card.CardRules;
import forge.card.CardType;
import forge.card.MagicColor;
import forge.card.mana.ManaCost;
import forge.card.mana.ManaCostParser;
import forge.card.mana.ManaCostShard;
import forge.card.mana.ManaAtom;
import forge.game.Game;
import forge.game.GameRules;
import forge.game.GameType;
import forge.game.Match;
import forge.game.ability.AbilityFactory;
import forge.game.card.Card;
import forge.game.cost.CostPayment;
import forge.game.event.GameEvent;
import forge.game.player.Player;
import forge.game.spellability.SpellAbility;
import forge.game.zone.ZoneType;
import forge.item.PaperCard;
import forge.util.MyRandom;
import org.testng.annotations.BeforeClass;
import org.testng.annotations.Test;

import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.concurrent.atomic.AtomicInteger;

import static org.testng.Assert.*;

/** The quote uses real unit provenance and existing payment rules without executing payment. */
public class ManaPoolPaymentQuoteTest {
    private static final byte C = (byte) ManaAtom.COLORLESS;
    @BeforeClass public void language() {
        forge.util.Localizer.getInstance().initialize("en-US", java.nio.file.Path.of("../forge-gui/res/languages").toAbsolutePath().normalize().toString());
        forge.util.Lang.createInstance("en-US");
    }

    private static final class Fixture {
        final Game game;
        final Player player;
        final Card source;
        final SpellAbility purpose;
        final ManaPool pool;
        Fixture() {
            var rules = new GameRules(GameType.Constructed);
            game = new Game(List.of(), rules, new Match(rules,List.of(),"pure pool quote"));
            game.setNoGUIUser(); game.getAction().enableStateBoundary();
            player = new Player("payer",game,0); game.getPlayers().add(player); game.getRegisteredPlayers().add(player);
            var paper = new PaperCard(CardRules.fromScript(List.of("Name:Pool resource", "ManaCost:2", "Types:Artifact", "Oracle:Mana resource.")),"TST",CardRarity.Common);
            source = new Card(100,paper,game); source.setOwner(player); source.setName("Pool resource");
            source.setType(CardType.parse("Artifact",false));
            player.getZone(ZoneType.Battlefield).add(source); source.setZone(player.getZone(ZoneType.Battlefield));
            purpose = new SpellAbility.EmptySa(source,player); pool = player.getManaPool();
            game.getReversibleManaAction().enable();
        }
        Mana add(byte color) { var unit = new Mana(color,source,null,player); pool.addManaNoEvent(unit); return unit; }
        Mana restricted(byte color) {
            var ability = AbilityFactory.getAbility("AB$ Mana | Cost$ T | Produced$ U | RestrictValid$ Creature",source);
            var unit = new Mana(color,source,ability.getManaPart(),player); pool.addManaNoEvent(unit); return unit;
        }
        ManaPoolPaymentQuote quote(String cost) { return pool.quoteFixedPayment(bill(cost),purpose); }
    }
    public static final class Events {
        final List<GameEvent> values = new ArrayList<>();
        @Subscribe public void event(GameEvent event) { values.add(event); }
    }
    private static ManaCostBeingPaid bill(String text) { return new ManaCostBeingPaid(new ManaCost(new ManaCostParser(text))); }
    private static void sameUnits(List<Mana> expected, ManaPool pool) {
        var actual = pool.captureReversibleUnits(); assertEquals(actual.size(),expected.size());
        for (int i=0;i<actual.size();i++) assertSame(actual.get(i),expected.get(i),"Unit identity and lane order are unchanged");
    }

    @Test public void completeWitnessAndPartialWalkNeverSpendPublishOrRewriteThePurpose() {
        var f = new Fixture(); var first=f.add(C); var second=f.add(C);
        var before = f.pool.captureReversibleUnits();
        var cost = bill("2"); var paid=f.purpose.captureManaActivationState();
        var params=Map.copyOf(f.purpose.getMapParams()); var activator=f.purpose.getActivatingPlayer();
        long timestamp=f.game.getTimestamp(), barrier=f.game.getReversibleManaAction().barrierVersion();
        var events=new Events(); f.game.subscribeToEvents(events);
        for (int repeat=0;repeat<20;repeat++) {
            var quote=f.pool.quoteFixedPayment(cost,f.purpose);
            assertEquals(quote.knowledge(),ManaPoolPaymentQuote.Knowledge.PAYABLE);
            assertTrue(quote.remainingBill().isPaid());
            assertSame(quote.selectedUnits().get(0),first); assertSame(quote.selectedUnits().get(1),second);
            sameUnits(before,f.pool); assertEquals(cost.getGenericManaAmount(),2);
        }
        assertTrue(events.values.isEmpty()); assertEquals(f.game.getTimestamp(),timestamp);
        assertEquals(f.game.getReversibleManaAction().barrierVersion(),barrier,"No rules/RNG invalidation occurs");
        assertEquals(f.purpose.captureManaActivationState(),paid); assertEquals(f.purpose.getMapParams(),params);
        assertSame(f.purpose.getActivatingPlayer(),activator); assertFalse(f.source.isTapped());
        var partial=f.quote("3"); assertEquals(partial.knowledge(),ManaPoolPaymentQuote.Knowledge.UNKNOWN);
        assertEquals(partial.remainingBill().getGenericManaAmount(),1);
        partial.remainingBill().decreaseGenericMana(1);
        assertEquals(partial.remainingBill().getGenericManaAmount(),1,"The residual belongs to the quote");
        assertThrows(UnsupportedOperationException.class,()->partial.selectedUnits().clear());
        sameUnits(before,f.pool);
    }

    @Test public void genericColorlessAndColoredRequirementsRemainDistinct() {
        var f=new Fixture(); f.add(C);
        assertTrue(f.quote("1").canPayEntireBill()); assertTrue(f.quote("C").canPayEntireBill());
        assertFalse(f.quote("U").canPayEntireBill());
        assertEquals(f.quote("U").remainingBill().getUnpaidShards(ManaCostShard.BLUE),1);
        var green=new Fixture(); green.add(MagicColor.GREEN);
        assertTrue(green.quote("1").canPayEntireBill()); assertFalse(green.quote("C").canPayEntireBill());
        assertTrue(green.quote("G").canPayEntireBill());
    }

    @Test public void repeatedReferencesAreDistinctUnitOccurrencesButOneOccurrenceCannotBeReused() {
        var f=new Fixture(); Mana unit=f.add(C); f.pool.addManaNoEvent(unit);
        var quote=f.quote("2"); assertTrue(quote.canPayEntireBill());
        assertEquals(quote.selectedUnits().size(),2); assertSame(quote.selectedUnits().get(0),unit); assertSame(quote.selectedUnits().get(1),unit);
        var shortfall=f.quote("3"); assertFalse(shortfall.canPayEntireBill());
        assertEquals(shortfall.selectedUnits().size(),2); assertEquals(shortfall.remainingBill().getGenericManaAmount(),1);
        assertEquals(f.pool.totalMana(),2);
    }

    @Test public void omittedRestrictedUnitsNeverSupplyAPartialAbsenceClaimButKnownSubsetCanWitnessSuccess() {
        var f=new Fixture(); f.restricted(MagicColor.BLUE); f.add(C);
        var uncertain=f.quote("2"); assertFalse(uncertain.canPayEntireBill()); assertNull(uncertain.remainingBill());
        assertThrows(IllegalStateException.class,()->uncertain.canUseColor(MagicColor.BLUE));
        var complete=f.quote("1"); assertTrue(complete.canPayEntireBill()); assertTrue(complete.remainingBill().isPaid());
        assertEquals(f.pool.totalMana(),2);
    }

    @Test public void purposeRestrictionsAndCustomEligibilityCallbacksAreUnsupportedWithoutInvokingThem() {
        var f=new Fixture(); f.add(C);
        f.purpose.getMapParams().put("ManaRestriction","ChosenColor");
        assertNull(f.quote("1").remainingBill()); f.purpose.getMapParams().remove("ManaRestriction");
        var invoked=new AtomicInteger();
        var custom=new SpellAbility.EmptySa(f.source,f.player) {
            @Override public boolean allowsPayingWithShard(Card source,byte color) { invoked.incrementAndGet(); throw new AssertionError("No purpose callback"); }
        };
        var quote=f.pool.quoteFixedPayment(bill("1"),custom);
        assertFalse(quote.canPayEntireBill()); assertNull(quote.remainingBill()); assertEquals(invoked.get(),0);
        var armed=new java.util.concurrent.atomic.AtomicBoolean();
        var customHost=new SpellAbility.EmptySa(f.source,f.player) {
            @Override public Card getHostCard() {
                if (armed.get()) { invoked.incrementAndGet(); throw new AssertionError("No host callback"); }
                return f.source;
            }
        };
        armed.set(true);
        assertNull(f.pool.quoteFixedPayment(bill("1"),customHost).remainingBill()); assertEquals(invoked.get(),0);
        var customBill=new ManaCostBeingPaid(ManaCost.ZERO) {
            @Override public Iterable<ManaCostShard> getDistinctShards() { invoked.incrementAndGet(); throw new AssertionError("No bill callback"); }
        };
        assertNull(f.pool.quoteFixedPayment(customBill,f.purpose).remainingBill()); assertEquals(invoked.get(),0);
        f.source.addStaticAbility("Mode$ ManaRestriction | ValidSource$ Creature");
        assertNull(f.quote("1").remainingBill());
    }

    @Test public void inheritedEligibilityCannotHideCustomRestrictionGetters() {
        var f=new Fixture(); f.add(C); var invoked=new AtomicInteger();
        var custom=new SpellAbility.EmptySa(f.source,f.player) {
            @Override public boolean hasParam(String key) { invoked.incrementAndGet(); return key.equals("ManaRestriction"); }
            @Override public String getParam(String key) { invoked.incrementAndGet(); return "ChosenColor"; }
        };
        assertFalse(custom.allowsPayingWithShard(f.source,C),"The inherited actual payment rule rejects an unchosen color");
        invoked.set(0);
        var quote=f.pool.quoteFixedPayment(bill("1"),custom);
        assertEquals(quote.knowledge(),ManaPoolPaymentQuote.Knowledge.UNKNOWN);
        assertNull(quote.remainingBill()); assertEquals(invoked.get(),0,"A pure quote cannot execute the virtual restriction getters");

        var customHasParam=new SpellAbility.EmptySa(f.source,f.player) {
            @Override public boolean hasParam(String key) { invoked.incrementAndGet(); throw new AssertionError("No presence callback"); }
        };
        assertNull(f.pool.quoteFixedPayment(bill("1"),customHasParam).remainingBill()); assertEquals(invoked.get(),0);
        var customParam=new SpellAbility.EmptySa(f.source,f.player) {
            @Override public String getParam(String key) { invoked.incrementAndGet(); throw new AssertionError("No parameter callback"); }
        };
        assertNull(f.pool.quoteFixedPayment(bill("1"),customParam).remainingBill()); assertEquals(invoked.get(),0);
    }

    @Test public void aCustomPurposeParameterMapIsUnsupportedBeforeAnyLookup() {
        var f=new Fixture(); f.add(C); var invoked=new AtomicInteger();
        var custom=new SpellAbility.EmptySa(f.source,f.player) {
            { mapParams=new java.util.HashMap<>() {
                @Override public boolean containsKey(Object key) { invoked.incrementAndGet(); throw new AssertionError("No custom map lookup"); }
            }; }
        };
        var quote=f.pool.quoteFixedPayment(bill("1"),custom);
        assertEquals(quote.knowledge(),ManaPoolPaymentQuote.Knowledge.UNKNOWN);
        assertNull(quote.remainingBill()); assertEquals(invoked.get(),0);
    }

    @Test public void theCapturedReadMatrixRetainsConversionRestrictionAndSnowFacts() {
        var f=new Fixture(); f.add(MagicColor.GREEN);
        f.pool.adjustColorReplacement(MagicColor.GREEN,MagicColor.BLUE,true);
        f.pool.adjustColorReplacement(MagicColor.GREEN,MagicColor.BLUE,false);
        f.pool.setSnowForColor(true);
        var snapshot=f.pool.paymentSnapshot();
        assertEquals(snapshot.getPossibleColorUses(MagicColor.GREEN),MagicColor.BLUE); assertTrue(snapshot.isSnowForColor());
        assertTrue(f.quote("U").canPayEntireBill()); assertFalse(f.quote("G").canPayEntireBill());
        var missing=f.quote("R"); assertTrue(missing.canUseColor(MagicColor.RED));
        f.pool.restoreColorReplacements();
        assertEquals(snapshot.getPossibleColorUses(MagicColor.GREEN),MagicColor.BLUE); assertTrue(snapshot.isSnowForColor());
        assertFalse(snapshot.canPayForShardWithColor(ManaCostShard.GREEN,MagicColor.GREEN));
        assertTrue(snapshot.canPayForShardWithColor(ManaCostShard.BLUE,MagicColor.GREEN));
        assertFalse(f.quote("U").canPayEntireBill());
    }

    @Test public void boundsAndUnresolvedOrUnsupportedBillsAreUnknownWithoutExpansion() {
        var f=new Fixture();
        var huge=new ManaCostBeingPaid(ManaCost.ZERO); huge.increaseShard(ManaCostShard.WHITE,1_000_000_000);
        assertNull(f.pool.quoteFixedPayment(huge,f.purpose).remainingBill());
        assertEquals(huge.getUnpaidShards(ManaCostShard.WHITE),1_000_000_000);
        for (String cost:List.of("X","W/U","S","W/P")) assertNull(f.quote(cost).remainingBill());
        for (int i=0;i<129;i++) f.add(C);
        assertTrue(f.quote("1").canPayEntireBill(),"A complete known-subset witness remains valid");
        var oversized=f.quote("U"); assertFalse(oversized.canPayEntireBill()); assertNull(oversized.remainingBill());
        assertTrue(f.quote("0").canPayEntireBill());
    }

    @Test public void pureChoiceUsesExistingPaymentWeightAndLegacyPaymentStillActuallySpends() {
        var f=new Fixture(); Mana blue=f.add(MagicColor.BLUE), colorless=f.add(C);
        var quote=f.quote("1"); assertSame(quote.selectedUnits().get(0),colorless,"Reuse ordinary preference for colorless on generic");
        Mana actual=CostPayment.getMana(f.player,ManaCostShard.GENERIC,f.purpose,(byte)-1,null);
        assertSame(actual,colorless);
        var bill=bill("1"); var spent=new ArrayList<Mana>();
        assertTrue(f.pool.payManaCostFromPool(bill,f.purpose,false,spent));
        assertTrue(bill.isPaid()); assertEquals(spent,List.of(colorless));
        assertEquals(f.pool.totalMana(),1); assertSame(f.pool.iterator().next(),blue);
    }

    @Test public void equalWeightGenericChoicesProduceAWitnessWithoutAnyController() {
        var f=new Fixture(); f.add(MagicColor.BLUE); f.add(MagicColor.GREEN);
        assertNull(f.player.getController(),"An accidental interactive tie chooser would fail this fixture");
        var units=f.pool.captureReversibleUnits();
        var tied=CostPayment.unrestrictedManaChoices(units,f.pool.paymentSnapshot(),ManaCostShard.GENERIC,(byte)-1);
        assertEquals(tied.size(),2,"Both differently colored unrestricted units have the same ordinary weight");
        var quote=f.quote("1"); assertTrue(quote.canPayEntireBill());
        assertEquals(quote.selectedUnits().size(),1); assertSame(quote.selectedUnits().get(0),tied.get(0));
        sameUnits(units,f.pool);
    }

    @Test public void queryDoesNotConsultRulesRngEvenOutsideTheGameStateWrapper() {
        var f=new Fixture(); f.add(C); var calls=new AtomicInteger();
        Runnable previous=MyRandom.exchangeUseObserver(calls::incrementAndGet);
        try { assertTrue(ManaPoolPaymentQuote.capture(f.pool,bill("1"),f.purpose).canPayEntireBill()); }
        finally { MyRandom.exchangeUseObserver(previous); }
        assertEquals(calls.get(),0);
    }

    @Test public void anIncompleteGreedyWalkIsUnknownEvenWhenAnotherUnitOrderCanPay() {
        var f=new Fixture(); Mana flexible=f.add(C), white=f.add(MagicColor.WHITE);
        f.pool.adjustColorReplacement(C,(byte)(MagicColor.WHITE|MagicColor.BLUE),true);
        var quote=f.quote("W U");
        assertEquals(quote.knowledge(),ManaPoolPaymentQuote.Knowledge.UNKNOWN);
        assertNotNull(quote.remainingBill(),"All units were supported, but this remains one ordinary walk");
        assertEquals(quote.remainingBill().getUnpaidShards(ManaCostShard.BLUE),1);
        var alternative=bill("W U"); var matrix=f.pool.paymentSnapshot();
        assertTrue(alternative.payMana(white,matrix)); assertTrue(alternative.payMana(flexible,matrix));
        assertTrue(alternative.isPaid(),"A concrete alternative witness proves UNKNOWN cannot mean unpayable");
        assertEquals(f.pool.totalMana(),2);
    }
}
