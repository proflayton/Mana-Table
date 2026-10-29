package forge.game;

import com.google.common.eventbus.Subscribe;
import forge.card.CardType;
import forge.card.mana.ManaAtom;
import forge.game.ability.AbilityFactory;
import forge.game.card.Card;
import forge.game.cost.CostTap;
import forge.game.cost.PaymentDecision;
import forge.game.event.GameEvent;
import forge.game.mana.Mana;
import forge.game.phase.PhaseType;
import forge.game.player.Player;
import forge.game.spellability.SpellAbility;
import forge.game.zone.ZoneType;
import forge.util.MyRandom;
import org.testng.annotations.Test;
import org.testng.annotations.BeforeClass;

import java.util.ArrayList;
import java.util.List;

import static org.testng.Assert.*;

/** Real core cost/stack/mana rules; the API suite separately exercises the full human runner and TCP scopes. */
public class ReversibleManaActionTest {
    @BeforeClass public void language() {
        forge.util.Localizer.getInstance().initialize("en-US", java.nio.file.Path.of("../forge-gui/res/languages").toAbsolutePath().normalize().toString());
        forge.util.Lang.createInstance("en-US");
    }
    private static final class Fixture {
        final Game game;
        final Player actor, other;
        final Card source;
        final SpellAbility ability;
        Fixture(boolean land, String produced) {
            var rules = new GameRules(GameType.Constructed);
            game = new Game(List.of(), rules, new Match(rules, List.of(), "reversible mana"));
            game.setNoGUIUser(); game.getAction().enableStateBoundary();
            actor = new Player("actor", game, 0); other = new Player("other", game, 1);
            game.getPlayers().add(actor); game.getPlayers().add(other);
            game.getRegisteredPlayers().add(actor); game.getRegisteredPlayers().add(other);
            actor.setLife(20, null); other.setLife(20, null);
            game.setAge(GameStage.Play);
            game.getPhaseHandler().devModeSet(PhaseType.MAIN1, actor, 1);
            game.getPhaseHandler().setPriority(actor);
            String name = land ? "Plain mana land" : "Plain mana artifact";
            var rulesForCard = forge.card.CardRules.fromScript(List.of("Name:" + name, "ManaCost:2", "Types:" + (land ? "Land" : "Artifact"), "Oracle:Add mana."));
            var paper = new forge.item.PaperCard(rulesForCard, "TST", forge.card.CardRarity.Common);
            source = new Card(100, paper, game); source.setName(name);
            source.setType(CardType.parse(land ? "Land" : "Artifact", false)); source.setOwner(actor);
            actor.getZone(ZoneType.Battlefield).add(source); source.setZone(actor.getZone(ZoneType.Battlefield));
            ability = AbilityFactory.getAbility("AB$ Mana | Cost$ T | Produced$ " + produced + " | SpellDescription$ Add mana.", source);
            source.addSpellAbility(ability); ability.setActivatingPlayer(actor);
            game.getReversibleManaAction().enable();
        }
        ReversibleManaAction.Offer activate() {
            return game.getAction().readState(() -> {
                var capture = game.getReversibleManaAction().begin(actor, ability);
                assertNotNull(capture, "The closed fixed tap-for-mana core route must be eligible");
                var tap = ability.getPayCosts().getCostParts().stream().filter(CostTap.class::isInstance).map(CostTap.class::cast).findFirst().orElseThrow();
                assertTrue(tap.payAsDecided(actor, PaymentDecision.number(1), ability, false));
                game.getStack().addAndUnfreeze(ability);
                game.getReversibleManaAction().settle(capture);
                var offer = game.getReversibleManaAction().current();
                assertNotNull(offer, "An actual cost, activation and resolution must produce the certified offer: " + game.getReversibleManaAction().lastInvalidationReason());
                return offer;
            });
        }
        ReversibleManaAction.Result restore(ReversibleManaAction.Offer offer) {
            return game.getReversibleManaAction().restore(offer.id(), offer.actionId(), offer.barrierVersion(), actor);
        }
        Card add(String name, String type, ZoneType zone, int id) {
            var paper = new forge.item.PaperCard(forge.card.CardRules.fromScript(List.of("Name:"+name, "ManaCost:2", "Types:"+type)), "TST", forge.card.CardRarity.Common);
            Card card = new Card(id, paper, game); card.setOwner(actor); card.setName(name); card.setType(CardType.parse(type,false));
            actor.getZone(zone).add(card); card.setZone(actor.getZone(zone)); return card;
        }
    }
    public static final class Events {
        final List<GameEvent> values = new ArrayList<>();
        @Subscribe public void event(GameEvent event) { values.add(event); }
    }

