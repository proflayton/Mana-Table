package forge.api;

import com.google.gson.Gson;
import com.google.gson.JsonObject;
import forge.StaticData;
import forge.deck.Deck;
import forge.deck.DeckSection;
import forge.game.card.CounterEnumType;
import forge.game.zone.ZoneType;
import forge.gamemodes.match.input.InputPayMana;
import forge.gamemodes.match.input.InputSelectCardsForConvokeOrImprovise;
import forge.player.PlayerControllerHuman;
import org.testng.annotations.DataProvider;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.nio.file.Path;
import java.time.Duration;
import java.util.*;
import java.util.function.Predicate;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** A real rules continuation pays from existing floating mana with no Forge Auto planner. */
public class AutomatedPaymentJourneyTest {
    private static final Gson JSON = new Gson();
    private static final Path RESOURCES = Path.of("../forge-gui/res");

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void exactZeroConvokeProgressRetainsOneEpisodeAndCanCompleteWithUnusedEligibleCards() throws Exception {
        if (StaticData.instance() == null) EngineResources.load(RESOURCES);
        var human = MatchTable.human(deck("Human", "Forest"), "Commander", "Human");
        var bot = MatchTable.automated(deck("Policy", "Scatter the Seeds"), "Commander", "Policy");
        bot.addExtraCardsOnBattlefield(Collections.nCopies(6, StaticData.instance().getCommonCards().getCard("Elvish Mystic")));
        try (var table = new MatchTable("Commander", List.of(human, bot), RESOURCES, Files.createTempDirectory("automated-convoke-progress"))) {
            int policyId = table.game.getPlayers().get(1).getId(); var driver = new Driver(table, policyId); table.start();
            var main = driver.until(input -> input.playerId == policyId && isPriority(input)
                    && input.state.get("activePlayerId").getAsInt() == policyId && text(input.state, "phaseKey").equals("MAIN1"));
            driver.use(main, card(main.state, "Hand", "Scatter the Seeds"), "cast");
            var reduction = driver.until(input -> input.playerId == policyId && text(input.prompt, "inputType").equals("InputSelectCardsForConvokeOrImprovise"));
            String episode = reductionEpisode(table, policyId);
            assertFalse(episode.isEmpty());
            assertEquals(reduction.prompt.getAsJsonObject("manaReduction").getAsJsonArray("remainingManaParts"),
                    JSON.toJsonTree(List.of(Map.of("symbol", "{G}", "count", 2), Map.of("symbol", "{3}", "count", 1))));
            for (int index = 0; index < 5; index++) {
                var candidate = zone(viewer(reduction.state), "Battlefield").getAsJsonArray("cards").asList().stream()
                        .map(value -> value.getAsJsonObject()).filter(value -> text(value, "name").equals("Elvish Mystic")
                                && !value.get("highlighted").getAsBoolean()).findFirst().orElseThrow();
                String oldScope = text(reduction.prompt, "id");
                driver.answer(reduction, Map.of("action", "card", "key", text(candidate, "key")));
                reduction = driver.until(input -> input.playerId == policyId && text(input.prompt, "inputType").equals("InputSelectCardsForConvokeOrImprovise")
                        && !text(input.prompt, "id").equals(oldScope));
                assertEquals(reductionEpisode(table, policyId), episode, "Refreshed decision scopes retain the actual cost-input episode");
            }
            assertTrue(reduction.prompt.getAsJsonObject("manaReduction").getAsJsonArray("remainingManaParts").isEmpty());
            assertTrue(reduction.prompt.get("okEnabled").getAsBoolean(), "Exactly reduced costs still require the current explicit Complete action");
            var unused = zone(viewer(reduction.state), "Battlefield").getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                    .filter(value -> !value.get("highlighted").getAsBoolean()).findFirst().orElseThrow();
            assertFalse(unused.get("selectable").getAsBoolean(), "A zero-bill contributor is no longer a productive selection");
            String zeroScope = text(reduction.prompt, "id");
            driver.answer(reduction, Map.of("action", "card", "key", text(unused, "key")));
            reduction = driver.until(input -> input.playerId == policyId && text(input.prompt, "inputType").equals("InputSelectCardsForConvokeOrImprovise")
                    && !text(input.prompt, "id").equals(zeroScope));
            assertTrue(reduction.prompt.getAsJsonObject("manaReduction").getAsJsonArray("remainingManaParts").isEmpty(), "An unusable extra click cannot reduce zero further");
            assertEquals(zone(viewer(reduction.state), "Battlefield").getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                    .filter(value -> value.get("highlighted").getAsBoolean()).count(), 5L);
            assertEquals(reductionEpisode(table, policyId), episode, "A no-progress gesture cannot refund the causal budget");
            table.game.getAction().withState(() -> {
                var controller = (PlayerControllerHuman)table.game.getPlayers().get(1).getController();
                var actual = (InputSelectCardsForConvokeOrImprovise)controller.getInputQueue().getInput();
                actual.getRemainingManaCost().increaseShard(forge.card.mana.ManaCostShard.GENERIC, 99);
                assertTrue(actual.getRemainingManaCost().isPaid(), "The progress getter returns a defensive copy");
            });
            driver.answer(reduction, Map.of("action", "ok"));
            var resolved = driver.until(input -> input.playerId == policyId && isPriority(input) && input.state.getAsJsonArray("stack").isEmpty()
                    && zone(viewer(input.state), "Battlefield").getAsJsonArray("cards").asList().stream()
                    .filter(value -> text(value.getAsJsonObject(), "name").contains("Saproling")).count() == 3);
            assertEquals(zone(viewer(resolved.state), "Battlefield").getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                    .filter(value -> text(value, "name").equals("Elvish Mystic") && value.get("tapped").getAsBoolean()).count(), 5L);
            assertFalse(resolved.prompt.has("manaReduction"));
        }
    }

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void aReentrantMulticolorConvokeChoiceRemainsObservableAndResumesTheSameCostEpisode() throws Exception {
        if (StaticData.instance() == null) EngineResources.load(RESOURCES);
        var human = MatchTable.human(deck("Human", "Island"), "Commander", "Human");
        var policyDeck = deck("Policy", "Island"); policyDeck.getOrCreate(DeckSection.Commander).clear();
        policyDeck.getOrCreate(DeckSection.Commander).add(StaticData.instance().getCommonCards().getCard("Kasla, the Broken Halo"));
        var bot = MatchTable.automated(policyDeck, "Commander", "Policy");
        var extras = new ArrayList<forge.item.IPaperCard>(Collections.nCopies(7, StaticData.instance().getCommonCards().getCard("Lightning Angel")));
        extras.addAll(Collections.nCopies(6, StaticData.instance().getCommonCards().getCard("Mystic Monastery")));
        bot.addExtraCardsOnBattlefield(extras);
        try (var table = new MatchTable("Commander", List.of(human, bot), RESOURCES, Files.createTempDirectory("automated-nested-convoke"))) {
            int policyId = table.game.getPlayers().get(1).getId(); var driver = new Driver(table, policyId); table.start();
            var main = driver.until(input -> input.playerId == policyId && isPriority(input)
                    && input.state.get("activePlayerId").getAsInt() == policyId && text(input.state, "phaseKey").equals("MAIN1"));
            driver.use(main, card(main.state, "Command", "Kasla, the Broken Halo"), "cast");
            var reduction = driver.until(input -> input.playerId == policyId && text(input.prompt, "inputType").equals("InputSelectCardsForConvokeOrImprovise"));
            String episode = reductionEpisode(table, policyId);
            driver.answer(reduction, Map.of("action", "card", "key", text(card(reduction.state, "Battlefield", "Lightning Angel"), "key")));
            var choice = driver.until(input -> input.playerId == policyId && text(input.prompt, "context").equals("colorChoice"));
            assertEquals(reductionEpisode(table, policyId), episode, "The private observation must be available while its UI-owned dialog awaits an answer");
            assertFalse(choice.prompt.has("manaReduction"), "Only the current entity-selection payload owns reduction-progress authority");
            assertEquals(text(choice.prompt.getAsJsonObject("manaPaymentContext").getAsJsonObject("source").getAsJsonObject("card"), "name"), "Kasla, the Broken Halo");
            assertEquals(choice.prompt.getAsJsonObject("manaPaymentContext").getAsJsonArray("unpaidManaParts"),
                    reduction.prompt.getAsJsonObject("manaReduction").getAsJsonArray("remainingManaParts"));
            assertTrue(choice.prompt.getAsJsonArray("choices").asList().stream().map(value -> value.getAsJsonObject())
                    .anyMatch(value -> text(value, "mana").equals("{1}")), "Colorless convoke contribution represents a generic reduction, not produced C mana");
            Files.createDirectories(Path.of("target/rules-regressions"));
            Files.writeString(Path.of("target/rules-regressions/convoke-owned-color-choice.json"), JSON.toJson(choice.state));
            int white = choice.prompt.getAsJsonArray("choices").asList().stream().map(value -> value.getAsJsonObject())
                    .filter(value -> text(value, "mana").equals("{W}")).findFirst().orElseThrow().get("index").getAsInt();
            driver.answer(choice, Map.of("action", "choice", "choices", List.of(white)));
            var resumed = driver.until(input -> input.playerId == policyId && text(input.prompt, "inputType").equals("InputSelectCardsForConvokeOrImprovise"));
            assertEquals(reductionEpisode(table, policyId), episode);
            assertFalse(resumed.prompt.getAsJsonObject("manaReduction").getAsJsonArray("remainingManaParts").asList().stream()
                    .anyMatch(value -> text(value.getAsJsonObject(), "symbol").equals("{W}")));
            assertThrows(IllegalArgumentException.class, () -> table.automatedSeat(policyId).action(request(choice, Map.of("action", "choice", "choices", List.of(white)))));
            for (int generic = 0; generic < 3; generic++) {
                var contributor = zone(viewer(resumed.state), "Battlefield").getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                        .filter(value -> text(value, "name").equals("Lightning Angel") && !value.get("highlighted").getAsBoolean()).findFirst().orElseThrow();
                driver.answer(resumed, Map.of("action", "card", "key", text(contributor, "key")));
                var genericChoice = driver.until(input -> input.playerId == policyId && text(input.prompt, "context").equals("colorChoice"));
                assertEquals(reductionEpisode(table, policyId), episode);
                int index = genericChoice.prompt.getAsJsonArray("choices").asList().stream().map(value -> value.getAsJsonObject())
                        .filter(value -> text(value, "mana").equals("{1}")).findFirst().orElseThrow().get("index").getAsInt();
                driver.answer(genericChoice, Map.of("action", "choice", "choices", List.of(index)));
                resumed = driver.until(input -> input.playerId == policyId && text(input.prompt, "inputType").equals("InputSelectCardsForConvokeOrImprovise"));
            }
            var contributor = zone(viewer(resumed.state), "Battlefield").getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                    .filter(value -> text(value, "name").equals("Lightning Angel") && !value.get("highlighted").getAsBoolean()).findFirst().orElseThrow();
            driver.answer(resumed, Map.of("action", "card", "key", text(contributor, "key")));
            var coloredOnly = driver.until(input -> input.playerId == policyId && text(input.prompt, "context").equals("colorChoice"));
            assertEquals(coloredOnly.prompt.getAsJsonArray("choices").asList().stream().map(value -> text(value.getAsJsonObject(), "mana"))
                    .collect(java.util.stream.Collectors.toSet()), Set.of("{U}", "{R}"), "A generic contribution is not offered after all generic cost is reduced");
            assertEquals(coloredOnly.prompt.getAsJsonObject("manaPaymentContext").getAsJsonArray("unpaidManaParts"),
                    JSON.toJsonTree(List.of(Map.of("symbol", "{U}", "count", 1), Map.of("symbol", "{R}", "count", 1))));
            assertEquals(text(coloredOnly.prompt.getAsJsonObject("manaPaymentContext").getAsJsonObject("source").getAsJsonObject("card"), "name"), "Kasla, the Broken Halo");
            assertEquals(reductionEpisode(table, policyId), episode);
            var observation = JSON.toJsonTree(table.automatedSeat(policyId).automatedObservation());
            assertEquals(JSON.toJsonTree(table.automatedSeat(policyId).automatedObservation()), observation, "Filtering choices and reading the remaining bill cannot contribute, publish or alter the episode");
            Files.writeString(Path.of("target/rules-regressions/convoke-colored-only-choice.json"), JSON.toJson(coloredOnly.state));
        }
    }

