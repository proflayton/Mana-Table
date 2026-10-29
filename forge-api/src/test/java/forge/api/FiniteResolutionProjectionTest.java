package forge.api;

import com.google.gson.Gson;
import com.google.gson.JsonObject;
import forge.StaticData;
import forge.deck.Deck;
import forge.game.ability.AbilityFactory;
import forge.game.card.Card;
import forge.game.cost.Cost;
import forge.game.mana.ManaCostBeingPaid;
import forge.game.player.Player;
import forge.game.spellability.SpellAbility;
import forge.game.zone.ZoneType;
import forge.gamemodes.match.input.InputPayMana;
import forge.player.PlayerControllerHuman;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.*;
import java.util.function.Predicate;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

public class FiniteResolutionProjectionTest {
    private static final Gson JSON = new Gson();

    @Test public void structuralGroupsAndPaymentTermsNeverBorrowDisplayTextOrUnknownCosts() throws Exception {
        try (var fixture = new Fixture()) {
            var source = fixture.add("Nadir Kraken");
            var ability = fixture.trigger(source);
            var first = fixture.group(ability);
            assertNotNull(first);
            var copy = ability.copy(); copy.setActivatingPlayer(fixture.actor());
            assertEquals(fixture.group(copy), first, "Distinct occurrences of one actual rule share a group");
            copy.getMapParams().put("SpellDescription", "A completely different localized label");
            copy.setDescription("Another rendered name");
            assertEquals(fixture.group(copy), first, "Rendered strings do not define equivalence");
            copy.getMapParams().put("CounterNum", "2");
            assertNotEquals(fixture.group(copy), first, "A changed actual rule is not an equal group");
            assertNotEquals(fixture.group(fixture.trigger(fixture.add("Nadir Kraken"))), first,
                    "Another physical source cannot borrow the original source's group");
            assertNull(ResolutionBatchProjection.group(ability, fixture.actor().getView(), card -> ""));

            var group = fixture.group(ability);
            var occurrence = new MatchExecutions.Occurrence("trigger", null, "trigger", "trigger", fixture.actor().getId(),
                    group.sourceCardId(), source.getName(), "cost", null, null, null);
            var frame = new MatchExecutions.Frame(1, 0, List.of(occurrence), List.of());
            var payment = fixture.payment(ability, 1, false);
            var one = fixture.offer(payment, "trigger", frame);
            assertNotNull(one);
            assertEquals(one.get("purpose"), "TriggeredAbilityCost");
            assertEquals(one.get("producingTriggerExecutionId"), "trigger");
            assertEquals(fixture.offer(fixture.payment(ability, 1, false), "trigger", frame).get("paymentKey"), one.get("paymentKey"));
            assertNotEquals(fixture.offer(fixture.payment(ability, 2, false), "trigger", frame).get("paymentKey"), one.get("paymentKey"),
                    "A changed current remaining bill is not the same optional payment");
            assertNull(fixture.offer(fixture.payment(ability, 1, true), "trigger", frame));
            assertNull(fixture.offer(payment, "missing", frame));
            var concealed = new MatchExecutions.Frame(1, 0, List.of(new MatchExecutions.Occurrence("trigger", null,
                    "trigger", "trigger", fixture.actor().getId(), null, null, "cost", null, null, null)), List.of());
            assertNull(fixture.offer(payment, "trigger", concealed), "A private/missing owner cannot be recovered from the current board");
            ability.setPayCosts(new Cost("1 Sac<1/CARDNAME>", true));
            assertNull(fixture.offer(payment, "trigger", frame), "A mixed nonmana commitment remains explicitly unsupported");
            ability.setPayCosts(new Cost("X", true));
            assertNull(fixture.offer(payment, "trigger", frame), "A variable commitment is not silently treated as a literal repeated cost");
        }
    }

