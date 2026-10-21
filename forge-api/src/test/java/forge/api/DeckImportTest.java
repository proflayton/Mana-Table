package forge.api;

import com.google.gson.JsonObject;
import forge.StaticData;
import forge.deck.DeckSection;
import forge.item.PaperCard;
import org.testng.annotations.BeforeClass;
import org.testng.annotations.DataProvider;
import org.testng.annotations.Test;

import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.Map;

import static org.testng.Assert.*;

public class DeckImportTest {
    @BeforeClass public void resources() {
        if (StaticData.instance() == null) EngineResources.load(Path.of("../forge-gui/res"));
    }

    private static DeckEditor.Entry entry(DeckImport.Preview preview, String name) {
        return preview.entries().stream().filter(row -> row.card().name().equals(name)).findFirst().orElseThrow();
    }

    private static CardCatalog catalog(DeckImport.Preview preview) {
        var cards = new ArrayList<PaperCard>();
        var data = StaticData.instance();
        for (var row : preview.entries()) {
            var info = row.card();
            PaperCard printing = null;
            for (var database : data.getAvailableDatabases().values()) {
                printing = database.getCardFromSet(info.name(), data.getCardEdition(info.edition()),
                        info.artIndex(), info.collectorNumber(), info.foil());
                if (printing != null) break;
            }
            assertNotNull(printing, info.id());
            cards.add(printing);
        }
        return new CardCatalog(cards);
    }

    @Test public void annotatedExportPreservesEveryRowQuantityPrintingAndCommander() throws Exception {
        String text;
        try (var resource = getClass().getResourceAsStream("/forge/api/annotated-toph.txt")) {
            assertNotNull(resource);
            text = new String(resource.readAllBytes(), StandardCharsets.UTF_8);
        }
        var preview = DeckImport.preview(text);
        assertTrue(preview.problems().isEmpty(), preview.problems().toString());
        assertEquals(preview.entries().size(), 95);
        assertEquals(preview.entries().stream().mapToInt(DeckEditor.Entry::quantity).sum(), 100);
        assertEquals(preview.warnings().size(), 3);
        assertTrue(preview.warnings().stream().allMatch(warning -> warning.kind().equals("PRINTING_SUBSTITUTED")));
        assertEquals(entry(preview, "Forest").quantity(), 4);
        assertEquals(entry(preview, "Mountain").quantity(), 3);
        var foil = entry(preview, "Force of Vigor").card();
        assertEquals(foil.edition(), "H1R");
        assertEquals(foil.collectorNumber(), "21");
        assertTrue(foil.foil());
        var fair = entry(preview, "Inventors' Fair").card();
        assertEquals(fair.edition(), "PLST");
        assertEquals(fair.collectorNumber(), "KLD-247");
        assertEquals(entry(preview, "The Arkenstone").card().edition(), "HOB");
        assertEquals(entry(preview, "The Arkenstone").card().collectorNumber(), "170");
        assertEquals(entry(preview, "Toph, the First Metalbender").section(), "Commander");
        assertEquals(entry(preview, "Zell Dincht").section(), "Main", "Inline commander annotations must not affect following cards");
        var deck = preview.open("Annotated export", catalog(preview)).toDeck();
        assertEquals(deck.getMain().countAll(), 99);
        assertEquals(deck.get(DeckSection.Commander).countAll(), 1);
        assertEquals(MatchSetup.suggestedFormat(deck), "Commander");
    }

    @Test public void alternateCommanderArtworkAndFinishSurviveExportAndReimport() throws Exception {
        var preview = DeckImport.preview("Commander\n1 Toph, the First Metalbender (TLA) 362 *F*\nMain\n99 Forest (M13) 249");
        assertTrue(preview.problems().isEmpty(), preview.problems().toString());
        var selected = entry(preview, "Toph, the First Metalbender").card();
        assertEquals(selected.collectorNumber(), "362"); assertTrue(selected.foil());
        assertEquals(selected.artSet(), "tla");
        var source = catalog(preview);
        var face = forge.game.card.CardView.getCardForUi(source.resolve(selected.id()));
        var projection = new java.util.LinkedHashMap<String, Object>();
        MatchSession.addCardPrinting(projection, face, false);
        assertTrue(projection.isEmpty(), "Hidden cards never publish printing metadata");
        MatchSession.addCardPrinting(projection, face, true);
        assertEquals(projection.get("collectorNumber"), "362", "Gameplay artwork follows the exact printed image key");
        assertEquals(projection.get("artSet"), "tla");
        try (var engine = new DesktopEngine(catalog(preview), Files.createTempDirectory("mana-printing-roundtrip-"))) {
            var request = new JsonObject(); request.addProperty("name", "Alternate commander");
            request.addProperty("text", "Commander\n1 Toph, the First Metalbender (TLA) 362 *F*\nMain\n99 Forest (M13) 249");
            engine.dispatch("import", request);
            String exported = (String)engine.dispatch("export", new JsonObject());
            var restored = DeckImport.preview(exported);
            assertTrue(restored.problems().isEmpty(), restored.problems().toString());
            assertEquals(entry(restored, "Toph, the First Metalbender").card().id(), selected.id());
            assertEquals(entry(restored, "Toph, the First Metalbender").section(), "Commander");
        }
    }

