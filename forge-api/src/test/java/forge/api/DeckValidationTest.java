package forge.api;

import forge.StaticData;
import forge.card.ColorSet;
import forge.deck.Deck;
import forge.deck.DeckFormat;
import forge.deck.DeckSection;
import forge.item.PaperCard;
import org.testng.annotations.BeforeClass;
import org.testng.annotations.Test;

import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import java.util.Set;
import java.util.stream.Collectors;

import static org.testng.Assert.*;

public class DeckValidationTest {
    @BeforeClass public void resources() {
        if (StaticData.instance() == null) EngineResources.load(Path.of("../forge-gui/res"));
    }

    private static PaperCard card(String name) {
        var card = StaticData.instance().getCommonCards().getCard(name);
        if (card == null) card = StaticData.instance().getVariantCards().getCard(name);
        assertNotNull(card, name);
        return card;
    }

    private static Deck deck(String commander, String basic, int quantity) {
        var deck = new Deck("Validation fixture");
        deck.getMain().add(card(basic), quantity);
        if (commander != null) deck.getOrCreate(DeckSection.Commander).add(card(commander));
        return deck;
    }

    private static DeckEditor editor(Deck deck) {
        var cards = new ArrayList<PaperCard>();
        for (var section : deck) for (var entry : section.getValue()) cards.add(entry.getKey());
        return new DeckEditor(new CardCatalog(cards), deck);
    }

    private static DeckEditor.Validation validate(DeckEditor editor, DeckFormat format) {
        var validation = editor.validate(format);
        assertEquals(validation.problem(), format.getDeckConformanceProblem(editor.toDeck()));
        assertEquals(validation.valid(), validation.problem() == null);
        if (validation.valid()) assertTrue(validation.issues().isEmpty(), "A conforming deck cannot have error highlights");
        else assertFalse(validation.issues().isEmpty(), "Every failed validation must explain the failure");
        return validation;
    }

    private static List<DeckEditor.Issue> issues(DeckEditor.Validation validation, String code) {
        return validation.issues().stream().filter(issue -> issue.code().equals(code)).toList();
    }

    @Test public void reportsIndependentColorCountAndCopyErrorsAcrossPrintingsAndSections() {
        var deck = deck("Rhys the Redeemed", "Forest", 95);
        var first = StaticData.instance().getCommonCards().getCard("Lightning Bolt", "M10");
        var second = StaticData.instance().getCommonCards().getCard("Lightning Bolt", "M11");
        assertNotNull(first); assertNotNull(second);
        var mountain = card("Mountain");
        deck.getMain().add(first); deck.getMain().add(second); deck.getMain().add(mountain);
        deck.getOrCreate(DeckSection.Sideboard).add(mountain);
        var validation = validate(editor(deck), DeckFormat.Commander);
        assertEquals(issues(validation, "MAIN_COUNT").size(), 1);
        assertTrue(issues(validation, "MAIN_COUNT").get(0).cardIds().isEmpty());
        assertEquals(issues(validation, "COLOR_IDENTITY").size(), 4);
        var duplicate = issues(validation, "CARD_COPY_LIMIT").get(0);
        assertNull(duplicate.section());
        assertEquals(Set.copyOf(duplicate.cardIds()), Set.of(CardCatalog.id(first), CardCatalog.id(second)));
        assertTrue(duplicate.message().contains("at most 1"));
        assertThrows(UnsupportedOperationException.class, () -> validation.issues().clear());
        assertThrows(UnsupportedOperationException.class, () -> duplicate.cardIds().clear());
    }

