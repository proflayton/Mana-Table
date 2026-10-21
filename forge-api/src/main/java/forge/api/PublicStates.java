package forge.api;

import forge.game.PublicState;

import java.util.List;

/** Additive scalar wire contract shared by card and table public state. */
final class PublicStates {
    record State(String id, String label, String kind, Integer value, Integer maximum, String detail, String key) { }
    private PublicStates() { }
    static List<State> of(List<PublicState> states) {
        return states.stream().map(state -> new State(state.id(), state.label(), state.kind().wireName(),
                state.value(), state.maximum(), state.detail(), state.key())).toList();
    }
}