    @Test public void exactDisclosedTriggerParentsAreDistinctFromAnUnlinkedBudgetRoot() throws Exception {
        try (var fixture = new Fixture()) {
            var ability = fixture.trigger(fixture.add("Nadir Kraken"));
            String source = fixture.group(ability).sourceCardId();
            int actor = fixture.actor().getId();
            var trigger = new MatchExecutions.Occurrence("trigger", null, "trigger", "trigger", actor, source, "Source", "resolution", null, null, null);
            var child = new MatchExecutions.Occurrence("child", "trigger", "trigger", "rules", actor, source, "Source", "cost", null, "replacement", null);
            var linked = new MatchExecutions.Frame(1, 0, List.of(trigger, child), List.of());
            assertEquals(fixture.offer(fixture.payment(ability, 1, false), "child", linked).get("producingTriggerExecutionId"), "trigger");
            var unlinked = new MatchExecutions.Frame(1, 0, List.of(trigger, new MatchExecutions.Occurrence("child", null,
                    "trigger", "rules", actor, source, "Source", "cost", null, "replacement", null)), List.of());
            assertNull(fixture.offer(fixture.payment(ability, 1, false), "child", unlinked),
                    "Sharing a budget root never invents a disclosed parent link");
        }
    }

    @Test public void expectedChoicesPreserveOnlyTheNewActionFence() {
        var fence = new StackInterruption();
        Object first = new Object(), second = new Object();
        fence.observeChoices(List.of(first));
        assertEquals(fence.version(), 1); assertEquals(fence.actionVersion(), 0);
        fence.observeChoices(List.of(first));
        assertEquals(fence.version(), 1);
        fence.observeChoices(List.of(second));
        assertEquals(fence.version(), 2); assertEquals(fence.actionVersion(), 0);
        fence.action(); assertEquals(fence.version(), 3); assertEquals(fence.actionVersion(), 1);
    }

