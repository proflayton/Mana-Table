package forge.api;

import forge.StaticData;
import forge.deck.Deck;
import forge.game.ability.AbilityFactory;
import forge.game.card.Card;
import forge.game.card.CardCopyService;
import forge.game.cost.Cost;
import forge.game.cost.CostPayment;
import forge.game.cost.CostTap;
import forge.game.cost.CostTapType;
import forge.game.cost.CostUntap;
import forge.game.cost.PaymentDecision;
import forge.game.player.Player;
import forge.game.spellability.SpellAbility;
import forge.game.zone.ZoneType;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.List;

import static org.testng.Assert.*;

/** Commit checks use live physical resources without starting a game or choosing costs for a player. */
public class TapCostExecutionTest {
    @Test public void sourceTapRechecksCurrentUntappedReadyBattlefieldOccurrence() throws Exception {
        try (var fixture = new Fixture()) {
            var source = fixture.add("Grizzly Bears", 0);
            var ability = fixture.ability(source, "T");
            var tap = new CostTap();
            assertTrue(tap.canPay(ability, fixture.actor(), false));
            assertTrue(tap.payAsDecided(fixture.actor(), PaymentDecision.number(1), ability, false));
            assertTrue(source.isTapped());
            assertFalse(tap.payAsDecided(fixture.actor(), PaymentDecision.number(1), ability, false),
                    "An already spent source cannot satisfy another source tap");

            source.setTapped(false); source.setSickness(true);
            assertFalse(tap.canPay(ability, fixture.actor(), false));
            assertFalse(tap.payAsDecided(fixture.actor(), PaymentDecision.number(1), ability, false));
            assertFalse(source.isTapped(), "A failed readiness check cannot consume the source");

            source.setSickness(false);
            source.getOwner().getZone(ZoneType.Battlefield).remove(source);
            source.getOwner().getZone(ZoneType.Hand).add(source);
            assertFalse(tap.canPay(ability, fixture.actor(), false));
            assertFalse(tap.payAsDecided(fixture.actor(), PaymentDecision.number(1), ability, false));
            assertFalse(source.isTapped(), "The former battlefield source cannot be tapped from its new zone");
        }
    }

    @Test public void typedTapRejectsReservedSourceAndInvalidWholeSelectionsBeforeMutation() throws Exception {
        try (var fixture = new Fixture()) {
            var source = fixture.add("Grizzly Bears", 0);
            var creature = fixture.add("Llanowar Elves", 0);
            var land = fixture.add("Forest", 0);
            var foreign = fixture.add("Grizzly Bears", 1);
            var ability = fixture.ability(source, "T tapXType<1/Creature>");
            var tap = ability.getPayCosts().getCostParts().stream().filter(CostTapType.class::isInstance)
                    .map(CostTapType.class::cast).findFirst().orElseThrow();
            assertEquals(tap.getEligibleCards(ability, fixture.actor()).size(), 1);
            assertSame(tap.getEligibleCards(ability, fixture.actor()).getFirst(), creature);
            assertFalse(tap.payAsDecided(fixture.actor(), PaymentDecision.card(source), ability, false));
            assertFalse(tap.payAsDecided(fixture.actor(), PaymentDecision.card(land), ability, false));
            assertFalse(tap.payAsDecided(fixture.actor(), PaymentDecision.card(foreign), ability, false));
            assertFalse(source.isTapped()); assertFalse(creature.isTapped()); assertFalse(land.isTapped());
            assertFalse(foreign.isTapped(), "A read-only foreign permanent is not an ordinary controlled tap cost");

            creature.setTapped(true);
            assertFalse(tap.payAsDecided(fixture.actor(), PaymentDecision.card(creature), ability, false));
            creature.setTapped(false);
            var pair = new CostTapType("2", "Creature", null, true);
            assertFalse(pair.payAsDecided(fixture.actor(), PaymentDecision.card(creature), ability, false));
            assertFalse(pair.payAsDecided(fixture.actor(), PaymentDecision.card(List.of(creature, land)), ability, false));
            assertFalse(pair.payAsDecided(fixture.actor(), PaymentDecision.card(List.of(creature, creature)), ability, false));
            var other = fixture.add("Grizzly Bears", 0);
            var stale = CardCopyService.getLKICopy(other);
            assertNotSame(stale, other);
            assertFalse(pair.payAsDecided(fixture.actor(), PaymentDecision.card(List.of(creature, stale)), ability, false));
            assertFalse(creature.isTapped()); assertFalse(other.isTapped(),
                    "Neither an invalid later member nor a stale same-ID copy may partially pay a list cost");

            assertTrue(tap.payAsDecided(fixture.actor(), PaymentDecision.card(creature), ability, false));
            assertTrue(creature.isTapped()); assertFalse(source.isTapped());
            assertTrue(new CostTap().payAsDecided(fixture.actor(), PaymentDecision.number(1), ability, false));
            assertTrue(source.isTapped(), "The distinct source remains available for its own source tap");
        }
    }

