package forge.api;

import java.util.List;

/** Public identities frozen before game creation; none are game-action capabilities. */
record EncounterContext(String partyId, String encounterId, MatchSetupSpecification specification,
                        List<EncounterContext.Participant> participants) {
    record Participant(String participantId, int index, int teamId, String role, String deckSelectionId) { }
    EncounterContext { participants = List.copyOf(participants); }
}
