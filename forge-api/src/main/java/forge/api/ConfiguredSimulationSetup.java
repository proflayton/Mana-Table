package forge.api;

import com.google.gson.JsonObject;
import forge.game.player.RegisteredPlayer;
import java.util.*;

/** Captured host-only qualification inputs. No encounter or player-progress authority. */
record ConfiguredSimulationSetup(MatchSetupSpecification specification, List<String> decks) {
    record Participant(int index, int playerId) { }
    record Started(ConfiguredSimulationSetup setup, List<Participant> participants, Map<String, Object> state) {
        Started { participants = List.copyOf(participants); }
    }

    ConfiguredSimulationSetup {
        decks = List.copyOf(decks);
        if (specification == null || specification.seats().size() != 4 || decks.size() != 4
                || decks.stream().anyMatch(value -> value == null || value.isBlank() || value.length() > 200))
            throw new IllegalArgumentException("Choose four current Commander decks and their simulation setup");
        if (specification.cooperative() && (specification.seats().stream().filter(seat -> seat.role().equals("party")).count() != 2
                || specification.seats().stream().filter(seat -> seat.role().equals("opposition")).count() != 2))
            throw new IllegalArgumentException("Configured cooperative simulations require two seats on each side");
    }

    static ConfiguredSimulationSetup read(JsonObject arguments) {
        try {
            var specification = MatchSetupSpecification.read(arguments.get("specification"));
            var supplied = arguments.getAsJsonArray("decks");
            if (supplied == null || supplied.size() != 4) throw new IllegalArgumentException();
            var decks = new ArrayList<String>();
            for (var value : supplied) {
                if (!value.isJsonPrimitive() || !value.getAsJsonPrimitive().isString()) throw new IllegalArgumentException();
                decks.add(value.getAsString());
            }
            return new ConfiguredSimulationSetup(specification, decks);
        } catch (RuntimeException invalid) {
            throw new IllegalArgumentException("Choose a valid four-seat configured simulation", invalid);
        }
    }

    List<Participant> participants(MatchTable table, List<RegisteredPlayer> registrations) {
        if (table == null || !table.format.equals("Commander") || table.partyId() != null || table.encounterId() != null
                || table.game.getRegisteredPlayers().size() != 4 || table.automatedSeats().size() != 4
                || registrations.size() != 4)
            throw new IllegalStateException("The simulation factory did not preserve the configured participants");
        var result = new ArrayList<Participant>(); var ids = new HashSet<Integer>();
        for (var seat : specification.seats()) {
            var supplied = registrations.get(seat.index());
            var matches = table.game.getRegisteredPlayers().stream().filter(player -> player.getRegisteredPlayer() == supplied).toList();
            if (matches.size() != 1) throw new IllegalStateException("The simulation factory changed a registered participant");
            var actual = matches.get(0);
            if (!MatchTable.isAutomated(actual) || actual.getId() < 0 || !ids.add(actual.getId()) || actual.getTeam() != seat.teamId())
                throw new IllegalStateException("The engine did not preserve the configured simulation team");
            result.add(new Participant(seat.index(), actual.getId()));
        }
        return List.copyOf(result);
    }
}
