package forge.api;

import com.google.common.eventbus.Subscribe;
import forge.StaticData;
import forge.card.MagicColor;
import forge.card.mana.ManaAtom;
import forge.deck.Deck;
import forge.game.card.Card;
import forge.game.event.EventValueChangeType;
import forge.game.event.GameEventManaPool;
import forge.game.mana.Mana;
import forge.game.mana.ManaCostBeingPaid;
import forge.game.mana.ManaPoolPaymentQuote;
import forge.game.player.Player;
import forge.game.spellability.SpellAbility;
import forge.game.zone.ZoneType;
import forge.gamemodes.match.input.InputPayManaOfCostPayment;
import forge.gui.interfaces.IGuiGame;
import forge.player.PlayerControllerHuman;
import org.testng.annotations.DataProvider;
import org.testng.annotations.Test;

import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Proxy;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;

import static org.testng.Assert.*;

/** Exercises the actual mandatory-payment constructor, not a substitute affordability projection. */
public class MandatoryManaQuoteTest {
    @Test(timeOut = 30000)
    public void aCompleteQuoteKeepsThePoolUntouchedAndDisablesTheActualCancelButton() throws Exception {
        try (var fixture = new Fixture()) {
            HeadlessPlatform.query(fixture.table, () -> {
                var spell = fixture.spell("Iron Myr");
                var source = fixture.add("Wastes", ZoneType.Battlefield);
                fixture.addMana((byte) ManaAtom.COLORLESS, source, null);
                fixture.addMana((byte) ManaAtom.COLORLESS, source, null);
                fixture.addMana(MagicColor.GREEN, source, null);
                var units = fixture.units();
                var cost = new ManaCostBeingPaid(spell.getHostCard().getManaCost());
                assertTrue(fixture.payer().getManaPool().quoteFixedPayment(cost, spell).canPayEntireBill());
                fixture.events.values.clear();

                try (var input = new ConstructorProbe(fixture.controller, cost, spell, fixture.payer())) {
                    assertTrue(input.isPaymentMandatory());
                    assertSame(fixture.payer().getPaidForSA(), spell, "The constructor still installs the real payment context");
                    assertSame(spell.getManaCostBeingPaid(), cost);
                    assertEquals(input.getRemainingManaCost().getGenericManaAmount(), 2);
                    fixture.assertSameUnits(units);
                    assertTrue(fixture.events.values.isEmpty(), "A positive quote must not borrow, refund, or publish pool changes");
                    input.publishPaymentButtons();
                    assertEquals(fixture.cancelEnabled, Boolean.FALSE, "The actual mandatory-payment controls disable Cancel");
                    fixture.assertSameUnits(units);
                    assertTrue(fixture.events.values.isEmpty());
                }
                assertNull(fixture.payer().getPaidForSA());
                assertNull(spell.getManaCostBeingPaid());
                return null;
            });
        }
    }

    @DataProvider public Object[][] unsupportedBills() {
        return new Object[][] {{"Iron Myr", true, 2}, {"Judge's Familiar", false, 1}};
    }

    @Test(dataProvider = "unsupportedBills", timeOut = 30000)
    public void anUnknownRestrictedOrHybridQuoteRetainsTheOrdinaryMandatoryResult(
            String cardName, boolean restricted, int manaCount) throws Exception {
        try (var fixture = new Fixture()) {
            HeadlessPlatform.query(fixture.table, () -> {
                var spell = fixture.spell(cardName);
                var source = fixture.add(restricted ? "Ancient Ziggurat" : "Island", ZoneType.Battlefield);
                var producer = restricted ? source.getManaAbilities().get(0) : null;
                for (int i = 0; i < manaCount; i++) fixture.addMana(MagicColor.BLUE, source, producer);
                var units = fixture.units();
                var cost = new ManaCostBeingPaid(spell.getHostCard().getManaCost());
                var quote = fixture.payer().getManaPool().quoteFixedPayment(cost, spell);
                assertEquals(quote.knowledge(), ManaPoolPaymentQuote.Knowledge.UNKNOWN);
                assertNull(quote.remainingBill(), "The unsupported restriction or hybrid bill has no absence claim");
                fixture.events.values.clear();

                try (var input = new ConstructorProbe(fixture.controller, cost, spell, fixture.payer())) {
                    assertTrue(input.isPaymentMandatory(), "Unknown cannot relax a forced payment that ordinary rules can fund");
                    assertTrue(fixture.events.has(EventValueChangeType.Removed), "The unsupported route still uses the ordinary test-payment check");
                    assertTrue(fixture.events.has(EventValueChangeType.Added), "The legacy check refunds the exact borrowed units");
                    fixture.assertSameUnits(units);
                    assertFalse(cost.isPaid(), "The mandatory check never pays the actual input bill");
                    input.publishPaymentButtons();
                    assertEquals(fixture.cancelEnabled, Boolean.FALSE);
                }
                return null;
            });
        }
    }

