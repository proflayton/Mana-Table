package forge.api;

import com.google.gson.Gson;
import forge.card.mana.ManaCost;
import forge.card.mana.ManaCostShard;
import forge.game.mana.ManaCostBeingPaid;
import org.testng.annotations.Test;

import java.util.List;
import java.util.Map;

import static org.testng.Assert.*;

public class ManaPaymentFactsTest {
    @Test(timeOut = 3000)
    public void distinctCountedPartsRetainHugeColoredAndGenericBillsWithoutExpandingOrMutatingThem() {
        var bill = new ManaCostBeingPaid(ManaCost.ZERO);
        bill.increaseGenericMana(129);
        bill.increaseShard(ManaCostShard.WHITE, 1_000_000_000);
        bill.increaseShard(ManaCostShard.BLUE, 2);
        var parts = ManaPaymentFacts.parts(bill);
        assertEquals(parts.size(), 3);
        assertTrue(parts.contains(Map.of("symbol", "{129}", "count", 1)));
        assertTrue(parts.contains(Map.of("symbol", "{W}", "count", 1_000_000_000)));
        assertTrue(parts.contains(Map.of("symbol", "{U}", "count", 2)));
        assertTrue(new Gson().toJson(parts).length() < 150, "Storage depends on shard kinds, not mana quantity");
        assertEquals(bill.getGenericManaAmount(), 129);
        assertEquals(bill.getUnpaidShards(ManaCostShard.WHITE), 1_000_000_000);
        assertEquals(ManaPaymentFacts.parts(bill), parts);
        assertThrows(UnsupportedOperationException.class, () -> parts.add(Map.of("symbol", "{G}", "count", 1)));
        assertThrows(UnsupportedOperationException.class, () -> parts.get(0).put("count", 0));
        assertEquals(ManaPaymentFacts.parts(new ManaCostBeingPaid(ManaCost.ZERO)), List.of());
    }
}
