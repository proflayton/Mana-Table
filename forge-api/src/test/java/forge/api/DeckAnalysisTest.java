package forge.api;

import forge.card.CardRarity;
import forge.card.CardRules;
import forge.item.PaperCard;
import org.testng.annotations.Test;
import java.util.List;
import static org.testng.Assert.*;

public class DeckAnalysisTest {
    static PaperCard card(String name, String mana, String type, String text) {
        return new PaperCard(new CardRules.Reader().readCard(List.of("Name:" + name, "ManaCost:" + mana, "Types:" + type, "Oracle:" + text)), "TST", CardRarity.Common);
    }
    static DeckEditor.Entry entry(PaperCard card, String section, int quantity) { return new DeckEditor.Entry(section, CardCatalog.describe(card), quantity); }
    static DeckEditor.Snapshot deck(DeckEditor.Entry... entries) { return new DeckEditor.Snapshot("Analysis test", 7, List.of(entries), false, false); }
    static DeckAnalysis.Profile profile(String strategy, String pace, List<String> protectedCards, List<String> pool) {
        return new DeckAnalysis.Profile("", strategy, "Execute the chosen engine", "", pace, 0, protectedCards, pool);
    }
    static DeckAnalysis.Report analyze(List<PaperCard> catalog, DeckEditor.Snapshot deck, String format, DeckAnalysis.Profile profile) {
        return DeckAnalysis.analyze(new CardCatalog(catalog), deck, format, "test", "version", profile, null);
    }
    private static PaperCard toph() { return card("Toph, the First Metalbender", "1 R G W", "Legendary Creature Human Warrior Ally", "Nontoken artifacts you control are lands in addition to their other types. At the beginning of your end step, earthbend 2."); }
    private static PaperCard student() { return card("Earthbending Student", "2 G", "Creature Human Warrior Ally", "When this creature enters, earthbend 2. Land creatures you control have vigilance."); }
    private static PaperCard ashaya() { return card("Ashaya, Soul of the Wild", "3 G G", "Legendary Creature Elemental", "Nontoken creatures you control are Forest lands in addition to their other types."); }
    private static PaperCard altar() { return card("Phyrexian Altar", "3", "Artifact", "Sacrifice a creature: Add one mana of any color."); }