    @Test(groups = "canonical-interaction", timeOut = 180000)
    public void pantherIdolMindsEyeDeclinesOnlyTheOtherSeatsFiftyFiveActualCosts() throws Exception {
        var own = new ArrayList<String>(List.of("Toothy, Imaginary Friend", "Reliquary Tower"));
        own.addAll(Collections.nCopies(54, "Memnite")); own.addAll(Collections.nCopies(5, "Forest"));
        try (var scenario = new RulesScenario("finite-panther-idol-55", Map.of(0, own,
                1, List.of("Mind's Eye"), 2, List.of("Reliquary Tower"), 3, List.of("Reliquary Tower")),
                "Shamanic Revelation", "Progenitus")) {
            // Primary edition data maps both MSC288 and MSC447 to the same
            // Mind's Eye rules with Panther Idol as the printing's flavor name.
            for (String collector : List.of("288", "447")) {
                var printing = StaticData.instance().getCommonCards().getCard("Panther Idol", "MSC", collector);
                assertNotNull(printing); assertEquals(printing.getRules().getName(), "Mind's Eye");
                assertEquals(printing.getDisplayName(), "Panther Idol");
            }
            var main = ready(scenario, input -> input.seat() == 0 && priority(input) && text(input.state(), "phaseKey").equals("MAIN1")
                    && input.state().get("activePlayerId").getAsInt() == input.state().get("viewerId").getAsInt()
                    && input.state().getAsJsonArray("stack").isEmpty());
            int before = counters(main.state(), "Toothy, Imaginary Friend");
            var eyeBefore = scenario.state(1);
            int handBefore = count(viewer(eyeBefore), "Hand");
            var manaBefore = viewer(eyeBefore).getAsJsonObject("mana").deepCopy();
            castRevelation(scenario, main);
            var burst = ready(scenario, input -> priority(input) && input.state().getAsJsonArray("stack").size() == 110);
            var original = new HashSet<String>(); var payments = new HashSet<String>();
            String groupKey = "", sourceId = "";
            for (var value : burst.state().getAsJsonArray("stack")) {
                var item = value.getAsJsonObject();
                assertTrue(original.add(text(item, "executionId")));
                var repetition = item.getAsJsonObject("repetition"); assertNotNull(repetition);
                if (text(item.getAsJsonObject("card"), "name").equals("Mind's Eye")) {
                    payments.add(text(item, "executionId"));
                    if (groupKey.isEmpty()) { groupKey = text(repetition, "groupKey"); sourceId = text(repetition, "sourceCardId"); }
                    assertEquals(text(repetition, "groupKey"), groupKey);
                    assertEquals(text(repetition, "sourceCardId"), sourceId);
                }
            }
            assertEquals(original.size(), 110); assertEquals(payments.size(), 55);
            long actionFence = burst.state().get("stackActionVersion").getAsLong();
            long legacyFence = burst.state().get("stackInterruptionVersion").getAsLong();
            var choiceFences = new long[4];
            for (int seat = 0; seat < 4; seat++) choiceFences[seat] = scenario.state(seat).get("resolutionChoiceVersion").getAsLong();
            var granted = new HashSet<Integer>(); var granting = burst;
            for (int count = 0; count < 4; count++) {
                assertTrue(granted.add(granting.seat())); grant(scenario, granting, "panther-seat-" + granting.seat());
                if (count < 3) granting = ready(scenario, input -> priority(input) && input.state().getAsJsonArray("stack").size() == 110);
            }
            var answered = new HashSet<String>();
            String paymentKey = "", previous = "";
            while (answered.size() < 55) {
                var input = waitOwnPaymentOrSettled(scenario, 1);
                assertTrue(payment(input)); assertEquals(input.seat(), 1);
                var offer = input.prompt().getAsJsonObject("optionalDecline");
                assertNotNull(offer, "Mind's Eye's real cancellable mana input is certified without an OptionalDecider script parameter");
                var support = input.prompt().getAsJsonObject("optionalPaymentSupport");
                assertNotNull(support, "The reported Panther Idol printing has an authoritative current mana-only support fact");
                assertEquals(text(support, "availability"), "Unpayable");
                assertFalse(support.get("canAutoPay").getAsBoolean());
                assertEquals(support.getAsJsonArray("originalManaParts"), JSON.toJsonTree(List.of(Map.of("symbol", "{1}", "count", 1))));
                assertFalse(offer.get("mandatory").getAsBoolean());
                assertTrue(input.prompt().get("cancelEnabled").getAsBoolean());
                assertEquals(offer.get("payerId").getAsInt(), input.state().get("viewerId").getAsInt());
                assertEquals(text(offer, "purpose"), "TriggeredAbilityCost");
                assertEquals(text(offer, "groupKey"), groupKey); assertEquals(text(offer, "sourceCardId"), sourceId);
                assertTrue(payments.contains(text(offer, "producingTriggerExecutionId")));
                assertTrue(answered.add(text(offer, "producingTriggerExecutionId")));
                if (paymentKey.isEmpty()) paymentKey = text(offer, "paymentKey");
                assertEquals(text(offer, "paymentKey"), paymentKey);
                if (!previous.isEmpty()) assertEquals(text(offer, "previousOfferId"), previous);
                previous = text(offer, "offerId"); assertFalse(previous.isEmpty());
                assertEquals(input.state().get("stackActionVersion").getAsLong(), actionFence);
                assertTrue(input.state().get("stackInterruptionVersion").getAsLong() > legacyFence);
                for (int seat = 0; seat < 4; seat++)
                    assertEquals(scenario.state(seat).get("resolutionChoiceVersion").getAsLong(), choiceFences[seat]);
                assertTrue(scenario.answer(input, Map.of("action", "cancel")));
            }
            assertEquals(answered, payments);
            var settled = waitOwnPaymentOrSettled(scenario);
            assertTrue(priority(settled)); assertTrue(settled.state().getAsJsonArray("stack").isEmpty());
            var eyeAfter = scenario.state(1);
            assertEquals(count(viewer(eyeAfter), "Hand"), handBefore, "All55 actual declines leave the artifact controller's hand unchanged");
            assertEquals(viewer(eyeAfter).getAsJsonObject("mana"), manaBefore);
            assertEquals(counters(scenario.state(0), "Toothy, Imaginary Friend"), before + 55);
            int passes = 0;
            for (int seat = 0; seat < 4; seat++) {
                var state = scenario.state(seat); var receipt = state.getAsJsonObject("finiteStackYield");
                assertEquals(text(receipt, "status"), "Completed");
                assertTrue(receipt.getAsJsonArray("remainingExecutionIds").isEmpty());
                passes += receipt.get("automaticPasses").getAsInt();
                assertEquals(state.get("stackActionVersion").getAsLong(), actionFence);
                assertEquals(state.get("resolutionChoiceVersion").getAsLong(), choiceFences[seat]);
                var terminals = state.getAsJsonObject("executions").getAsJsonArray("occurrences").asList().stream()
                        .map(value -> value.getAsJsonObject()).filter(value -> original.contains(text(value, "id"))).toList();
                assertEquals(terminals.size(), 110);
                assertTrue(terminals.stream().allMatch(value -> text(value, "step").equals("complete") && text(value, "outcome").equals("resolved")),
                        "Negative payment receipts do not relabel normal trigger resolution as declined");
            }
            assertEquals(passes, 440);
            Files.writeString(scenario.evidence.resolve("private-panther-idol-settled.json"), JSON.toJson(settled.state()));
        }
    }