    @DataProvider public Object[][] textFormats() {
        return new Object[][] {
                {"MTGO/plain", "4 Forest\n\nSideboard\n2 Shock"},
                {"Arena/ManaBox", "Deck\n4 Forest (M13) 249\nSideboard\n2 Shock (M14) 155"},
                {"Moxfield", "4 Forest (M13) 249\nSB: 2 Shock (M14) 155"},
                {"Archidekt", "4x Forest (m13) 249 [Land] [Mana] ^Land,#37d67a^\n2x Shock (m14) 155 [Sideboard] [Removal]"},
                {"Deckstats", "//Lands\n4 [M13] Forest\n//Sideboard\n2 [M14] Shock"},
                {"bracketed printings", "4 Forest [M13]\nSB: 2 Shock [M14]"},
                {"Forge", "[Main]\n4 Forest|M13|1\n[Sideboard]\n2 Shock|M14|1"}
        };
    }

    @Test public void categoryListsPreserveCommanderRolesAndRejectConflictingBoards() {
        for (String tags : java.util.List.of("[Ramp,Commander{top}{noPrice}]", "[Commander{top},Ramp]")) {
            var preview = DeckImport.preview("1x Toph, the First Metalbender (tla) 247 " + tags + "\n1x Forest [Land]");
            assertTrue(preview.problems().isEmpty(), preview.problems().toString());
            assertEquals(entry(preview, "Toph, the First Metalbender").section(), "Commander");
            assertEquals(entry(preview, "Forest").section(), "Main");
        }
        assertFalse(DeckImport.preview("1x Toph, the First Metalbender [Commander{top},Sideboard]").problems().isEmpty());
        assertFalse(DeckImport.preview("1x Forest [Land,Ideas{noDeck}]").problems().isEmpty());
        var printing = DeckImport.preview("1 Forest [M13]");
        assertEquals(entry(printing, "Forest").card().edition(), "M13");
        var wrongSet = DeckImport.preview("1 Bayou [M13]");
        assertTrue(wrongSet.entries().isEmpty(), "A wrong known set cannot fall back to another printing");
        assertEquals(wrongSet.problems().size(), 1);
        assertTrue(wrongSet.warnings().isEmpty());
    }

    @Test public void reservedSectionTagsTakePrecedenceOverEditionNames() {
        var preview = DeckImport.preview("1x Ghave, Guru of Spores (CMD) 200 [Commander]\n"
                + "1x Sol Ring (CMD) 261 [Sideboard]\n1x Forest [Main]");
        assertTrue(preview.problems().isEmpty(), preview.problems().toString());
        assertEquals(entry(preview, "Ghave, Guru of Spores").section(), "Commander");
        assertEquals(entry(preview, "Ghave, Guru of Spores").card().edition(), "CMD");
        assertEquals(entry(preview, "Ghave, Guru of Spores").card().collectorNumber(), "200");
        assertEquals(entry(preview, "Sol Ring").section(), "Sideboard");
        assertEquals(entry(preview, "Forest").section(), "Main");
        var invalidLeader = DeckImport.preview("1 Sol Ring [Commander]");
        assertTrue(invalidLeader.entries().isEmpty());
        assertEquals(invalidLeader.problems().get(0).kind(), "INVALID_SECTION");
    }

    @Test(dataProvider = "textFormats") public void commonTextFormatsKeepQuantitiesAndSideboards(String format, String text) {
        var preview = DeckImport.preview(text);
        assertTrue(preview.problems().isEmpty(), format + ": " + preview.problems());
        assertTrue(preview.warnings().isEmpty(), format + ": " + preview.warnings());
        assertEquals(preview.entries().size(), 2, format);
        assertEquals(entry(preview, "Forest").quantity(), 4);
        assertEquals(entry(preview, "Forest").section(), "Main");
        assertEquals(entry(preview, "Shock").quantity(), 2);
        assertEquals(entry(preview, "Shock").section(), "Sideboard");
    }

