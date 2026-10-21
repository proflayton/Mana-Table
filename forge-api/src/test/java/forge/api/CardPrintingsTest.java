package forge.api;

import com.google.gson.JsonObject;
import forge.card.CardRarity;
import forge.card.CardRules;
import forge.deck.Deck;
import forge.deck.DeckSection;
import forge.item.PaperCard;
import org.testng.annotations.Test;
import org.testng.annotations.BeforeClass;

import java.nio.file.Files;
import java.util.ConcurrentModificationException;
import java.util.List;
import java.util.Map;
import java.util.UUID;

import static org.testng.Assert.*;

public class CardPrintingsTest {
    @BeforeClass public void language() { forge.util.Localizer.getInstance().initialize("en-US", "../forge-gui/res/languages"); }
    private static PaperCard card(String name, String edition, int art, String number) {
        var rules = new CardRules.Reader().readCard(List.of("Name:" + name, "ManaCost:G",
                "Types:Legendary Creature Elf", "PT:2/2", "Oracle:Partner", "K:Partner"));
        return new PaperCard(rules, edition, CardRarity.Rare, art, false, number, "Artist", "");
    }
    private static final PaperCard FIRST = card("Leader", "AAA", 1, "1");
    private static final PaperCard SECOND = card("Leader", "AAA", 2, "200");
    private static final PaperCard OTHER_SET = card("Leader", "BBB", 1, "1");
    private static final PaperCard PARTNER = card("Other leader", "AAA", 1, "2");
    private static CardCatalog catalog() { return new CardCatalog(List.of(FIRST, SECOND, SECOND.getFoiled(), OTHER_SET, PARTNER)); }

    @Test public void listingIncludesAlternateArtAndFinishesWithoutSimilarNames() {
        var catalog = catalog();
        var first = catalog.printings(CardCatalog.id(FIRST), 0, 2);
        var next = catalog.printings(CardCatalog.id(FIRST), 2, 2);
        assertEquals(first.total(), 4);
        assertEquals(first.cards().size(), 2);
        assertTrue(first.cards().stream().noneMatch(next.cards()::contains));
        assertTrue(first.cards().stream().allMatch(c -> c.name().equals("Leader")));
        assertTrue(catalog.printings(CardCatalog.id(FIRST), 0, 200).cards().stream().anyMatch(CardCatalog.CardInfo::foil));
        var filtered = catalog.printings(CardCatalog.id(FIRST), 0, 6, "aaa 200", "foil");
        assertEquals(filtered.total(), 1);
        assertEquals(filtered.cards().get(0).id(), CardCatalog.id(SECOND.getFoiled()));
        assertEquals(catalog.printings(CardCatalog.id(FIRST), 0, 6, "BBB", "nonfoil").total(), 1);
        assertEquals(catalog.printings(CardCatalog.id(FIRST), 0, 6, "missing", "").total(), 0);
        assertThrows(IllegalArgumentException.class, () -> catalog.printings(CardCatalog.id(FIRST), 0, 6, "", "invalid"));
        assertThrows(IllegalArgumentException.class, () -> catalog.printings("missing", 0, 6));
        assertThrows(IllegalArgumentException.class, () -> catalog.printings(CardCatalog.id(FIRST), -1, 6));
    }

    @Test public void swappingCommandersPreservesPartnersOtherSectionsAndUndo() {
        var deck = new Deck("Partners");
        deck.getOrCreate(DeckSection.Commander).add(FIRST); deck.get(DeckSection.Commander).add(PARTNER);
        deck.getMain().add(FIRST, 3);
        var editor = new DeckEditor(catalog(), deck);
        var before = editor.snapshot();
        editor.replacePrinting(0, "Commander", CardCatalog.id(FIRST), CardCatalog.id(SECOND.getFoiled()));
        assertEquals(editor.toDeck().getMain().count(FIRST), 3);
        assertEquals(editor.toDeck().getCommanders().size(), 2);
        assertTrue(editor.toDeck().getCommanders().containsAll(List.of(SECOND.getFoiled(), PARTNER)));
        assertEquals(DeckCommanders.choices(editor.toDeck()).stream().filter(c -> c.id().equals(CardCatalog.id(SECOND.getFoiled())))
                .findFirst().orElseThrow().partners(), List.of(CardCatalog.id(PARTNER)));
        editor.undo(1); assertEquals(editor.snapshot().entries(), before.entries());
        editor.redo(2); assertEquals(editor.toDeck().get(DeckSection.Commander).count(SECOND.getFoiled()), 1);
        var after = editor.snapshot();
        assertThrows(ConcurrentModificationException.class, () -> editor.replacePrinting(0, "Main", CardCatalog.id(FIRST), CardCatalog.id(SECOND)));
        assertThrows(IllegalArgumentException.class, () -> editor.replacePrinting(after.revision(), "Main", CardCatalog.id(FIRST), CardCatalog.id(PARTNER)));
        assertThrows(IllegalArgumentException.class, () -> editor.replacePrinting(after.revision(), "Sideboard", CardCatalog.id(FIRST), CardCatalog.id(SECOND)));
        assertEquals(editor.snapshot(), after);
    }

