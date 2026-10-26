package forge.api;

import forge.card.CardStateName;
import forge.game.card.Card;
import forge.game.card.CardView;
import forge.game.player.Player;
import forge.game.player.PlayerView;
import forge.game.spellability.SpellAbility;
import forge.game.zone.ZoneType;
import forge.gamemodes.match.input.Input;
import org.apache.commons.lang3.StringUtils;

import java.util.ArrayList;
import java.util.HashMap;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.UUID;
import java.util.function.Function;

/** Viewer-private discovery, separate from the input-owned source-action catalog. */
final class PlayAccessProjection {
    // Numeric kind is the closed native PlayAccessKind enum: Cast=0, PlayLand=1.
    record Route(String id, int kind, String sourceCardId, String sourceName, String detail, boolean canStart) {
        Route withoutCurrentOffer() { return canStart ? new Route(id, kind, sourceCardId, sourceName, detail, false) : this; }
    }
    record Access(int playerId, String zone, String cardId, List<Route> routes) { }
    private final Map<String, String> ids = new HashMap<>();

    List<Access> project(Player actor, Iterable<PlayerView> players, Input currentInput,
            Function<CardView, Card> actual, Function<CardView, String> occurrence) {
        var result = new ArrayList<Access>();
        var retained = new HashSet<String>();
        PlayerView viewer = actor.getView();
        for (PlayerView owner : players) {
            for (ZoneType zone : List.of(ZoneType.Graveyard, ZoneType.Exile, ZoneType.Library)) {
                var cards = owner.getCards(zone);
                if (cards == null) continue;
                for (CardView face : cards) {
                    // A viewer's permission does not reveal a face. Library
                    // access is confined to the actual already-disclosed top.
                    if (face.getZone() != zone || face.isFaceDown() || !face.canBeShownTo(viewer)
                            || zone == ZoneType.Library && !face.equals(cards.get(0))) continue;
                    Card card = actual.apply(face);
                    // Prepared copies remain reachable through their permanent;
                    // duplicating that virtual face as an independent exile card
                    // would misrepresent its engine-owned source relationship.
                    if (card == null || card.getCurrentStateName() == CardStateName.PreparedSpell) continue;
                    String cardId = occurrence.apply(face);
                    var routes = new LinkedHashMap<String, Route>();
                    List<SpellAbility> offers = null;
                    for (SpellAbility ability : card.getPlayAccessAbilities(actor)) {
                        String signature = signature(ability);
                        String identity = cardId + ":" + signature;
                        retained.add(identity);
                        String id = ids.computeIfAbsent(identity, ignored -> UUID.randomUUID().toString());
                        Card granting = ability.getMayPlay() == null ? null : ability.getMayPlay().getHostCard();
                        if (granting != null && granting.isImmutable() && granting.getEffectSource() != null)
                            granting = granting.getEffectSource();
                        boolean publicGrant = granting == null || !granting.isFaceDown() && granting.getView().canBeShownTo(viewer);
                        String sourceId = granting != null && publicGrant ? occurrence.apply(granting.getView()) : "";
                        String sourceName = granting != null && publicGrant ? granting.getView().getCurrentState().getName() : "";
                        String detail = detail(ability);
                        if (offers == null) offers = currentInput == null ? List.of() : currentInput.getSourceActions(card);
                        boolean canStart = offers.stream().anyMatch(offer -> signature(offer).equals(signature));
                        routes.putIfAbsent(id, new Route(id, ability.isLandAbility() ? 1 : 0,
                                sourceId, sourceName, detail, canStart));
                    }
                    if (!routes.isEmpty()) result.add(new Access(owner.getId(), zone.name(), cardId, List.copyOf(routes.values())));
                }
            }
        }
        ids.keySet().retainAll(retained);
        return List.copyOf(result);
    }

    private static String signature(SpellAbility ability) {
        // Generated alternate abilities receive fresh engine object IDs. Their
        // form and granting static ability identify a stable display route;
        // no part of this key is a command permission or crosses the boundary.
        return (ability.isLandAbility() ? "land" : "spell") + ":" + ability.getCardStateName()
                + ":" + ability.getAlternativeCost() + ":" + ability.getPayCosts().toSimpleString()
                + ":" + (ability.getMayPlay() == null ? "" : ability.getMayPlay().getId())
                + ":" + ability.getDescription();
    }

    private static String detail(SpellAbility ability) {
        if (ability.isLandAbility()) return "Play land";
        String label = ability.getAlternativeCost() == null ? "Cast"
                : String.join(" ", StringUtils.splitByCharacterTypeCamelCase(ability.getAlternativeCost().name()));
        String cost = ability.getPayCosts().toSimpleString();
        return label + (cost.isBlank() ? "" : " " + cost);
    }

    static List<Access> withoutCurrentOffers(Object projected) {
        if (!(projected instanceof List<?> accesses)) return List.of();
        return accesses.stream().filter(Access.class::isInstance).map(Access.class::cast)
                .map(access -> new Access(access.playerId(), access.zone(), access.cardId(),
                        access.routes().stream().map(Route::withoutCurrentOffer).toList())).toList();
    }
}