    @Test public void castDrawKeepsItsConditionsAndLandsRetainTheirOtherRoles() {
        var bestiary = card("Lifecrafter's Bestiary", "3", "Artifact", "At the beginning of your upkeep, scry 1. Whenever you cast a creature spell, you may pay {G}. If you do, draw a card.");
        var draw = DeckAnalysis.functions(CardCatalog.describe(bestiary)).stream().filter(f -> f.role().equals("Draw")).findFirst().orElseThrow();
        assertEquals(draw.event(), "cast"); assertTrue(draw.requires().stream().anyMatch(r -> r.contains("Additional mana")));
        var land = card("Draw land", "no cost", "Land", "{T}: Add {C}. {2}, {T}: Draw a card.");
        var roles = DeckAnalysis.functions(CardCatalog.describe(land)).stream().map(DeckAnalysis.Function::role).toList();
        assertTrue(roles.containsAll(List.of("Lands", "Draw")));
        assertFalse(roles.contains("Ramp"), "Land mana production is not counted as additional ramp");
    }
    @Test public void opponentsDrawingAndDrawTriggerConditionsAreNotOurCardAdvantage() {
        var dreams = card("Underworld Dreams", "B B B", "Enchantment", "Whenever an opponent draws a card, Underworld Dreams deals 1 damage to that player.");
        var corrosion = card("Psychic Corrosion", "2 U", "Enchantment", "Whenever you draw a card, each opponent mills two cards.");
        assertFalse(DeckAnalysis.functions(CardCatalog.describe(dreams)).stream().anyMatch(f -> f.role().equals("Draw")));
        assertFalse(DeckAnalysis.functions(CardCatalog.describe(corrosion)).stream().anyMatch(f -> f.role().equals("Draw")));
        var sentinel = card("Esper Sentinel", "W", "Artifact Creature Human Soldier", "Whenever an opponent casts their first noncreature spell each turn, draw a card unless that player pays {X}, where X is Esper Sentinel's power.");
        var draw = DeckAnalysis.functions(CardCatalog.describe(sentinel)).stream().filter(f -> f.role().equals("Draw")).findFirst().orElseThrow();
        assertEquals(draw.event(), "opponent-cast");
        assertTrue(draw.requires().stream().anyMatch(r -> r.contains("prevent")));
    }
    @Test public void scheduledAndBoardDependentDrawAreRecognizedWithoutClaimingImmediateAccess() {
        var library = card("Sylvan Library", "1 G", "Enchantment", "At the beginning of your draw step, you may draw two additional cards.");
        var march = card("Last March of the Ents", "6 G G", "Sorcery", "Draw cards equal to the greatest toughness among creatures you control, then put any number of creature cards from your hand onto the battlefield.");
        var scheduled = DeckAnalysis.functions(CardCatalog.describe(library)).stream().filter(f -> f.role().equals("Draw")).findFirst().orElseThrow();
        // The additional-card wording is also a common scheduled draw instruction.
        var scheduledSimple = card("Scheduled draw", "2 U", "Enchantment", "At the beginning of your draw step, draw a card.");
        assertEquals(DeckAnalysis.functions(CardCatalog.describe(scheduledSimple)).stream().filter(f -> f.role().equals("Draw")).findFirst().orElseThrow().event(), "scheduled");
        var variable = DeckAnalysis.functions(CardCatalog.describe(march)).stream().filter(f -> f.role().equals("Draw")).findFirst().orElseThrow();
        assertFalse(variable.requires().isEmpty());
        assertEquals(scheduled.event(), "scheduled");
    }
    @Test public void selfRenewingEarthbendLoopProtectsItsActualPieces() {
        var ashaya = ashaya(); var student = student(); var altar = altar();
        var tremors = card("Impact Tremors", "1 R", "Enchantment", "Whenever a creature you control enters, Impact Tremors deals 1 damage to each opponent.");
        var cards = List.of(ashaya, student, altar, tremors);
        var report = analyze(cards, deck(cards.stream().map(c -> entry(c, "Main", 1)).toArray(DeckEditor.Entry[]::new)), "Constructed", profile("combo", "fast", List.of(), List.of()));
        var engine = report.engines().stream().filter(e -> e.missing().isEmpty()).findFirst().orElseThrow();
        assertTrue(engine.cards().containsAll(List.of("Ashaya, Soul of the Wild", "Earthbending Student", "Phyrexian Altar")));
        assertEquals(engine.payoffs(), List.of("Impact Tremors"));
        assertTrue(engine.steps().stream().anyMatch(s -> s.contains("itself again")));
        assertTrue(report.cards().stream().filter(c -> !c.name().equals("Impact Tremors")).allMatch(DeckAnalysis.CardReview::protectedCard));
        assertFalse(report.cards().stream().filter(c -> c.name().equals("Impact Tremors")).findFirst().orElseThrow().engineIds().isEmpty());
    }
    @Test public void incompleteLoopIsNotACompleteEngineAndSideboardCannotSupplyItsMissingPiece() {
        var ashaya = ashaya(); var student = student(); var altar = altar(); var cards = List.of(ashaya, student, altar);
        var report = analyze(cards, deck(entry(ashaya, "Sideboard", 1), entry(student, "Main", 1), entry(altar, "Main", 1)), "Constructed", DeckAnalysis.Profile.defaults());
        assertFalse(report.engines().isEmpty());
        assertTrue(report.engines().stream().allMatch(e -> e.missing().contains("Ashaya, Soul of the Wild")));
        assertFalse(report.cards().stream().anyMatch(DeckAnalysis.CardReview::protectedCard));
    }
    @Test public void oneShotArtifactReturnDoesNotInventAnInfiniteLoop() {
        var toph = toph(); var petal = card("Lotus Petal", "0", "Artifact", "{T}, Sacrifice Lotus Petal: Add one mana of any color.");
        var altar = altar(); var cards = List.of(toph, petal, altar);
        var report = analyze(cards, deck(entry(toph, "Commander", 1), entry(petal, "Main", 1), entry(altar, "Main", 1)), "Commander", DeckAnalysis.Profile.defaults());
        assertTrue(report.engines().isEmpty());
        assertTrue(report.findings().stream().anyMatch(f -> f.id().equals("earthbend-renewal")));
    }
    @Test public void partialEnginesIdentifyMissingOutletsAndLatticeCanSupplyTheConversion() {
        var ashaya = ashaya(); var student = student(); var altar = altar();
        var partial = analyze(List.of(ashaya, student, altar), deck(entry(ashaya, "Main", 1), entry(student, "Main", 1)), "Constructed", DeckAnalysis.Profile.defaults());
        assertTrue(partial.engines().stream().anyMatch(e -> e.missing().equals(List.of("Phyrexian Altar"))));
        assertTrue(partial.engines().stream().noneMatch(e -> e.missing().isEmpty()));
        var toph = toph(); var lattice = card("Mycosynth Lattice", "6", "Artifact", "All permanents are artifacts in addition to their other types.");
        var full = analyze(List.of(toph, lattice, student, altar), deck(entry(toph, "Commander", 1), entry(lattice, "Main", 1), entry(student, "Main", 1), entry(altar, "Main", 1)), "Commander", DeckAnalysis.Profile.defaults());
        assertTrue(full.engines().stream().anyMatch(e -> e.missing().isEmpty() && e.cards().contains("Mycosynth Lattice")));
    }
    @Test public void restrictedAdditionsNeverExceedOneCopyAndInteractionSwapsExplainCoverageLoss() {
        var murder = card("Murder", "1 B B", "Instant", "Destroy target creature.");
        var throat = card("Go for the Throat", "1 B", "Instant", "Destroy target nonartifact creature.");
        var cards = List.of(murder, throat);
        var report = analyze(cards, deck(entry(murder, "Main", 1)), "Constructed", DeckAnalysis.Profile.defaults());
        assertEquals(report.recommendations().size(), 1);
        assertTrue(report.recommendations().get(0).tradeoff().contains("artifact creatures"));
        var restricted = DeckAnalysis.analyze(new CardCatalog(cards), deck(entry(murder, "Main", 1), entry(throat, "Sideboard", 1)), "Constructed", "test", "v", DeckAnalysis.Profile.defaults(), (card, format) -> "restricted");
        assertTrue(restricted.recommendations().isEmpty());
    }
    @Test public void latticeDisablesCurioAndPanharmoniconStillHasItsRealLandfallRole() {
        var toph = toph(); var curio = card("Cloudstone Curio", "3", "Artifact", "Whenever a nonartifact permanent you control enters, you may return another permanent you control that shares a permanent type with it to its owner's hand.");
        var lattice = card("Mycosynth Lattice", "6", "Artifact", "All permanents are artifacts in addition to their other types.");
        var pan = card("Panharmonicon", "4", "Artifact", "If an artifact or creature entering causes a triggered ability of a permanent you control to trigger, that ability triggers an additional time.");
        var cards = List.of(toph, curio, lattice, pan);
        var report = analyze(cards, deck(entry(toph, "Commander", 1), entry(curio, "Main", 1), entry(lattice, "Main", 1), entry(pan, "Main", 1)), "Commander", DeckAnalysis.Profile.defaults());
        assertTrue(report.findings().stream().map(DeckAnalysis.Finding::id).toList().containsAll(List.of("curio-lattice", "panharmonicon-landfall", "toph-zones")));
    }
    @Test public void recommendedSwapPreservesAPlayersProtectedCardAndExplainsItsLosses() {
        var toph = toph();
        var bestiary = card("Lifecrafter's Bestiary", "3", "Artifact", "Whenever you cast a creature spell, you may pay {G}. If you do, draw a card.");
        var well = card("Ichor Wellspring", "2", "Artifact", "When Ichor Wellspring enters or is put into a graveyard from the battlefield, draw a card.");
        var cards = List.of(toph, bestiary, well); var deck = deck(entry(toph, "Commander", 1), entry(bestiary, "Main", 1));
        var report = analyze(cards, deck, "Commander", profile("artifact-recursion", "fast", List.of(), List.of()));
        assertEquals(report.recommendations().size(), 1);
        var swap = report.recommendations().get(0);
        assertEquals(swap.add().name(), "Ichor Wellspring"); assertEquals(swap.manaValueDelta(), -1);
        assertTrue(swap.tradeoff().contains("sacrifice"));
        assertTrue(analyze(cards, deck, "Commander", profile("artifact-recursion", "fast", List.of("lifecrafter's bestiary"), List.of())).recommendations().isEmpty());
    }
    @Test public void constructedCanRecommendMultipleCopiesButNeverAFifthAcrossMainAndSideboard() {
        var divination = card("Divination", "2 U", "Sorcery", "Draw two cards.");
        var quick = card("Quick Study", "2 U", "Instant", "Draw two cards.");
        var cards = List.of(divination, quick);
        var three = analyze(cards, deck(entry(divination, "Main", 1), entry(quick, "Main", 2), entry(quick, "Sideboard", 1)), "Constructed", DeckAnalysis.Profile.defaults());
        assertEquals(three.recommendations().size(), 1);
        assertTrue(analyze(cards, deck(entry(divination, "Main", 1), entry(quick, "Main", 2), entry(quick, "Sideboard", 2)), "Constructed", DeckAnalysis.Profile.defaults()).recommendations().isEmpty());
    }
    @Test public void limitedUsesOnlyAvailablePoolAndFormatEligibilityRejectsIllegalAdditions() {
        var divination = card("Divination", "2 U", "Sorcery", "Draw two cards."); var quick = card("Quick Study", "2 U", "Instant", "Draw two cards.");
        var cards = List.of(divination, quick); var deck = deck(entry(divination, "Main", 1));
        assertTrue(analyze(cards, deck, "Limited", DeckAnalysis.Profile.defaults()).recommendations().isEmpty());
        assertEquals(analyze(cards, deck, "Limited", profile("control", "balanced", List.of(), List.of("Quick Study"))).recommendations().size(), 1);
        var denied = DeckAnalysis.analyze(new CardCatalog(cards), deck, "Constructed", "test", "v", DeckAnalysis.Profile.defaults(), (card, format) -> "ineligible");
        assertTrue(denied.recommendations().isEmpty());
    }
    @Test public void probabilityHasExactBoundaryBehaviorAndNoClaimsForIncompleteDecks() {
        assertEquals(DeckAnalysis.atLeast(10, 5, 3, 2), .5, 1e-12);
        assertEquals(DeckAnalysis.atLeast(60, 24, 10, 0), 1.0);
        assertEquals(DeckAnalysis.atLeast(60, 24, 3, 4), 0.0);
        assertEquals(DeckAnalysis.atLeast(10, 10, 3, 3), 1.0, 1e-12);
        assertThrows(IllegalArgumentException.class, () -> DeckAnalysis.atLeast(5, 6, 3, 1));
        var toph = toph(); var forest = card("Forest", "no cost", "Basic Land Forest", "{T}: Add {G}.");
        var report = analyze(List.of(toph, forest), deck(entry(toph, "Commander", 1), entry(forest, "Main", 20)), "Commander", DeckAnalysis.Profile.defaults());
        assertNull(report.metrics().landProbability());
        assertFalse(report.findings().stream().anyMatch(f -> f.id().equals("land-consistency")));
    }
    @Test public void completeDeckMetricsExcludeCommanderAndSideboardFromTheLibrary() {
        var toph = toph(); var forest = card("Forest", "no cost", "Basic Land Forest", "{T}: Add {G}.");
        var blank = card("Blank spell", "2 G", "Sorcery", "You gain 3 life.");
        var report = analyze(List.of(toph, forest, blank), deck(entry(toph, "Commander", 1), entry(forest, "Main", 34), entry(blank, "Main", 65), entry(forest, "Sideboard", 10)), "Commander", DeckAnalysis.Profile.defaults());
        assertEquals(report.metrics().mainCards(), 99); assertEquals(report.metrics().lands(), 34);
        assertEquals(report.metrics().cardsSeen(), 11); assertNotNull(report.metrics().landProbability());
        assertEquals(report.format().minimumMain(), 99);
    }

