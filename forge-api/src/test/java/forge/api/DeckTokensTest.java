package forge.api;

import com.google.gson.JsonArray;
import com.google.gson.JsonObject;
import forge.StaticData;
import forge.deck.Deck;
import forge.game.ability.AbilityFactory;
import forge.game.card.CardFactory;
import forge.game.card.token.TokenInfo;
import forge.token.TokenDb;
import forge.item.PaperToken;
import forge.item.IPaperCard;
import forge.card.CardEdition;
import org.testng.annotations.BeforeClass;
import org.testng.annotations.Test;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.*;
import static org.testng.Assert.*;

public class DeckTokensTest {
    private static final Path RESOURCES = Path.of("../forge-gui/res");
    @BeforeClass public void resources() { if (StaticData.instance() == null) EngineResources.load(RESOURCES); }
    private static Deck deck() {
        var deck = new Deck("Retreat tokens");
        deck.getMain().add(StaticData.instance().getCommonCards().getCard("Felidar Retreat"));
        return deck;
    }
    private static CardCatalog catalog() { return new CardCatalog(List.of(StaticData.instance().getCommonCards().getCard("Felidar Retreat"))); }
    private static String script() { return DeckTokens.definitions(deck()).keySet().stream().filter(id -> id.contains("cat")).findFirst().orElseThrow(); }

    @Test public void missingCollectorNumbersUseTheSentinelAndTokenRichCampaignDeckImports() throws Exception {
        var rules = StaticData.instance().getAllTokens().getRules().get(script());
        for (String missing : new String[] {null, "", "  "}) {
            var token = new PaperToken(rules, CardEdition.UNKNOWN, script(), missing, "");
            assertEquals(token.getCollectorNumber(), IPaperCard.NO_COLLECTOR_NUMBER);
            assertNotNull(token.getImageKey(false), "An unnumbered token keeps its fallback artwork route");
        }
        String text;
        try (var resource = getClass().getResourceAsStream("/forge/api/ai-campaign/rhys-token-equipment.v1.txt")) {
            assertNotNull(resource);
            text = new String(resource.readAllBytes(), java.nio.charset.StandardCharsets.UTF_8);
        }
        var preview = DeckImport.preview(text);
        assertTrue(preview.problems().isEmpty(), preview.problems().toString());
        assertEquals(preview.entries().stream().mapToInt(DeckEditor.Entry::quantity).sum(), 100);
        var cards = preview.entries().stream().map(entry -> StaticData.instance().getCommonCards().getCard(entry.card().name())).toList();
        try (var engine = new DesktopEngine(new CardCatalog(cards), Files.createTempDirectory("token-rich-campaign-import-"))) {
            var imported = (Map<?, ?>) engine.dispatch("import", args("name", "Token campaign", "text", text, "format", "Commander"));
            assertNotNull(imported.get("id"));
            var editor = ((DeckEditor.Snapshot) imported.get("deck"));
            var tokens = (DeckTokens.View) engine.dispatch("deckTokens", args("deckId", imported.get("id"), "revision", editor.revision()));
            assertTrue(tokens.tokens().size() > 3, "The actual singleton campaign deck exposes its diverse token definitions");
            for (var token : tokens.tokens()) assertNotNull(token.card().collectorNumber());
        }
    }

