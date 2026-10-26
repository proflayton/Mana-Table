package forge.api;

import forge.game.card.CardView;
import forge.game.zone.ZoneType;

/** Live rules wording only; CardView.getText also appends unscoped engine debug objects. */
final class CardRulesText {
    static String of(CardView card) {
        var text = new StringBuilder();
        append(text, card.getCurrentState().getRulesText());
        if (card.isSplitCard() && !card.isFaceDown() && card.getZone() != ZoneType.Stack && card.getZone() != ZoneType.Battlefield) {
            append(text, "(" + card.getLeftSplitState().getName() + ") " + card.getLeftSplitState().getAbilityText());
            append(text, "(" + card.getRightSplitState().getName() + ") " + card.getRightSplitState().getAbilityText());
        } else {
            // AbilityText includes current granted abilities and keywords. Using
            // printed oracle text here would discard legitimate rules changes.
            append(text, card.getCurrentState().getAbilityText());
        }
        return text.toString().trim().replace("\\r", "\r").replace("\\n", "\n");
    }

    private static void append(StringBuilder text, String part) {
        if (part == null || part.isEmpty()) return;
        if (!text.isEmpty()) text.append("\r\n\r\n");
        text.append(part);
    }

    private CardRulesText() { }
}
