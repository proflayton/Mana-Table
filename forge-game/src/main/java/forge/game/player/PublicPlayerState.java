package forge.game.player;

import java.util.Objects;

/** A copied public mechanic value, containing no live game objects or card identities. */
public record PublicPlayerState(String id, String label, Kind kind, Integer value, Integer maximum, String detail) {
    public enum Kind {
        COUNTER("counter"), PROGRESS("progress"), STATUS("status");

        private final String wireName;
        Kind(String wireName) { this.wireName = wireName; }
        public String wireName() { return wireName; }
    }

    public PublicPlayerState {
        requireText(id, "id");
        requireText(label, "label");
        requireText(detail, "detail");
        Objects.requireNonNull(kind, "kind");
        if (kind == Kind.STATUS) {
            if (value != null || maximum != null) throw new IllegalArgumentException("Status values have no numeric range");
        } else {
            if (value == null || value <= 0) throw new IllegalArgumentException("Only active positive mechanic values are public states");
            validateMaximum(kind, maximum);
            if (maximum != null && value > maximum) throw new IllegalArgumentException("Progress exceeds its maximum");
        }
    }

    static void validateMaximum(Kind kind, Integer maximum) {
        if (kind == Kind.PROGRESS && (maximum == null || maximum <= 0))
            throw new IllegalArgumentException("Progress needs a positive maximum");
        if (kind != Kind.PROGRESS && maximum != null)
            throw new IllegalArgumentException("Only progress has a maximum");
    }

    static void requireText(String value, String field) {
        if (value == null || value.isBlank()) throw new IllegalArgumentException("Missing public-state " + field);
    }
}