    @Test public void plainLandRestoresTheLandFlagAndEveryRulesLedgerWithoutGeneratingAnUntap() {
        var f = new Fixture(true, "G");
        f.source.addAbilityActivated(f.ability); f.source.addAbilityActivated(f.ability);
        f.source.addAbilityResolved(f.ability);
        var prior = new Mana((byte)ManaAtom.BLUE, f.source, f.ability.getManaPart(), f.actor);
        f.actor.getManaPool().addManaNoEvent(prior);
        var events = new Events(); f.game.subscribeToEvents(events);
        var offer = f.activate();
        assertEquals(f.source.getAbilityActivatedThisTurn(f.ability), 3);
        assertEquals(f.source.getAbilityActivatedThisGame(f.ability), 3);
        assertEquals(f.source.getAbilityResolvedThisTurn(f.ability), 2);
        assertTrue(f.actor.hasTappedLandForManaThisTurn());
        int emitted = events.values.size();
        assertEquals(f.restore(offer), ReversibleManaAction.Result.APPLIED);
        assertEquals(events.values.size(), emitted, "Administrative restoration emits no inverse game effects");
        assertFalse(f.source.isTapped()); assertFalse(f.actor.hasTappedLandForManaThisTurn());
        assertEquals(f.source.captureManaActivationState().tappedThisTurn(), 0);
        assertEquals(f.source.getAbilityActivatedThisTurn(f.ability), 2);
        assertEquals(f.source.getAbilityActivatedThisGame(f.ability), 2);
        assertEquals(f.source.getAbilityResolvedThisTurn(f.ability), 1);
        assertTrue(f.game.getStack().getAbilityActivatedThisTurn().isEmpty());
        assertEquals(f.actor.getManaPool().totalMana(), 1);
        assertSame(f.actor.getManaPool().iterator().next(), prior);
        assertTrue(f.ability.getManaPart().getLastManaProduced().isEmpty());
        assertEquals(f.restore(offer), ReversibleManaAction.Result.REJECTED);
        assertEquals(f.game.getReversibleManaAction().lastRestoration().actionId(), offer.actionId());
    }

    @Test public void multiManaArtifactRemovesBothExactUnitsAndCanActuallyActivateAgain() {
        var f = new Fixture(false, "2");
        var first = f.activate(); assertEquals(f.actor.getManaPool().totalMana(), 2);
        assertEquals(f.restore(first), ReversibleManaAction.Result.APPLIED);
        assertEquals(f.actor.getManaPool().totalMana(), 0); assertFalse(f.source.isTapped());
        var second = f.activate();
        assertNotEquals(second.id(), first.id());
        assertEquals(f.source.getAbilityActivatedThisGame(f.ability), 1);
        assertEquals(f.actor.getManaPool().totalMana(), 2);
        assertEquals(f.restore(first), ReversibleManaAction.Result.REJECTED);
        assertTrue(f.source.isTapped()); assertEquals(f.actor.getManaPool().totalMana(), 2);
        assertEquals(f.restore(second), ReversibleManaAction.Result.APPLIED);
    }

    @Test public void spentOrReplacedManaAndForeignScopesNeverPartiallyRestore() {
        var f = new Fixture(false, "2"); var offer = f.activate();
        assertEquals(f.game.getReversibleManaAction().restore(offer.id(), offer.actionId(), offer.barrierVersion(), f.other), ReversibleManaAction.Result.REJECTED);
        var units = f.actor.getManaPool().captureReversibleUnits();
        f.actor.getManaPool().restoreReversibleUnits(List.of(units.get(0)));
        assertEquals(f.restore(offer), ReversibleManaAction.Result.REJECTED);
        assertTrue(f.source.isTapped()); assertEquals(f.actor.getManaPool().totalMana(), 1);
        assertEquals(f.game.getReversibleManaAction().restorationVersion(), 0);
    }

