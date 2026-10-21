package forge.api;

import forge.game.player.Player;
import forge.game.player.PublicPlayerStateRegistry;

import java.util.List;

/** Wire adapter for the engine's immutable, explicitly public mechanic snapshots. */
final class PublicPlayerStates {
    record State(String id, String label, String kind, Integer value, Integer maximum, String detail) { }

    private PublicPlayerStates() { }

    static List<State> of(Player player) { return of(player, PublicPlayerStateRegistry.standard()); }

    static List<State> of(Player player, PublicPlayerStateRegistry registry) {
        return registry.snapshot(player).stream().map(state -> new State(state.id(), state.label(), state.kind().wireName(),
                state.value(), state.maximum(), state.detail())).toList();
    }
}
