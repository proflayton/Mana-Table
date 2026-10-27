package forge.api;

import forge.StaticData;
import forge.card.MagicColor;
import forge.deck.Deck;
import forge.game.card.Card;
import forge.game.mana.Mana;
import forge.game.player.Player;
import forge.game.player.PlayerController;
import forge.game.spellability.SpellAbility;
import forge.game.zone.ZoneType;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.*;

import static org.testng.Assert.*;

/** Announcement facts from real scripts, without spending or choosing resources. */
public class NumericDecisionProjectionTest {
    @Test(timeOut = 90000)
    public void realVariableCostsPreserveMultiplicityAndAlternativeRoutesShareOnePhysicalSource() throws Exception {
        try (var fixture = new Fixture()) {
            var walker = fixture.spell("Hangarback Walker");
            var krasis = fixture.spell("Hydroid Krasis");
            var pool = fixture.add("Breeding Pool", ZoneType.Battlefield, 0);
            var ring = fixture.add("Sol Ring", ZoneType.Battlefield, 0);
            var forest = fixture.add("Forest", ZoneType.Battlefield, 0);
            var originals = new ArrayList<SpellAbility>();
            for (var source : List.of(pool, ring, forest)) for (var ability : source.getManaAbilities()) {
                ability.setActivatingPlayer(fixture.opponent()); originals.add(ability);
            }
            var before = fixture.table.game.getView().getTurn();
            var numeric = fixture.describe(walker);
            assertEquals(numeric.get("purpose"), "AnnounceX");
            assertEquals(numeric.get("sourceCardId"), fixture.id(walker.getHostCard()));
            assertEquals(numeric.get("manaCostKnowledge"), "Unknown", "Printed costs do not certify the final payment bill");
            assertEquals(symbols(numeric), List.of("{X}", "{X}"), "XX is two investments of the same announced value");
            assertEquals(symbols(fixture.describe(krasis)), List.of("{X}", "{G}", "{U}"));
            var funding = map(numeric.get("funding"));
            assertEquals(funding.get("knowledge"), "Unknown", "The visible battlefield subset cannot prove absent external funding");
            assertEquals(map(funding.get("pool")), Map.of());
            var sources = list(funding.get("sources"));
            assertEquals(sources.size(), 3);
            assertEquals(sources.stream().filter(source -> source.get("sourceCardId").equals(fixture.id(pool))).count(), 1L);
            var poolRoutes = routes(funding, fixture.id(pool));
            assertEquals(poolRoutes.size(), 2, "Two basic-land-type abilities compete for one untapped physical land");
            assertEquals(poolRoutes.stream().map(NumericDecisionProjectionTest::bundles).toList(),
                    List.of(List.of(Map.of("colors", Map.of("U", 1))), List.of(Map.of("colors", Map.of("G", 1)))));
            assertEquals(bundles(routes(funding, fixture.id(ring)).get(0)), List.of(Map.of("colors", Map.of("C", 2))));
            for (var source : sources) for (var route : list(source.get("routes"))) assertEquals(route.get("eligibility"), "Known");
            assertEquals(fixture.describe(walker), numeric, "Repeated projection preserves the same facts");
            assertTrue(originals.stream().allMatch(ability -> ability.getActivatingPlayer() == fixture.opponent()),
                    "The live abilities must not inherit the observing actor from an eligibility probe");
            assertTrue(originals.stream().allMatch(ability -> ability.getManaPart().getExpressChoice().isEmpty()));
            assertTrue(List.of(pool, ring, forest).stream().noneMatch(Card::isTapped));
            assertEquals(fixture.actor().getManaPool().totalMana(), 0);
            assertEquals(fixture.table.game.getView().getTurn(), before);
            assertEquals(walker.getXManaCostPaid(), null, "Reading an announcement cannot announce X");
        }
    }

    @Test(timeOut = 90000)
    public void currentTapResourcesExcludeUnavailableAndPrivateOccurrencesWithoutInventingPrerequisiteFunding() throws Exception {
        try (var fixture = new Fixture()) {
            var spell = fixture.spell("Hangarback Walker");
            var forest = fixture.add("Forest", ZoneType.Battlefield, 0);
            var tapped = fixture.add("Island", ZoneType.Battlefield, 0); tapped.setTapped(true);
            var sick = fixture.add("Birds of Paradise", ZoneType.Battlefield, 0); sick.setSickness(true);
            var opponent = fixture.add("Mountain", ZoneType.Battlefield, 1);
            var spirit = fixture.add("Elvish Spirit Guide", ZoneType.Hand, 0);
            var signet = fixture.add("Azorius Signet", ZoneType.Battlefield, 0);
            fixture.actor().getController().getFullControl().add(PlayerController.FullControlFlag.AllowPaymentStartWithMissingResources);
            var funding = map(fixture.describe(spell).get("funding"));
            var sources = list(funding.get("sources"));
            var ids = sources.stream().map(source -> source.get("sourceCardId")).toList();
            assertTrue(ids.contains(fixture.id(forest)));
            assertFalse(ids.contains(fixture.id(tapped)), "Full control cannot turn a tapped land into a certain tap resource");
            assertFalse(ids.contains(fixture.id(sick)), "A summoning-sick mana creature cannot witness tap funding");
            assertFalse(ids.contains(fixture.id(opponent)), "An opponent's producer is not the actor's funding");
            assertFalse(ids.contains(fixture.id(spirit)), "External routes stay omitted rather than inventing executable offers");
            assertEquals(funding.get("knowledge"), "Unknown");
            var filter = routes(funding, fixture.id(signet)).get(0);
            assertEquals(filter.get("eligibility"), "Unknown", "A paid filter needs a coupled funding assignment");
            assertEquals(map(filter.get("production")).get("knowledge"), "Known", "Nominal output and available activation are distinct facts");
            assertTrue(list(filter.get("costs")).stream().anyMatch(cost -> cost.get("kind").equals("Mana")
                    && Objects.equals(cost.get("amount"), 1)));
            var hidden = fixture.add("Gilded Lotus", ZoneType.Battlefield, 0); hidden.turnFaceDown(true);
            var concealed = fixture.describe(spell);
            assertFalse(list(map(concealed.get("funding")).get("sources")).stream()
                    .anyMatch(source -> source.get("sourceCardId").equals(fixture.id(hidden))));
            assertFalse(new com.google.gson.Gson().toJson(concealed).contains("Gilded Lotus"));
            assertEquals(fixture.actor().getManaPool().totalMana(), 0);
            assertFalse(forest.isTapped()); assertTrue(tapped.isTapped()); assertTrue(sick.isSick());
        }
    }