    @Test public void currentRemainingPaymentOverridesConstructionTimeSourceReservation() throws Exception {
        try (var fixture = new Fixture()) {
            var source = fixture.add("Grizzly Bears", 0);
            var ability = fixture.ability(source, "tapXType<1/Creature>");
            var typed = new CostTapType("1", "Creature", null, false);
            var addedSourceTap = new Cost("T tapXType<1/Creature>", true);
            var payment = new CostPayment(addedSourceTap, ability);
            fixture.table.game.costPaymentStack.push(typed, payment);
            try {
                assertFalse(typed.getEligibleCards(ability, fixture.actor()).contains(source),
                        "An actual unpaid added source tap reserves the source even when the parsed typed part did not");
                assertFalse(typed.payAsDecided(fixture.actor(), PaymentDecision.card(source), ability, false));
                var inner = new Cost("tapXType<1/Creature>", true);
                fixture.table.game.costPaymentStack.push(typed, new CostPayment(inner, ability));
                try {
                    assertTrue(typed.getEligibleCards(ability, fixture.actor()).contains(source),
                            "The nearest same-ability payment owns the current remaining commitment, not an older recursive payment");
                } finally { fixture.table.game.costPaymentStack.pop(); }
                assertFalse(typed.getEligibleCards(ability, fixture.actor()).contains(source),
                        "Returning to the outer payment restores its own still-unpaid source commitment");
                addedSourceTap.getCostParts().add(new CostUntap());
                assertTrue(typed.getEligibleCards(ability, fixture.actor()).contains(source),
                        "A pending untap leaves payment ordering available instead of declaring two taps impossible");
                assertFalse(source.isTapped(), "Eligibility reads neither pay nor reorder the outstanding parts");
            } finally { fixture.table.game.costPaymentStack.pop(); }

            var cachedReservation = new CostTapType("1", "Creature", null, true);
            var remaining = new Cost("tapXType<1/Creature>", true);
            fixture.table.game.costPaymentStack.push(cachedReservation, new CostPayment(remaining, ability));
            try {
                assertTrue(cachedReservation.getEligibleCards(ability, fixture.actor()).contains(source),
                        "A current remaining bill without a source tap releases its earlier construction-time reservation");
                assertTrue(cachedReservation.payAsDecided(fixture.actor(), PaymentDecision.card(source), ability, false));
                assertTrue(source.isTapped());
            } finally { fixture.table.game.costPaymentStack.pop(); }
        }
    }

