package forge.game.event;

import forge.game.player.PlayerView;
import forge.game.event.GameEventCardChangeZone.Origin;

/** Successful rules draws after opening hands; contains no card identity. */
public record GameEventCardDrawn(PlayerView player, Origin origin) implements GameEvent {
    public GameEventCardDrawn {
        if (origin == null) origin = Origin.OTHER;
    }

    public GameEventCardDrawn(PlayerView player) { this(player, Origin.OTHER); }

    @Override public <T> T visit(IGameEventVisitor<T> visitor) { return visitor.visit(this); }
}