    @Test public void unclassifiedSourceWriteAndRandomUseCloseTheWindow() {
        var changed = new Fixture(false, "2"); var first = changed.activate();
        changed.source.setSVar("UnclassifiedMutation", "1");
        assertNull(changed.game.getReversibleManaAction().current());
        assertEquals(changed.restore(first), ReversibleManaAction.Result.REJECTED);
        assertTrue(changed.source.isTapped()); assertEquals(changed.actor.getManaPool().totalMana(), 2);
        var random = new Fixture(true, "G"); var second = random.activate();
        random.game.getAction().withState(() -> MyRandom.getRandom().nextBoolean());
        assertNull(random.game.getReversibleManaAction().current());
        assertEquals(random.restore(second), ReversibleManaAction.Result.REJECTED);
    }

    @Test public void unsupportedScriptKeysAndNonTapCostsRemainPlayableButHaveNoCapture() {
        var f = new Fixture(false, "2");
        var optional = AbilityFactory.getAbility("AB$ Mana | Cost$ T | Produced$ 2 | Optional$ True", f.source);
        optional.setActivatingPlayer(f.actor);
        assertNull(f.game.getReversibleManaAction().begin(f.actor, optional));
        var life = AbilityFactory.getAbility("AB$ Mana | Cost$ PayLife<1> T | Produced$ 2", f.source);
        life.setActivatingPlayer(f.actor);
        assertNull(f.game.getReversibleManaAction().begin(f.actor, life));
        assertFalse(f.source.isTapped()); assertEquals(f.actor.getLife(), 20);
    }

    @Test public void silentOtherCardMutationCannotReviveAnOfferByReturningToItsOldValue() {
        var f = new Fixture(false, "2");
        Card otherCard = new Card(101, f.source.getPaperCard(), f.game);
        otherCard.setOwner(f.other); otherCard.setName("Other resource"); otherCard.setType(CardType.parse("Artifact", false));
        f.other.getZone(ZoneType.Battlefield).add(otherCard); otherCard.setZone(f.other.getZone(ZoneType.Battlefield));
        otherCard.setTapped(true);
        var offer = f.activate();
        otherCard.setTapped(false); otherCard.setTapped(true);
        assertNull(f.game.getReversibleManaAction().current());
        assertEquals(f.restore(offer), ReversibleManaAction.Result.REJECTED);
        assertEquals(f.actor.getManaPool().totalMana(), 2); assertTrue(f.source.isTapped());
        var variable = new Fixture(false, "2"); var old = variable.activate();
        variable.source.setSVar("Unknown", "changed"); variable.source.removeSVar("Unknown");
        assertNull(variable.game.getReversibleManaAction().current());
        assertEquals(variable.restore(old), ReversibleManaAction.Result.REJECTED);
    }

    @Test public void cachedRandomAndPreviouslyCreatedLazyStreamsStillInvalidateAtConsumption() {
        java.util.Random retained = MyRandom.getRandom();
        var bytes = new Fixture(false, "2"); var bytesOffer = bytes.activate();
        bytes.game.getAction().withState(() -> retained.nextBytes(new byte[16]));
        assertEquals(bytes.restore(bytesOffer), ReversibleManaAction.Result.REJECTED);
        var stream = retained.ints(-20, 20).iterator();
        var lazy = new Fixture(true, "G"); var lazyOffer = lazy.activate();
        lazy.game.getAction().withState(stream::nextInt);
        assertEquals(lazy.restore(lazyOffer), ReversibleManaAction.Result.REJECTED);
        var groups = new Fixture(false, "2"); var groupsOffer = groups.activate();
        groups.game.getAction().withState(() -> MyRandom.splitIntoRandomGroups(4, 2));
        assertEquals(groups.restore(groupsOffer), ReversibleManaAction.Result.REJECTED);
    }

    @Test public void observingRandomPreservesTheSuppliedProviderSequenceAndGaussianCache() {
        java.util.Random previous = MyRandom.getRandom();
        try {
            var expected = new java.util.Random(60211);
            MyRandom.setRandom(new java.util.Random(60211));
            var actual = MyRandom.getRandom();
            assertEquals(actual.nextInt(), expected.nextInt());
            assertEquals(actual.nextInt(17), expected.nextInt(17));
            assertEquals(actual.nextInt(-200, 900), expected.nextInt(-200, 900));
            assertEquals(actual.nextLong(), expected.nextLong());
            assertEquals(actual.nextLong(91), expected.nextLong(91));
            assertEquals(actual.nextBoolean(), expected.nextBoolean());
            assertEquals(actual.nextFloat(), expected.nextFloat());
            assertEquals(actual.nextDouble(), expected.nextDouble());
            assertEquals(actual.nextGaussian(), expected.nextGaussian());
            assertEquals(actual.nextGaussian(), expected.nextGaussian());
            var a = new byte[33]; var b = new byte[33]; actual.nextBytes(a); expected.nextBytes(b); assertEquals(a,b);
            assertEquals(actual.ints(9,-20,20).toArray(), expected.ints(9,-20,20).toArray());
            assertEquals(actual.longs(9,-200,200).toArray(), expected.longs(9,-200,200).toArray());
            assertEquals(actual.doubles(9,-200,200).toArray(), expected.doubles(9,-200,200).toArray());
            actual.setSeed(91); expected.setSeed(91);
            assertEquals(actual.nextGaussian(), expected.nextGaussian());
        } finally { MyRandom.setRandom(previous); }
    }

