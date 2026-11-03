package forge.api;

import forge.game.zone.ZoneType;
import forge.player.PlayerControllerHuman;
import org.testng.annotations.Test;

import java.util.List;
import java.util.Map;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Timing metadata describes restrictions without creating a future action permission. */
public class ActionPhaseRestrictionTest {
    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void distinguishesOrdinaryDevelopmentFromAnActualDrawStepOnlyOpportunity() throws Exception {
        try (var game = new RulesScenario("action-phase-restrictions", Map.of(0,
                List.of("Rhys the Redeemed", "Concordant Crossroads", "Well of Knowledge",
                        "Forest", "Forest", "Plains")), "Forest", "Rhys the Redeemed", true)) {
            game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").equals("InputPassPriority")
                    && text(next.state(), "phaseKey").equals("DRAW"));
            var actor = game.table.game.getPlayers().get(0);
            var rhys = actor.getCardsIn(ZoneType.Battlefield).stream()
                    .filter(card -> card.getName().equals("Rhys the Redeemed")).findFirst().orElseThrow();
            var well = actor.getCardsIn(ZoneType.Battlefield).stream()
                    .filter(card -> card.getName().equals("Well of Knowledge")).findFirst().orElseThrow();
            var input = ((PlayerControllerHuman) actor.getController()).getInputQueue().getInput();
            var rhysRoutes = SourceActions.describe(input, rhys, "rhys", 1, card -> true).actions();
            assertFalse(rhysRoutes.isEmpty(), "The actual haste-granted Rhys has current activated routes");
            assertTrue(rhysRoutes.stream().allMatch(route ->
                    Boolean.FALSE.equals(ActionSemantics.describe(route).get("phaseRestricted"))));
            var wellRoutes = SourceActions.describe(input, well, "well", 1, card -> true).actions();
            assertFalse(wellRoutes.isEmpty(), "The draw-step-only ability is genuinely available now");
            assertTrue(wellRoutes.stream().allMatch(route ->
                    Boolean.TRUE.equals(ActionSemantics.describe(route).get("phaseRestricted"))));

            game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").equals("InputPassPriority")
                    && text(next.state(), "phaseKey").equals("MAIN1"));
            input = ((PlayerControllerHuman) actor.getController()).getInputQueue().getInput();
            assertTrue(SourceActions.describe(input, well, "well-main", 2, card -> true).actions().isEmpty(),
                    "Waiting really loses the Well's current opportunity; phase metadata must prevent this defer");
            assertFalse(SourceActions.describe(input, rhys, "rhys-main", 2, card -> true).actions().isEmpty(),
                    "Ordinary Rhys development retains a fresh rules-owned main-phase offer");
        }
    }
}
