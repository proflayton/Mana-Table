package forge.api;

/** A completed scoped response-fact preparation, never consent or gameplay credit. */
record AutomatedPriorityResponsesPrepared(int schemaVersion, String status, String gameId, int seatId,
        String decisionId, long boardRevision, long publicActivitySequence, String responseAvailability) { }