    @Test(groups = "canonical-interaction", timeOut = 180000)
    public void fiftyFiveHumanOptionalPaymentsRetainExactFiniteMembersAndNoSpentResources() throws Exception {
        var own = new ArrayList<String>(List.of("Nadir Kraken", "Toothy, Imaginary Friend", "Reliquary Tower"));
        own.addAll(Collections.nCopies(53, "Memnite")); own.addAll(Collections.nCopies(5, "Forest"));
        try (var scenario = new RulesScenario("finite-optional-55", Map.of(0, own, 1, List.of("Reliquary Tower"),
                2, List.of("Reliquary Tower"), 3, List.of("Reliquary Tower")), "Shamanic Revelation", "Progenitus")) {
            var main = ready(scenario, input -> input.seat() == 0 && priority(input) && text(input.state(), "phaseKey").equals("MAIN1")
                    && input.state().get("activePlayerId").getAsInt() == input.state().get("viewerId").getAsInt()
                    && input.state().getAsJsonArray("stack").isEmpty());
            int before = counters(main.state(), "Toothy, Imaginary Friend");
            castRevelation(scenario, main);
            var burst = ready(scenario, input -> input.seat() == 0 && priority(input) && input.state().getAsJsonArray("stack").size() == 110);
            var members = new HashSet<String>();
            String groupKey = "";
            for (var value : burst.state().getAsJsonArray("stack")) {
                var item = value.getAsJsonObject(); var repetition = item.getAsJsonObject("repetition");
                assertNotNull(repetition); assertFalse(text(repetition, "groupKey").isEmpty());
                if (text(item.getAsJsonObject("card"), "name").equals("Nadir Kraken")) {
                    members.add(text(item, "executionId"));
                    if (groupKey.isEmpty()) groupKey = text(repetition, "groupKey");
                    assertEquals(text(repetition, "groupKey"), groupKey);
                }
            }
            assertEquals(members.size(), 55);
            long actionFence = burst.state().get("stackActionVersion").getAsLong();
            long choiceFence = burst.state().get("resolutionChoiceVersion").getAsLong();
            long legacyFence = burst.state().get("stackInterruptionVersion").getAsLong();
            long otherChoice = scenario.state(1).get("resolutionChoiceVersion").getAsLong();
            var granting = burst;
            for (int seat = 0; seat < 4; seat++) {
                grant(scenario, granting, "optional-seat-" + granting.seat());
                if (seat < 3) granting = ready(scenario, next -> priority(next) && next.state().getAsJsonArray("stack").size() == 110);
            }
            var answered = new HashSet<String>();
            String paymentKey = "", previous = "";
            RulesScenario.Input settled = null;
            while (answered.size() <= 55) {
                var input = waitOwnPaymentOrSettled(scenario);
                if (!payment(input)) { settled = input; break; }
                var offer = input.prompt().getAsJsonObject("optionalDecline");
                assertNotNull(offer, "The actual current human payment carries its exact negative-answer provenance");
                assertEquals(text(offer, "groupKey"), groupKey);
                assertEquals(text(offer, "purpose"), "TriggeredAbilityCost");
                assertFalse(offer.get("mandatory").getAsBoolean());
                assertEquals(offer.get("payerId").getAsInt(), input.state().get("viewerId").getAsInt());
                assertTrue(members.contains(text(offer, "producingTriggerExecutionId")));
                assertTrue(answered.add(text(offer, "producingTriggerExecutionId")), "Only one decline belongs to each original occurrence");
                if (paymentKey.isEmpty()) paymentKey = text(offer, "paymentKey");
                assertEquals(text(offer, "paymentKey"), paymentKey);
                if (!previous.isEmpty()) assertEquals(text(offer, "previousOfferId"), previous);
                previous = text(offer, "offerId"); assertFalse(previous.isEmpty());
                assertEquals(input.state().get("stackActionVersion").getAsLong(), actionFence);
                assertEquals(input.state().get("resolutionChoiceVersion").getAsLong(), choiceFence);
                assertEquals(scenario.state(1).get("resolutionChoiceVersion").getAsLong(), otherChoice);
                assertTrue(input.state().get("stackInterruptionVersion").getAsLong() > legacyFence,
                        "Expected optional choices still revoke ordinary finite-stack consent");
                assertTrue(scenario.answer(input, Map.of("action", "cancel")));
            }
            assertNotNull(settled); assertEquals(answered, members);
            assertEquals(counters(settled.state(), "Toothy, Imaginary Friend"), before + 55);
            assertEquals(counters(settled.state(), "Nadir Kraken"), 0);
            assertEquals(zone(viewer(settled.state()), "Battlefield").getAsJsonArray("cards").asList().stream()
                    .map(value -> value.getAsJsonObject()).filter(value -> text(value, "name").contains("Tentacle")).count(), 0);
            assertEquals(settled.state().get("stackActionVersion").getAsLong(), actionFence);
            int passes = 0;
            for (int seat = 0; seat < 4; seat++) {
                var receipt = scenario.state(seat).getAsJsonObject("finiteStackYield");
                assertEquals(text(receipt, "status"), "Completed", "Only exact owned negative payments pause and resume the original grant");
                passes += receipt.get("automaticPasses").getAsInt();
            }
            assertEquals(passes, 440, "All110 triggers keep four actual rules passes while55 negative costs remain explicit commands");
        }
    }