    @Test public void editUndoAndRedoPublishCurrentIssuesWithoutMutatingRetainedValidation() {
        var deck = deck("Rhys the Redeemed", "Forest", 98);
        var mountain = card("Mountain"); deck.getMain().add(mountain);
        var editor = editor(deck);
        var before = validate(editor, DeckFormat.Commander);
        assertEquals(issues(before, "COLOR_IDENTITY").get(0).cardIds(), List.of(CardCatalog.id(mountain)));
        editor.apply(0, List.of(new DeckEditor.Edit("Main", CardCatalog.id(mountain), 0),
                new DeckEditor.Edit("Main", CardCatalog.id(card("Forest")), 99)));
        assertTrue(validate(editor, DeckFormat.Commander).valid());
        editor.undo(1);
        assertEquals(validate(editor, DeckFormat.Commander).issues(), before.issues());
        editor.redo(2);
        assertTrue(validate(editor, DeckFormat.Commander).issues().isEmpty());
        assertEquals(issues(before, "COLOR_IDENTITY").size(), 1);
    }

    @Test public void commanderSetupIssuesIdentifyMissingIllegalAndIncompatibleLeaders() {
        var missing = validate(editor(deck(null, "Forest", 100)), DeckFormat.Commander);
        assertEquals(issues(missing, "MISSING_COMMANDER").size(), 1);
        assertTrue(issues(missing, "MISSING_COMMANDER").get(0).cardIds().isEmpty());
        var illegal = deck("Forest", "Forest", 99);
        var illegalResult = validate(editor(illegal), DeckFormat.Commander);
        assertEquals(issues(illegalResult, "ILLEGAL_COMMANDER").get(0).cardIds(), List.of(CardCatalog.id(card("Forest"))));
        var pair = deck("Rhys the Redeemed", "Forest", 98);
        pair.getOrCreate(DeckSection.Commander).add(card("Goreclaw, Terror of Qal Sisma"));
        var badPair = validate(editor(pair), DeckFormat.Commander);
        assertEquals(issues(badPair, "COMMANDER_PARTNERSHIP").get(0).cardIds().size(), 2);
        assertTrue(issues(badPair, "COLOR_IDENTITY").isEmpty(), "Identity checks need a legal commander setup");
        pair.getOrCreate(DeckSection.Commander).add(card("Anara, Wolvid Familiar"));
        assertEquals(issues(validate(editor(pair), DeckFormat.Commander), "COMMANDER_COUNT").get(0).cardIds().size(), 3);
    }

    @Test public void copyLimitsReuseBasicUnlimitedSpecificAndConstructedRules() {
        var rats = deck("Ayara, First of Locthwain", "Swamp", 1);
        rats.getMain().add(card("Relentless Rats"), 98);
        assertTrue(validate(editor(rats), DeckFormat.Commander).valid());
        var dwarves = deck("Torbran, Thane of Red Fell", "Mountain", 91);
        var dwarf = card("Seven Dwarves");
        dwarves.getMain().add(dwarf, 7); dwarves.getOrCreate(DeckSection.Sideboard).add(dwarf, 5);
        var editor = editor(dwarves);
        var result = validate(editor, DeckFormat.Commander);
        assertEquals(issues(result, "MAIN_COUNT").size(), 1);
        assertTrue(issues(result, "CARD_COPY_LIMIT").isEmpty(), "Forge's DeckLimit exception counts Main only");
        editor.apply(0, List.of(new DeckEditor.Edit("Main", CardCatalog.id(dwarf), 8)));
        var excess = issues(validate(editor, DeckFormat.Commander), "CARD_COPY_LIMIT").get(0);
        assertEquals(excess.section(), "Main");
        assertEquals(excess.cardIds(), List.of(CardCatalog.id(dwarf)));
        var constructed = deck(null, "Mountain", 56);
        constructed.getMain().add(card("Lightning Bolt"), 4);
        assertTrue(validate(editor(constructed), DeckFormat.Constructed).valid());
        constructed.getOrCreate(DeckSection.Sideboard).add(card("Lightning Bolt"));
        assertEquals(issues(validate(editor(constructed), DeckFormat.Constructed), "CARD_COPY_LIMIT").size(), 1);
    }