    @Test public void pureCommandPermissionIsRebuiltNormallyThenRestoredWithItsOriginalOwnerObjects() {
        var f = new Fixture(false, "2");
        Card commander = f.add("Command permanent", "Artifact", ZoneType.Command, 102); commander.setCommander(true);
        Card effect = f.add("Permission effect", "Enchantment", ZoneType.Command, 103);
        var permission = effect.addStaticAbility("Mode$ Continuous | EffectZone$ Command | MayPlay$ True | Affected$ Card.IsCommander+YouOwn | AffectedZone$ Command");
        var moved = forge.game.replacement.ReplacementHandler.parseReplacement("Event$ Moved | ValidCard$ Card | Origin$ Battlefield | Destination$ Graveyard", effect, true);
        moved.setOverridingAbility(AbilityFactory.getAbility("DB$ ChangeZone | Defined$ ReplacedCard | Destination$ Exile", effect));
        effect.addReplacementEffect(moved);
        f.game.getAction().withState(() -> f.game.getAction().checkStaticAbilities());
        var originalOption = commander.getMayPlay().get(permission); assertNotNull(originalOption);
        var originalEffect = f.game.getStaticEffects().getEffects().iterator().next();
        var offer = f.activate();
        assertNotSame(commander.getMayPlay().get(permission), originalOption, "Normal static refresh actually rebuilt the permission");
        assertEquals(f.restore(offer), ReversibleManaAction.Result.APPLIED);
        assertSame(commander.getMayPlay().get(permission), originalOption);
        assertSame(f.game.getStaticEffects().getEffects().iterator().next(), originalEffect);
        assertFalse(f.source.isTapped()); assertEquals(f.actor.getManaPool().totalMana(),0);
    }

    @Test public void otherContinuousEffectsAndManaReplacementsAreNotSilentlyCertified() {
        var continuous = new Fixture(false,"2");
        Card effect = continuous.add("Unknown continuous effect", "Enchantment", ZoneType.Command, 102);
        effect.addStaticAbility("Mode$ Continuous | EffectZone$ Command | Affected$ Card.YouCtrl | AddPower$ 1");
        assertNull(continuous.game.getReversibleManaAction().begin(continuous.actor,continuous.ability));
        var replacement = new Fixture(false,"2");
        var replacing = forge.game.replacement.ReplacementHandler.parseReplacement("Event$ ProduceMana | ValidCard$ Card", replacement.source, true);
        replacing.setOverridingAbility(AbilityFactory.getAbility("DB$ Mana | Produced$ G", replacement.source));
        replacement.source.addReplacementEffect(replacing);
        assertNull(replacement.game.getReversibleManaAction().begin(replacement.actor,replacement.ability));
        assertFalse(continuous.source.isTapped()); assertFalse(replacement.source.isTapped());
    }

