package forge.game;

import java.util.List;

/** Explicitly public table mechanics copied at the engine's publication boundary. */
public final class PublicTableStates {
    private PublicTableStates() { }

    public static List<PublicState> snapshot(Game game) {
        Boolean night = game.getDayTime();
        if (night == null) return List.of();
        return List.of(new PublicState("day-night", night ? "Night" : "Day", PublicState.Kind.STATUS, null, null,
                night ? "It is night for the whole table."
                    : "It is day for the whole table.", "day-night"));
    }
}