    @Test public void exactDefinitionsDefaultsAndUndoDoNotChangePlayableContent() throws Exception {
        String script = script();
        var editor = new DeckEditor(catalog(), deck());
        var baseline = editor.snapshot();
        var view = DeckTokens.view("deck", editor);
        var token = view.tokens().stream().filter(item -> item.id().equals(script)).findFirst().orElseThrow();
        var printings = StaticData.instance().getAllTokens().getPrintings(script);
        assertTrue(printings.size() > 1);
        assertTrue(token.automatic()); assertEquals(token.printingId(), TokenDb.printingId(script, printings.get(0)));
        assertEquals(token.sources(), List.of("Felidar Retreat"));
        var staleDeck = deck(); staleDeck.setTokenPrintings(Map.of(script, script + "|REMOVED|1"));
        var fallback = DeckTokens.view("deck", new DeckEditor(catalog(), staleDeck)).tokens().get(0);
        assertTrue(fallback.unavailable()); assertFalse(fallback.automatic());
        assertEquals(fallback.printingId(), token.defaultPrintingId());
        for (int i = 1; i < printings.size(); i++) assertFalse(StaticData.instance().getCardEdition(printings.get(i).getEdition()).getDate()
                .after(StaticData.instance().getCardEdition(printings.get(i - 1).getEdition()).getDate()));
        var workshop = new DeckWorkshopStore(Files.createTempDirectory("token-version-"), catalog());
        String deckId = UUID.randomUUID().toString();
        String version = workshop.ensureVersion(deckId, baseline, "Constructed");
        String choice = TokenDb.printingId(script, printings.get(1));
        editor.setTokenPrinting(0, script, choice);
        assertEquals(editor.snapshot().entries(), baseline.entries());
        assertEquals(workshop.ensureVersion(deckId, editor.snapshot(), "Constructed"), version);
        assertEquals(editor.toDeck().getTokenPrintings().get(script), choice);
        assertThrows(ConcurrentModificationException.class, () -> editor.setTokenPrinting(0, script, choice));
        assertThrows(IllegalArgumentException.class, () -> editor.setTokenPrinting(1, script, "another-token|M21|1"));
        assertEquals(editor.snapshot().revision(), 1);
        editor.undo(1); assertTrue(editor.toDeck().getTokenPrintings().isEmpty());
        editor.redo(2); assertEquals(editor.toDeck().getTokenPrintings().get(script), choice);
        editor.setTokenPrinting(3, script, null); assertTrue(DeckTokens.view("deck", editor).tokens().get(0).automatic());
        assertTrue(DeckTokens.readPreferences(null).isEmpty(), "Older saved decks default without migration");
        assertThrows(IllegalArgumentException.class, () -> DeckTokens.readPreferences(new com.google.gson.Gson().toJsonTree(Map.of(script, "other|M21|1"))));
        var listToken = StaticData.instance().getAllTokens().getPrintings("gw_1_1_elf_warrior").stream()
                .filter(printing -> printing.getEdition().equals("PLST")).findFirst().orElseThrow();
        assertEquals(DeckTokens.describe("gw_1_1_elf_warrior", listToken).artSet(), "plst",
                "Token reprints use the edition's token catalog code, not an invented t-prefixed set");
    }

    @Test public void saveReopenAndDuplicatePreserveTheChoice() throws Exception {
        var directory = Files.createTempDirectory("deck-token-save-");
        String id, choice, script = script();
        try (var engine = new DesktopEngine(catalog(), directory)) {
            id = (String)((Map<?, ?>)engine.dispatch("new", args("name", "Token deck", "format", "Constructed"))).get("id");
            var edits = new JsonArray(); edits.add(args("section", "Main", "cardId", CardCatalog.id(deck().getMain().iterator().next().getKey()), "quantity", 1));
            var edit = args("revision", 0); edit.add("edits", edits); engine.dispatch("edit", edit);
            var page = (CardCatalog.Page)engine.dispatch("tokenPrintings", args("deckId", id, "revision", 1, "tokenId", script));
            choice = page.cards().get(1).id();
            engine.dispatch("setTokenPrinting", args("deckId", id, "revision", 1, "tokenId", script, "printingId", choice));
            assertThrows(IllegalArgumentException.class, () -> engine.dispatch("deckTokens", args("deckId", UUID.randomUUID().toString(), "revision", 2)));
        }
        try (var engine = new DesktopEngine(catalog(), directory)) {
            var opened = (Map<?, ?>)engine.dispatch("open", args("id", id));
            long revision = ((DeckEditor.Snapshot)opened.get("deck")).revision();
            var view = (DeckTokens.View)engine.dispatch("deckTokens", args("deckId", id, "revision", revision));
            assertEquals(view.tokens().get(0).printingId(), choice); assertFalse(view.tokens().get(0).automatic());
            var copy = (Map<?, ?>)engine.dispatch("duplicate", args("name", "Copied token deck"));
            var copied = (DeckTokens.View)engine.dispatch("deckTokens", args("deckId", copy.get("id"), "revision", 0));
            assertEquals(copied.tokens().get(0).printingId(), choice);
        }
        var documents = new DeckDocumentSession(catalog(), directory, new DeckPlaytests(directory));
        assertEquals(documents.loadLobbyDeck(id).deck().getTokenPrintings().get(script), choice,
                "Detached multiplayer deck loading preserves token cosmetics through the document owner");
    }

