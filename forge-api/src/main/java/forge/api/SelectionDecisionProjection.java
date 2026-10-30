package forge.api;

import java.util.List;
import java.util.Map;
import java.util.Objects;

/** Current target/list choices and their meaning, copied together for one audience. */
final class SelectionDecisionProjection {
    record Capture(String decisionId, long boardRevision, long sourceVersion,
                   String executionId, String executionStep, Map<String, Object> promptFacts,
                   Map<String, Object> automatedFacts, List<Integer> playerChoices) {
        Capture {
            promptFacts = PublishedDecisionFacts.freeze(promptFacts);
            automatedFacts = PublishedDecisionFacts.freeze(automatedFacts);
            playerChoices = List.copyOf(playerChoices);
        }

        boolean matches(String decision, long board, long version) {
            return Objects.equals(decisionId, decision) && boardRevision == board && sourceVersion == version;
        }
    }

    private SelectionDecisionProjection() { }
}