    @Test public void mergingCopiesIsAtomicAndRejectsOverflow() {
        var deck = new Deck("Copies"); deck.getMain().add(FIRST, 4); deck.getMain().add(SECOND, 2);
        var editor = new DeckEditor(catalog(), deck);
        editor.replacePrinting(0, "Main", CardCatalog.id(FIRST), CardCatalog.id(SECOND));
        assertEquals(editor.toDeck().getMain().count(SECOND), 6);
        assertEquals(editor.snapshot().entries().size(), 1);
        editor.undo(1); assertEquals(editor.toDeck().getMain().count(FIRST), 4);
        editor.apply(2, List.of(new DeckEditor.Edit("Main", CardCatalog.id(SECOND), 999)));
        var before = editor.snapshot();
        assertThrows(IllegalArgumentException.class, () -> editor.replacePrinting(before.revision(), "Main", CardCatalog.id(FIRST), CardCatalog.id(SECOND)));
        assertEquals(editor.snapshot(), before);
    }

    @Test public void printingMergePreservesBothPlansAndTheRulesVersionAcrossRestart() throws Exception {
        var directory = Files.createTempDirectory("mana-printing-plan-");
        var catalog = catalog();
        var store = new DeckWorkshopStore(directory, catalog);
        String id = UUID.randomUUID().toString();
        var deck = new Deck("Printed plan");
        deck.getMain().add(FIRST, 3); deck.getMain().add(SECOND, 2);
        var editor = new DeckEditor(catalog, deck);
        var before = store.view(id, editor.snapshot(), "Constructed");
        var gson = new com.google.gson.Gson();
        var metadata = gson.toJsonTree(Map.of("roles", List.of(
                new DeckWorkshopStore.Role(CardCatalog.id(FIRST), List.of("Draw"), true, "local"),
                new DeckWorkshopStore.Role(CardCatalog.id(SECOND), List.of("Ramp"), false, "local")),
                "question", "Do I need more card access?")).getAsJsonObject();
        store.metadata(id, editor.snapshot(), "Constructed", metadata);
        store.replacePrinting(id, editor, "Constructed", "Main", CardCatalog.id(FIRST), CardCatalog.id(SECOND));
        var after = store.view(id, editor.snapshot(), "Constructed");
        assertEquals(after.versionId(), before.versionId(), "Artwork does not create another rules version or detach earlier test evidence");
        assertEquals(after.question(), "Do I need more card access?");
        var plan = after.roles().stream().filter(role -> role.cardId().equals(CardCatalog.id(SECOND))).findFirst().orElseThrow();
        assertEquals(plan.roles(), List.of("Ramp", "Draw"));
        assertTrue(plan.pinned(), "A favorite remains protected when its copies merge into an already categorized printing");
        var reopened = new DeckWorkshopStore(directory, catalog).view(id, editor.snapshot(), "Constructed");
        assertEquals(reopened.roles(), after.roles());
        assertEquals(reopened.versionId(), before.versionId());
        editor.undo(1);
        assertEquals(editor.toDeck().getMain().count(FIRST), 3);
        assertEquals(editor.toDeck().getMain().count(SECOND), 2);
        assertEquals(store.view(id, editor.snapshot(), "Constructed").versionId(), before.versionId());
    }

    @Test public void adapterPersistsExactPrintingAndRejectsWrongDocuments() throws Exception {
        var directory = Files.createTempDirectory("mana-printings-");
        String id;
        try (var engine = new DesktopEngine(catalog(), directory)) {
            var created = (Map<?, ?>)engine.dispatch("new", args("name", "Printed commander", "format", "Commander"));
            id = (String)created.get("id");
            var edit = args("deckId", id, "revision", 0);
            var edits = new com.google.gson.JsonArray();
            edits.add(args("section", "Commander", "cardId", CardCatalog.id(FIRST), "quantity", 1));
            edit.add("edits", edits); engine.dispatch("edit", edit);
            var replace = args("deckId", id, "revision", 1, "section", "Commander", "cardId", CardCatalog.id(FIRST), "printingId", CardCatalog.id(SECOND.getFoiled()));
            replace.addProperty("deckId", UUID.randomUUID().toString());
            assertThrows(IllegalArgumentException.class, () -> engine.dispatch("replacePrinting", replace));
            replace.addProperty("deckId", id); engine.dispatch("replacePrinting", replace);
            assertThrows(IllegalArgumentException.class, () -> engine.dispatch("replacePrinting", replace));
            var state = (Map<?, ?>)engine.dispatch("snapshot", new JsonObject());
            var snapshot = (DeckEditor.Snapshot)state.get("deck");
            assertEquals(snapshot.entries().get(0).card().id(), CardCatalog.id(SECOND.getFoiled()));
            assertNull(state.get("saveError"));
        }
        try (var reopened = new DesktopEngine(catalog(), directory)) {
            var saved = (Map<?, ?>)reopened.dispatch("open", args("id", id));
            var entry = ((DeckEditor.Snapshot)saved.get("deck")).entries().get(0);
            assertEquals(entry.section(), "Commander");
            assertEquals(entry.card().collectorNumber(), "200");
            assertTrue(entry.card().foil());
        }
    }
    private static JsonObject args(Object... pairs) {
        var result = new JsonObject();
        for (int i = 0; i < pairs.length; i += 2) {
            if (pairs[i + 1] instanceof Number number) result.addProperty((String)pairs[i], number);
            else result.addProperty((String)pairs[i], (String)pairs[i + 1]);
        }
        return result;
    }
}
