package forge.api;

import com.google.gson.Gson;
import forge.StaticData;
import forge.card.MagicColor;
import forge.card.mana.ManaCost;
import forge.deck.Deck;
import forge.game.ability.AbilityFactory;
import forge.game.card.Card;
import forge.game.cost.Cost;
import forge.game.mana.Mana;
import forge.game.mana.ManaCostBeingPaid;
import forge.game.player.Player;
import forge.game.spellability.SpellAbility;
import forge.game.zone.ZoneType;
import forge.gamemodes.match.input.InputPayMana;
import forge.player.PlayerControllerHuman;
import org.testng.annotations.DataProvider;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.*;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

public class OptionalManaPaymentTest {
    private static final Gson JSON = new Gson();

    @Test public void noResourcesIsDifferentFromAPlanThatDoesNotCoverManualOrRestrictedRoutes() throws Exception {
        try (var fixture = new Fixture()) {
            var payment = fixture.payment(1);
            fixture.expect(payment, "Unpayable", false);
            var forest = fixture.add("Forest", ZoneType.Battlefield); forest.setTapped(true);
            fixture.expect(payment, "Unpayable", false);
            forest.setTapped(false);
            fixture.expect(payment, "Payable", true);
            forest.setTapped(true);
            var restricted = fixture.add("Ancient Ziggurat", ZoneType.Battlefield);
            fixture.expect(payment, "Unknown", false);
            restricted.setTapped(true);
            fixture.add("Elvish Spirit Guide", ZoneType.Hand);
            fixture.expect(payment, "Unknown", false);
        }
        for (String manual : List.of("Victory Chimes", "Ashnod's Altar")) {
            try (var fixture = new Fixture()) {
                fixture.add(manual, ZoneType.Battlefield); fixture.add("Memnite", ZoneType.Battlefield);
                fixture.expect(fixture.payment(1), "Unknown", false);
            }
        }
        try (var fixture = new Fixture()) {
            var outside = fixture.add("Spellbook", ZoneType.Sideboard);
            // A controlled rules-domain variant proves that the absence scan
            // does not silently omit an activation zone beyond the battlefield.
            outside.addSpellAbility(AbilityFactory.getAbility("AB$ Mana | Cost$ 0 | Produced$ C | ActivationZone$ Sideboard", outside));
            fixture.expect(fixture.payment(1), "Unknown", false);
        }
    }

    @Test public void actualFloatingManaRestrictionsAndPureProbesDoNotSpendOrChangeTheSource() throws Exception {
        try (var fixture = new Fixture()) {
            var source = fixture.add("Ancient Ziggurat", ZoneType.Battlefield); source.setTapped(true);
            var restricted = new Mana(MagicColor.BLUE, source, source.getManaAbilities().get(0).getManaPart(), fixture.actor());
            fixture.actor().getManaPool().addMana(restricted);
            var payment = fixture.payment(1);
            fixture.expect(payment, "Unpayable", false);
            var ordinary = new Mana(MagicColor.COLORLESS, fixture.source, null, fixture.actor());
            fixture.actor().getManaPool().addMana(ordinary);
            var pool = pool(fixture.actor());
            var originalActivator = fixture.ability.getActivatingPlayer();
            var params = Map.copyOf(fixture.ability.getMapParams());
            var publicBefore = fixture.table.seat(0).state();
            for (int repeat = 0; repeat < 20; repeat++) fixture.expect(payment, "Payable", true);
            assertEquals(pool(fixture.actor()), pool);
            assertSame(fixture.ability.getActivatingPlayer(), originalActivator);
            assertEquals(fixture.ability.getMapParams(), params);
            assertEquals(payment.getRemainingManaCost().getGenericManaAmount(), 1);
            assertEquals(payment.getOriginalManaCost().getGenericManaAmount(), 1);
            assertEquals(fixture.table.seat(0).state(), publicBefore, "A funding probe never publishes, pays, or installs an AI controller");
            assertTrue(source.isTapped());
        }
    }

