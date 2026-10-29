package forge.api;

import com.google.gson.JsonObject;
import forge.StaticData;
import forge.deck.Deck;
import forge.game.ability.AbilityFactory;
import forge.game.card.Card;
import forge.game.player.Player;
import forge.game.player.PlayerController;
import forge.game.mana.ManaCostBeingPaid;
import forge.game.spellability.SpellAbility;
import forge.gamemodes.match.input.InputPayMana;
import forge.player.PlayerControllerHuman;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.charset.StandardCharsets;
import java.io.ByteArrayOutputStream;
import java.io.PrintStream;
import java.util.List;
import java.util.concurrent.atomic.AtomicBoolean;

import static org.testng.Assert.*;

/** Host-owned policy inputs must never become public seat capabilities or Forge strategy. */
public class AutomatedSeatBoundaryTest {
    private static final Path RESOURCES = Path.of("../forge-gui/res");

    private static void initialize() throws Exception {
        if (StaticData.instance() == null) EngineResources.load(RESOURCES);
    }

    @Test
    public void automatedControllersHaveAnExplicitRoleAndPrivateSeatCapability() throws Exception {
        initialize();
        var players = List.of(MatchTable.human(new Deck(), "Constructed", "Human"),
                MatchTable.automated(new Deck(), "Constructed", "Policy"));
        try (var table = new MatchTable("Constructed", players, RESOURCES,
                Files.createTempDirectory("automated-seat-boundary"))) {
            var human = table.game.getPlayers().get(0);
            var policy = table.game.getPlayers().get(1);
            assertFalse(MatchTable.isAutomated(human));
            assertTrue(MatchTable.isAutomated(policy));
            assertTrue(policy.getController() instanceof PlayerControllerHuman,
                    "The rules/input executor remains the production controller");
            assertFalse(policy.getController().isAI(), "Forge's strategic controller flag must remain false");
            assertTrue(((PlayerControllerHuman) policy.getController()).isExternallyAutomated());
            assertFalse(((PlayerControllerHuman) human.getController()).isExternallyAutomated());
            assertTrue(policy.getController().isFullControl(PlayerController.FullControlFlag.ChooseCostOrder));
            assertTrue(policy.getController().isFullControl(PlayerController.FullControlFlag.ChooseCostReductionOrderAndVariableAmount));
            assertTrue(policy.getController().isFullControl(PlayerController.FullControlFlag.ChooseManaPoolShard));
            assertTrue(policy.getController().isFullControl(PlayerController.FullControlFlag.LayerTimestampOrder));
            ((PlayerControllerHuman) policy.getController()).getYieldController().setAutoPassUntilEndOfTurn(true);
            assertFalse(((PlayerControllerHuman) policy.getController()).mayAutoPass(),
                    "Human yield preferences cannot answer an externally owned decision");
            assertEquals(table.automatedSeats(), List.of(table.automatedSeat(policy.getId())));
            assertThrows(IllegalArgumentException.class, () -> table.automatedSeat(human.getId()));
            assertThrows(IllegalArgumentException.class, () -> table.automatedSeat(Integer.MAX_VALUE));
            assertThrows(IllegalArgumentException.class, () -> table.seat(1));
            assertNotNull(table.seat(0));
            assertEquals(table.testViews().size(), 1, "Even the seat audit retains the human capability boundary");
            assertFalse(table.finishIfNoHumans(), "An idle automated seat cannot terminate a live human's game");
        }
    }

    @Test
    public void liveTablesStillRequireARealHumanAndEndWhenTheirLastHumanLeaves() throws Exception {
        initialize();
        assertThrows(IllegalArgumentException.class, () -> new MatchTable("Constructed",
                List.of(MatchTable.automated(new Deck(), "Constructed", "Policy 1"),
                        MatchTable.automated(new Deck(), "Constructed", "Policy 2")),
                RESOURCES, Files.createTempDirectory("automated-no-human")));
        var players = List.of(MatchTable.human(new Deck(), "Commander", "Human"),
                MatchTable.automated(new Deck(), "Commander", "Policy 1"),
                MatchTable.automated(new Deck(), "Commander", "Policy 2"));
        try (var table = new MatchTable("Commander", players, RESOURCES,
                Files.createTempDirectory("automated-human-departure"))) {
            var request = new JsonObject();
            request.addProperty("sessionId", table.id);
            table.seat(0).concede(request);
            RulesScenario.awaitConcession(table, table.seat(0));
            assertTrue(table.game.isGameOver(), "Living policy seats cannot keep a departed human's table running");
            assertFalse(table.automatedSeats().stream().anyMatch(MatchSession::eliminated),
                    "The match ends because the real human left, not because policies were eliminated");
        }
    }

