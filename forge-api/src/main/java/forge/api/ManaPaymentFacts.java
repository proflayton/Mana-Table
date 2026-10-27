package forge.api;

import forge.card.MagicColor;
import forge.card.mana.ManaCostShard;
import forge.game.card.CardView;
import forge.game.mana.ManaCostBeingPaid;
import forge.gamemodes.match.input.InputPayMana;

import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.function.Function;

/** Fresh remaining-bill facts, copied only at the current input's state boundary. */
final class ManaPaymentFacts {
    static List<String> unpaid(InputPayMana payment) {
        var remaining = payment.getRemainingManaCost();
        if (remaining == null) return List.of();
        var result = new ArrayList<String>();
        for (var shard : remaining.getDistinctShards()) {
            int count = remaining.getUnpaidShards(shard);
            if (shard == ManaCostShard.GENERIC && count > 128) result.add("{" + count + "}");
            else for (int i = 0; i < count; i++) result.add(shard.toString());
        }
        for (int i = 0; i < remaining.getXcounter(); i++) result.add(ManaCostShard.X.toString());
        return List.copyOf(result);
    }

    static List<Map<String, Object>> parts(ManaCostBeingPaid remaining) {
        if (remaining == null) return List.of();
        var result = new ArrayList<Map<String, Object>>();
        for (var shard : remaining.getDistinctShards()) {
            int count = remaining.getUnpaidShards(shard);
            if (count <= 0) continue;
            result.add(shard == ManaCostShard.GENERIC ? Map.of("symbol", "{" + count + "}", "count", 1)
                    : Map.of("symbol", shard.toString(), "count", count));
        }
        if (remaining.getXcounter() > 0) result.add(Map.of("symbol", ManaCostShard.X.toString(), "count", remaining.getXcounter()));
        return List.copyOf(result);
    }

    static Map<String, Object> describe(InputPayMana payment, Function<CardView, String> identity) {
        var ability = payment.getPaidForAbility();
        if (!payment.isPaymentActive() || ability == null || payment.getPayer() == null) return Map.of();
        return Map.of("payerId", payment.getPayer().getId(), "unpaidManaParts", parts(payment.getRemainingManaCost()),
                "fundingContext", NumericDecisionProjection.funding(ability, payment.getPayer(), identity),
                "usablePoolManaColors", payment.getUsablePoolManaColors().stream().map(MagicColor::toShortString).toList());
    }

    private ManaPaymentFacts() { }
}