    @Test public void colorlessBasicExceptionDoesNotHighlightAnAllowedMainRowForASideboardViolation() {
        var deck = deck("Kozilek, the Great Distortion", "Forest", 99);
        assertTrue(validate(editor(deck), DeckFormat.Commander).valid(), "Preserve Forge's one-basic-type colorless exception");
        var forest = card("Forest"); deck.getOrCreate(DeckSection.Sideboard).add(forest);
        var violations = issues(validate(editor(deck), DeckFormat.Commander), "COLOR_IDENTITY");
        assertEquals(violations.size(), 1);
        assertEquals(violations.get(0).section(), "Sideboard");
        assertEquals(violations.get(0).cardIds(), List.of(CardCatalog.id(forest)));
    }

    @Test public void partnerWildColorsAndDeckRulesKeepTheirExistingExceptions() {
        var wild = deck("The Prismatic Piper", "Forest", 97);
        wild.getOrCreate(DeckSection.Commander).add(card("Gilanra, Caller of Wirewood"));
        wild.getMain().add(card("Mountain"));
        assertTrue(validate(editor(wild), DeckFormat.Commander).valid());
        var exemptions = deck("The Everforger", "Forest", 98);
        exemptions.getMain().add(card("Etherium Sculptor"));
        assertTrue(DeckFormat.Commander.getColorIdentityViolations(exemptions).isEmpty());
        assertTrue(issues(validate(editor(exemptions), DeckFormat.Commander), "COLOR_IDENTITY").isEmpty());
        var additional = deck(null, "Forest", 98);
        additional.getOrCreate(DeckSection.Commander).add(card("Tolabow, Loch Rascal").copyWithMarkedColors(ColorSet.R));
        additional.getMain().add(card("Lightning Bolt"));
        assertTrue(DeckFormat.Commander.getColorIdentityViolations(additional).isEmpty());
        assertTrue(issues(validate(editor(additional), DeckFormat.Commander), "COLOR_IDENTITY").isEmpty());
        var size = deck("Whtz, the Bibliophile", "Island", 120);
        assertTrue(issues(validate(editor(size), DeckFormat.Commander), "MAIN_COUNT").isEmpty());
        var proclamation = deck(null, "Forest", 55);
        proclamation.getOrCreate(DeckSection.Conspiracy).add(card("Advantageous Proclamation"));
        proclamation.getOrCreate(DeckSection.Sideboard).add(card("Forest"), 16);
        var adjusted = validate(editor(proclamation), DeckFormat.Constructed);
        assertTrue(issues(adjusted, "MAIN_COUNT").isEmpty());
        assertEquals(issues(adjusted, "SIDEBOARD_COUNT").size(), 1);
    }

    @Test public void structuredIdentityRowsMatchTheEngineMessageExactly() {
        var deck = deck("Rhys the Redeemed", "Forest", 96);
        deck.getMain().add(card("Island")); deck.getMain().add(card("Mountain")); deck.getMain().add(card("Lightning Bolt"));
        var violations = DeckFormat.Commander.getColorIdentityViolations(deck);
        String expected = "contains one or more cards that do not match the commanders color identity:"
                + violations.stream().map(violation -> "\n" + violation.card().getName()).collect(Collectors.joining());
        assertEquals(DeckFormat.Commander.getDeckConformanceProblem(deck), expected);
        var reported = issues(validate(editor(deck), DeckFormat.Commander), "COLOR_IDENTITY");
        assertEquals(reported.size(), violations.size());
    }

    @Test public void unprojectedFormatsRetainTheEngineProblemAsAFallbackIssue() {
        var result = validate(editor(deck(null, "Forest", 40)), DeckFormat.Pauper);
        assertEquals(result.issues().size(), 1);
        assertEquals(result.issues().get(0).code(), "ENGINE_CONFORMANCE");
        assertEquals(result.issues().get(0).message(), result.problem());
    }
}
