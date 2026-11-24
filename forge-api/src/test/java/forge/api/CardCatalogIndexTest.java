package forge.api;

import forge.card.CardRarity;
import forge.card.CardRules;
import forge.item.PaperCard;
import org.testng.annotations.Test;

import java.util.List;

import static org.testng.Assert.*;

public class CardCatalogIndexTest {
    private static PaperCard card(String name, String oracle, String edition) {
        var rules = new CardRules.Reader().readCard(List.of("Name:" + name, "ManaCost:1",
                "Types:Artifact", "Oracle:" + oracle));
        return new PaperCard(rules, edition, CardRarity.Common);
    }

    private static PaperCard doubleFaced(String edition, String frontType, String backType, String marker) {
        var rules = new CardRules.Reader().readCard(List.of("Name:Shared front", "ManaCost:1",
                "Types:" + frontType, "PT:2/2", "Oracle:" + marker + " front rules", "AlternateMode:DoubleFaced",
                "ALTERNATE", "Name:" + marker + " back", "ManaCost:no cost", "Types:" + backType,
                "PT:3/3", "Oracle:" + marker + " back rules"));
        return new PaperCard(rules, edition, CardRarity.Rare);
    }

    private static CardCatalog.Query query(String text, int offset, int limit) {
        return new CardCatalog.Query(text, null, null, offset, limit);
    }

    private static List<String> ids(CardCatalog.Page page) {
        return page.cards().stream().map(CardCatalog.CardInfo::id).toList();
    }

    @Test public void samePrintedNameDoesNotMergeDistinctFacesTypesOrOracleTextAcrossPrintings() {
        var alpha = doubleFaced("AAA", "Creature Elf", "Creature Wolf", "AlphaSentinel");
        var beta = doubleFaced("BBB", "Artifact Creature Golem", "Artifact Creature Beast", "BetaSentinel");
        assertEquals(alpha.getName(), beta.getName(), "The printings intentionally share their exact raw name");
        var catalog = new CardCatalog(List.of(beta.getFoiled(), alpha.getFoiled(), beta, alpha));
        var alphaIds = List.of(CardCatalog.id(alpha), CardCatalog.id(alpha.getFoiled()));
        var betaIds = List.of(CardCatalog.id(beta), CardCatalog.id(beta.getFoiled()));
        assertEquals(ids(catalog.search(query("AlphaSentinel front rules", 0, 20))), alphaIds);
        assertEquals(ids(catalog.search(query("AlphaSentinel back rules", 0, 20))), alphaIds);
        assertEquals(ids(catalog.search(query("BetaSentinel back", 0, 20))), betaIds);
        assertEquals(ids(catalog.browse(query("", 0, 20), "Wolf", "name", false)), alphaIds);
        assertEquals(ids(catalog.browse(query("", 0, 20), "Artifact", "name", false)), betaIds);
        assertEquals(catalog.resolve(CardCatalog.id(alpha)).getRules().getOtherPart().getName(), "AlphaSentinel back");
        assertEquals(catalog.printings("Shared front").size(), 4, "Every printing and foil remains in the name group");
    }

    @Test public void normalizationPreservesUnicodeAliasesLigaturesAndRawNameUniqueness() {
        var composed = card("Caf\u00e9", "First rules", "BBB");
        var decomposed = card("Cafe\u0301", "Second rules", "AAA");
        var ligatures = card("\u00c6ther\u2019s \u0152uvre", "Third rules", "CCC");
        var catalog = new CardCatalog(List.of(composed, ligatures, decomposed));
        var cafeIds = List.of(CardCatalog.id(decomposed), CardCatalog.id(composed));
        assertEquals(ids(catalog.search(query("CAF\u00c9", 0, 20))), cafeIds);
        assertEquals(catalog.printings("cafe").stream().map(CardCatalog.CardInfo::id).toList(), cafeIds);
        assertEquals(catalog.named("CAFE").id(), CardCatalog.id(decomposed), "The first sorted alias remains the representative");
        assertEquals(ids(catalog.search(query("AETHER'S OEUVRE", 0, 20))), List.of(CardCatalog.id(ligatures)));
        assertEquals(query("\u00c6THER\u2019S \u0152UVRE", 0, 20).text(), "aether's oeuvre");
        var unique = catalog.browse(query("cafe", 0, 20), "", "name", true);
        assertEquals(unique.total(), 2, "Normalized aliases retain their two distinct raw printed names");
        assertEquals(unique.catalogTotal(), 3);
    }

    @Test public void duplicateIdsKeepTheFirstPrintingAndStableFoilPagination() {
        var first = card("A name", "FirstSentinel", "AAA");
        var duplicate = card("A name", "DiscardedSentinel", "AAA");
        var other = card("B name", "Other rules", "BBB");
        var catalog = new CardCatalog(List.of(other, first, duplicate, first.getFoiled(), other));
        assertEquals(catalog.size(), 3);
        assertSame(catalog.resolve(CardCatalog.id(first)), first);
        assertEquals(catalog.search(query("DiscardedSentinel", 0, 20)).total(), 0);
        assertEquals(ids(catalog.search(query("", 0, 2))), List.of(CardCatalog.id(first), CardCatalog.id(first.getFoiled())));
        assertEquals(ids(catalog.search(query("", 2, 2))), List.of(CardCatalog.id(other)));
        assertEquals(catalog.search(query("", 2, 2)).catalogTotal(), 3);
        assertEquals(ids(catalog.browse(query("", 0, 20), "", "name", true)), List.of(CardCatalog.id(first), CardCatalog.id(other)));
        assertThrows(UnsupportedOperationException.class, () -> catalog.printings("A name").clear());
    }

    @Test public void aNewConstructorUsesItsOwnCurrentRawCorpus() {
        var oldPrinting = card("Same identity", "OldSentinel", "AAA");
        var replacement = card("Same identity", "NewSentinel", "AAA");
        assertEquals(CardCatalog.id(oldPrinting), CardCatalog.id(replacement));
        var oldCatalog = new CardCatalog(List.of(oldPrinting));
        var newCatalog = new CardCatalog(List.of(replacement));
        assertEquals(oldCatalog.search(query("OldSentinel", 0, 20)).total(), 1);
        assertEquals(oldCatalog.search(query("NewSentinel", 0, 20)).total(), 0);
        assertEquals(newCatalog.search(query("NewSentinel", 0, 20)).total(), 1);
        assertEquals(newCatalog.search(query("OldSentinel", 0, 20)).total(), 0);
        assertSame(newCatalog.resolve(CardCatalog.id(replacement)), replacement);
    }
}