    @Test public void realTokenFactoryUsesIndependentDeckChoicesAndPublishesExactArt() throws Exception {
        String script = script();
        var printings = StaticData.instance().getAllTokens().getPrintings(script);
        var first = deck(); first.setTokenPrintings(Map.of(script, TokenDb.printingId(script, printings.get(1))));
        var second = deck(); second.setTokenPrintings(Map.of(script, TokenDb.printingId(script, printings.get(2))));
        var copy = new Deck(first); copy.setTokenPrintings(Map.of()); assertFalse(first.getTokenPrintings().isEmpty());
        try (var table = new MatchTable("Constructed", List.of(MatchTable.human(first, "Constructed", "One"),
                MatchTable.human(second, "Constructed", "Two")), RESOURCES, Files.createTempDirectory("token-factory-"))) {
            for (int seat = 0; seat < 2; seat++) {
                var owner = table.game.getPlayers().get(seat);
                var source = CardFactory.getCard(StaticData.instance().getCommonCards().getCard("Felidar Retreat"), owner, table.game);
                var ability = AbilityFactory.getAbility("DB$ Token | TokenScript$ " + script + " | TokenAmount$ 1", source);
                var token = TokenInfo.getProtoType(script, ability, owner);
                var again = TokenInfo.getProtoType(script, ability, owner);
                assertNotNull(token); assertTrue(token.isToken());
                assertEquals(token.getImageKey(), printings.get(seat + 1).getImageKey(false));
                assertEquals(again.getImageKey(), token.getImageKey());
                assertEquals(token.getBasePower(), 2); assertEquals(token.getBaseToughness(), 2);
                var projected = new LinkedHashMap<String, Object>();
                MatchSession.addCardPrinting(projected, token.getView(), true);
                assertEquals(projected.get("collectorNumber"), printings.get(seat + 1).getCollectorNumber());
                assertEquals(projected.get("artSet"), DeckTokens.describe(script, printings.get(seat + 1)).artSet());
                var hidden = new LinkedHashMap<String, Object>(); MatchSession.addCardPrinting(hidden, token.getView(), false);
                assertTrue(hidden.isEmpty(), "A hidden token cannot disclose its artwork identity");
            }
        }
    }

    @Test public void ownerlessTokenPreviewsDoNotRequireOrConsumeADeckPrintingChoice() throws Exception {
        String script = script();
        var printings = StaticData.instance().getAllTokens().getPrintings(script);
        var configured = deck();
        configured.setTokenPrintings(Map.of(script, TokenDb.printingId(script, printings.get(1))));
        try (var table = new MatchTable("Constructed", List.of(MatchTable.human(configured, "Constructed", "One"),
                MatchTable.human(deck(), "Constructed", "Two")), RESOURCES, Files.createTempDirectory("token-preview-"))) {
            var owner = table.game.getPlayers().get(0);
            var source = CardFactory.getCard(StaticData.instance().getCommonCards().getCard("Felidar Retreat"), owner, table.game);
            var ability = AbilityFactory.getAbility("DB$ Token | TokenScript$ " + script + " | TokenAmount$ 1", source);
            var preview = TokenInfo.getProtoType(script, ability, null);
            assertNotNull(preview, "Creature-type ranking deliberately requests a prototype without an owner");
            assertTrue(preview.getType().hasSubtype("Cat"));
            assertNull(preview.getOwner());
            var actual = TokenInfo.getProtoType(script, ability, owner);
            assertEquals(actual.getImageKey(), printings.get(1).getImageKey(false),
                    "An ownerless preview must not replace the real player's pinned token artwork");
            assertEquals(actual.getOwner(), owner);
        }
    }

    private static JsonObject args(Object... pairs) {
        var result = new JsonObject();
        for (int i = 0; i < pairs.length; i += 2) {
            if (pairs[i + 1] instanceof Number value) result.addProperty((String)pairs[i], value);
            else result.addProperty((String)pairs[i], String.valueOf(pairs[i + 1]));
        }
        return result;
    }
}