    @Test public void onlyTheActualComputedFlashbackViewRefreshSurvivesWhileZoneMutationStillRetires() {
        var view = new Fixture(true, "G"); var offer = view.activate();
        view.game.getAction().withState(() -> {
            view.actor.updateFlashbackForView(); view.other.updateFlashbackForView();
            view.game.copyLastState();
        });
        assertNotNull(view.game.getReversibleManaAction().current());
        assertEquals(view.restore(offer), ReversibleManaAction.Result.APPLIED);

        var unscoped = new Fixture(false, "2"); var old = unscoped.activate();
        unscoped.game.fireEvent(new forge.game.event.GameEventZone(ZoneType.Flashback,
                unscoped.actor, forge.game.event.EventValueChangeType.Added, null));
        assertEquals(unscoped.restore(old), ReversibleManaAction.Result.REJECTED);
        assertTrue(unscoped.source.isTapped()); assertEquals(unscoped.actor.getManaPool().totalMana(), 2);

        var moved = new Fixture(true, "G"); var stale = moved.activate();
        moved.actor.getZone(ZoneType.Battlefield).remove(moved.source);
        assertEquals(moved.restore(stale), ReversibleManaAction.Result.REJECTED);
        assertEquals(moved.actor.getManaPool().totalMana(), 1);

        var replaced = new Fixture(false,"2"); var replacedOffer = replaced.activate();
        replaced.actor.getZone(ZoneType.Battlefield).setCards(List.of(replaced.source));
        assertEquals(replaced.restore(replacedOffer), ReversibleManaAction.Result.REJECTED,
                "A live-zone setCards remains a rules boundary even when its final membership is unchanged");
    }

    @Test public void keywordOriginsAndForeignLegacyHistoryCannotEnterTheClosedManaRoute() {
        var keyword = new Fixture(false, "2");
        keyword.ability.setKeyword(forge.game.keyword.Keyword.getInstance("Firebending:1"));
        assertNull(keyword.game.getReversibleManaAction().begin(keyword.actor, keyword.ability));
        assertFalse(keyword.source.isTapped()); assertEquals(keyword.actor.getManaPool().totalMana(), 0);

        var owner = new Fixture(false, "2"); owner.activate();
        owner.game.getPhaseHandler().setPriority(owner.other);
        Card source = owner.add("Other mana resource", "Artifact", ZoneType.Battlefield, 104);
        source.setController(owner.other, 0);
        SpellAbility ability = AbilityFactory.getAbility("AB$ Mana | Cost$ T | Produced$ 2", source);
        ability.setActivatingPlayer(owner.other);
        assertNull(owner.game.getReversibleManaAction().begin(owner.other, ability));
        assertTrue(owner.game.getStack().canUndo(owner.actor));
        assertFalse(source.isTapped());
    }

    @Test public void oversizedPrivateGuardsFailClosedBeforeCapturingOrMutatingRules() {
        var text = new Fixture(false, "2");
        text.source.setSVar("Unbounded", "x".repeat(8193));
        assertNull(text.game.getReversibleManaAction().begin(text.actor,text.ability));
        assertFalse(text.source.isTapped()); assertEquals(text.actor.getManaPool().totalMana(),0);
        var history = new Fixture(true,"G");
        for (int i=0; i<1025; i++) history.source.addAbilityActivated(history.ability);
        assertNull(history.game.getReversibleManaAction().begin(history.actor,history.ability));
        assertEquals(history.source.getAbilityActivatedThisGame(history.ability),1025);
        assertFalse(history.source.isTapped());
    }

    @Test public void silentPoolFlagAndLedgerMutationsCannotRestoreAChangedThenRecreatedCut() {
        var pool = new Fixture(false,"2"); var first = pool.activate();
        Mana unit = pool.actor.getManaPool().iterator().next();
        assertTrue(pool.actor.getManaPool().removeManaNoEvent(unit));
        pool.actor.getManaPool().addManaNoEvent(unit);
        assertEquals(pool.restore(first),ReversibleManaAction.Result.REJECTED);
        assertTrue(pool.source.isTapped()); assertEquals(pool.actor.getManaPool().totalMana(),2);

        var flag = new Fixture(true,"G"); var second = flag.activate();
        flag.actor.setTappedLandForManaThisTurn(false); flag.actor.setTappedLandForManaThisTurn(true);
        assertEquals(flag.restore(second),ReversibleManaAction.Result.REJECTED);
        assertTrue(flag.source.isTapped());

        var ledger = new Fixture(false,"2"); var third = ledger.activate();
        var prior = ledger.source.captureManaActivationState();
        ledger.source.addAbilityResolved(ledger.ability);
        ledger.source.restoreManaActivationState(prior);
        assertEquals(ledger.restore(third),ReversibleManaAction.Result.REJECTED);
        assertEquals(ledger.source.getAbilityResolvedThisTurn(ledger.ability),1);
    }