    private static String reductionEpisode(MatchTable table, int policyId) {
        var observation = JSON.toJsonTree(table.automatedSeat(policyId).automatedObservation()).getAsJsonObject();
        return text(observation.getAsJsonObject("semantics"), "episodeId");
    }

    @DataProvider public Object[][] flexibleOutputStage() { return new Object[][] {{false}, {true}}; }

    @Test(dataProvider = "flexibleOutputStage", groups = "canonical-interaction", timeOut = 90000)
    public void aPlannedRedAlternativeSurvivesOrdinaryPriorityOrPaymentActivationAndCastsABlueRedCommander(boolean prefloat) throws Exception {
        if (StaticData.instance() == null) EngineResources.load(RESOURCES);
        var human = MatchTable.human(deck("Human", "Island"), "Commander", "Human");
        var policyDeck = deck("Policy", "Island");
        policyDeck.getOrCreate(DeckSection.Commander).clear();
        policyDeck.getOrCreate(DeckSection.Commander).add(StaticData.instance().getCommonCards().getCard("Balmor, Battlemage Captain"));
        var bot = MatchTable.automated(policyDeck, "Commander", "Policy");
        bot.addExtraCardsOnBattlefield(List.of(StaticData.instance().getCommonCards().getCard("Temple of Epiphany"),
                StaticData.instance().getCommonCards().getCard("Island"), StaticData.instance().getCommonCards().getCard("Island"),
                StaticData.instance().getCommonCards().getCard("Island")));
        try (var table = new MatchTable("Commander", List.of(human, bot), RESOURCES,
                Files.createTempDirectory("automated-planned-red-output"))) {
            int policyId = table.game.getPlayers().get(1).getId();
            var driver = new Driver(table, policyId); table.start();
            var main = driver.until(input -> input.playerId == policyId && isPriority(input)
                    && input.state.get("activePlayerId").getAsInt() == policyId && text(input.state, "phaseKey").equals("MAIN1"));
            if (prefloat) {
                driver.use(main, card(main.state, "Battlefield", "Temple of Epiphany"), "activate", Map.of("R", 1));
                main = driver.until(input -> input.playerId == policyId && isPriority(input)
                        && viewer(input.state).getAsJsonObject("mana").get("R").getAsInt() == 1);
            }
            driver.use(main, card(main.state, "Command", "Balmor, Battlemage Captain"), "cast");
            var payment = driver.until(input -> input.playerId == policyId && text(input.prompt, "inputType").startsWith("InputPayMana"));
            if (prefloat) {
                assertEquals(payment.prompt.getAsJsonArray("usablePoolManaColors").toString(), "[\"R\"]");
                driver.answer(payment, Map.of("action", "spendPoolMana", "manaColor", "R"));
            } else {
                var observation = JSON.toJsonTree(table.automatedSeat(policyId).automatedObservation()).getAsJsonObject();
                Files.createDirectories(Path.of("target/rules-regressions"));
                Files.writeString(Path.of("target/rules-regressions/automated-temple-before-output.json"), JSON.toJson(observation));
                driver.use(payment, card(payment.state, "Battlefield", "Temple of Epiphany"), "activate", Map.of("R", 1));
            }
            payment = driver.until(input -> input.playerId == policyId && text(input.prompt, "inputType").startsWith("InputPayMana")
                    && input.prompt.getAsJsonArray("unpaidManaParts").equals(JSON.toJsonTree(List.of(Map.of("symbol", "{U}", "count", 1)))));
            assertTrue(card(payment.state, "Battlefield", "Temple of Epiphany").get("tapped").getAsBoolean());
            driver.use(payment, card(payment.state, "Battlefield", "Island"), "activate", Map.of("U", 1));
            var resolved = driver.until(input -> input.playerId == policyId && isPriority(input) && input.state.getAsJsonArray("stack").isEmpty()
                    && zone(viewer(input.state), "Battlefield").getAsJsonArray("cards").asList().stream()
                    .anyMatch(value -> text(value.getAsJsonObject(), "name").equals("Balmor, Battlemage Captain")));
            assertEquals(text(card(resolved.state, "Battlefield", "Balmor, Battlemage Captain"), "power"), "1");
            assertEquals(zone(viewer(resolved.state), "Battlefield").getAsJsonArray("cards").asList().stream()
                    .map(value -> value.getAsJsonObject()).filter(value -> text(value, "name").equals("Island") && value.get("tapped").getAsBoolean()).count(), 1L);
            assertEquals(viewer(resolved.state).getAsJsonObject("mana").get("U").getAsInt(), 0);
            assertEquals(viewer(resolved.state).getAsJsonObject("mana").get("R").getAsInt(), 0);
        }
    }

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void exactPoolPaymentCastsARealSpellAndRejectsAnUnavailableColor() throws Exception {
        if (StaticData.instance() == null) EngineResources.load(RESOURCES);
        var human = MatchTable.human(deck("Human", "Forest"), "Commander", "Human");
        var bot = MatchTable.automated(deck("Policy", "Sol Ring"), "Commander", "Policy");
        bot.addExtraCardsOnBattlefield(List.of(StaticData.instance().getCommonCards().getCard("Sol Ring"),
                StaticData.instance().getCommonCards().getCard("Forest")));
        try (var table = new MatchTable("Commander", List.of(human, bot), RESOURCES,
                Files.createTempDirectory("automated-floating-payment"))) {
            int policyId = table.game.getPlayers().get(1).getId();
            var driver = new Driver(table, policyId);
            table.start();
            var priority = driver.until(input -> input.playerId == policyId && isPriority(input)
                    && input.state.get("activePlayerId").getAsInt() == policyId
                    && text(input.state, "phaseKey").equals("MAIN1"));
            var ring = card(priority.state, "Battlefield", "Sol Ring");
            driver.use(priority, ring, "activate");
            priority = driver.until(input -> input.playerId == policyId && isPriority(input)
                    && viewer(input.state).getAsJsonObject("mana").get("C").getAsInt() == 2);
            driver.use(priority, card(priority.state, "Hand", "Sol Ring"), "cast");
            var payment = driver.until(input -> input.playerId == policyId
                    && text(input.prompt, "inputType").startsWith("InputPayMana"));
            assertFalse(payment.prompt.get("okEnabled").getAsBoolean(), "A policy cannot invoke Forge's Auto payment");
            var observation = JSON.toJsonTree(table.automatedSeat(policyId).automatedObservation()).getAsJsonObject();
            assertPaymentOwner(observation, "SpellCost", policyId);
            assertEquals(observation.getAsJsonObject("state").getAsJsonObject("prompt").get("payerId").getAsInt(), policyId);
            assertEquals(observation.getAsJsonObject("state").getAsJsonObject("prompt").getAsJsonArray("unpaidManaParts"),
                    JSON.toJsonTree(List.of(Map.of("symbol", "{1}", "count", 1))),
                    "Human-readable payment facts retain the exact current bill without unit expansion");
            assertEquals(observation.getAsJsonObject("semantics").getAsJsonArray("unpaidMana").toString(), "[\"{1}\"]");
            assertEquals(observation.getAsJsonObject("state").getAsJsonObject("prompt").getAsJsonArray("usablePoolManaColors").toString(), "[\"C\"]");
            assertEquals(observation.getAsJsonObject("domain").getAsJsonArray("poolManaColors").toString(), "[\"C\"]");
            var before = table.automatedSeat(policyId).state();
            assertThrows(IllegalArgumentException.class, () -> table.automatedSeat(policyId).action(
                    request(payment, Map.of("action", "spendPoolMana", "manaColor", "G"))));
            assertEquals(table.automatedSeat(policyId).state(), before, "Rejected pool payment cannot spend resources");
            driver.answer(payment, Map.of("action", "spendPoolMana", "manaColor", "C"));
            driver.until(input -> input.playerId == policyId && isPriority(input)
                    && !input.state.getAsJsonArray("stack").isEmpty());
            var resolved = driver.until(input -> input.playerId == policyId && isPriority(input)
                    && input.state.getAsJsonArray("stack").isEmpty()
                    && zone(viewer(input.state), "Battlefield").getAsJsonArray("cards").asList().stream()
                    .filter(value -> text(value.getAsJsonObject(), "name").equals("Sol Ring")).count() == 2);
            assertFalse(card(resolved.state, "Battlefield", "Forest").get("tapped").getAsBoolean(),
                    "Existing floating mana pays the spell without selecting a fresh mana source");
            assertEquals(viewer(resolved.state).getAsJsonObject("mana").get("C").getAsInt(), 1);
            assertTrue(zone(player(JSON.toJsonTree(table.seat(0).state()).getAsJsonObject(), policyId), "Hand")
                    .getAsJsonArray("cards").isEmpty(), "The human never receives the policy's hidden hand");
        }
    }

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void announcedDoubleXUsesCurrentTypedFundingThenManualPaymentAndResolvesWithThatInvestment() throws Exception {
        if (StaticData.instance() == null) EngineResources.load(RESOURCES);
        var human = MatchTable.human(deck("Human", "Forest"), "Commander", "Human");
        var bot = MatchTable.automated(deck("Policy", "Hangarback Walker"), "Commander", "Policy");
        bot.addExtraCardsOnBattlefield(Collections.nCopies(4, StaticData.instance().getCommonCards().getCard("Forest")));
        try (var table = new MatchTable("Commander", List.of(human, bot), RESOURCES,
                Files.createTempDirectory("automated-x-investment"))) {
            int policyId = table.game.getPlayers().get(1).getId();
            var driver = new Driver(table, policyId);
            table.start();
            var main = driver.until(input -> input.playerId == policyId && isPriority(input)
                    && input.state.get("activePlayerId").getAsInt() == policyId
                    && text(input.state, "phaseKey").equals("MAIN1"));
            driver.use(main, card(main.state, "Hand", "Hangarback Walker"), "cast");
            var number = driver.until(input -> input.playerId == policyId && text(input.prompt, "kind").equals("number"));
            var observation = JSON.toJsonTree(table.automatedSeat(policyId).automatedObservation()).getAsJsonObject();
            var numeric = observation.getAsJsonObject("semantics").getAsJsonObject("numeric");
            assertNotNull(numeric, "The real announcement callback must retain its exact current ability, not an untyped integer prompt");
            assertEquals(text(numeric, "purpose"), "AnnounceX");
            assertFalse(text(numeric, "sourceCardId").isEmpty());
            assertEquals(text(numeric, "sourceCardId"), text(number.prompt, "sourceCardId"));
            assertEquals(text(number.prompt.getAsJsonObject("sourceCard"), "name"), "Hangarback Walker");
            var symbols = numeric.getAsJsonArray("costs").asList().stream().map(value -> value.getAsJsonObject())
                    .filter(cost -> text(cost, "kind").equals("Mana"))
                    .flatMap(cost -> cost.getAsJsonArray("manaSymbols").asList().stream()).map(value -> value.getAsString()).toList();
            assertEquals(symbols, List.of("{X}", "{X}"));
            var funding = numeric.getAsJsonObject("funding");
            assertEquals(funding.getAsJsonArray("sources").size(), 4);
            assertEquals(funding.getAsJsonArray("sources").asList().stream()
                    .map(value -> text(value.getAsJsonObject(), "sourceCardId")).distinct().count(), 4L,
                    "Four forests are four independent physical resources");
            assertEquals(text(funding, "knowledge"), "Unknown");
            var sourceOccurrences = zone(viewer(number.state), "Battlefield").getAsJsonArray("cards").asList().stream()
                    .map(value -> text(value.getAsJsonObject(), "combatId")).toList();
            for (var source : funding.getAsJsonArray("sources")) {
                var route = source.getAsJsonObject().getAsJsonArray("routes").get(0).getAsJsonObject();
                assertTrue(sourceOccurrences.contains(text(source.getAsJsonObject(), "sourceCardId")));
                assertEquals(text(route, "eligibility"), "Known");
                assertEquals(route.getAsJsonObject("production").getAsJsonArray("alternatives").get(0)
                        .getAsJsonObject().getAsJsonObject("colors").get("G").getAsInt(), 1);
            }
            assertEquals(JSON.toJsonTree(table.automatedSeat(policyId).automatedObservation()), observation,
                    "Reading funding must not announce X, tap a source, or change the current scope");
            driver.answer(number, Map.of("action", "number", "value", 2));
            var payment = driver.until(input -> input.playerId == policyId && text(input.prompt, "inputType").startsWith("InputPayMana"));
            assertFalse(payment.prompt.get("okEnabled").getAsBoolean(), "The policy must fund XX itself");
            HeadlessPlatform.query(table, () -> null);
            var beforeStale = table.automatedSeat(policyId).state();
            var stale = expectThrows(IllegalArgumentException.class,
                    () -> table.automatedSeat(policyId).action(request(number, Map.of("action", "number", "value", 0))));
            assertEquals(stale.getMessage(), "That choice has changed. Use the current prompt.");
            assertEquals(table.automatedSeat(policyId).state(), beforeStale, "An expired numeric scope cannot revise the investment");
            var paying = JSON.toJsonTree(table.automatedSeat(policyId).automatedObservation()).getAsJsonObject();
            assertPaymentOwner(paying, "SpellCost", policyId);
            assertFalse(paying.getAsJsonObject("semantics").has("numeric"), "The announcement context ends when its decision is consumed");
            assertEquals(paying.getAsJsonObject("semantics").getAsJsonArray("unpaidMana").size(), 4, "XX with X=2 costs four mana");
            for (int resource = 0; resource < 4; resource++) {
                if (resource > 0) payment = driver.until(input -> input.playerId == policyId
                        && text(input.prompt, "inputType").startsWith("InputPayMana"));
                var forest = zone(viewer(payment.state), "Battlefield").getAsJsonArray("cards").asList().stream()
                        .map(value -> value.getAsJsonObject()).filter(value -> text(value, "name").equals("Forest")
                                && !value.get("tapped").getAsBoolean()).findFirst().orElseThrow();
                driver.use(payment, forest, "activate");
                int expected = resource + 1;
                driver.awaitState(state -> tappedForests(state) == expected);
            }
            var committed = driver.until(input -> input.playerId == policyId && isPriority(input)
                    && !input.state.getAsJsonArray("stack").isEmpty());
            assertEquals(tappedForests(committed.state), 4L);
            var resolved = driver.until(input -> input.playerId == policyId && isPriority(input)
                    && input.state.getAsJsonArray("stack").isEmpty()
                    && zone(viewer(input.state), "Battlefield").getAsJsonArray("cards").asList().stream()
                    .anyMatch(value -> text(value.getAsJsonObject(), "name").equals("Hangarback Walker")));
            var walker = card(resolved.state, "Battlefield", "Hangarback Walker");
            assertEquals(text(walker, "power"), "2"); assertEquals(text(walker, "toughness"), "2");
            assertEquals(walker.getAsJsonObject("counters").get("+1/+1").getAsInt(), 2);
            table.game.getAction().withState(() -> {
                var actual = table.game.getRegisteredPlayers().get(1).getCardsIn(ZoneType.Battlefield).stream()
                        .filter(value -> value.getName().equals("Hangarback Walker")).findFirst().orElseThrow();
                assertEquals(actual.getXManaCostPaid(), 2);
                assertEquals(actual.getCounters(CounterEnumType.P1P1), 2);
                assertEquals(actual.getNetPower(), 2); assertEquals(actual.getNetToughness(), 2);
            });
        }
    }