    @Test(groups = "canonical-interaction", timeOut = 180000)
    public void fourHumanFiniteGrantsReplaceTwoHundredPriorityRequestsWithoutSkippingTriggers() throws Exception {
        var own = new ArrayList<String>(List.of("Toothy, Imaginary Friend", "Pir, Imaginative Rascal", "Reliquary Tower"));
        own.addAll(Collections.nCopies(48, "Memnite")); own.addAll(Collections.nCopies(5, "Forest"));
        try (var scenario = new RulesScenario("finite-server-50", Map.of(0, own, 1, List.of("Reliquary Tower"),
                2, List.of("Reliquary Tower"), 3, List.of("Reliquary Tower")), "Shamanic Revelation", "Progenitus")) {
            var main = ready(scenario, input -> input.seat() == 0 && priority(input) && text(input.state(), "phaseKey").equals("MAIN1")
                    && input.state().get("activePlayerId").getAsInt() == input.state().get("viewerId").getAsInt()
                    && input.state().getAsJsonArray("stack").isEmpty());
            int before = counters(main.state(), "Toothy, Imaginary Friend");
            castRevelation(scenario, main);
            var granted = new HashSet<Integer>(); var original = new HashSet<String>();
            for (int count = 0; count < 4; count++) {
                var input = ready(scenario, next -> priority(next) && next.state().getAsJsonArray("stack").size() == 50);
                assertTrue(granted.add(input.seat()), "Each seat independently grants only its own priority");
                var members = input.state().getAsJsonArray("stack").asList().stream().map(value -> text(value.getAsJsonObject(), "executionId")).toList();
                if (original.isEmpty()) original.addAll(members); else assertEquals(new HashSet<>(members), original);
                assertEquals(members.size(), 50);
                assertTrue(scenario.answer(input, Map.of("action", "grantFiniteStackYield", "grantId", "grant-seat-" + input.seat(),
                        "executionIds", members, "stackActionVersion", input.state().get("stackActionVersion").getAsLong(),
                        "resolutionChoiceVersion", input.state().get("resolutionChoiceVersion").getAsLong())));
            }
            // From here on no driver answers priority. Only actual provider-owned
            // InputPassPriority operations may consume these four finite grants.
            long deadline = System.nanoTime() + 45_000_000_000L;
            JsonObject settled = null;
            while (System.nanoTime() < deadline) {
                boolean complete = true;
                for (int seat = 0; seat < 4; seat++) {
                    var state = scenario.state(seat); var grant = state.getAsJsonObject("finiteStackYield");
                    assertNotEquals(text(grant, "status"), "Revoked", grant.toString());
                    complete &= text(grant, "status").equals("Completed");
                    if (seat == 0) settled = state;
                }
                if (complete) break;
                Thread.sleep(5);
            }
            int passes = 0;
            for (int seat = 0; seat < 4; seat++) {
                var state = scenario.state(seat); var grant = state.getAsJsonObject("finiteStackYield");
                assertEquals(text(grant, "status"), "Completed", "Every original occurrence has an authoritative terminal receipt");
                assertTrue(grant.getAsJsonArray("remainingExecutionIds").isEmpty());
                passes += grant.get("automaticPasses").getAsInt();
                assertEquals(grant.getAsJsonArray("executionIds").asList().stream().map(value -> value.getAsString())
                        .collect(java.util.stream.Collectors.toSet()), original);
                var receipts = state.getAsJsonObject("executions").getAsJsonArray("occurrences").asList().stream()
                        .map(value -> value.getAsJsonObject()).filter(value -> original.contains(text(value, "id"))).toList();
                assertEquals(receipts.size(), 50);
                assertTrue(receipts.stream().allMatch(value -> text(value, "step").equals("complete") && text(value, "outcome").equals("resolved")));
            }
            assertEquals(passes, 200, "Four grants replace 200 separate priority commands, preserving all rules passes");
            assertNotNull(settled); assertTrue(settled.getAsJsonArray("stack").isEmpty());
            assertEquals(counters(settled, "Toothy, Imaginary Friend"), before + 100,
                    "All fifty effects and their actual Pir replacements commit through the real engine");
            assertEquals(settled.getAsJsonArray("activity").asList().stream().map(value -> value.getAsJsonObject())
                    .filter(value -> text(value, "kind").equals("resolved") && original.contains(text(value, "executionId")))
                    .map(value -> text(value, "executionId")).collect(java.util.stream.Collectors.toSet()), original,
                    "No public settlement announcement is elided by the grant");
            Files.writeString(scenario.evidence.resolve("private-finite-grants-settled.json"), JSON.toJson(settled));
        }
    }

