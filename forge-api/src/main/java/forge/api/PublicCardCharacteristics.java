package forge.api;

import forge.card.GamePieceType;
import forge.game.card.CardView;
import forge.game.combat.CombatView;
import forge.game.player.PlayerView;

import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Objects;

/** Detached physical characteristics shared by every authorized card portrait.
 * Callers own disclosure admission, identities, actions and attachments. This
 * capture uses their established face/stat visibility without discovering rules
 * actions. Its values retain that caller's disclosure and privacy scope; they
 * contain no live engine views, action handles or KnownFace permission grant. */
final class PublicCardCharacteristics {
    static Map<String, Object> capture(CardView card, PlayerView viewer, CombatView combat, boolean hidden, boolean publicStats) {
        var face = card.getCurrentState();
        var defender = CombatDefenders.card(combat, card, viewer);
        var counters = new LinkedHashMap<String, Integer>();
        if (card.getCounters() != null) for (var entry : card.getCounters().entrySet())
            counters.put(entry.getElement().getName(), entry.getCount());
        var facts = new LinkedHashMap<String, Object>();
        facts.put("name", hidden ? "Face-down card" : face.getName());
        facts.put("type", hidden ? publicStats ? face.getType().toString() : "" : visibleType(card));
        facts.put("gamePieceType", gamePieceType(card, hidden));
        facts.put("manaCost", hidden ? "" : face.getManaCost().toString());
        facts.put("commanderTax", commanderTax(card, hidden));
        facts.put("power", publicStats ? face.getPower() : null);
        facts.put("toughness", publicStats ? face.getToughness() : null);
        facts.put("text", hidden ? "" : CardRulesText.of(card));
        facts.put("tapped", card.isTapped()); facts.put("phasedOut", card.isPhasedOut());
        facts.put("sick", card.isSick()); facts.put("damage", card.getDamage());
        facts.put("attacking", card.isAttacking()); facts.put("blocking", card.isBlocking());
        facts.put("counters", Collections.unmodifiableMap(counters));
        facts.put("defenderId", defender.playerId()); facts.put("defender", defender.label());
        facts.put("faceDown", hidden);
        facts.put("combatKeywords", publicStats ? keywords(face) : List.of());
        facts.put("blockingCapacity", card.getPublicBlockingCapacity(viewer));
        facts.put("states", publicStats ? PublicStates.of(card.getPublicStates(viewer)) : List.of());
        return Collections.unmodifiableMap(facts);
    }

    static int commanderTax(CardView card, boolean hidden) {
        return hidden || !card.isCommander() || card.getOwner() == null ? 0 : 2 * card.getOwner().getCommanderCast(card);
    }

    static String gamePieceType(CardView card, boolean hidden) {
        return hidden ? "card" : Objects.requireNonNullElse(card.getGamePieceType(), GamePieceType.CARD).name().toLowerCase(Locale.ROOT);
    }

    static String visibleType(CardView card) {
        String type = card.getCurrentState().getType().toString();
        return type.isBlank() && card.getGamePieceType() == GamePieceType.EFFECT ? "Effect" : type;
    }

    static List<String> keywords(CardView.CardStateView face) {
        return face.getPublicStates().stream().map(forge.game.PublicState::key).distinct().toList();
    }

    private PublicCardCharacteristics() { }
}