    @Test public void typedTapCanUseSourceWithoutAnotherTapAndExplicitOriginalHostRemainsDistinct() throws Exception {
        try (var fixture = new Fixture()) {
            var source = fixture.add("Grizzly Bears", 0);
            var ability = fixture.ability(source, "tapXType<1/Creature>");
            var ordinary = new CostTapType("1", "Creature", null, false);
            assertTrue(ordinary.getEligibleCards(ability, fixture.actor()).contains(source));
            assertTrue(ordinary.payAsDecided(fixture.actor(), PaymentDecision.card(source), ability, false));
            source.setTapped(false);

            var original = fixture.add("Fishing Pole", 1);
            var granted = fixture.ability(original, "T tapXType<1/OriginalHost>");
            granted.setHostCard(source); granted.setActivatingPlayer(fixture.actor());
            assertSame(granted.getOriginalHost(), original);
            var explicit = new CostTapType("1", "OriginalHost", null, true);
            assertEquals(explicit.getEligibleCards(granted, fixture.actor()).size(), 1);
            assertSame(explicit.getEligibleCards(granted, fixture.actor()).getFirst(), original,
                    "Explicit OriginalHost names its current physical card, distinct from the granted ability's source");
            assertTrue(explicit.payAsDecided(fixture.actor(), PaymentDecision.card(original), granted, false));
            assertTrue(original.isTapped()); assertFalse(source.isTapped());
            assertTrue(new CostTap().payAsDecided(fixture.actor(), PaymentDecision.number(1), granted, false));
            assertTrue(source.isTapped());

            original.setTapped(false);
            original.getOwner().getZone(ZoneType.Battlefield).remove(original);
            original.getOwner().getZone(ZoneType.Hand).add(original);
            assertTrue(explicit.getEligibleCards(granted, fixture.actor()).isEmpty());
            assertFalse(explicit.payAsDecided(fixture.actor(), PaymentDecision.card(original), granted, false));
            assertFalse(original.isTapped(), "OriginalHost is not permission to tap a former battlefield occurrence");
        }
    }

    @Test public void typedTapValidatesSharedTypesAndPowerBeforeTappingAnyMember() throws Exception {
        try (var fixture = new Fixture()) {
            var source = fixture.add("Sol Ring", 0);
            var bear = fixture.add("Grizzly Bears", 0);
            var elf = fixture.add("Llanowar Elves", 0);
            var ability = fixture.ability(source, "tapXType<2/Creature.sharesCreatureTypeWith>");
            var matching = new CostTapType("2", "Creature.sharesCreatureTypeWith", null, false);
            assertFalse(matching.payAsDecided(fixture.actor(), PaymentDecision.card(List.of(bear, elf)), ability, false));
            var power = new CostTapType("Any", "Creature+withTotalPowerGE3", null, false);
            assertFalse(power.payAsDecided(fixture.actor(), PaymentDecision.card(bear), ability, false));
            assertFalse(bear.isTapped()); assertFalse(elf.isTapped());
            assertTrue(power.payAsDecided(fixture.actor(), PaymentDecision.card(List.of(bear, elf)), ability, false));
            assertTrue(bear.isTapped()); assertTrue(elf.isTapped());
        }
    }

    private static final class Fixture implements AutoCloseable {
        final MatchTable table;
        Fixture() throws Exception {
            var resources = Path.of("../forge-gui/res");
            if (StaticData.instance() == null) EngineResources.load(resources);
            table = new MatchTable("Constructed", List.of(MatchTable.human(new Deck(), "Constructed", "Actor"),
                    MatchTable.human(new Deck(), "Constructed", "Other")), resources, Files.createTempDirectory("tap-cost-execution"));
            HeadlessPlatform.activate(table);
        }
        Player actor() { return table.game.getRegisteredPlayers().get(0); }
        Card add(String name, int seat) {
            var owner = table.game.getRegisteredPlayers().get(seat);
            var card = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard(name), owner);
            owner.getZone(ZoneType.Battlefield).add(card); card.setSickness(false); return card;
        }
        SpellAbility ability(Card source, String cost) {
            var ability = AbilityFactory.getAbility("AB$ Draw | Cost$ " + cost + " | NumCards$ 1", source);
            ability.setActivatingPlayer(actor()); return ability;
        }
        @Override public void close() { table.close(); }
    }
}