    @Test(groups = "canonical-interaction", timeOut = 120000)
    public void explicitStopAndHistoryOverflowWinBeforeAnAlreadyQueuedAutomaticPass() throws Exception {
        for (boolean overflow : List.of(false, true)) {
            try (var scenario = new RulesScenario("finite-queued-stop-" + overflow,
                    Map.of(0, List.of("Toothy, Imaginary Friend", "Reliquary Tower")))) {
                var input = ready(scenario, next -> next.seat() == 0 && priority(next) && !next.state().getAsJsonArray("stack").isEmpty());
                var entered = new CountDownLatch(1); var release = new CountDownLatch(1);
                HeadlessPlatform.later(() -> {
                    entered.countDown();
                    try (var suspended = scenario.table.game.getAction().suspendState()) {
                        if (!release.await(10, TimeUnit.SECONDS)) throw new AssertionError("Input barrier expired");
                    } catch (InterruptedException error) { Thread.currentThread().interrupt(); throw new AssertionError(error); }
                });
                assertTrue(entered.await(5, TimeUnit.SECONDS));
                try {
                    var tooMany = java.util.stream.IntStream.range(0, 257).mapToObj(index -> "execution-" + index).toList();
                    assertThrows(IllegalArgumentException.class, () -> scenario.session(0).action(request(input,
                            Map.of("action", "grantFiniteStackYield", "grantId", "oversized", "executionIds", tooMany,
                                    "stackActionVersion", input.state().get("stackActionVersion").getAsLong(),
                                    "resolutionChoiceVersion", input.state().get("resolutionChoiceVersion").getAsLong()))));
                    grant(scenario, input, "queued");
                    assertThrows(IllegalArgumentException.class, () -> scenario.session(1).action(request(input,
                            Map.of("action", "revokeFiniteStackYield", "grantId", "queued"))));
                    if (overflow) {
                        String member = text(input.state().getAsJsonArray("stack").get(0).getAsJsonObject(), "executionId");
                        scenario.table.game.getAction().withState(() -> {
                            var source = scenario.table.game.getPlayers().get(0).getCardsIn(ZoneType.Battlefield).stream()
                                    .filter(value -> value.getName().equals("Toothy, Imaginary Friend")).findFirst().orElseThrow();
                            // Authored emission pressure validates guard/retention;
                            // no running card's resources or counters are modified.
                            try (var execution = scenario.table.game.getExecutions().enterCause(member)) {
                                for (int count = 0; count <= MatchActivity.MAXIMUM_FINITE_ENTRIES; count++)
                                    scenario.table.game.fireEvent(new forge.game.event.GameEventCardCounters(source, forge.game.card.CounterEnumType.P1P1, count, count + 1));
                            }
                            scenario.table.publish();
                        });
                    } else scenario.session(0).action(request(input, Map.of("action", "revokeFiniteStackYield", "grantId", "queued")));
                } finally { release.countDown(); }
                HeadlessPlatform.query(scenario.table, () -> null); // Drain the already queued finite task.
                var stopped = scenario.state(0); var receipt = stopped.getAsJsonObject("finiteStackYield");
                assertEquals(text(receipt, "status"), "Revoked");
                assertEquals(text(receipt, "reasonCode"), overflow ? "history-overflow" : "explicit");
                assertEquals(receipt.get("automaticPasses").getAsInt(), 0);
                assertEquals(text(stopped.getAsJsonObject("prompt"), "id"), text(input.prompt(), "id"),
                        "Revocation leaves the same still-owned priority input available for manual play");
            }
        }
    }