    @Test public void sacrificeFunctionsKeepManaAndTapCostsInsteadOfCallingEveryOutletFree() {
        var free = DeckAnalysis.functions(CardCatalog.describe(altar())).stream().filter(function -> function.role().equals("Sacrifice")).findFirst().orElseThrow();
        assertTrue(free.provides().contains("no mana or tap"));
        for (var card : List.of(card("Vampiric Rites", "B", "Enchantment", "{1}{B}, Sacrifice a creature: You gain 1 life and draw a card."),
                card("Tapped outlet", "2", "Artifact", "{T}, Sacrifice a land: Draw a card."))) {
            var function = DeckAnalysis.functions(CardCatalog.describe(card)).stream().filter(f -> f.role().equals("Sacrifice")).findFirst().orElseThrow();
            assertFalse(function.provides().contains("no mana or tap"));
            assertTrue(function.requires().stream().anyMatch(requirement -> requirement.contains("complete printed activation cost")));
        }
    }

    @Test public void structuralAnalysisOverridesCannotChangeEitherDirectionOfTheSavedFormat() {
        for (String actual : List.of("Constructed", "Commander", "Limited")) for (String requested : List.of("Constructed", "Commander", "Limited")) {
            if (actual.equals(requested)) continue;
            var profile = new DeckAnalysis.Profile(requested, "unspecified", "", "", "balanced", 0, List.of(), List.of());
            assertThrows(IllegalArgumentException.class, () -> DeckAnalysis.analyze(new CardCatalog(List.of()), deck(), actual, "deck", "version", profile, null));
        }
    }
}
