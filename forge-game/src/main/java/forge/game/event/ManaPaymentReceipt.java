package forge.game.event;

import forge.card.MagicColor;
import forge.card.mana.ManaAtom;
import forge.card.mana.ManaCostShard;
import forge.game.card.Card;
import forge.game.card.CardView;
import forge.game.mana.Mana;
import forge.game.mana.ManaCostBeingPaid;
import forge.game.player.Player;
import forge.game.player.PlayerView;
import forge.game.spellability.SpellAbility;

import java.util.*;

/** Actual payment facts copied by their rules owner. Views stay inside the
 * provider; an audience projection must admit identities before export. */
public record ManaPaymentReceipt(String kind, PlayerView payer, String origin,
        String executionId, String producerExecutionId, CardView source, String producerMode,
        CardView paidForSource, List<BillPart> billBefore, List<BillPart> billAfter,
        Map<String, Integer> produced, Map<String, Integer> spent,
        Map<String, Integer> poolAfter, Map<String, Integer> sourceCounters,
        String restoredExecutionId) implements Event {
    public record BillPart(String symbol, int count) { }

    public ManaPaymentReceipt {
        billBefore = billBefore == null ? null : List.copyOf(billBefore);
        billAfter = billAfter == null ? null : List.copyOf(billAfter);
        produced = copy(produced); spent = copy(spent); poolAfter = copy(poolAfter); sourceCounters = copy(sourceCounters);
    }

    private static Map<String, Integer> copy(Map<String, Integer> value) {
        return value == null ? null : Map.copyOf(value);
    }

    public static List<BillPart> bill(ManaCostBeingPaid cost) {
        if (cost == null) return null;
        var parts = new ArrayList<BillPart>();
        for (var shard : cost.getDistinctShards()) {
            int count = cost.getUnpaidShards(shard);
            if (count > 0) parts.add(shard == ManaCostShard.GENERIC
                    ? new BillPart("{" + count + "}", 1) : new BillPart(shard.toString(), count));
        }
        if (cost.getXcounter() > 0) parts.add(new BillPart("{X}", cost.getXcounter()));
        return List.copyOf(parts);
    }

    public static Map<String, Integer> pool(Player player) {
        var counts = new LinkedHashMap<String, Integer>();
        for (byte color : ManaAtom.MANATYPES) {
            int amount = player.getManaPool().getAmountOfColor(color);
            if (amount > 0) counts.put(MagicColor.toShortString(color), amount);
        }
        return Map.copyOf(counts);
    }

    public static Map<String, Integer> counters(Card card) {
        if (card == null) return null;
        var result = new LinkedHashMap<String, Integer>();
        for (var entry : card.getCounters().entrySet()) result.put(entry.getElement().getName(), entry.getCount());
        return Map.copyOf(result);
    }

    public static ManaPaymentReceipt produced(Player player, SpellAbility producer, Iterable<Mana> units) {
        var counts = new LinkedHashMap<String, Integer>();
        for (Mana unit : units) counts.merge(MagicColor.toShortString(unit.getColor()), 1, Integer::sum);
        var paid = player.getPaidForSA();
        var source = producer == null ? null : producer.getHostCard();
        return new ManaPaymentReceipt("produced", player.getView(), player.getGame().getExecutions().paymentOrigin(),
                paid == null ? null : paid.getExecutionId(), producer == null ? null : producer.getExecutionId(),
                CardView.get(source), producer == null || producer.getDescription() == null || producer.getDescription().isBlank()
                        ? null : producer.getDescription(),
                paid == null ? null : CardView.get(paid.getHostCard()), null, null,
                counts, null, pool(player), counters(source), null);
    }

    public static ManaPaymentReceipt spent(Player player, SpellAbility paid, Mana unit,
            List<BillPart> before, ManaCostBeingPaid after) {
        return new ManaPaymentReceipt("spent", player.getView(), player.getGame().getExecutions().paymentOrigin(),
                paid == null ? null : paid.getExecutionId(), null, CardView.get(unit.getSourceCard()),
                null, paid == null ? null : CardView.get(paid.getHostCard()),
                before, bill(after), null, Map.of(MagicColor.toShortString(unit.getColor()), 1),
                pool(player), counters(unit.getSourceCard()), null);
    }

    public static ManaPaymentReceipt payment(String kind, Player player, SpellAbility paid,
            ManaCostBeingPaid original, ManaCostBeingPaid remaining, String origin) {
        return new ManaPaymentReceipt(kind, player.getView(), origin,
                paid == null ? null : paid.getExecutionId(), null,
                paid == null ? null : CardView.get(paid.getHostCard()), null,
                paid == null ? null : CardView.get(paid.getHostCard()), bill(original), bill(remaining),
                null, null, pool(player), null, null);
    }

    public static ManaPaymentReceipt restored(Player player, SpellAbility paid) {
        return new ManaPaymentReceipt("restored", player.getView(), player.getGame().getExecutions().paymentOrigin(),
                paid == null ? null : paid.getExecutionId(), null,
                paid == null ? null : CardView.get(paid.getHostCard()), null,
                paid == null ? null : CardView.get(paid.getHostCard()), null, null, null, null,
                pool(player), null, paid == null ? null : paid.getExecutionId());
    }

}