    @Test public void originalCostAndPolicyAreStableThroughPartialPaymentsButUnknownOrOtherCommitmentsAreUnsupported() throws Exception {
        try (var fixture = new Fixture()) {
            var payment = fixture.payment(2);
            var original = fixture.describe(payment);
            payment.setRemaining(1);
            var partial = fixture.describe(payment);
            assertEquals(partial.get("policyKey"), original.get("policyKey"));
            assertEquals(partial.get("originalManaParts"), List.of(Map.of("symbol", "{2}", "count", 1)));
            payment.getOriginalManaCost().decreaseGenericMana(2);
            assertEquals(payment.getOriginalManaCost().getGenericManaAmount(), 2, "Original bill access is defensive");
            payment.setRemaining(3);
            assertNull(fixture.describe(payment), "An increased or changed bill cannot borrow the original consent");
            payment.setRemaining(1);
            payment.requirePayment();
            assertNull(fixture.describe(payment), "Mandatory payments never become optional auto answers");
            assertNull(fixture.describe(fixture.uncaptured()), "A late first observation never guesses the original pre-payment cost");
            fixture.ability.setPayCosts(new Cost("1 Sac<1/CARDNAME>", true));
            assertNull(fixture.describe(fixture.payment(1)), "Nonmana commitments need their own explicit journey");
            fixture.ability.setPayCosts(new Cost("X", true));
            assertNull(fixture.describe(fixture.payment(1)), "A variable rule cost is not certified by a currently numeric remainder");
        }
    }

    @DataProvider public Object[][] realFunding() { return new Object[][] {{false}, {true}}; }