    @DataProvider public Object[][] optionalFunding() { return new Object[][] {{false}, {true}}; }

    @Test(dataProvider = "optionalFunding", groups = "canonical-interaction", timeOut = 90000)
    public void aRealOptionalTriggeredPaymentOwnsItsCurrentBillAndCanBeDeclinedWithoutInventingACast(boolean funded) throws Exception {
        if (StaticData.instance() == null) EngineResources.load(RESOURCES);
        var human = MatchTable.human(deck("Human", "Forest"), "Commander", "Human");
        var bot = MatchTable.automated(deck("Policy", "Memnite"), "Commander", "Policy");
        var permanents = new ArrayList<forge.item.IPaperCard>();
        permanents.add(StaticData.instance().getCommonCards().getCard("Mentor of the Meek"));
        if (funded) permanents.add(StaticData.instance().getCommonCards().getCard("Forest"));
        bot.addExtraCardsOnBattlefield(permanents);
        try (var table = new MatchTable("Commander", List.of(human, bot), RESOURCES,
                Files.createTempDirectory("automated-optional-trigger-payment"))) {
            int policyId = table.game.getPlayers().get(1).getId();
            var driver = new Driver(table, policyId);
            table.start();
            var main = driver.until(input -> input.playerId == policyId && isPriority(input)
                    && input.state.get("activePlayerId").getAsInt() == policyId
                    && text(input.state, "phaseKey").equals("MAIN1"));
            driver.use(main, card(main.state, "Hand", "Memnite"), "cast");
            // The rules deliberately go straight to this optional trigger's
            // payment. No artificial confirm/accept step belongs before it.
            var payment = driver.until(input -> input.playerId == policyId
                    && text(input.prompt, "inputType").startsWith("InputPayMana"));
            var observation = JSON.toJsonTree(table.automatedSeat(policyId).automatedObservation()).getAsJsonObject();
            assertPaymentOwner(observation, "TriggeredAbilityCost", policyId);
            assertEquals(text(payment.prompt.getAsJsonObject("sourceCard"), "name"), "Mentor of the Meek");
            assertEquals(observation.getAsJsonObject("semantics").getAsJsonArray("unpaidMana").toString(), "[\"{1}\"]");
            assertFalse(observation.getAsJsonObject("semantics").getAsJsonObject("payment").get("mandatory").getAsBoolean());
            assertTrue(payment.prompt.get("cancelEnabled").getAsBoolean());
            assertFalse(payment.prompt.get("okEnabled").getAsBoolean(), "Forge Auto never pays for the policy");
            int beforeHand = count(viewer(payment.state), "Hand");
            table.game.getAction().withState(() -> {
                var payer = table.game.getRegisteredPlayers().get(1);
                var controller = (PlayerControllerHuman)payer.getController();
                var input = (InputPayMana)controller.getInputQueue().getInput();
                assertSame(input.getPayer(), payer);
                assertTrue(input.getPaidForAbility().isTrigger());
                var activator = input.getPaidForAbility().getActivatingPlayer();
                int poolBefore = payer.getManaPool().totalMana();
                assertNull(PaymentDecisionProjection.describe(input, card -> ""), "An unentitled source cannot mint payment identity metadata");
                assertEquals(PaymentDecisionProjection.describe(input, table.identities::id),
                        PaymentDecisionProjection.describe(input, table.identities::id));
                assertSame(input.getPaidForAbility().getActivatingPlayer(), activator);
                assertEquals(payer.getManaPool().totalMana(), poolBefore);
            });
            if (funded) {
                driver.use(payment, card(payment.state, "Battlefield", "Forest"), "activate");
                driver.awaitState(state -> tappedForests(state) == 1);
            } else {
                assertTrue(observation.getAsJsonObject("domain").getAsJsonArray("sources").isEmpty());
                assertTrue(observation.getAsJsonObject("domain").getAsJsonArray("poolManaColors").isEmpty());
                driver.answer(payment, Map.of("action", "cancel"));
            }
            var resolved = driver.until(input -> input.playerId == policyId && isPriority(input)
                    && input.state.getAsJsonArray("stack").isEmpty());
            assertEquals(count(viewer(resolved.state), "Hand"), beforeHand + (funded ? 1 : 0));
            assertNotNull(card(resolved.state, "Battlefield", "Memnite"));
            var after = JSON.toJsonTree(table.automatedSeat(policyId).automatedObservation()).getAsJsonObject();
            assertFalse(after.getAsJsonObject("semantics").has("payment"), "Payment provenance ends with its owned input");
            assertThrows(IllegalArgumentException.class,
                    () -> table.automatedSeat(policyId).action(request(payment, Map.of("action", "cancel"))));
        }
    }

