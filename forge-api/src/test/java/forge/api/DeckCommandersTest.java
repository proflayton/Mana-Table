package forge.api;

import forge.card.CardRarity;
import forge.card.CardRules;
import forge.deck.Deck;
import forge.deck.DeckSection;
import forge.item.PaperCard;
import java.util.*;
import org.testng.annotations.Test;
import static org.testng.Assert.*;

public class DeckCommandersTest {
    private static PaperCard card(String name, String type, String keyword) {
        return new PaperCard(new CardRules.Reader().readCard(List.of("Name:" + name, "ManaCost:G",
                "Types:" + type, "PT:2/2", "Oracle:Test", "K:" + keyword)), "TST", CardRarity.Common);
    }
    private static final PaperCard A = card("Leader A", "Legendary Creature Elf", "Partner");
    private static final PaperCard B = card("Leader B", "Legendary Creature Elf", "Partner");
    private static final PaperCard C = card("Leader C", "Legendary Creature Elf", "Vigilance");
    private static final PaperCard LAND = card("Forest", "Basic Land Forest", "");
    private DeckEditor editor() {
        var deck = new Deck("Imported list");
        deck.getMain().add(A); deck.getMain().add(B); deck.getMain().add(C); deck.getMain().add(LAND, 97);
        return new DeckEditor(new CardCatalog(List.of(A, B, C, LAND)), deck);
    }
    private void select(DeckEditor editor, PaperCard... cards) {
        DeckCommanders.set(editor, editor.snapshot().revision(), Arrays.stream(cards).map(CardCatalog::id).toList());
    }
    @Test public void selectingAndReplacingMovesCardsWithoutChangingTheList() {
        var editor = editor(); select(editor, A);
        assertEquals(editor.toDeck().getMain().countAll(), 99);
        assertEquals(editor.toDeck().getCommanders(), List.of(A));
        select(editor, C);
        assertEquals(editor.toDeck().getMain().count(A), 1);
        assertEquals(editor.toDeck().getMain().count(C), 0);
        assertEquals(editor.toDeck().getCommanders(), List.of(C));
        assertEquals(editor.toDeck().getMain().countAll(), 99);
    }
    @Test public void partnerEligibilityAndAtomicSelectionComeFromRules() {
        var editor = editor();
        var choices = DeckCommanders.choices(editor.toDeck());
        assertEquals(choices.size(), 3);
        assertEquals(choices.stream().filter(c -> c.id().equals(CardCatalog.id(A))).findFirst().orElseThrow().partners(), List.of(CardCatalog.id(B)));
        select(editor, A, B);
        assertEquals(editor.toDeck().getMain().countAll(), 98);
        assertEquals(editor.toDeck().getCommanders().size(), 2);
        var before = editor.snapshot();
        assertThrows(IllegalArgumentException.class, () -> select(editor, A, C));
        assertEquals(editor.snapshot(), before);
    }
    @Test public void missingPartnerDiscoveryDoesNotGrantAnOutOfDeckCommanderChoice() {
        var toothy = card("Toothy, Imaginary Friend", "Legendary Creature Illusion", "Partner with:Pir, Imaginative Rascal:Pir");
        var deck = new Deck("Missing partner"); deck.getMain().add(toothy); deck.getMain().add(A); deck.getMain().add(C);
        var choices = DeckCommanders.choices(deck);
        var named = choices.stream().filter(choice -> choice.id().equals(CardCatalog.id(toothy))).findFirst().orElseThrow();
        assertTrue(named.canPartner()); assertEquals(named.partnerName(), "Pir, Imaginative Rascal");
        assertTrue(named.partners().isEmpty(), "A named partner outside the deck is discovery guidance, never a legal selection handle");
        var generic = choices.stream().filter(choice -> choice.id().equals(CardCatalog.id(A))).findFirst().orElseThrow();
        assertTrue(generic.canPartner()); assertEquals(generic.partnerName(), ""); assertTrue(generic.partners().isEmpty());
        var ordinary = choices.stream().filter(choice -> choice.id().equals(CardCatalog.id(C))).findFirst().orElseThrow();
        assertFalse(ordinary.canPartner()); assertEquals(ordinary.partnerName(), ""); assertTrue(ordinary.partners().isEmpty());
        var editor = new DeckEditor(new CardCatalog(List.of(toothy, A, C)), deck);
        assertThrows(IllegalArgumentException.class, () -> DeckCommanders.set(editor, 0, List.of(named.id(), "missing-partner")));
        assertEquals(editor.snapshot().revision(), 0L);
    }
    @Test public void invalidAndStaleSelectionsLeaveTheDeckUntouched() {
        var editor = editor(); select(editor, A);
        var before = editor.snapshot();
        assertThrows(IllegalArgumentException.class, () -> select(editor, LAND));
        assertThrows(IllegalArgumentException.class, () -> select(editor, A, A));
        assertThrows(IllegalArgumentException.class, () -> select(editor, A, B, C));
        assertThrows(IllegalArgumentException.class, () -> DeckCommanders.set(editor, before.revision(), List.of("missing")));
        assertThrows(ConcurrentModificationException.class, () -> DeckCommanders.set(editor, 0, List.of(CardCatalog.id(B))));
        assertEquals(editor.snapshot(), before);
    }
    @Test public void selectingFromSideboardAndClearingPreservesEveryCopy() {
        var editor = editor();
        editor.apply(0, List.of(new DeckEditor.Edit("Main", CardCatalog.id(A), 0), new DeckEditor.Edit("Sideboard", CardCatalog.id(A), 1)));
        select(editor, A);
        assertEquals(editor.toDeck().get(DeckSection.Sideboard).count(A), 0);
        assertEquals(editor.toDeck().getMain().countAll(), 99);
        select(editor);
        assertTrue(editor.toDeck().getCommanders().isEmpty());
        assertEquals(editor.toDeck().getMain().countAll(), 100);
        assertEquals(editor.toDeck().getMain().count(A), 1);
    }
    @Test public void changingCommandersIsOneUndoableEditAndMovesOnlyOneCopy() {
        var editor = editor();
        editor.apply(0, List.of(new DeckEditor.Edit("Main", CardCatalog.id(A), 2)));
        var before = editor.snapshot(); select(editor, A, B);
        assertEquals(editor.toDeck().getMain().count(A), 1);
        editor.undo(editor.snapshot().revision());
        assertEquals(editor.snapshot().entries(), before.entries());
    }
    @Test public void backgroundChoicesUseTheEnginesPairingRules() {
        var leader = card("Background leader", "Legendary Creature Elf", "Choose a Background");
        var background = card("Background", "Legendary Enchantment Background", "");
        var deck = new Deck("Background deck"); deck.getMain().add(leader); deck.getMain().add(background);
        var editor = new DeckEditor(new CardCatalog(List.of(leader, background)), deck);
        assertTrue(DeckCommanders.choices(deck).stream().allMatch(c -> c.partners().size() == 1));
        select(editor, leader, background);
        assertEquals(editor.toDeck().getCommanders().size(), 2);
        assertTrue(editor.toDeck().getMain().isEmpty());
    }
}