    @Test(groups = "canonical-interaction", dataProvider = "realFunding", timeOut = 90000)
    public void mindsEyePaysOnceThroughTheCurrentRealInputWithoutForgeAiOrAnExtraConfirmation(boolean floating) throws Exception {
        try (var scenario = new RulesScenario("optional-mana-eye-" + floating,
                Map.of(0, List.of("Mind's Eye", "Forest", "Spellbook")), "Forest", "Progenitus")) {
            scenario.until(input -> input.seat() == 0 && priority(input) && text(input.state(), "phaseKey").equals("MAIN1"));
            var trigger = scenario.until(input -> input.seat() == 0 && priority(input)
                    && input.state().getAsJsonArray("stack").asList().stream().map(value -> value.getAsJsonObject())
                        .anyMatch(value -> text(value, "name").equals("Mind's Eye")));
            if (floating) {
                var forest = card(trigger.state(), "Forest");
                var actions = JSON.toJsonTree(scenario.session(0).sourceActions(request(trigger, Map.of("key", text(forest, "key"))))).getAsJsonObject();
                assertTrue(scenario.answer(trigger, Map.of("action", "useSourceAction", "key", text(forest, "key"),
                        "actionId", text(actions.getAsJsonArray("actions").get(0).getAsJsonObject(), "id"))));
                scenario.until(input -> input.seat() == 0 && priority(input) && viewer(input.state()).getAsJsonObject("mana").get("G").getAsInt() == 1);
            }
            var payment = scenario.until(input -> input.seat() == 0 && text(input.prompt(), "inputType").startsWith("InputPayMana"));
            var support = payment.prompt().getAsJsonObject("optionalPaymentSupport");
            assertNotNull(support); assertEquals(text(support, "availability"), "Payable"); assertTrue(support.get("canAutoPay").getAsBoolean());
            String offer = text(payment.prompt().getAsJsonObject("optionalDecline"), "offerId");
            assertFalse(offer.isEmpty());
            assertEquals(card(payment.state(), "Forest").get("tapped").getAsBoolean(), floating);
            scenario.rejected(payment, Map.of("action", "autoPayOptionalPayment", "offerId", "foreign-offer"),
                    "Automatic payment is not available for this current optional cost");
            int hand = count(viewer(payment.state()), "Hand");
            assertTrue(scenario.answer(payment, Map.of("action", "autoPayOptionalPayment", "offerId", offer)));
            assertThrows(IllegalArgumentException.class, () -> scenario.session(0).action(request(payment,
                    Map.of("action", "autoPayOptionalPayment", "offerId", offer))));
            var settled = scenario.until(input -> input.seat() == 0 && priority(input) && input.state().getAsJsonArray("stack").isEmpty());
            assertEquals(count(viewer(settled.state()), "Hand"), hand + 1);
            assertTrue(card(settled.state(), "Forest").get("tapped").getAsBoolean());
            assertEquals(viewer(settled.state()).getAsJsonObject("mana").get("G").getAsInt(), 0);
            assertNotEquals(text(settled.prompt(), "id"), text(payment.prompt(), "id"));
            assertFalse(((PlayerControllerHuman)scenario.table.game.getPlayers().get(0).getController()).isExternallyAutomated());
            Files.writeString(scenario.evidence.resolve("private-optional-mana-settled.json"), JSON.toJson(settled.state()));
        }
    }

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void oneGestureAtATimeRetainsTheOriginalFourManaBillAndChoiceFenceAcrossTwoFreshInputs() throws Exception {
        try (var scenario = new RulesScenario("optional-mana-vault-partial",
                Map.of(0, List.of("Mana Vault", "Forest", "Spellbook")), "Forest", "Progenitus")) {
            var payment = scenario.until(input -> input.seat() == 0 && text(input.prompt(), "inputType").startsWith("InputPayMana"));
            var initialSupport = payment.prompt().getAsJsonObject("optionalPaymentSupport");
            assertNotNull(initialSupport); assertTrue(initialSupport.get("canAutoPay").getAsBoolean());
            assertEquals(initialSupport.getAsJsonArray("originalManaParts"), JSON.toJsonTree(List.of(Map.of("symbol", "{4}", "count", 1))));
            String policy = text(initialSupport, "policyKey");
            String offer = text(payment.prompt().getAsJsonObject("optionalDecline"), "offerId");
            String originalPaymentKey = text(payment.prompt().getAsJsonObject("optionalDecline"), "paymentKey");
            long choice = payment.state().get("resolutionChoiceVersion").getAsLong();
            long action = payment.state().get("stackActionVersion").getAsLong();
            assertTrue(scenario.answer(payment, Map.of("action", "autoPayOptionalPayment", "offerId", offer)));
            var partial = scenario.until(input -> input.seat() == 0 && text(input.prompt(), "inputType").startsWith("InputPayMana"));
            assertNotEquals(text(partial.prompt(), "id"), text(payment.prompt(), "id"));
            var partialOffer = partial.prompt().getAsJsonObject("optionalDecline");
            assertEquals(text(partialOffer, "offerId"), offer);
            assertNotEquals(text(partialOffer, "paymentKey"), originalPaymentKey);
            assertEquals(text(partial.prompt().getAsJsonObject("optionalPaymentSupport"), "policyKey"), policy);
            assertEquals(partial.prompt().getAsJsonObject("optionalPaymentSupport").get("originalManaParts"), initialSupport.get("originalManaParts"));
            assertEquals(partial.prompt().getAsJsonArray("unpaidManaParts"), JSON.toJsonTree(List.of(Map.of("symbol", "{1}", "count", 1))));
            assertEquals(partial.state().get("resolutionChoiceVersion").getAsLong(), choice);
            assertTrue(partial.state().get("stackActionVersion").getAsLong() > action, "A real mana action remains an action even inside one optional cost");
            assertTrue(card(partial.state(), "Mana Vault").get("tapped").getAsBoolean());
            assertTrue(scenario.answer(partial, Map.of("action", "autoPayOptionalPayment", "offerId", offer)));
            var settled = scenario.until(input -> input.seat() == 0 && priority(input) && input.state().getAsJsonArray("stack").isEmpty());
            assertTrue(card(settled.state(), "Forest").get("tapped").getAsBoolean());
            assertFalse(card(settled.state(), "Mana Vault").get("tapped").getAsBoolean(), "The actual paid upkeep effect untaps its source");
            assertEquals(settled.state().get("resolutionChoiceVersion").getAsLong(), choice);
            Files.writeString(scenario.evidence.resolve("private-two-step-settled.json"), JSON.toJson(settled.state()));
        }
    }

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void anAcceptedAutomaticPaymentRechecksTheLiveResourceBeforeItsQueuedGesture() throws Exception {
        try (var scenario = new RulesScenario("optional-mana-resource-race",
                Map.of(0, List.of("Mana Vault", "Forest", "Spellbook")), "Forest", "Progenitus")) {
            var payment = scenario.until(input -> input.seat() == 0 && text(input.prompt(), "inputType").startsWith("InputPayMana"));
            String offer = text(payment.prompt().getAsJsonObject("optionalDecline"), "offerId");
            var entered = new CountDownLatch(1); var release = new CountDownLatch(1);
            HeadlessPlatform.later(() -> {
                entered.countDown();
                try (var suspended = scenario.table.game.getAction().suspendState()) {
                    if (!release.await(10, TimeUnit.SECONDS)) throw new AssertionError("Input barrier expired");
                } catch (InterruptedException error) { Thread.currentThread().interrupt(); throw new AssertionError(error); }
            });
            assertTrue(entered.await(5, TimeUnit.SECONDS));
            try {
                assertTrue(scenario.answer(payment, Map.of("action", "autoPayOptionalPayment", "offerId", offer)));
                scenario.table.game.getAction().withState(() -> {
                    var vault = scenario.table.game.getPlayers().get(0).getCardsIn(ZoneType.Battlefield).stream()
                            .filter(value -> value.getName().equals("Mana Vault")).findFirst().orElseThrow();
                    // A controlled intervening rules mutation exercises the
                    // acceptance/execution race without substituting a policy.
                    vault.setTapped(true);
                    scenario.table.game.fireEvent(new forge.game.event.GameEventCardTapped(vault, true));
                });
            } finally { release.countDown(); }
            HeadlessPlatform.query(scenario.table, () -> null);
            var current = scenario.state(0); var prompt = current.getAsJsonObject("prompt");
            assertEquals(text(prompt, "id"), text(payment.prompt(), "id"), "The unanswered current input remains available after the late recheck");
            assertEquals(text(prompt.getAsJsonObject("optionalPaymentSupport"), "availability"), "Unknown",
                    "One remaining Forest is a manual resource, not a complete four-mana witness or proof of no funding");
            assertFalse(prompt.getAsJsonObject("optionalPaymentSupport").get("canAutoPay").getAsBoolean());
            assertFalse(card(current, "Forest").get("tapped").getAsBoolean(), "A revoked witness never taps its replacement resource");
            assertEquals(prompt.get("unpaidManaParts"), payment.prompt().get("unpaidManaParts"));
            scenario.rejected(new Input(0, current, prompt), Map.of("action", "autoPayOptionalPayment", "offerId", offer),
                    "Automatic payment is not available for this current optional cost");
            assertTrue(scenario.answer(new Input(0, current, prompt), Map.of("action", "cancel")));
            scenario.until(input -> input.seat() == 0 && priority(input));
        }
    }