    private static void castRevelation(RulesScenario scenario, RulesScenario.Input main) throws Exception {
        use(scenario, main, card(main.state(), "Hand", "Shamanic Revelation"), "cast");
        var paying = ready(scenario, input -> input.seat() == 0 && payment(input));
        assertFalse(paying.prompt().has("optionalDecline"), "An ordinary spell payment is never a repeated optional trigger answer");
        for (int mana = 0; mana < 5; mana++) {
            var forest = zone(viewer(paying.state()), "Battlefield").getAsJsonArray("cards").asList().stream()
                    .map(value -> value.getAsJsonObject()).filter(value -> text(value, "name").equals("Forest") && !value.get("tapped").getAsBoolean())
                    .findFirst().orElseThrow();
            use(scenario, paying, forest, "activate");
            paying = ready(scenario, input -> input.seat() == 0 && (payment(input) || priority(input)));
        }
    }

    private static void grant(RulesScenario scenario, RulesScenario.Input input, String id) {
        var members = input.state().getAsJsonArray("stack").asList().stream().map(value -> text(value.getAsJsonObject(), "executionId")).toList();
        assertTrue(scenario.answer(input, Map.of("action", "grantFiniteStackYield", "grantId", id, "executionIds", members,
                "stackActionVersion", input.state().get("stackActionVersion").getAsLong(),
                "resolutionChoiceVersion", input.state().get("resolutionChoiceVersion").getAsLong())));
    }
    private static RulesScenario.Input waitOwnPaymentOrSettled(RulesScenario scenario) throws Exception {
        return waitOwnPaymentOrSettled(scenario, 0);
    }
    private static RulesScenario.Input waitOwnPaymentOrSettled(RulesScenario scenario, int seat) throws Exception {
        long deadline = System.nanoTime() + 45_000_000_000L;
        while (System.nanoTime() < deadline) {
            var state = scenario.state(seat);
            assertNotEquals(text(state.getAsJsonObject("finiteStackYield"), "status"), "Revoked", state.get("finiteStackYield").toString());
            if (state.has("prompt") && !state.get("prompt").isJsonNull()) {
                var input = new RulesScenario.Input(seat, state, state.getAsJsonObject("prompt"));
                if (payment(input) || priority(input) && state.getAsJsonArray("stack").isEmpty()) return input;
            }
            Thread.sleep(2);
        }
        throw new AssertionError("Finite optional sequence did not publish its next owned cost or terminal priority");
    }

