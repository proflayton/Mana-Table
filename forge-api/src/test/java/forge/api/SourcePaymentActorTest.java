package forge.api;

import com.google.gson.Gson;
import forge.game.ability.AbilityFactory;
import forge.game.mana.ManaCostBeingPaid;
import forge.game.zone.ZoneType;
import forge.gamemodes.match.input.InputPayManaOfCostPayment;
import forge.player.PlayerControllerHuman;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.util.List;
import java.util.Map;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** An owned payment input can name a payer different from its human controller. */
public class SourcePaymentActorTest {
    private static final Gson JSON = new Gson();

    @Test(timeOut = 90000)
    public void currentPaymentPayerOwnsTheInvocationWhileTheViewerOwnsTheDecision() throws Exception {
        try (var game = new RulesScenario("source-query-controlled-payer", Map.of(1, List.of("Island")))) {
            game.until(input -> input.seat() == 0 && text(input.prompt(), "inputType").equals("InputPassPriority")
                    && text(input.state(), "phaseKey").equals("MAIN1"));
            var payment = HeadlessPlatform.query(game.table, () -> {
                var viewer = game.table.game.getRegisteredPlayers().get(0);
                var payer = game.table.game.getRegisteredPlayers().get(1);
                var controller = (PlayerControllerHuman)viewer.getController();
                var source = payer.getCardsIn(ZoneType.Battlefield).get(0);
                var ability = AbilityFactory.getAbility("AB$ Draw | Cost$ U | NumCards$ 1", source);
                ability.setActivatingPlayer(payer);
                var input = new InputPayManaOfCostPayment(controller,
                        new ManaCostBeingPaid(ability.getPayCosts().getTotalMana()), ability, payer, null, true);
                controller.getInputQueue().setInput(input);
                return input;
            });
            var input = game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").startsWith("InputPayMana"));
            var island = zone(input.state().getAsJsonArray("players").get(1).getAsJsonObject(), "Battlefield")
                    .getAsJsonArray("cards").get(0).getAsJsonObject();
            var catalog = JSON.toJsonTree(game.session(0).sourceActions(request(input,
                    Map.of("key", text(island, "key"))))).getAsJsonObject();
            assertEquals(catalog.getAsJsonArray("actions").size(), 1);
            assertNotEquals(payment.getPayer().getId(), input.state().get("viewerId").getAsInt());
            assertTrue(game.answer(input, Map.of("action", "useSourceAction", "key", text(catalog, "sourceCardKey"),
                    "actionId", text(catalog.getAsJsonArray("actions").get(0).getAsJsonObject(), "id"))));
            game.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").equals("InputPassPriority")
                    && zone(next.state().getAsJsonArray("players").get(1).getAsJsonObject(), "Battlefield")
                        .getAsJsonArray("cards").get(0).getAsJsonObject().get("tapped").getAsBoolean());
            HeadlessPlatform.query(game.table, () -> {
                assertTrue(game.table.game.getRegisteredPlayers().get(1).getCardsIn(ZoneType.Battlefield).get(0).isTapped(),
                        "The current input's actual payer, rather than the viewer, activates the exact offered Island");
                assertEquals(payment.getRemainingManaCost().getConvertedManaCost(), 0);
                assertEquals(game.table.game.getRegisteredPlayers().get(0).getManaPool().totalMana(), 0);
                assertNull(game.session(0).state().get("error"));
                Files.writeString(game.evidence.resolve("controlled-payer-outcome.json"), JSON.toJson(Map.of(
                        "before", input.state(), "after", game.state(0), "catalog", catalog,
                        "payerId", payment.getPayer().getId())));
                return null;
            });
        }
    }
}