    @Test public void publishedPaymentFactsRemainReadableWithoutReopeningTheInputAndRefreshOnItsNextPublication() throws Exception {
        try (var fixture = new Fixture(true)) {
            var payment = fixture.payment(2);
            var forest = fixture.add("Forest", ZoneType.Battlefield);
            var controller = (PlayerControllerHuman)fixture.actor().getController();
            var session = fixture.table.automatedSeat(fixture.actor().getId());
            controller.getInputQueue().setInput(payment);
            HeadlessPlatform.query(fixture.table, () -> {
                fixture.table.publish();
                // Warm the legal action domain before forbidding another read of
                // the cost. Discovery remains a separate live rules operation.
                var raw = session.automatedObservation();
                var retained = JSON.toJsonTree(raw).getAsJsonObject();
                var prompt = retained.getAsJsonObject("state").getAsJsonObject("prompt");
                assertEquals(prompt.get("payerId").getAsInt(), fixture.actor().getId());
                assertEquals(prompt.get("unpaidManaParts"), JSON.toJsonTree(List.of(Map.of("symbol", "{2}", "count", 1))));
                assertEquals(retained.getAsJsonObject("semantics").get("unpaidMana"), JSON.toJsonTree(List.of("{1}", "{1}")));
                assertEquals(retained.getAsJsonObject("semantics").getAsJsonObject("payment").get("sourceCardId"),
                        prompt.getAsJsonObject("sourceCard").get("combatId"));
                var rawPrompt = (Map<?, ?>)((Map<?, ?>)raw.get("state")).get("prompt");
                var sources = (List<?>)((Map<?, ?>)rawPrompt.get("fundingContext")).get("sources");
                assertEquals(sources.size(), 1, "The real Forest supplies the published nominal funding route");
                var routes = (List<?>)((Map<?, ?>)sources.get(0)).get("routes");
                var costs = (List<?>)((Map<?, ?>)routes.get(0)).get("costs");
                var tap = (Map<?, ?>)costs.get(0);
                assertEquals(tap.get("kind"), "Tap");
                assertThrows("A consumer cannot rewrite a nested published resource cost",
                        UnsupportedOperationException.class, tap::clear);
                assertEquals(JSON.toJsonTree(session.automatedObservation()), retained);
                var publicState = JSON.toJsonTree(fixture.table.spectatorState()).getAsJsonObject();
                for (String field : List.of("prompt", "actionSources", "semantics", "payment", "manaPaymentContext",
                        "optionalDecline", "optionalPaymentSupport", "unpaidManaParts", "fundingContext"))
                    assertFalse(publicState.has(field), "A public spectator cannot inherit private " + field);
                var other = JSON.toJsonTree(fixture.table.automatedSeat(fixture.table.game.getRegisteredPlayers().get(1).getId())
                        .automatedObservation()).getAsJsonObject();
                assertFalse(other.getAsJsonObject("semantics").has("payment"));
                payment.rejectProjectionReads = true;
                try {
                    for (int read = 0; read < 3; read++) {
                        assertEquals(JSON.toJsonTree(session.automatedObservation()), retained,
                                "The accepted payment observation must not reopen the live payment to reconstruct its facts");
                        assertEquals(JSON.toJsonTree(session.state()).getAsJsonObject().get("prompt"), prompt);
                    }
                } finally { payment.rejectProjectionReads = false; }

                payment.setRemaining(1);
                fixture.table.publish();
                var refreshed = JSON.toJsonTree(session.automatedObservation()).getAsJsonObject();
                var next = refreshed.getAsJsonObject("state").getAsJsonObject("prompt");
                assertEquals(next.get("id"), prompt.get("id"), "This is a new publication of the same unanswered rules input");
                assertTrue(refreshed.getAsJsonObject("state").get("boardRevision").getAsLong()
                        > retained.getAsJsonObject("state").get("boardRevision").getAsLong());
                assertEquals(next.get("unpaidManaParts"), JSON.toJsonTree(List.of(Map.of("symbol", "{1}", "count", 1))));
                assertEquals(refreshed.getAsJsonObject("semantics").get("unpaidMana"), JSON.toJsonTree(List.of("{1}")));
                assertEquals(prompt.get("unpaidManaParts"), JSON.toJsonTree(List.of(Map.of("symbol", "{2}", "count", 1))),
                        "A retained human-compatible payment snapshot cannot shrink with a later publication");
                assertEquals(fixture.actor().getManaPool().totalMana(), 0);
                assertFalse(forest.isTapped(), "Inspecting or attempting to mutate retained facts cannot spend their source");
                return null;
            });
        }
    }