    @Test public void randomStreamCountPreservesEntropyAndCustomProviderStreamsWhileParallelDrawsFence() {
        var previous = MyRandom.getRandom();
        try {
            var expected = new java.util.Random(7721);
            MyRandom.setRandom(new java.util.Random(7721)); var actual = MyRandom.getRandom();
            assertEquals(actual.ints(3).count(),expected.ints(3).count());
            assertEquals(actual.longs(7).count(),expected.longs(7).count());
            assertEquals(actual.doubles(11).count(),expected.doubles(11).count());
            assertEquals(actual.nextLong(),expected.nextLong(),"SIZED count does not consume entropy");
            var untouched = new Fixture(false,"2"); var countOffer = untouched.activate();
            untouched.game.getAction().withState(() -> assertEquals(actual.ints(3).count(),3));
            assertEquals(untouched.restore(countOffer),ReversibleManaAction.Result.APPLIED);
            var parallel = actual.longs(200).parallel();
            var changed = new Fixture(false,"2"); var old = changed.activate();
            changed.game.getAction().withState(parallel::sum);
            assertEquals(changed.restore(old),ReversibleManaAction.Result.REJECTED);
            var closes = new java.util.concurrent.atomic.AtomicInteger();
            MyRandom.setRandom(new java.util.Random(1) {
                @Override public java.util.stream.IntStream ints(long size) {
                    return java.util.stream.IntStream.range(700,700+Math.toIntExact(size)).onClose(closes::incrementAndGet);
                }
            });
            try (var custom = MyRandom.getRandom().ints(3)) { assertEquals(custom.toArray(),new int[]{700,701,702}); }
            assertEquals(closes.get(),1,"The exact provider stream and close handler are retained");
        } finally { MyRandom.setRandom(previous); }
    }

    @Test public void oneRecordOnAFourHundredCardTableReportsBoundedCaptureAndRestoreAllocation() {
        var f = new Fixture(true,"G");
        var seats = new ArrayList<Player>(); seats.add(f.actor); seats.add(f.other);
        for (int seat=2; seat<4; seat++) {
            var player = new Player("seat"+seat,f.game,seat); player.setLife(20,null);
            f.game.getPlayers().add(player); f.game.getRegisteredPlayers().add(player); seats.add(player);
        }
        for (int seat=0; seat<4; seat++) for (int i=seat==0?1:0; i<100; i++) {
            var player = seats.get(seat); var zone = player.getZone(i<10?ZoneType.Battlefield:ZoneType.Library);
            var card = new Card(1000+seat*100+i,f.source.getPaperCard(),f.game);
            card.setOwner(player); card.setName("Resource "+seat+" "+i); card.setType(CardType.parse("Land",false));
            zone.add(card); card.setZone(zone);
        }
        assertEquals(f.game.getCardsInGame().size(),400);
        var bean = (com.sun.management.ThreadMXBean)java.lang.management.ManagementFactory.getThreadMXBean();
        assertTrue(bean.isThreadAllocatedMemorySupported());
        bean.setThreadAllocatedMemoryEnabled(true); long thread = Thread.currentThread().getId();
        f.game.getAction().withState(() -> {
            long bytes = bean.getThreadAllocatedBytes(thread), time = System.nanoTime();
            var capture = f.game.getReversibleManaAction().begin(f.actor,f.ability); assertNotNull(capture);
            long captureNanos = System.nanoTime()-time, captureBytes = bean.getThreadAllocatedBytes(thread)-bytes;
            var tap = (CostTap)f.ability.getPayCosts().getCostParts().stream().filter(CostTap.class::isInstance).findFirst().orElseThrow();
            assertTrue(tap.payAsDecided(f.actor,PaymentDecision.number(1),f.ability,false));
            f.game.getStack().addAndUnfreeze(f.ability); f.game.getReversibleManaAction().settle(capture);
            var offer = f.game.getReversibleManaAction().current(); assertNotNull(offer);
            bytes = bean.getThreadAllocatedBytes(thread); time = System.nanoTime();
            assertEquals(f.restore(offer),ReversibleManaAction.Result.APPLIED);
            long restoreNanos = System.nanoTime()-time, restoreBytes = bean.getThreadAllocatedBytes(thread)-bytes;
            System.out.println("ReversibleManaAction capacity: cards=400 battlefield=40 captureAllocatedBytes="+captureBytes
                    +" captureNanos="+captureNanos+" restoreAllocatedBytes="+restoreBytes+" restoreNanos="+restoreNanos);
        });
        assertFalse(f.source.isTapped()); assertEquals(f.actor.getManaPool().totalMana(),0);
    }
}
