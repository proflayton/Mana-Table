package forge.game.mulligan;

/** Consequences of the next redraw, supplied by the active mulligan policy. */
public record OpeningHandOffer(int handSize, int nextHandSize, int cardsToReturn, boolean freeMulligan) implements java.io.Serializable { }
