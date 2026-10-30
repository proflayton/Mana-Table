package forge.api;

import java.util.List;
import java.util.Map;
import java.util.Objects;

/** One audience's complete decision context at the committed board publication. */
final class CurrentDecisionProjection {
    record Capture(String decisionId, long boardRevision, long sourceVersion,
                   String executionId, String executionStep, Map<String, Object> promptFacts,
                   Map<String, Object> automatedFacts, List<Integer> playerChoices, List<String> poolManaColors) {
        Capture {
            promptFacts = PublishedDecisionFacts.freeze(promptFacts);
            automatedFacts = PublishedDecisionFacts.freeze(automatedFacts);
            playerChoices = List.copyOf(playerChoices);
            poolManaColors = List.copyOf(poolManaColors);
        }

        boolean matches(String decision, long board, long version, String execution, String step) {
            return Objects.equals(decisionId, decision) && boardRevision == board && sourceVersion == version
                    && Objects.equals(executionId, execution) && Objects.equals(executionStep, step);
        }
    }

    private CurrentDecisionProjection() { }
}
