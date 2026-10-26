package forge.api;

import com.google.gson.Gson;
import forge.game.card.Card;
import forge.game.zone.ZoneType;
import org.testng.annotations.Test;

import java.util.List;
import java.util.Locale;
import java.util.Map;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Actual private peek, remembered library LKI and subsequent optional move. */
public class CardRulesTextPrivacyTest {
    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void privatePeekRememberedObjectsNeverBecomePublicCardRulesText() throws Exception {
        String sentinel = "Desert of the Indomitable";
        try (var scenario = new RulesScenario("private-peek-rules-text", Map.of(0,
                List.of("Into the Wilds", "Privileged Position")), sentinel, "Rhys the Redeemed")) {
            var peek = scenario.until(input -> text(input.prompt(), "kind").equals("reveal"));
            assertEquals(peek.seat(), 0, "Only the actual controller looks at its library card");
            assertTrue(peek.prompt().toString().contains(sentinel), "The authorized private peek still shows its card");
            assertTrue(scenario.answer(peek, Map.of("choices", List.of())));
            var move = scenario.until(input -> text(input.prompt(), "inputType").equals("InputConfirm"));
            assertEquals(move.seat(), 0);

            String publicJson = scenario.table.game.getAction().readState(() -> {
                var owner = scenario.table.game.getRegisteredPlayers().get(0);
                Card source = owner.getCardsIn(ZoneType.Battlefield).stream()
                        .filter(card -> card.getName().equals("Into the Wilds")).findFirst().orElseThrow();
                assertTrue(source.getRemembered().iterator().hasNext(), "The real NoReveal effect remembered its private top card");
                assertTrue(source.getView().getText().contains(sentinel), "The legacy engine debug text actually contains the hidden name");
                assertTrue(java.util.stream.StreamSupport.stream(source.getRemembered().spliterator(), false).anyMatch(value -> value instanceof Card card
                        && card.getName().equals(sentinel) && card.isInZone(ZoneType.Library)));
                return new Gson().toJson(SpectatorObservation.project(scenario.table,
                        move.state().get("boardRevision").getAsLong(), null));
            });
            assertFalse(publicJson.contains(sentinel), "An independent public audience receives no hidden remembered card name");
            for (int seat = 0; seat < 4; seat++) {
                var state = scenario.state(seat);
                var source = zone(player(state, move.state().get("viewerId").getAsInt()), "Battlefield")
                        .getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                        .filter(card -> text(card, "name").equals("Into the Wilds")).findFirst().orElseThrow();
                String rules = text(source, "text");
                assertFalse(rules.contains(sentinel), "No seat projection includes another card through unscoped remembered metadata");
                assertTrue(rules.toLowerCase(Locale.ROOT).contains("look at the top card of your library"));
                assertTrue(rules.toLowerCase(Locale.ROOT).contains("hexproof"), "Current granted abilities remain readable");
            }
            assertTrue(scenario.answer(move, Map.of("action", "cancel")));
            scenario.until(input -> text(input.prompt(), "inputType").equals("InputPassPriority")
                    && input.state().getAsJsonArray("stack").isEmpty());
        }
    }
}
