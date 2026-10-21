package forge.game;

import java.util.Objects;

/** Immutable human-readable public state; never includes scripts or live game objects. */
public record PublicState(String id, String label, Kind kind, Integer value, Integer maximum, String detail, String key)
        implements java.io.Serializable {
    public enum Kind {
        ABILITY("ability"), STATUS("status"), COUNTER("counter"), PROGRESS("progress");
        private final String wireName;
        Kind(String wireName) { this.wireName = wireName; }
        public String wireName() { return wireName; }
    }

    public PublicState {
        if (id == null || id.isBlank() || label == null || label.isBlank())
            throw new IllegalArgumentException("Public states require an id and label");
        Objects.requireNonNull(kind, "kind");
        Objects.requireNonNull(detail, "detail");
        Objects.requireNonNull(key, "key");
        if (kind == Kind.STATUS) {
            if (value != null || maximum != null) throw new IllegalArgumentException("Status has no numeric range");
        } else {
            if (value == null || value <= 0) throw new IllegalArgumentException("Active public values must be positive");
            if (kind == Kind.PROGRESS) {
                if (maximum == null || maximum <= 0 || value > maximum)
                    throw new IllegalArgumentException("Progress requires its actual positive maximum");
            } else if (maximum != null) throw new IllegalArgumentException("Only progress has a maximum");
        }
        if (kind == Kind.ABILITY && key.isBlank()) throw new IllegalArgumentException("Abilities require a public key");
    }
}