    @Test public void aSupersededPaymentReadCannotDeleteTheNewPublicationItInterrupted() throws Exception {
        try (var fixture = new Fixture(true)) {
            var payment = fixture.payment(2);
            var forest = fixture.add("Forest", ZoneType.Battlefield);
            fixture.actor().getManaPool().addMana(new Mana(MagicColor.GREEN, forest, null, fixture.actor()));
            var controller = (PlayerControllerHuman)fixture.actor().getController();
            var session = fixture.table.automatedSeat(fixture.actor().getId());
            controller.getInputQueue().setInput(payment);
            HeadlessPlatform.query(fixture.table, () -> {
                fixture.table.publish();
                var previous = JSON.toJsonTree(session.state()).getAsJsonObject();
                var previousPrompt = previous.getAsJsonObject("prompt");
                // Actual source discovery reenters publication while this read
                // still owns the previous bill. No rules input is replaced.
                payment.beforeNextSourceRead = () -> {
                    payment.setRemaining(1);
                    fixture.table.publish();
                };
                assertThrows(StaleObservationException.class, session::automatedObservation);
                assertNull(payment.beforeNextSourceRead, "The source query must actually exercise the reentrant publication");

                var published = JSON.toJsonTree(session.state()).getAsJsonObject();
                var prompt = published.getAsJsonObject("prompt");
                assertEquals(prompt.get("id"), previousPrompt.get("id"));
                assertTrue(published.get("boardRevision").getAsLong() > previous.get("boardRevision").getAsLong());
                // The next read must recover from the already published cohort;
                // a refresh here would conceal destruction of its retained facts.
                var recovered = JSON.toJsonTree(session.automatedObservation()).getAsJsonObject();
                assertEquals(recovered.get("state"), published);
                assertEquals(prompt.get("unpaidManaParts"), JSON.toJsonTree(List.of(Map.of("symbol", "{1}", "count", 1))));
                assertEquals(recovered.getAsJsonObject("semantics").get("unpaidMana"), JSON.toJsonTree(List.of("{1}")));
                assertEquals(prompt.get("payerId").getAsInt(), fixture.actor().getId());
                assertEquals(recovered.getAsJsonObject("semantics").getAsJsonObject("payment").get("sourceCardId"),
                        prompt.getAsJsonObject("sourceCard").get("combatId"));
                assertEquals(prompt.get("usablePoolManaColors"), JSON.toJsonTree(List.of("G")));
                assertEquals(recovered.getAsJsonObject("domain").get("poolManaColors"), prompt.get("usablePoolManaColors"));
                assertEquals(previousPrompt.get("unpaidManaParts"), JSON.toJsonTree(List.of(Map.of("symbol", "{2}", "count", 1))));

                String key = text(card(published, "Forest"), "key");
                assertThrows(IllegalArgumentException.class, () -> session.sourceActions(request(
                        new Input(0, previous, previousPrompt), Map.of("key", key, "expectedRevision", previous.get("revision").getAsLong()))));
                var catalog = JSON.toJsonTree(session.sourceActions(request(new Input(0, published, prompt),
                        Map.of("key", key, "expectedRevision", published.get("revision").getAsLong())))).getAsJsonObject();
                assertEquals(catalog.get("decisionId"), prompt.get("id"));
                assertFalse(catalog.getAsJsonArray("actions").isEmpty(), "The newly published source remains answerable");
                assertEquals(JSON.toJsonTree(session.automatedObservation()), recovered);
                assertEquals(fixture.actor().getManaPool().totalMana(), 1);
                assertFalse(forest.isTapped());
                return null;
            });
        }
    }