    @Test public void annotationsAreLocalAndPreserveFoilsAndPrintingFallbackWarnings() {
        var preview = DeckImport.preview("\uFEFFDeck\r\n1x Toph, the First Metalbender (tla) 247 [Commander{top}{noPrice}]\r\n"
                + "SB: 2x Lightning Bolt (ZZZZZZ) 1 *E* [Removal]\r\n4 x Forest [Land]\r\n1000x Mountain [Land]");
        assertTrue(preview.problems().isEmpty(), preview.problems().toString());
        assertEquals(entry(preview, "Toph, the First Metalbender").section(), "Commander");
        assertEquals(entry(preview, "Lightning Bolt").section(), "Sideboard");
        assertEquals(entry(preview, "Lightning Bolt").quantity(), 2);
        assertTrue(entry(preview, "Lightning Bolt").card().foil());
        assertEquals(entry(preview, "Forest").section(), "Main");
        assertEquals(entry(preview, "Forest").quantity(), 4);
        assertEquals(entry(preview, "Mountain").quantity(), 1000);
        assertEquals(preview.warnings().size(), 1);
        assertEquals(preview.warnings().get(0).line(), 3);
        assertTrue(preview.warnings().get(0).text().contains("ZZZZZZ is not in the library; using"));
    }

    @DataProvider public Object[][] rejectedRows() {
        return new Object[][] {
                {"1x Forest [Maybeboard{noDeck}{noPrice}]"}, {"1x Forest [Ideas{noDeck}]"},
                {"1x Forest [Companion]"}, {"//Maybeboard\n1 Forest"}, {"Companion\n1 Forest"},
                {"1x Forest [Commander{top}]"}, {"SB: 1x Toph, the First Metalbender [Commander{top}]"},
                {"0x Forest [Land]"}, {"1001x Forest [Land]"}, {"99999999999999999x Forest"},
                {"2x Creature [Ramp]"}, {"<Deck><Cards Name=\"Forest\" Quantity=\"4\" /></Deck>"},
                {"Name,Quantity,Set\nForest,4,M13"}, {"1 Bayou|SUM|1"}
        };
    }

    @Test(dataProvider = "rejectedRows") public void unsupportedRowsBlockImportInsteadOfChangingTheDeck(String text) {
        var preview = DeckImport.preview(text);
        assertFalse(preview.problems().isEmpty(), text);
        var error = expectThrows(IllegalArgumentException.class, () -> preview.open("Rejected", new CardCatalog(java.util.List.of())));
        assertTrue(error.getMessage().contains("Line 1:"), error.getMessage());
    }

    @Test public void unknownNamesAndWrongSetsReportOriginalLinesAndCannotPartiallySave() throws Exception {
        var good = DeckImport.preview("4 Forest");
        var directory = Files.createTempDirectory(Path.of("target"), "deck-import-regression-");
        try (var engine = new DesktopEngine(catalog(good), directory)) {
            var initial = new JsonObject(); initial.addProperty("text", "4 Forest"); initial.addProperty("name", "Saved deck");
            var saved = (Map<?, ?>)engine.dispatch("import", initial);
            var request = new JsonObject(); request.addProperty("name", "Invalid deck");
            request.addProperty("text", "4x Forest [Land]\n1x Definitely Not A Real Card (SUM) 283 [Removal]\n1x Bayou (M13) 283 [Land]");
            var preview = DeckImport.preview(request.get("text").getAsString());
            assertEquals(preview.entries().size(), 1);
            assertEquals(preview.problems().stream().map(DeckImport.Problem::line).toList(), java.util.List.of(2, 3));
            var error = expectThrows(IllegalArgumentException.class, () -> engine.dispatch("import", request));
            assertTrue(error.getMessage().contains("Line 2:"));
            assertTrue(error.getMessage().contains("Definitely Not A Real Card"));
            assertTrue(error.getMessage().contains("Line 3:"));
            assertEquals(((Map<?, ?>)engine.dispatch("snapshot", null)).get("id"), saved.get("id"));
            request.addProperty("text", "1x Definitely Not A Real Card [Removal]");
            assertFalse(expectThrows(IllegalArgumentException.class, () -> engine.dispatch("import", request))
                    .getMessage().contains("Paste at least one card"));
            request.addProperty("text", " \r\n");
            assertEquals(expectThrows(IllegalArgumentException.class, () -> engine.dispatch("import", request))
                    .getMessage(), "Paste at least one card");
        }
    }
}