    @Test
    public void externallyOwnedManaPaymentCannotInvokeTheLegacyStrategicController() throws Exception {
        initialize();
        try (var table = new MatchTable("Constructed", List.of(
                MatchTable.human(new Deck(), "Constructed", "Human"),
                MatchTable.automated(new Deck(), "Constructed", "Policy")), RESOURCES,
                Files.createTempDirectory("automated-payment-boundary"))) {
            var policy = table.game.getPlayers().get(1);
            var human = table.game.getPlayers().get(0);
            var source = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard("Forest"), policy);
            var ability = AbilityFactory.getAbility("AB$ Draw | Cost$ 2 U | NumCards$ 1 | SpellDescription$ Draw a card.", source);
            var payment = new PaymentProbe((PlayerControllerHuman) policy.getController(), ability, policy);
            assertFalse(payment.autoSupported());
            var called = new AtomicBoolean();
            assertThrows(IllegalStateException.class, () -> payment.invokeLegacy(() -> called.set(true)));
            assertFalse(called.get(), "No legacy strategic callback may run before rejection");
            var copiedCost = payment.getRemainingManaCost();
            assertEquals(copiedCost.getGenericManaAmount(), 2);
            copiedCost.decreaseGenericMana(2);
            assertEquals(payment.getRemainingManaCost().getGenericManaAmount(), 2,
                    "Reading payment semantics cannot change the live bill");
            assertTrue(new PaymentProbe((PlayerControllerHuman) human.getController(), ability, human).autoSupported(),
                    "The existing human Auto payment gesture is retained");
        }
    }

    @Test
    public void anExplicitSimulationHasFourPrivatePolicySeatsAndNaturalElimination() throws Exception {
        initialize();
        var players = java.util.stream.IntStream.range(0, 4)
                .mapToObj(index -> MatchTable.automated(new Deck(), "Commander", "Policy " + index)).toList();
        try (var table = new MatchTable("Commander", players, RESOURCES,
                Files.createTempDirectory("automated-four-seat-simulation"), true)) {
            assertEquals(table.automatedSeats().size(), 4);
            assertTrue(table.testViews().isEmpty(), "A simulation creates no private human seat capability");
            assertFalse(table.finishIfNoHumans());
            assertFalse(table.game.isGameOver(), "Simulation is not ended merely because it has no humans");
            var first = table.game.getPlayers().get(0);
            var request = new JsonObject(); request.addProperty("sessionId", table.id);
            table.automatedSeat(first.getId()).concede(request);
            var departed = RulesScenario.awaitConcession(table, table.automatedSeat(first.getId()));
            assertTrue(RulesScenario.viewer(departed).get("eliminated").getAsBoolean(),
                    "Admission is followed by the actual policy seat's rules departure");
            assertEquals(table.game.getPlayers().size(), 3);
            assertFalse(table.game.isGameOver(), "Three remaining policies continue after a real elimination");
            assertFalse(table.finishIfNoHumans());
        }
        assertThrows(IllegalArgumentException.class, () -> new MatchTable("Commander", List.of(
                MatchTable.human(new Deck(), "Commander", "Human"),
                MatchTable.automated(new Deck(), "Commander", "Policy")), RESOURCES,
                Files.createTempDirectory("automated-mixed-simulation"), true));
    }

    @Test
    public void aPrivatePolicyFailureCannotExposeItsCardToHumanOrSpectatorSnapshots() throws Exception {
        initialize();
        String secret = "PRIVATE_POLICY_CARD_SENTINEL";
        var diagnostics = new ByteArrayOutputStream();
        var previousErrors = System.err;
        try (var capture = new PrintStream(diagnostics, true, StandardCharsets.UTF_8);
             var table = new MatchTable("Constructed", List.of(
                     MatchTable.human(new Deck(), "Constructed", "Human"),
                     MatchTable.automated(new Deck(), "Constructed", "Policy")), RESOURCES,
                     Files.createTempDirectory("automated-private-failure"))) {
            System.setErr(capture);
            table.fail(new IllegalStateException(secret));
            var human = table.seat(0).state();
            assertEquals(human.get("status"), "error");
            assertFalse(String.valueOf(human.get("error")).contains(secret), "An engine fault is not permission to disclose another seat's knowledge");
            assertFalse(String.valueOf(human.get("result")).contains(secret));
            assertFalse(String.valueOf(table.automatedSeats().get(0).state().get("error")).contains(secret));
        } finally { System.setErr(previousErrors); }
        String log = diagnostics.toString(StandardCharsets.UTF_8);
        assertTrue(log.contains(secret), "The original cause remains available in private engine diagnostics");
        assertEquals(log.indexOf(secret), log.lastIndexOf(secret), "The private cause is logged once for the table");
        try (var table = new MatchTable("Commander", List.of(
                MatchTable.automated(new Deck(), "Commander", "Policy 1"),
                MatchTable.automated(new Deck(), "Commander", "Policy 2")), RESOURCES,
                Files.createTempDirectory("spectator-private-failure"), true)) {
            // Keep a second private diagnostic out of the test runner's public output.
            try (var capture = new PrintStream(new ByteArrayOutputStream())) {
                System.setErr(capture);
                table.fail(new IllegalStateException(secret));
            } finally { System.setErr(previousErrors); }
            assertEquals(table.spectatorState().get("status"), "error");
            assertFalse(String.valueOf(table.spectatorState().get("error")).contains(secret));
        }
    }

    private static final class PaymentProbe extends InputPayMana {
        PaymentProbe(PlayerControllerHuman controller, SpellAbility ability, Player player) {
            super(controller, ability, player, false);
            manaCost = new ManaCostBeingPaid(ability.getPayCosts().getTotalMana());
        }
        boolean autoSupported() { return supportAutoPay(); }
        void invokeLegacy(Runnable callback) { runAsAi(callback); }
        @Override protected void done() { }
        @Override protected String getMessage() { return ""; }
    }
}