    @Test public void aReentrantPaymentPublicationCannotOverwriteTheNewAllAudienceCohort() throws Exception {
        try (var fixture = new Fixture(true)) {
            var payment = fixture.payment(2);
            var forest = fixture.add("Forest", ZoneType.Battlefield);
            fixture.actor().getManaPool().addMana(new Mana(MagicColor.GREEN, forest, null, fixture.actor()));
            var controller = (PlayerControllerHuman)fixture.actor().getController();
            var session = fixture.table.automatedSeat(fixture.actor().getId());
            controller.getInputQueue().setInput(payment);
            HeadlessPlatform.query(fixture.table, () -> {
                fixture.table.publish();
                var previous = JSON.toJsonTree(session.state()).getAsJsonObject();
                var inner = new ArrayList<com.google.gson.JsonObject>();
                payment.beforeNextRemainingRead = () -> {
                    payment.setRemaining(1);
                    fixture.table.publish();
                    inner.add(JSON.toJsonTree(session.state()).getAsJsonObject());
                };
                fixture.table.publish();
                assertNull(payment.beforeNextRemainingRead);
                assertEquals(inner.size(), 1, "A real bill projection must exercise the nested publication");
                var published = inner.get(0);
                long board = published.get("boardRevision").getAsLong();
                assertTrue(board > previous.get("boardRevision").getAsLong());
                for (var audience : fixture.table.automatedSeats())
                    assertEquals(((Number)audience.state().get("boardRevision")).longValue(), board,
                            "An obsolete outer publication cannot replace any newer seat's board cohort");
                assertEquals(((Number)fixture.table.spectatorState().get("boardRevision")).longValue(), board,
                        "The public spectator must retain the same new cohort as the private seats");
                assertEquals(JSON.toJsonTree(session.state()), published);
                var recovered = JSON.toJsonTree(session.automatedObservation()).getAsJsonObject();
                assertEquals(recovered.get("state"), published, "The inner capture remains readable without another publication");
                var prompt = published.getAsJsonObject("prompt");
                assertEquals(prompt.get("id"), previous.getAsJsonObject("prompt").get("id"));
                assertEquals(prompt.get("unpaidManaParts"), JSON.toJsonTree(List.of(Map.of("symbol", "{1}", "count", 1))));
                assertEquals(recovered.getAsJsonObject("semantics").get("unpaidMana"), JSON.toJsonTree(List.of("{1}")));
                assertEquals(prompt.get("usablePoolManaColors"), JSON.toJsonTree(List.of("G")));
                assertEquals(recovered.getAsJsonObject("domain").get("poolManaColors"), prompt.get("usablePoolManaColors"));
                assertEquals(prompt.get("payerId").getAsInt(), fixture.actor().getId());
                assertEquals(recovered.getAsJsonObject("semantics").getAsJsonObject("payment").get("sourceCardId"),
                        prompt.getAsJsonObject("sourceCard").get("combatId"));
                assertEquals(fixture.actor().getManaPool().totalMana(), 1);
                assertFalse(forest.isTapped());
                return null;
            });
        }
    }

