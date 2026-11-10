package forge.api;

import forge.game.card.Card;
import forge.game.card.CardView;
import forge.game.player.Player;
import forge.game.zone.ZoneType;
import forge.gamemodes.match.input.InputSelectTargets;

import java.util.ArrayList;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.function.Function;
import java.util.function.Predicate;

/** Current negative target facts, never selection permissions or action handles. */
final class TargetRestrictionProjection {
    private static final int MAX_CARDS = 512, MAX_RESTRICTIONS = 256, MAX_DETAIL = 1024;

    private TargetRestrictionProjection() { }

    static List<Map<String, Object>> describe(InputSelectTargets input, Iterable<Card> cards, Player viewer,
            Predicate<CardView> publishedSelectable, Function<CardView, String> identity) {
        if (!input.isTargetSelectionActive()) return List.of();
        var result = new ArrayList<Map<String, Object>>();
        var seen = new HashSet<String>();
        int inspected = 0;
        for (var card : cards) {
            if (!publicBattlefield(card, viewer)) continue;
            if (++inspected > MAX_CARDS || result.size() == MAX_RESTRICTIONS) break;
            // The published selectable domain already came from this input's
            // exact gate. It needs no second evaluation or negative annotation.
            if (publishedSelectable.test(card.getView())) continue;
            var problem = input.cardSelectionProblem(card);
            if (problem == null) continue;
            String cardId = identity.apply(card.getView());
            if (cardId == null || cardId.isBlank() || !seen.add(cardId)) continue;
            var row = new LinkedHashMap<String, Object>();
            row.put("cardId", cardId);
            row.put("reason", problem.reason().wireName());
            row.put("detail", problem.detail().substring(0, Math.min(problem.detail().length(), MAX_DETAIL)));
            var source = problem.grantingSource();
            if (source != card && publicBattlefield(source, viewer)) {
                String sourceId = identity.apply(source.getView());
                if (sourceId != null && !sourceId.isBlank()) row.put("sourceCardId", sourceId);
            }
            result.add(Map.copyOf(row));
        }
        return List.copyOf(result);
    }

    private static boolean publicBattlefield(Card card, Player viewer) {
        return card != null && viewer != null && card.isInZone(ZoneType.Battlefield)
                && !card.isFaceDown() && card.getView().canBeShownTo(viewer.getView());
    }
}