    private static RulesScenario.Input ready(RulesScenario scenario, Predicate<RulesScenario.Input> stop) throws Exception {
        while (true) {
            var input = scenario.until(next -> stop.test(next) || payment(next)
                    || next.prompt().has("ordered") && next.prompt().get("ordered").getAsBoolean()
                    || text(next.prompt(), "inputType").equals("InputSelectTargets") || next.prompt().has("librarySearch"));
            if (stop.test(input)) return input;
            if (payment(input)) {
                assertTrue(input.prompt().has("optionalDecline"), "The setup driver never guesses an unknown payment's answer");
                scenario.answer(input, Map.of("action", "cancel"));
            } else if (text(input.prompt(), "inputType").equals("InputSelectTargets"))
                scenario.answer(input, Map.of("action", "player", "playerId", input.state().get("viewerId").getAsInt()));
            else if (input.prompt().has("librarySearch")) scenario.answer(input, Map.of("action", "cancel"));
            else scenario.answer(input, Map.of("choices", java.util.stream.IntStream.range(0, input.prompt().getAsJsonArray("choices").size()).boxed().toList()));
        }
    }
    private static boolean payment(RulesScenario.Input input) { return text(input.prompt(), "inputType").startsWith("InputPayMana"); }
    private static boolean priority(RulesScenario.Input input) { return text(input.prompt(), "inputType").equals("InputPassPriority"); }
    private static JsonObject card(JsonObject state, String zone, String name) {
        return RulesScenario.zone(viewer(state), zone).getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "name").equals(name)).findFirst().orElseThrow();
    }
    private static int counters(JsonObject state, String name) {
        var counters = card(state, "Battlefield", name).getAsJsonObject("counters");
        return counters.has("+1/+1") ? counters.get("+1/+1").getAsInt() : 0;
    }
    private static void use(RulesScenario scenario, RulesScenario.Input input, JsonObject card, String kind) {
        var catalog = JSON.toJsonTree(scenario.session(input.seat()).sourceActions(request(input, Map.of("key", text(card, "key"))))).getAsJsonObject();
        var action = catalog.getAsJsonArray("actions").asList().stream().map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "kind").equals(kind)).findFirst().orElseThrow();
        assertTrue(scenario.answer(input, Map.of("action", "useSourceAction", "key", text(card, "key"), "actionId", text(action, "id"))));
    }

    private static final class Fixture implements AutoCloseable {
        final MatchTable table;
        Fixture() throws Exception {
            var resources = Path.of("../forge-gui/res");
            if (StaticData.instance() == null) EngineResources.load(resources);
            table = new MatchTable("Constructed", List.of(MatchTable.human(new Deck(), "Constructed", "Actor"),
                    MatchTable.human(new Deck(), "Constructed", "Opponent")), resources, Files.createTempDirectory("finite-resolution"));
            HeadlessPlatform.activate(table);
        }
        Player actor() { return table.game.getRegisteredPlayers().get(0); }
        Card add(String name) {
            var card = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard(name), actor());
            actor().getZone(ZoneType.Battlefield).add(card); return card;
        }
        SpellAbility trigger(Card card) {
            var ability = AbilityFactory.getAbility(card.getSVar("TrigPutCounter"), card);
            ability.setActivatingPlayer(actor()); ability.setTrigger(card.getTriggers().get(0));
            return ability;
        }
        ResolutionBatchProjection.Group group(SpellAbility ability) {
            return ResolutionBatchProjection.group(ability, actor().getView(), card -> "source-" + card.getId());
        }
        PaymentProbe payment(SpellAbility ability, int generic, boolean mandatory) {
            return new PaymentProbe((PlayerControllerHuman)actor().getController(), ability, actor(), generic, mandatory);
        }
        Map<String, Object> offer(InputPayMana input, String id, MatchExecutions.Frame frame) {
            return ResolutionBatchProjection.optional(input, actor().getView(), card -> "source-" + card.getId(), id, frame);
        }
        @Override public void close() { table.close(); }
    }
    private static final class PaymentProbe extends InputPayMana {
        PaymentProbe(PlayerControllerHuman controller, SpellAbility ability, Player payer, int generic, boolean required) {
            super(controller, ability, payer, false); mandatory = required;
            manaCost = new ManaCostBeingPaid(forge.card.mana.ManaCost.get(generic));
        }
        @Override protected void done() { }
        @Override protected String getMessage() { return ""; }
    }
}
