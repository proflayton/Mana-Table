package forge.game.event;

import forge.game.player.PlayerView;

/** Successful rules draws after opening hands; contains no card identity. */
public record GameEventCardDrawn(PlayerView player) implements GameEvent {
    @Override public <T> T visit(IGameEventVisitor<T> visitor) { return visitor.visit(this); }
}
