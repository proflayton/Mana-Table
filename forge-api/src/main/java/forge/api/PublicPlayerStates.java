package forge.api;

import forge.game.player.Player;
import forge.game.card.CounterEnumType;

import java.util.ArrayList;
import java.util.Comparator;
import java.util.List;
import java.util.Locale;
import java.util.Map;

/** An immutable allowlist of public player mechanics, independent of their source cards. */
final class PublicPlayerStates {
    record State(String id, String label, String kind, Integer value, Integer maximum, String detail) { }

    private PublicPlayerStates() { }

    static List<State> of(Player player) {
        var states = new ArrayList<State>();
        if (player.getSpeed() > 0) states.add(new State("speed", "Speed", "progress", player.getSpeed(), 4,
                "Speed starts at 1. Once each of this player's turns, when an opponent loses life, speed increases by 1, up to 4. "
                        + "It persists after its source leaves. Cards may have abilities at max speed."));
        if (player.isMonarch()) states.add(new State("monarch", "Monarch", "status", null, null,
                "At this player's end step, they draw a card. A player whose creature deals combat damage to them becomes the monarch."));
        if (player.hasInitiative()) states.add(new State("initiative", "Initiative", "status", null, null,
                "When this player takes the initiative and at their upkeep, they venture into Undercity. "
                        + "A player whose creature deals combat damage to them takes the initiative."));
        if (player.hasBlessing()) states.add(new State("citys-blessing", "City's blessing", "status", null, null,
                "This player keeps the city's blessing for the rest of the game. It enables abilities that refer to it."));
        MatchSession.publicCounters(player.getView()).entrySet().stream().sorted(Comparator
                        .comparing((Map.Entry<String, Integer> entry) -> !entry.getKey().equals(CounterEnumType.POISON.getName()))
                        .thenComparing(entry -> entry.getKey().toLowerCase(Locale.ROOT)))
                .forEach(entry -> states.add(new State("counter:" + entry.getKey().toLowerCase(Locale.ROOT), entry.getKey(), "counter", entry.getValue(), null,
                        entry.getKey() + " counters on this player. Cards and effects describe how they are used.")));
        return List.copyOf(states);
    }
}