    @Test(timeOut = 90000)
    public void restrictedPoolUnitsAndSpellSourceRestrictionsNeverBecomeCertainGenericFunding() throws Exception {
        try (var fixture = new Fixture()) {
            var spell = fixture.spell("Hangarback Walker");
            var forest = fixture.add("Forest", ZoneType.Battlefield, 0);
            var ziggurat = fixture.add("Ancient Ziggurat", ZoneType.Battlefield, 0);
            var plain = forest.getManaAbilities().get(0);
            var restricted = ziggurat.getManaAbilities().get(0);
            fixture.actor().getManaPool().addManaNoEvent(new Mana(MagicColor.GREEN, forest, plain.getManaPart(), fixture.actor()));
            fixture.actor().getManaPool().addManaNoEvent(new Mana(MagicColor.BLUE, ziggurat, restricted.getManaPart(), fixture.actor()));
            var funding = map(fixture.describe(spell).get("funding"));
            assertEquals(map(funding.get("pool")), Map.of("G", 1), "Unknown spend restrictions are not ordinary floating mana");
            assertEquals(routes(funding, fixture.id(ziggurat)).get(0).get("eligibility"), "Unknown");
            var superion = fixture.spell("Myr Superion");
            assertFalse(superion.allowsPayingWithShard(forest, MagicColor.GREEN), "The real script has a static source restriction");
            var restrictedSpell = map(fixture.describe(superion).get("funding"));
            assertEquals(map(restrictedSpell.get("pool")), Map.of());
            assertTrue(list(restrictedSpell.get("sources")).stream().flatMap(source -> list(source.get("routes")).stream())
                    .allMatch(route -> route.get("eligibility").equals("Unknown")),
                    "Source-dependent spell restrictions cannot be checked using an unannounced X or unrelated bill");
            assertEquals(fixture.actor().getManaPool().totalMana(), 2, "Projection neither consumes nor transforms restricted units");
            assertFalse(forest.isTapped()); assertFalse(ziggurat.isTapped());
            spell.setActivatingPlayer(fixture.opponent());
            var controlledAnnouncement = map(fixture.describe(spell).get("funding"));
            assertEquals(controlledAnnouncement.get("knowledge"), "Unknown");
            assertEquals(map(controlledAnnouncement.get("pool")), Map.of());
            assertTrue(list(controlledAnnouncement.get("sources")).isEmpty(),
                    "A player controlling another seat's decision cannot fund that announcement with their own resources");
        }
    }

    private static List<String> symbols(Map<String, Object> numeric) {
        return list(numeric.get("costs")).stream().filter(cost -> cost.get("kind").equals("Mana"))
                .flatMap(cost -> ((List<?>)cost.get("manaSymbols")).stream()).map(Object::toString).toList();
    }
    private static List<Map<String, Object>> routes(Map<String, Object> funding, String id) {
        return list(list(funding.get("sources")).stream().filter(source -> source.get("sourceCardId").equals(id)).findFirst().orElseThrow().get("routes"));
    }
    private static List<Map<String, Object>> bundles(Map<String, Object> route) { return list(map(route.get("production")).get("alternatives")); }
    @SuppressWarnings("unchecked") private static Map<String, Object> map(Object value) { return (Map<String, Object>)value; }
    @SuppressWarnings("unchecked") private static List<Map<String, Object>> list(Object value) { return (List<Map<String, Object>>)value; }

    private static final class Fixture implements AutoCloseable {
        private final MatchTable table;
        Fixture() throws Exception {
            var resources = Path.of("../forge-gui/res");
            if (StaticData.instance() == null) EngineResources.load(resources);
            table = new MatchTable("Constructed", List.of(MatchTable.human(new Deck(), "Constructed", "Actor"),
                    MatchTable.human(new Deck(), "Constructed", "Opponent")), resources, Files.createTempDirectory("numeric-projection"));
            HeadlessPlatform.activate(table);
        }
        Player actor() { return table.game.getRegisteredPlayers().get(0); }
        Player opponent() { return table.game.getRegisteredPlayers().get(1); }
        Card add(String name, ZoneType zone, int seat) {
            var card = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard(name), table.game.getRegisteredPlayers().get(seat));
            card.getOwner().getZone(zone).add(card); return card;
        }
        SpellAbility spell(String name) {
            var card = add(name, ZoneType.Hand, 0);
            var spell = card.getSpellAbilities().stream().filter(SpellAbility::isSpell).findFirst().orElseThrow();
            spell.setActivatingPlayer(actor()); return spell;
        }
        String id(Card card) { return table.identities.id(card.getView()); }
        Map<String, Object> describe(SpellAbility ability) {
            return table.game.getAction().readState(() -> NumericDecisionProjection.describe(ability, "X", actor(), table.identities::id));
        }
        @Override public void close() { table.close(); }
    }
}