    private static void assertPaymentOwner(JsonObject observation, String purpose, int payerId) {
        var payment = observation.getAsJsonObject("semantics").getAsJsonObject("payment");
        assertNotNull(payment, "The current payment must retain typed rules provenance");
        assertEquals(text(payment, "purpose"), purpose);
        assertEquals(payment.get("payerId").getAsInt(), payerId);
        assertFalse(text(payment, "sourceCardId").isEmpty());
        assertEquals(text(payment, "sourceCardId"), text(observation.getAsJsonObject("state").getAsJsonObject("prompt")
                .getAsJsonObject("sourceCard"), "combatId"));
    }

    private static long tappedForests(JsonObject state) {
        return zone(viewer(state), "Battlefield").getAsJsonArray("cards").asList().stream()
                .map(value -> value.getAsJsonObject()).filter(value -> text(value, "name").equals("Forest")
                        && value.get("tapped").getAsBoolean()).count();
    }

    private static Deck deck(String name, String main) {
        var deck = new Deck(name);
        deck.getMain().add(StaticData.instance().getCommonCards().getCard(main), 99);
        deck.getOrCreate(DeckSection.Commander).add(StaticData.instance().getCommonCards().getCard("Rhys the Redeemed"));
        return deck;
    }

    private record Input(int playerId, MatchSession session, JsonObject state, JsonObject prompt) { }
    private static boolean isPriority(Input input) { return text(input.prompt, "inputType").equals("InputPassPriority"); }
    private static JsonObject card(JsonObject state, String zoneName, String name) {
        return zone(viewer(state), zoneName).getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "name").equals(name)).findFirst().orElseThrow();
    }
    private static JsonObject request(Input input, Map<String, ?> answer) {
        var request = JSON.toJsonTree(answer).getAsJsonObject();
        request.addProperty("sessionId", text(input.state, "id")); request.addProperty("promptId", text(input.prompt, "id"));
        return request;
    }

    private static final class Driver {
        private final MatchTable table;
        private final int policyId;
        private final List<MatchSession> seats;
        private final Set<String> answered = new HashSet<>();
        Driver(MatchTable table, int policyId) {
            this.table = table; this.policyId = policyId;
            seats = List.of(table.seat(0), table.automatedSeat(policyId));
        }
        Input until(Predicate<Input> stop) throws Exception {
            long deadline = System.nanoTime() + Duration.ofSeconds(20).toNanos();
            while (System.nanoTime() < deadline) {
                for (var session : seats) {
                    var state = JSON.toJsonTree(session.state()).getAsJsonObject();
                    assertNotEquals(text(state, "status"), "error", state.toString());
                    if (!state.has("prompt") || state.get("prompt").isJsonNull()) continue;
                    var prompt = state.getAsJsonObject("prompt");
                    if (answered.contains(text(prompt, "id"))) continue;
                    var input = new Input(state.get("viewerId").getAsInt(), session, state, prompt);
                    if (stop.test(input)) return input;
                    String kind = text(prompt, "inputType");
                    if (state.get("turn").getAsInt() == 0 && prompt.has("playerChoices") && !prompt.getAsJsonArray("playerChoices").isEmpty())
                        answer(input, Map.of("action", "player", "playerId", policyId));
                    else if ((kind.equals("InputPassPriority") || kind.equals("InputConfirmMulligan") || kind.equals("InputConfirm")
                            || kind.equals("InputAttack") || kind.equals("InputBlock")) && prompt.get("okEnabled").getAsBoolean())
                        answer(input, Map.of("action", kind.equals("InputPassPriority") ? "passPriority" : "ok"));
                    else throw new AssertionError("Unexpected fixture decision: " + prompt);
                }
                Thread.sleep(3);
            }
            throw new AssertionError("Timed out waiting for automated payment: " + seats.stream().map(MatchSession::state).toList());
        }
        void answer(Input input, Map<String, ?> answer) {
            input.session.action(request(input, answer)); answered.add(text(input.prompt, "id"));
        }
        void awaitState(Predicate<JsonObject> stop) throws Exception {
            long deadline = System.nanoTime() + Duration.ofSeconds(20).toNanos();
            while (System.nanoTime() < deadline) {
                var state = JSON.toJsonTree(table.automatedSeat(policyId).state()).getAsJsonObject();
                assertNotEquals(text(state, "status"), "error", state.toString());
                if (stop.test(state)) return;
                Thread.sleep(3);
            }
            throw new AssertionError("Timed out awaiting payment publication: " + table.automatedSeat(policyId).state());
        }
        void use(Input input, JsonObject source, String kind) {
            use(input, source, kind, null);
        }
        void use(Input input, JsonObject source, String kind, Map<String, Integer> output) {
            var query = request(input, Map.of("key", text(source, "key")));
            var catalog = JSON.toJsonTree(input.session.sourceActions(query)).getAsJsonObject();
            var action = catalog.getAsJsonArray("actions").asList().stream().map(value -> value.getAsJsonObject())
                    .filter(value -> text(value, "kind").equals(kind)).findFirst().orElseThrow(() ->
                            new AssertionError("Missing " + kind + " for " + source + "; catalog=" + catalog + "; state=" + input.state));
            var answer = new LinkedHashMap<String, Object>(Map.of("action", "useSourceAction", "key", text(source, "key"), "actionId", text(action, "id")));
            if (output != null) answer.put("manaOutput", Map.of("colors", output));
            answer(input, answer);
        }
    }
}