    @Test(timeOut = 30000)
    public void unknownRestrictedManaDoesNotForceAnIllegalNoncreaturePayment() throws Exception {
        try (var fixture = new Fixture()) {
            HeadlessPlatform.query(fixture.table, () -> {
                var spell = fixture.spell("Opt");
                var source = fixture.add("Ancient Ziggurat", ZoneType.Battlefield);
                fixture.addMana(MagicColor.BLUE, source, source.getManaAbilities().get(0));
                var units = fixture.units();
                var cost = new ManaCostBeingPaid(spell.getHostCard().getManaCost());
                assertEquals(fixture.payer().getManaPool().quoteFixedPayment(cost, spell).knowledge(), ManaPoolPaymentQuote.Knowledge.UNKNOWN);
                fixture.events.values.clear();

                try (var input = new ConstructorProbe(fixture.controller, cost, spell, fixture.payer())) {
                    assertFalse(input.isPaymentMandatory(), "The ordinary fallback still honors the creature-spell restriction");
                    input.publishPaymentButtons();
                    assertEquals(fixture.cancelEnabled, Boolean.TRUE);
                    fixture.assertSameUnits(units);
                    assertTrue(fixture.events.values.isEmpty(), "An ineligible unit is never borrowed");
                    assertFalse(cost.isPaid());
                }
                return null;
            });
        }
    }

    public static final class ManaEvents {
        final List<GameEventManaPool> values = new ArrayList<>();
        @Subscribe public void event(GameEventManaPool event) { values.add(event); }
        boolean has(EventValueChangeType mode) { return values.stream().anyMatch(event -> event.mode() == mode); }
    }

    private static final class Fixture implements AutoCloseable {
        final MatchTable table;
        final PlayerControllerHuman controller;
        final IGuiGame originalGui;
        final ManaEvents events = new ManaEvents();
        Boolean cancelEnabled;

        Fixture() throws Exception {
            var resources = Path.of("../forge-gui/res");
            if (StaticData.instance() == null) EngineResources.load(resources);
            table = new MatchTable("Constructed", List.of(MatchTable.human(new Deck(), "Constructed", "Payer"),
                    MatchTable.human(new Deck(), "Constructed", "Opponent")), resources, Files.createTempDirectory("mandatory-mana-quote"));
            HeadlessPlatform.activate(table);
            controller = (PlayerControllerHuman) payer().getController();
            originalGui = controller.getGui();
            controller.setGui((IGuiGame) Proxy.newProxyInstance(IGuiGame.class.getClassLoader(), new Class<?>[]{IGuiGame.class},
                    (proxy, method, args) -> {
                        if (method.getName().equals("updateButtons") && args.length == 6) cancelEnabled = (boolean) args[4];
                        try { return method.invoke(originalGui, args); }
                        catch (InvocationTargetException error) { throw error.getCause(); }
                    }));
            table.game.subscribeToEvents(events);
        }
        Player payer() { return table.game.getRegisteredPlayers().get(0); }
        Card add(String name, ZoneType zone) {
            var card = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard(name), payer());
            payer().getZone(zone).add(card);
            return card;
        }
        SpellAbility spell(String name) {
            var ability = add(name, ZoneType.Hand).getSpellAbilities().stream().filter(SpellAbility::isSpell).findFirst().orElseThrow();
            ability.setActivatingPlayer(payer());
            ability.getPayCosts().setMandatory(true);
            return ability;
        }
        void addMana(byte color, Card source, SpellAbility producer) {
            payer().getManaPool().addMana(new Mana(color, source, producer == null ? null : producer.getManaPart(), payer()));
        }
        List<Mana> units() {
            var result = new ArrayList<Mana>();
            payer().getManaPool().forEach(result::add);
            return result;
        }
        void assertSameUnits(List<Mana> before) {
            var after = units();
            assertEquals(after.size(), before.size());
            for (int i = 0; i < before.size(); i++) assertSame(after.get(i), before.get(i), "Exact pool units, provenance and lane order remain intact");
        }
        @Override public void close() {
            table.game.unsubscribeFromEvents(events);
            controller.setGui(originalGui);
            table.close();
        }
    }

    /** Exposes only the normal button publication and constructor-state cleanup, without Auto planning or a synthetic answer. */
    private static final class ConstructorProbe extends InputPayManaOfCostPayment implements AutoCloseable {
        ConstructorProbe(PlayerControllerHuman controller, ManaCostBeingPaid cost, SpellAbility ability, Player payer) {
            super(controller, cost, ability, payer, null, false);
        }
        void publishPaymentButtons() { updateButtons(); }
        @Override public void close() { onStop(); }
    }
}