    private static boolean priority(Input input) { return text(input.prompt(), "inputType").equals("InputPassPriority"); }
    private static List<Mana> pool(Player actor) {
        var values = new ArrayList<Mana>(); actor.getManaPool().forEach(values::add); return List.copyOf(values);
    }
    private static com.google.gson.JsonObject card(com.google.gson.JsonObject state, String name) {
        return zone(viewer(state), "Battlefield").getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "name").equals(name)).findFirst().orElseThrow();
    }

    private static final class Fixture implements AutoCloseable {
        final MatchTable table;
        final Card source;
        final SpellAbility ability;
        Fixture() throws Exception { this(false); }
        Fixture(boolean automated) throws Exception {
            var resources = Path.of("../forge-gui/res");
            if (StaticData.instance() == null) EngineResources.load(resources);
            var players = automated ? List.of(MatchTable.automated(new Deck(), "Constructed", "Actor"),
                    MatchTable.automated(new Deck(), "Constructed", "Opponent"))
                    : List.of(MatchTable.human(new Deck(), "Constructed", "Actor"), MatchTable.human(new Deck(), "Constructed", "Opponent"));
            table = new MatchTable("Constructed", players, resources, Files.createTempDirectory("optional-mana"), automated);
            HeadlessPlatform.activate(table);
            source = add("Mind's Eye", ZoneType.Battlefield);
            ability = AbilityFactory.getAbility(source.getSVar("TrigDraw"), source);
            ability.setActivatingPlayer(actor()); ability.setTrigger(source.getTriggers().get(0));
        }
        Player actor() { return table.game.getRegisteredPlayers().get(0); }
        Card add(String name, ZoneType zone) {
            var card = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard(name), actor());
            actor().getZone(zone).add(card); return card;
        }
        Probe payment(int generic) { return new Probe((PlayerControllerHuman)actor().getController(), ability, actor(), generic, true); }
        Probe uncaptured() { return new Probe((PlayerControllerHuman)actor().getController(), ability, actor(), 1, false); }
        Map<String, Object> describe(Probe payment) {
            return OptionalManaPayment.describe(payment, Map.of("groupKey", "source-" + source.getId(), "purpose", "TriggeredAbilityCost", "payerId", actor().getId()));
        }
        void expect(Probe payment, String availability, boolean auto) {
            var value = describe(payment); assertNotNull(value); assertEquals(value.get("availability"), availability); assertEquals(value.get("canAutoPay"), auto);
        }
        @Override public void close() { table.close(); }
    }
    private static final class Probe extends InputPayMana {
        boolean rejectProjectionReads;
        Runnable beforeNextRemainingRead;
        Runnable beforeNextSourceRead;
        Probe(PlayerControllerHuman controller, SpellAbility ability, Player player, int generic, boolean capture) {
            super(controller, ability, player, false); manaCost = new ManaCostBeingPaid(ManaCost.get(generic));
            if (capture) captureOriginalManaCost();
        }
        void setRemaining(int generic) { manaCost = new ManaCostBeingPaid(ManaCost.get(generic)); }
        void requirePayment() { mandatory = true; }
        private void requireProjectionRead() {
            if (rejectProjectionReads) throw new AssertionError("A retained payment projection reopened the live input");
        }
        @Override public ManaCostBeingPaid getRemainingManaCost() {
            requireProjectionRead();
            var remaining = super.getRemainingManaCost();
            var callback = beforeNextRemainingRead; beforeNextRemainingRead = null;
            if (callback != null) callback.run();
            return remaining;
        }
        @Override public SpellAbility getPaidForAbility() { requireProjectionRead(); return super.getPaidForAbility(); }
        @Override public Player getPayer() { requireProjectionRead(); return super.getPayer(); }
        @Override public Set<Byte> getUsablePoolManaColors() { requireProjectionRead(); return super.getUsablePoolManaColors(); }
        @Override public List<SpellAbility> getSourceActions(Card card) {
            var callback = beforeNextSourceRead; beforeNextSourceRead = null;
            if (callback != null) callback.run();
            return super.getSourceActions(card);
        }
        @Override protected void done() { }
        @Override protected String getMessage() { return ""; }
    }
}
