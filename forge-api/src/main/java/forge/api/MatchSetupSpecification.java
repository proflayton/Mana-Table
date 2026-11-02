package forge.api;

import com.google.gson.JsonElement;
import java.util.*;

/** Frozen public rules setup. Roles describe encounter sides, never hidden seat knowledge. */
record MatchSetupSpecification(String rulesetId, List<MatchSetupSpecification.Seat> seats) {
    static final String FREE_FOR_ALL = "commander.free-for-all.v1";
    static final String COOPERATIVE = "commander.cooperative.v1";
    record Seat(int index, int teamId, String role) { }

    MatchSetupSpecification {
        seats = List.copyOf(seats);
        if (!List.of(FREE_FOR_ALL, COOPERATIVE).contains(rulesetId) || seats.size() < 2 || seats.size() > 6)
            throw new IllegalArgumentException("Choose a supported Commander encounter setup");
        for (int i = 0; i < seats.size(); i++) {
            var seat = seats.get(i);
            if (seat.index() != i || seat.teamId() < 0)
                throw new IllegalArgumentException("Every seat needs one current index and a valid team");
        }
        if (FREE_FOR_ALL.equals(rulesetId)) {
            if (seats.stream().anyMatch(seat -> !"participant".equals(seat.role()))
                    || seats.stream().map(Seat::teamId).distinct().count() != seats.size())
                throw new IllegalArgumentException("Free-for-all seats need separate teams");
        } else {
            var party = seats.stream().filter(seat -> "party".equals(seat.role())).toList();
            var opposition = seats.stream().filter(seat -> "opposition".equals(seat.role())).toList();
            if (party.size() < 1 || party.size() > 4 || opposition.size() < 1 || opposition.size() > 2
                    || party.size() + opposition.size() != seats.size() || !"party".equals(seats.get(0).role())
                    || party.stream().map(Seat::teamId).distinct().count() != 1
                    || opposition.stream().map(Seat::teamId).distinct().count() != 1
                    || party.get(0).teamId() == opposition.get(0).teamId())
                throw new IllegalArgumentException("Use one party of 1–4 seats against one AI side of 1–2 seats");
        }
    }

    boolean cooperative() { return COOPERATIVE.equals(rulesetId); }
    int partyTeam() { return seats.stream().filter(seat -> seat.role().equals("party")).findFirst().orElseThrow().teamId(); }
    static MatchSetupSpecification freeForAll(int count) {
        var seats = new ArrayList<Seat>();
        for (int i = 0; i < count; i++) seats.add(new Seat(i, i, "participant"));
        return new MatchSetupSpecification(FREE_FOR_ALL, seats);
    }
    static MatchSetupSpecification read(JsonElement value) {
        try {
            var object = value.getAsJsonObject();
            if (!object.get("rulesetId").getAsJsonPrimitive().isString()) throw new IllegalArgumentException();
            var seats = new ArrayList<Seat>();
            for (var entry : object.getAsJsonArray("seats")) {
                var seat = entry.getAsJsonObject();
                if (seats.size() == 6 || !seat.get("role").getAsJsonPrimitive().isString()
                        || !seat.get("index").getAsJsonPrimitive().isNumber() || !seat.get("teamId").getAsJsonPrimitive().isNumber())
                    throw new IllegalArgumentException();
                seats.add(new Seat(seat.get("index").getAsBigDecimal().intValueExact(),
                        seat.get("teamId").getAsBigDecimal().intValueExact(), seat.get("role").getAsString()));
            }
            return new MatchSetupSpecification(object.get("rulesetId").getAsString(), seats);
        } catch (RuntimeException invalid) { throw new IllegalArgumentException("Choose a valid current match setup", invalid); }
    }
    static Map<String, Object> capabilities(boolean automated) {
        return Map.of("rulesetIds", automated ? List.of(FREE_FOR_ALL, COOPERATIVE) : List.of(FREE_FOR_ALL),
                "maximumSeats", 6, "maximumPartySeats", 4, "maximumOppositionSeats", 2);
    }
}
