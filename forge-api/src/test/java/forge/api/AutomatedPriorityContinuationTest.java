package forge.api;

import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import forge.StaticData;
import forge.deck.Deck;
import forge.deck.DeckSection;
import forge.game.event.GameEventCardTapped;
import forge.game.zone.ZoneType;
import forge.gamemodes.match.input.InputPassPriority;
import forge.player.PlayerControllerHuman;
import org.testng.annotations.DataProvider;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.function.Predicate;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Explicit bot consent uses the real controller's scan and each observer's own credit. */
public class AutomatedPriorityContinuationTest {
    private static final Gson JSON = new GsonBuilder().serializeNulls().create();
    private static final Path RESOURCES = Path.of("../forge-gui/res");

    @Test(timeOut = 90000)
    public void theActualProductionReadyEnvelopeNegotiatesTheSeparateBotContract() throws Exception {
        var evidence = ApiTestWorkspace.evidence("bot-auto-ready");
        var profile = Files.createDirectories(evidence.resolve("profile"));
        var cache = Files.createDirectories(evidence.resolve("cache"));
        var executable = Path.of(System.getProperty("java.home"), "bin", System.getProperty("os.name").startsWith("Windows") ? "java.exe" : "java");
        var process = new ProcessBuilder(executable.toString(), "-Xmx1200m", "-Dfile.encoding=UTF-8", "-Djava.awt.headless=true",
                "-Dforge.resources=" + RESOURCES.toAbsolutePath().normalize() + java.io.File.separator,
                "-Dforge.userDir=" + profile, "-Dforge.cacheDir=" + cache, "-cp", System.getProperty("java.class.path"),
                DesktopEngine.class.getName(), RESOURCES.toString(), evidence.resolve("decks").toString())
                .redirectOutput(evidence.resolve("protocol.jsonl").toFile()).redirectError(evidence.resolve("diagnostics.log").toFile()).start();
        try {
            process.getOutputStream().close();
            assertTrue(process.waitFor(45, TimeUnit.SECONDS), "The isolated production ready probe must finish");
            assertEquals(process.exitValue(), 0, Files.readString(evidence.resolve("diagnostics.log")));
        } finally { if (process.isAlive()) { process.destroyForcibly(); process.waitFor(5, TimeUnit.SECONDS); } }
        var ready = Files.readString(evidence.resolve("protocol.jsonl")).lines().map(line -> JsonParser.parseString(line).getAsJsonObject())
                .filter(value -> text(value, "event").equals("ready")).findFirst().orElseThrow();
        var capabilities = ready.getAsJsonArray("capabilities").asList().stream().map(value -> value.getAsString()).toList();
        assertTrue(capabilities.containsAll(List.of("automated-seats", "priority-continuation-v1", "automated-priority-continuation-v1", "automated-priority-metadata-v1", "automated-priority-responses-v1")),
                "Human Auto support alone cannot negotiate the bot's completed-scan and self-observer contract");
        assertEquals(Set.copyOf(capabilities).size(), capabilities.size());
    }

    @Test(timeOut = 90000)
    public void anUnregisteredBotCannotEnableAutoAndLegacyFullControlKeepsItsUnknownScan() throws Exception {
        try (var fixture = new Fixture("bot-auto-no-observer", false)) {
            var main = fixture.main(); var input = fixture.actual();
            assertEquals(text(main.prompt(), "responseAvailability"), "Unknown");
            assertThrows(IllegalArgumentException.class, () -> fixture.bot().action(grant(main, 1, List.of())));
            assertTrue(input.isPriorityActive());
            assertEquals(text(fixture.state(2).getAsJsonObject("priorityContinuation"), "status"), "Idle");
            assertEquals(text(fixture.state(2).getAsJsonObject("prompt"), "responseAvailability"), "Unknown");
        }
    }

    @Test(timeOut = 90000)
    public void scopedPreparationPublishesRealResponsesWithoutConsentOrPassing() throws Exception {
        for (boolean available : new boolean[] { false, true }) try (var fixture = new Fixture("bot-preparation-" + available, available);
                var engine = fixture.controlEngine()) {
            var main = fixture.main(); var input = fixture.actual();
            for (int read = 0; read < 3; read++) {
                fixture.bot().automatedObservation();
                assertEquals(text(fixture.state(2).getAsJsonObject("prompt"), "responseAvailability"), "Unknown",
                        "Ordinary snapshots and action discovery cannot silently request a controller scan");
            }
            fixture.acknowledge(2);
            var before = fixture.state(2); var physical = physicalFacts(before);
            var observation = before.getAsJsonObject("priorityObservation").deepCopy();
            var receipt = JSON.toJsonTree(engine.dispatch("automatedPriorityControl", preparation(fixture, main))).getAsJsonObject();
            assertEquals(receipt.keySet(), Set.of("schemaVersion", "status", "gameId", "seatId", "decisionId",
                    "boardRevision", "publicActivitySequence", "responseAvailability"));
            assertEquals(text(receipt, "status"), "prepared"); assertEquals(receipt.get("schemaVersion").getAsInt(), 1);
            assertEquals(text(receipt, "gameId"), fixture.table.id); assertEquals(receipt.get("seatId").getAsInt(), 2);
            assertEquals(text(receipt, "decisionId"), text(main.prompt(), "id"));
            assertEquals(text(receipt, "responseAvailability"), available ? "Available" : "Absent");
            var after = fixture.state(2);
            assertEquals(text(after.getAsJsonObject("prompt"), "responseAvailability"), text(receipt, "responseAvailability"));
            assertEquals(physicalFacts(after), physical, "Preparation cannot spend/refund a different pool, tap a card or move any physical card");
            assertEquals(after.get("activity"), before.get("activity")); assertEquals(after.get("stack"), before.get("stack"));
            assertEquals(after.get("executions"), before.get("executions"));
            assertEquals(after.get("priorityContinuation"), before.get("priorityContinuation"));
            assertEquals(after.get("finiteStackYield"), before.get("finiteStackYield"));
            assertEquals(after.getAsJsonObject("priorityObservation").get("acknowledgedBoardRevision"), observation.get("acknowledgedBoardRevision"));
            assertEquals(after.getAsJsonObject("priorityObservation").get("acknowledgedPublicActivitySequence"), observation.get("acknowledgedPublicActivitySequence"));
            assertEquals(text(after.getAsJsonObject("prompt"), "id"), text(main.prompt(), "id"));
            assertSame(fixture.actual(), input); assertTrue(input.isPriorityActive());
            var repeated = JSON.toJsonTree(engine.dispatch("automatedPriorityControl", preparation(fixture, fixture.input(2))));
            assertEquals(repeated, receipt, "The same completed current-input facts are idempotent, not another publication or scan");
            Files.writeString(fixture.evidence.resolve("prepared-without-consent.json"), JSON.toJson(Map.of("before", before, "prepared", receipt, "after", after)));
            if (!available) {
                try (var barrier = new Barrier(fixture.table)) {
                    fixture.bot().action(grant(fixture.input(2), 1, List.of()));
                    assertTrue(input.isPriorityActive(), "Only the later independent grant may queue a real pass");
                    barrier.release(); HeadlessPlatform.query(fixture.table, () -> null);
                }
                assertFalse(input.isPriorityActive());
                assertTrue(fixture.state(2).getAsJsonObject("priorityContinuation").get("automaticPasses").getAsInt() > 0);
                fixture.revoke(1);
                var next = fixture.until(candidate -> candidate.seat() == 2 && text(candidate.prompt(), "inputType").equals("InputPassPriority"));
                assertEquals(text(next.prompt(), "responseAvailability"), "Unknown", "One-shot preparation cannot enable future FullControl scans");
            }
        }
    }

    @Test(timeOut = 90000)
    public void incompletePreparationCannotRenewOnMetadataButRealPublicChangesAllowNewFacts() throws Exception {
        try (var fixture = new Fixture("bot-preparation-incomplete", true); var engine = fixture.controlEngine()) {
            fixture.main(); fixture.acknowledge(2);
            engine.dispatch("automatedPriorityControl", preparation(fixture, fixture.input(2)));
            HeadlessPlatform.query(fixture.table, () -> {
                // Controlled incomplete-fact fault; no physical card/rule event.
                // A repeated scan would wrongly turn this flag true again.
                fixture.actual().setAvailabilityVerified(false); fixture.table.publish(); return null;
            });
            fixture.acknowledge(2);
            long beforeRevision = fixture.state(2).get("revision").getAsLong();
            var incomplete = JSON.toJsonTree(engine.dispatch("automatedPriorityControl", preparation(fixture, fixture.input(2)))).getAsJsonObject();
            assertEquals(text(incomplete, "responseAvailability"), "Unknown");
            assertEquals(fixture.state(2).get("revision").getAsLong(), beforeRevision,
                    "A metadata-only revision cannot renew this exact input/source/activity scan attempt");
            assertEquals(fixture.actual().getResponseAvailability(), InputPassPriority.ResponseAvailability.Unknown);
            fixture.publicChanges(0, 1);
            var fresh = JSON.toJsonTree(engine.dispatch("automatedPriorityControl", preparation(fixture, fixture.input(2)))).getAsJsonObject();
            assertEquals(text(fresh, "responseAvailability"), "Available", "A genuine public/rules generation can prepare fresh current facts");
            assertTrue(fresh.get("publicActivitySequence").getAsLong() > incomplete.get("publicActivitySequence").getAsLong());
        }
    }

    @Test(timeOut = 90000)
    public void aPreparedRealFloatingManaCastKeepsItsPoolAndPhysicalResources() throws Exception {
        try (var fixture = new Fixture("bot-preparation-real-mana", false, false, true); var engine = fixture.controlEngine()) {
            var main = fixture.main();
            var ring = zone(viewer(main.state()), "Battlefield").getAsJsonArray("cards").asList().stream()
                    .map(value -> value.getAsJsonObject()).filter(card -> text(card, "name").equals("Sol Ring")).findFirst().orElseThrow();
            var actions = JSON.toJsonTree(fixture.bot().sourceActions(request(main, Map.of("key", text(ring, "key"))))).getAsJsonObject();
            var activation = actions.getAsJsonArray("actions").get(0).getAsJsonObject();
            fixture.bot().action(request(main, Map.of("action", "useSourceAction", "key", text(ring, "key"), "actionId", text(activation, "id"))));
            main = fixture.until(candidate -> candidate.seat() == 2 && text(candidate.prompt(), "inputType").equals("InputPassPriority")
                    && viewer(candidate.state()).getAsJsonObject("mana").get("C").getAsInt() == 2);
            assertEquals(text(main.prompt(), "responseAvailability"), "Unknown");
            fixture.acknowledge(2);
            var before = fixture.state(2); var receipt = JSON.toJsonTree(engine.dispatch("automatedPriorityControl", preparation(fixture, fixture.input(2)))).getAsJsonObject();
            assertEquals(text(receipt, "responseAvailability"), "Available", "Two real floating C plus the separate Forest fund a real Manalith cast");
            assertEquals(physicalFacts(fixture.state(2)), physicalFacts(before), "The funding scan must restore the exact borrowed pool without tapping the Forest");
            assertEquals(fixture.state(2).get("activity"), before.get("activity"));
            Files.writeString(fixture.evidence.resolve("prepared-real-floating-mana.json"), JSON.toJson(Map.of("before", before, "prepared", receipt, "after", fixture.state(2))));
        }
    }

    @Test(timeOut = 90000)
    public void ordinaryHumansAlreadyPublishInitialAbsentAndAvailableWithoutPreparation() throws Exception {
        for (boolean available : new boolean[] { false, true }) try (var fixture = new Fixture("human-initial-responses-" + available, false, available, false)) {
            var main = fixture.until(candidate -> candidate.seat() == 0 && text(candidate.prompt(), "inputType").equals("InputPassPriority")
                    && text(candidate.state(), "phaseKey").equals("MAIN1") && candidate.state().get("activePlayerId").getAsInt() == 0);
            assertEquals(text(main.prompt(), "responseAvailability"), available ? "Available" : "Absent");
            assertEquals(main.prompt().get("canAutoPass").getAsBoolean(), !available);
            assertEquals(main.state().getAsJsonObject("priorityContinuation").get("sequence").getAsInt(), 0);
        }
    }

    @Test(timeOut = 90000)
    public void preparationRejectsMalformedForeignStaleAndConcededInputsWithoutBorrowingAuthority() throws Exception {
        try (var fixture = new Fixture("bot-preparation-rejections", false); var engine = fixture.controlEngine()) {
            var main = fixture.main(); fixture.acknowledge(2); var before = fixture.state(2);
            for (int seat : new int[] { 0, 1, 3, 999 }) {
                var command = preparation(fixture, main); command.addProperty("seatId", seat);
                assertThrows(AutomatedPriorityControlRetired.class, () -> engine.dispatch("automatedPriorityControl", command));
            }
            for (String malformed : List.of("action", "promptId", "expectedTask")) {
                var command = preparation(fixture, main); command.addProperty(malformed, 5);
                var error = expectThrows(IllegalArgumentException.class, () -> fixture.bot().preparePriorityResponses(command));
                assertFalse(error instanceof AutomatedPriorityControlRetired);
            }
            var contradictory = preparation(fixture, main); contradictory.addProperty("grantId", "not-consent");
            assertThrows(IllegalArgumentException.class, () -> engine.dispatch("automatedPriorityControl", contradictory));
            var wrongInput = preparation(fixture, main); wrongInput.addProperty("promptId", "another-current-input");
            assertThrows(AutomatedPriorityControlRetired.class, () -> engine.dispatch("automatedPriorityControl", wrongInput));
            var wrongGame = preparation(fixture, main); wrongGame.addProperty("sessionId", "another-game");
            assertThrows(AutomatedPriorityControlRetired.class, () -> engine.dispatch("automatedPriorityControl", wrongGame));
            assertEquals(fixture.state(2), before, "Rejected preparation cannot register credit, consent, facts or another input");
            fixture.bot().action(request(main, Map.of("action", "passPriority")));
            fixture.until(candidate -> candidate.seat() == 2 && text(candidate.prompt(), "inputType").equals("InputPassPriority")
                    && !text(candidate.prompt(), "id").equals(text(main.prompt(), "id")));
            assertThrows(AutomatedPriorityControlRetired.class, () -> engine.dispatch("automatedPriorityControl", preparation(fixture, main)));
            fixture.eliminate(2);
            assertThrows(AutomatedPriorityControlRetired.class, () -> engine.dispatch("automatedPriorityControl", preparation(fixture, main)));
        }
    }

    @Test(timeOut = 90000)
    public void preparationCannotCertifyOrOverwriteAReentrantNewerRulesPublication() throws Exception {
        try (var fixture = new Fixture("bot-preparation-reentrant", false); var engine = fixture.controlEngine()) {
            var main = fixture.main(); fixture.acknowledge(2); var before = fixture.state(2); var input = fixture.actual();
            var armed = new AtomicBoolean(); var nested = new JsonObject[1];
            fixture.bot().observePlaytest(frame -> {
                if (!armed.compareAndSet(true, false)) return;
                fixture.publicChanges(0, 1);
                nested[0] = fixture.state(2).deepCopy();
            });
            armed.set(true);
            assertThrows(AutomatedPriorityControlRetired.class, () -> engine.dispatch("automatedPriorityControl", preparation(fixture, main)));
            assertNotNull(nested[0], "The actual preparation publication must encounter the one-shot nested rules callback");
            assertEquals(fixture.state(2), nested[0], "The retired outer operation cannot overwrite the newer committed cohort");
            assertEquals(fixture.state(2).get("priorityContinuation"), before.get("priorityContinuation"));
            assertEquals(fixture.state(2).get("finiteStackYield"), before.get("finiteStackYield"));
            assertSame(fixture.actual(), input); assertTrue(input.isPriorityActive());
            Files.writeString(fixture.evidence.resolve("prepared-callback-retired.json"), JSON.toJson(Map.of("before", before, "nested", nested[0], "after", fixture.state(2))));
        }
    }

    private static JsonObject preparation(Fixture fixture, Input input) {
        return JSON.toJsonTree(Map.of("sessionId", fixture.table.id, "seatId", input.seat(), "action", "preparePriorityResponses",
                "promptId", text(input.prompt(), "id"), "expectedTask", "priority")).getAsJsonObject();
    }

    private static Object physicalFacts(JsonObject state) {
        return state.getAsJsonArray("players").asList().stream().map(value -> value.getAsJsonObject()).map(player -> List.of(
                player.get("id"), player.get("life"), player.get("mana"), player.getAsJsonArray("zones").asList().stream().map(value -> value.getAsJsonObject())
                        .map(zone -> List.of(zone.get("name"), zone.get("count"), zone.getAsJsonArray("cards").asList().stream()
                                .map(value -> value.getAsJsonObject()).map(card -> List.of(card.get("name"), card.get("tapped"), card.get("counters"))).toList())).toList())).toList();
    }

    @Test(timeOut = 90000)
    public void explicitBotConsentReturnsTheCompletedScanBeforeItsFirstRealPassAndWithdrawalRetiresFutureScans() throws Exception {
        try (var fixture = new Fixture("bot-auto-positive", false)) {
            var main = fixture.main(); var input = fixture.actual(); fixture.acknowledge(2);
            try (var barrier = new Barrier(fixture.table)) {
                var receipt = JSON.toJsonTree(fixture.bot().action(grant(main, 1, List.of()))).getAsJsonObject();
                assertEquals(text(receipt.getAsJsonObject("prompt"), "responseAvailability"), "Absent",
                        "The control receipt contains the real completed controller scan, not the old Unknown frame");
                assertTrue(receipt.getAsJsonObject("prompt").get("canAutoPass").getAsBoolean());
                assertEquals(text(receipt.getAsJsonObject("priorityContinuation"), "status"), "Active");
                Files.writeString(fixture.evidence.resolve("actual-grant-completed-scan.json"), JSON.toJson(receipt));
                assertTrue(input.isPriorityActive(), "Control admission is not the queued rules pass");
                barrier.release(); HeadlessPlatform.query(fixture.table, () -> null);
            }
            assertFalse(input.isPriorityActive());
            assertTrue(fixture.state(2).getAsJsonObject("priorityContinuation").get("automaticPasses").getAsInt() > 0);
            Files.writeString(fixture.evidence.resolve("actual-certified-pass.json"), JSON.toJson(fixture.state(2)));
            assertFalse(fixture.table.game.getPlayer(2).getController().isAI(), "Forge's strategy controller never owns this bot");
            fixture.revoke(1);
            var next = fixture.until(candidate -> candidate.seat() == 2 && text(candidate.prompt(), "inputType").equals("InputPassPriority"));
            assertEquals(text(next.prompt(), "responseAvailability"), "Unknown", "Withdrawing explicit consent restores the legacy FullControl scan policy");
        }
    }

    @Test(timeOut = 90000)
    public void aRealAvailableActivationHoldsBotConsentWithoutConsumingItsInput() throws Exception {
        try (var fixture = new Fixture("bot-auto-available", true)) {
            var main = fixture.main(); var input = fixture.actual(); fixture.acknowledge(2);
            var receipt = JSON.toJsonTree(fixture.bot().action(grant(main, 1, List.of()))).getAsJsonObject();
            assertEquals(text(receipt.getAsJsonObject("prompt"), "responseAvailability"), "Available");
            assertEquals(text(receipt.getAsJsonObject("priorityContinuation"), "reasonCode"), "response-available");
            HeadlessPlatform.query(fixture.table, () -> null); assertTrue(input.isPriorityActive());
            assertEquals(receipt.getAsJsonObject("priorityContinuation").get("automaticPasses").getAsInt(), 0);
        }
    }

    @Test(timeOut = 90000)
    public void acceptedBotConsentUsesTheRealCompletedScanAgainAtItsNextPhaseStop() throws Exception {
        try (var fixture = new Fixture("bot-auto-future-scan", false)) {
            var main = fixture.main(); fixture.acknowledge(2);
            fixture.bot().action(grant(main, 1, List.of("MAIN2")));
            var next = fixture.until(candidate -> candidate.seat() == 2 && text(candidate.state(), "phaseKey").equals("MAIN2")
                    && text(candidate.prompt(), "inputType").equals("InputPassPriority"));
            assertNotEquals(text(main.prompt(), "id"), text(next.prompt(), "id"));
            assertEquals(text(next.prompt(), "responseAvailability"), "Absent");
            assertEquals(text(next.state().getAsJsonObject("priorityContinuation"), "reasonCode"), "phase-stop");
            assertTrue(fixture.actual().isPriorityActive());
        }
    }

    @Test(timeOut = 90000)
    public void anInstalledBotGrantCannotSpendOldUnusedCreditOnANewHumanSpellBeforeItIsRead() throws Exception {
        try (var fixture = new Fixture("bot-auto-new-human-cast", false)) {
            var main = fixture.main();
            fixture.table.game.getAction().withState(() -> {
                var player = fixture.table.game.getPlayer(0);
                player.getZone(ZoneType.Hand).add(forge.game.card.Card.fromPaperCard(
                        StaticData.instance().getCommonCards().getCard("Intervention Pact"), player), null, null);
                fixture.table.publish();
            });
            fixture.acknowledge(2);
            var originalObservation = fixture.state(2).getAsJsonObject("priorityObservation");
            fixture.bot().action(grant(fixture.input(2), 1, List.of()));
            var human = fixture.until(candidate -> candidate.seat() == 0 && text(candidate.prompt(), "inputType").equals("InputPassPriority"));
            var pact = zone(viewer(human.state()), "Hand").getAsJsonArray("cards").asList().stream()
                    .map(value -> value.getAsJsonObject()).filter(card -> text(card, "name").equals("Intervention Pact")).findFirst().orElseThrow();
            var actions = JSON.toJsonTree(fixture.endpoint(0).sourceActions(request(human, Map.of("key", text(pact, "key"))))).getAsJsonObject();
            var cast = actions.getAsJsonArray("actions").asList().stream().map(value -> value.getAsJsonObject())
                    .filter(action -> text(action, "kind").equals("cast")).findFirst().orElseThrow();
            fixture.endpoint(0).action(request(human, Map.of("action", "useSourceAction", "key", text(pact, "key"), "actionId", text(cast, "id"))));
            var stopped = fixture.until(candidate -> candidate.seat() == 2 && text(candidate.prompt(), "inputType").equals("InputPassPriority")
                    || text(candidate.prompt(), "executionStep").equals("resolution"));
            Files.writeString(fixture.evidence.resolve("new-cast-next-decision.json"), JSON.toJson(stopped.state()));
            assertEquals(text(stopped.prompt(), "inputType"), "InputPassPriority",
                    "The human's new spell must remain on the stack until the bot's current audience has actually observed its new read signature");
            assertEquals(stopped.seat(), 2);
            var input = fixture.actual(); var state = fixture.state(2); var observation = state.getAsJsonObject("priorityObservation");
            assertFalse(state.getAsJsonArray("stack").isEmpty(), "The new spell remains on the real stack before the host has observed or read it");
            assertTrue(observation.get("publicActivitySequence").getAsLong() - observation.get("acknowledgedPublicActivitySequence").getAsLong()
                    < PriorityObservation.MAXIMUM_LEAD, "The old grant still has ordinary event credit; the new read signature is the missing permission");
            assertEquals(text(state.getAsJsonObject("priorityContinuation"), "reasonCode"), "awaiting-observation");
            assertEquals(text(stopped.prompt(), "responseAvailability"), "Absent");
            HeadlessPlatform.query(fixture.table, () -> null); assertTrue(input.isPriorityActive());
            fixture.acknowledge(2, originalObservation);
            assertEquals(text(fixture.state(2).getAsJsonObject("priorityContinuation"), "reasonCode"), "awaiting-observation",
                    "A still-valid old cohort ACK retains its actual old read signature; it cannot certify the new cast");
            HeadlessPlatform.query(fixture.table, () -> null); assertTrue(input.isPriorityActive());
            Files.writeString(fixture.evidence.resolve("new-cast-held-before-observation.json"), JSON.toJson(state));
            try (var barrier = new Barrier(fixture.table)) {
                fixture.acknowledge(2);
                assertEquals(text(fixture.state(2).getAsJsonObject("priorityContinuation"), "status"), "Active");
                barrier.release(); HeadlessPlatform.query(fixture.table, () -> null);
            }
            assertFalse(input.isPriorityActive(), "Only a validated actual new-signature ACK permits the real bot pass");
        }
    }

    @Test(timeOut = 90000)
    public void invalidatingTheCompletedBotScanHoldsUnknownWithoutGuessingOrLaunchingAnotherScan() throws Exception {
        try (var fixture = new Fixture("bot-auto-unknown", false)) {
            var main = fixture.main(); var input = fixture.actual(); fixture.acknowledge(2);
            try (var barrier = new Barrier(fixture.table)) {
                fixture.bot().action(grant(main, 1, List.of()));
                fixture.table.game.getAction().withState(() -> input.setAvailabilityVerified(false));
                barrier.release(); HeadlessPlatform.query(fixture.table, () -> null);
            }
            assertTrue(input.isPriorityActive());
            assertEquals(text(fixture.state(2).getAsJsonObject("priorityContinuation"), "reasonCode"), "response-unknown");
            assertEquals(text(fixture.state(2).getAsJsonObject("prompt"), "responseAvailability"), "Unknown");
        }
    }

    @Test(timeOut = 90000)
    public void onlyKnownPreMutationScopeRetirementHasTheMethodScopedMetadataCode() throws Exception {
        try (var fixture = new Fixture("bot-auto-retirement", false)) {
            var main = fixture.main(); fixture.acknowledge(2);
            var malformed = grant(main, 1, List.of()); malformed.addProperty("expectedTask", "selection");
            var invalid = expectThrows(IllegalArgumentException.class, () -> fixture.bot().action(malformed));
            assertFalse(invalid instanceof AutomatedPriorityControlRetired, "Malformed metadata does not become known scope retirement");
            var retired = grant(main, 1, List.of()); retired.addProperty("promptId", "retired-input");
            var failure = expectThrows(AutomatedPriorityControlRetired.class, () -> fixture.bot().action(retired));
            assertEquals(fixture.state(2).getAsJsonObject("priorityContinuation").get("sequence").getAsInt(), 0,
                    "The retired grant failed before changing any consent high-water or controller scan");
            assertEquals(text(fixture.state(2).getAsJsonObject("prompt"), "responseAvailability"), "Unknown");
            var envelope = JSON.toJsonTree(Map.of("id", 7, "method", "automatedAction", "params", retired)).getAsJsonObject();
            assertEquals(text(DesktopEngine.errorReply(envelope, failure), "errorCode"), "automated-priority-control-retired");
            assertFalse(DesktopEngine.errorReply(envelope, new IllegalArgumentException(failure.getMessage())).has("errorCode"),
                    "Matching a generic error's text cannot manufacture a no-effect receipt");
            var malformedAck = new JsonObject(); malformedAck.addProperty("sessionId", fixture.table.id);
            malformedAck.addProperty("action", "acknowledgePriorityObservation"); malformedAck.addProperty("boardRevision", Long.MAX_VALUE);
            malformedAck.addProperty("publicActivitySequence", 0);
            var invalidAck = expectThrows(IllegalArgumentException.class, () -> fixture.bot().action(malformedAck));
            var ackEnvelope = JSON.toJsonTree(Map.of("id", 9, "method", "automatedAction", "params", malformedAck)).getAsJsonObject();
            assertFalse(DesktopEngine.errorReply(ackEnvelope, invalidAck).has("errorCode"),
                    "Ordering an unprovable acknowledgement cannot manufacture known scope retirement");
            envelope.addProperty("method", "matchAction");
            assertFalse(DesktopEngine.errorReply(envelope, failure).has("errorCode"));
            envelope.addProperty("method", "automatedAction"); envelope.getAsJsonObject("params").addProperty("action", "passPriority");
            assertFalse(DesktopEngine.errorReply(envelope, failure).has("errorCode"), "Gameplay dispatch failures are never metadata retirement");
        }
    }

    @Test(timeOut = 90000)
    public void aFailureAfterGrantAdmissionCannotBecomeAKnownNoEffectRetirement() throws Exception {
        try (var fixture = new Fixture("bot-auto-after-admission", false)) {
            var main = fixture.main(); fixture.acknowledge(2);
            var armed = new AtomicBoolean();
            fixture.bot().observePlaytest(frame -> { if (armed.get()) throw new IllegalStateException("post-admission-publication-failure"); });
            armed.set(true);
            var command = grant(main, 1, List.of());
            try {
                var failure = expectThrows(IllegalStateException.class, () -> fixture.bot().action(command));
                assertEquals(failure.getMessage(), "post-admission-publication-failure");
                assertEquals(fixture.state(2).getAsJsonObject("priorityContinuation").get("sequence").getAsInt(), 1,
                        "The failure occurred after the positive consent was admitted");
                var envelope = JSON.toJsonTree(Map.of("id", 8, "method", "automatedAction", "params", command)).getAsJsonObject();
                assertFalse(DesktopEngine.errorReply(envelope, failure).has("errorCode"),
                        "A scan/publication failure after begin remains uncertain and requires a negative fence");
            } finally { armed.set(false); fixture.revoke(1); }
        }
    }

    @DataProvider public Object[][] observers() { return new Object[][] {{false}, {true}}; }

    @Test(dataProvider = "observers", timeOut = 90000)
    public void realBotAutoWaitsForItsOwnCreditAndRegisteredHumanReadingWithoutBreakingLegacyHumans(boolean humanReading) throws Exception {
        try (var fixture = new Fixture("bot-auto-credit-" + humanReading, false)) {
            var main = fixture.main(); var input = fixture.actual(); fixture.acknowledge(2);
            if (humanReading) fixture.acknowledge(0);
            try (var barrier = new Barrier(fixture.table)) {
                fixture.bot().action(grant(main, 1, List.of("MAIN1")));
                fixture.publicChanges(PriorityObservation.MAXIMUM_LEAD);
                var current = fixture.input(2);
                fixture.bot().action(grant(current, 2, List.of()));
                var held = fixture.state(2).getAsJsonObject("priorityContinuation");
                assertEquals(text(held, "status"), "Held"); assertEquals(text(held, "reasonCode"), "awaiting-observation");
                fixture.acknowledge(2);
                if (humanReading) {
                    assertEquals(text(fixture.state(2).getAsJsonObject("priorityContinuation"), "reasonCode"), "awaiting-observation",
                            "A bot cannot return reading credit for the registered human");
                    assertTrue(input.isPriorityActive()); fixture.acknowledge(0);
                }
                assertFalse(fixture.state(1).getAsJsonObject("priorityObservation").get("registered").getAsBoolean(),
                        "The other human deliberately retains legacy ordinary publication behavior");
                barrier.release(); HeadlessPlatform.query(fixture.table, () -> null);
            }
            assertFalse(input.isPriorityActive());
            assertTrue(fixture.state(2).getAsJsonObject("priorityContinuation").get("automaticPasses").getAsInt() > 0);
        }
    }

    @DataProvider public Object[][] journals() { return new Object[][] {{false}, {true}}; }

    @Test(dataProvider = "journals", timeOut = 90000)
    public void overflowingTheBotsOwnDeliveryJournalCannotBeClearedByAnAcknowledgementOrPass(boolean executions) throws Exception {
        try (var fixture = new Fixture("bot-auto-overflow-" + executions, false)) {
            var main = fixture.main(); var input = fixture.actual(); fixture.acknowledge(2);
            try (var barrier = new Barrier(fixture.table)) {
                fixture.bot().action(grant(main, 1, List.of("MAIN1")));
                if (executions) fixture.publicExecutions(MatchActivity.MAXIMUM_FINITE_ENTRIES / 2 + 1);
                else fixture.publicChanges(MatchActivity.MAXIMUM_FINITE_ENTRIES + 1);
                var state = fixture.state(2);
                assertTrue(state.getAsJsonObject("priorityObservation").get("overflowed").getAsBoolean());
                assertEquals(text(state.getAsJsonObject("priorityContinuation"), "reasonCode"), "observation-overflow");
                assertThrows(IllegalArgumentException.class, () -> fixture.acknowledge(2));
                barrier.release(); HeadlessPlatform.query(fixture.table, () -> null);
            }
            assertTrue(input.isPriorityActive());
            assertEquals(fixture.state(2).getAsJsonObject("priorityContinuation").get("automaticPasses").getAsInt(), 0);
        }
    }

    @DataProvider public Object[][] registrationSuffixes() {
        return new Object[][] {{40, false, false}, {150, true, false}, {40, false, true}, {300, true, true}};
    }

    @Test(dataProvider = "registrationSuffixes", timeOut = 90000)
    public void acknowledgingAnOlderCohortRetainsItsActualUnreadSuffixOrDeclaresTheMissingGap(int count, boolean missing, boolean executions) throws Exception {
        try (var fixture = new Fixture("bot-auto-late-registration-" + count + "-" + executions, false)) {
            var main = fixture.main(); var input = fixture.actual();
            var originalState = fixture.state(2); var original = originalState.getAsJsonObject("priorityObservation");
            if (executions) fixture.publicExecutions(count); else fixture.publicChanges(count);
            fixture.acknowledge(2, original);
            var state = fixture.state(2); var observed = state.getAsJsonObject("priorityObservation");
            assertEquals(observed.get("acknowledgedPublicActivitySequence"), original.get("publicActivitySequence"),
                    "Registering cannot acknowledge the unread suffix after the supplied actual cohort");
            assertEquals(observed.get("overflowed").getAsBoolean(), missing,
                    "A missing retained suffix is explicit; it is never invented or silently treated as read");
            try (var barrier = new Barrier(fixture.table)) {
                if (missing) assertThrows(IllegalArgumentException.class, () -> fixture.bot().action(grant(fixture.input(2), 1, List.of())));
                else {
                    fixture.bot().action(grant(fixture.input(2), 1, List.of("MAIN1")));
                    if (executions) fixture.publicExecutions(300); else fixture.publicChanges(130);
                    var retained = fixture.state(2);
                    var entries = executions ? retained.getAsJsonObject("executions").getAsJsonArray("transitions") : retained.getAsJsonArray("activity");
                    long firstUnread = (executions ? originalState.getAsJsonObject("executions").get("lastSequence") : original.get("publicActivitySequence")).getAsLong() + 1;
                    assertTrue(entries.asList().stream().anyMatch(entry -> entry.getAsJsonObject().get(executions ? "sequence" : "id").getAsLong() == firstUnread),
                            "The first unread fact survives after another burst evicts it from the ordinary rolling tail");
                }
            }
            assertTrue(input.isPriorityActive());
        }
    }

    @DataProvider public Object[][] departedReaders() { return new Object[][] {{0}, {2}}; }

    @Test(dataProvider = "departedReaders", timeOut = 90000)
    public void anEliminatedConnectedReaderCanConsumeDeliveryWithoutRegainingActionAuthority(int reader) throws Exception {
        try (var fixture = new Fixture("departed-delivery-reader-" + reader, false)) {
            var main = fixture.main(); fixture.acknowledge(reader);
            var original = fixture.state(reader).getAsJsonObject("priorityObservation");
            if (reader == 2) fixture.bot().action(grant(main, 1, List.of("MAIN1")));
            fixture.eliminate(reader);
            fixture.publicChanges(3, 160); fixture.publicExecutions(3, 320);
            var retained = fixture.state(reader);
            int activity = retained.getAsJsonArray("activity").size();
            int transitions = retained.getAsJsonObject("executions").getAsJsonArray("transitions").size();
            assertTrue(activity > 120, "The connected departed reader retains its unread activity beyond the rolling tail");
            assertTrue(transitions > 512, "The same reader retains unread execution transitions beyond the rules tail");
            assertFalse(retained.getAsJsonObject("priorityObservation").get("overflowed").getAsBoolean());

            fixture.acknowledge(reader, original);
            fixture.table.game.getAction().withState(fixture.table::publish);
            var unread = fixture.state(reader);
            assertEquals(unread.getAsJsonObject("priorityObservation").get("acknowledgedPublicActivitySequence"),
                    original.get("publicActivitySequence"), "A valid old cohort cannot acknowledge the later unread suffix");
            assertEquals(unread.getAsJsonArray("activity").size(), activity);
            assertEquals(unread.getAsJsonObject("executions").getAsJsonArray("transitions").size(), transitions);

            var impossible = unread.getAsJsonObject("priorityObservation").deepCopy();
            impossible.addProperty("boardRevision", Long.MAX_VALUE);
            var invalid = expectThrows(IllegalArgumentException.class, () -> fixture.acknowledge(reader, impossible));
            assertFalse(invalid instanceof AutomatedPriorityControlRetired, "Loss cannot turn an unprovable prefix into known retirement");
            var wrongGame = new JsonObject(); wrongGame.addProperty("sessionId", "retired-table");
            wrongGame.addProperty("action", "acknowledgePriorityObservation");
            wrongGame.add("boardRevision", original.get("boardRevision"));
            wrongGame.add("publicActivitySequence", original.get("publicActivitySequence"));
            assertThrows(IllegalArgumentException.class, () -> fixture.endpoint(reader).action(wrongGame));

            fixture.acknowledge(reader, unread.getAsJsonObject("priorityObservation"));
            fixture.table.game.getAction().withState(fixture.table::publish);
            var compacted = fixture.state(reader);
            assertTrue(compacted.getAsJsonArray("activity").size() <= 120);
            assertTrue(compacted.getAsJsonObject("executions").getAsJsonArray("transitions").size() <= 512);
            assertEquals(compacted.getAsJsonObject("priorityObservation").get("acknowledgedPublicActivitySequence"),
                    unread.getAsJsonObject("priorityObservation").get("publicActivitySequence"));
            assertTrue(viewer(compacted).get("eliminated").getAsBoolean());
            assertEquals(text(compacted, "status"), "playing");
            assertTrue(compacted.get("prompt").isJsonNull(), "Consumption cannot give a departed reader another decision");
            for (var player : compacted.getAsJsonArray("players")) {
                var seat = player.getAsJsonObject();
                if (!seat.get("id").equals(compacted.get("viewerId")))
                    assertTrue(zone(seat, "Hand").getAsJsonArray("cards").isEmpty(), "Delivery never reveals another reader's hand");
            }
            assertThrows(IllegalArgumentException.class, () -> fixture.endpoint(reader).action(request(main, Map.of("action", "passPriority"))));
            assertThrows(IllegalArgumentException.class, () -> fixture.endpoint(reader).action(grant(main, 2, List.of())));
            var consent = compacted.getAsJsonObject("priorityContinuation");
            assertFalse(List.of("Active", "Held").contains(text(consent, "status")));
            assertEquals(consent.get("automaticPasses").getAsInt(), 0);
            var survived = fixture.until(candidate -> candidate.seat() != reader && text(candidate.prompt(), "inputType").equals("InputPassPriority"));
            assertEquals(text(survived.state(), "status"), "playing", "The surviving controllers keep their actual rules decisions");
            Files.writeString(fixture.evidence.resolve("departed-reader-delivery.json"), JSON.toJson(Map.of(
                    "reader", reader, "retainedActivity", activity, "retainedTransitions", transitions,
                    "compactedActivity", compacted.getAsJsonArray("activity").size(),
                    "compactedTransitions", compacted.getAsJsonObject("executions").getAsJsonArray("transitions").size(),
                    "observation", compacted.getAsJsonObject("priorityObservation"), "consent", consent)));
            if (reader == 2) {
                fixture.table.close();
                assertThrows(AutomatedPriorityControlRetired.class, () -> fixture.acknowledge(reader));
            }
        }
    }

    @Test(timeOut = 90000)
    public void compactControlsRetainTheExactReturnedCohortWithoutPublishingPrivateGraphs() throws Exception {
        try (var fixture = new Fixture("bot-compact-controls", false); var engine = fixture.controlEngine()) {
            var main = fixture.main(); fixture.acknowledge(0); fixture.acknowledge(1);
            var ack = acknowledgement(fixture, 2);
            var result = engine.dispatch("automatedPriorityControl", ack);
            assertTrue(result instanceof AutomatedPriorityMetadata);
            var receipt = JSON.toJsonTree(result).getAsJsonObject();
            assertEquals(receipt.keySet(), Set.of("schemaVersion", "gameId", "seatId", "revision", "boardRevision",
                    "stackInterruptionVersion", "turn", "status", "playerActive", "decisionOwnerId", "priorityPlayerId",
                    "decisionId", "priorityDecisionId", "continuation", "observation"));
            assertEquals(receipt.get("schemaVersion").getAsInt(), 1);
            assertEquals(text(receipt, "gameId"), fixture.table.id);
            assertEquals(receipt.get("seatId").getAsInt(), 2);
            assertTrue(receipt.get("playerActive").getAsBoolean());
            assertEquals(text(receipt, "priorityDecisionId"), text(main.prompt(), "id"));
            assertEquals(receipt.get("decisionOwnerId").getAsInt(), 2);
            assertEquals(receipt.get("priorityPlayerId").getAsInt(), 2);
            assertTrue(receipt.getAsJsonObject("observation").get("registered").getAsBoolean());
            var returned = fixture.bot().state();
            assertEquals(receipt, JSON.toJsonTree(AutomatedPriorityMetadata.from(returned)),
                    "The metadata describes the exact action-returned committed publication");
            for (String changed : List.of("decisionOwnerId", "priorityPlayerId", "turn", "status", "players", "prompt")) {
                var altered = new HashMap<>(returned);
                altered.put(changed, switch (changed) {
                    case "turn" -> 0;
                    case "status" -> "finished";
                    case "players" -> List.of();
                    case "prompt" -> Map.of("id", "required-choice", "kind", "choice", "task", "choice", "okEnabled", true);
                    default -> null;
                });
                assertNull(AutomatedPriorityMetadata.from(altered).priorityDecisionId(),
                        "Incomplete ownership, liveness or typed priority facts cannot certify a continuation input");
            }
            var wrongOwner = new HashMap<>(returned); wrongOwner.put("decisionOwnerId", 3);
            assertNull(AutomatedPriorityMetadata.from(wrongOwner).priorityDecisionId());
            var duplicateViewer = new HashMap<>(returned);
            duplicateViewer.put("players", List.of(Map.of("id", 2, "eliminated", false), Map.of("id", 2, "eliminated", false)));
            var duplicateMetadata = AutomatedPriorityMetadata.from(duplicateViewer);
            assertFalse(duplicateMetadata.playerActive(), "An ambiguous audience cannot certify an active viewer");
            assertNull(duplicateMetadata.priorityDecisionId());
            var disabled = new HashMap<>((Map<String, Object>)returned.get("prompt")); disabled.put("okEnabled", false);
            var unavailable = new HashMap<>(returned); unavailable.put("prompt", disabled);
            assertNull(AutomatedPriorityMetadata.from(unavailable).priorityDecisionId());
            fixture.publicChanges(1); fixture.table.publish();
            assertTrue(((Number)fixture.bot().state().get("boardRevision")).longValue() > receipt.get("boardRevision").getAsLong());
            assertEquals(JSON.toJsonTree(result), receipt, "A later publication cannot rewrite an earlier compact receipt");
            fixture.acknowledge(0); fixture.acknowledge(1); fixture.acknowledge(2);
            main = fixture.input(2);
            var grant = grant(main, 1, List.of("MAIN1")); grant.addProperty("seatId", 2);
            var consent = JSON.toJsonTree(engine.dispatch("automatedPriorityControl", grant)).getAsJsonObject();
            assertEquals(text(consent.getAsJsonObject("continuation"), "mode"), "NoResponse");
            assertEquals(consent.getAsJsonObject("continuation").get("sequence").getAsInt(), 1);
            var legacy = engine.dispatch("automatedAction", grant);
            assertTrue(legacy instanceof Map<?, ?> && ((Map<?, ?>)legacy).containsKey("players"), "The legacy endpoint still returns its full snapshot");
            assertEquals(JSON.toJsonTree(((Map<?, ?>)legacy).get("priorityContinuation")), consent.get("continuation"));
            var withdraw = JSON.toJsonTree(Map.of("sessionId", fixture.table.id, "seatId", 2,
                    "action", "revokePriorityContinuation", "throughSequence", 1)).getAsJsonObject();
            var revoked = JSON.toJsonTree(engine.dispatch("automatedPriorityControl", withdraw)).getAsJsonObject();
            assertEquals(text(revoked.getAsJsonObject("continuation"), "status"), "Revoked");
            Files.writeString(fixture.evidence.resolve("compact-control-receipts.json"), JSON.toJson(List.of(receipt, consent, revoked)));
        }
    }

    @Test(timeOut = 90000)
    public void compactControlRoutingRejectsGameplayForeignSeatsAndUnsupportedPoliciesBeforeAdmission() throws Exception {
        try (var fixture = new Fixture("bot-compact-rejections", false); var engine = fixture.controlEngine()) {
            var main = fixture.main();
            fixture.acknowledge(3);
            var beforeObservation = fixture.state(2).getAsJsonObject("priorityObservation").deepCopy();
            for (String action : List.of("passPriority", "card", "grantFiniteStackYield", "revokeFiniteStackYield")) {
                var rejected = JSON.toJsonTree(Map.of("sessionId", fixture.table.id, "seatId", 2, "action", action)).getAsJsonObject();
                assertThrows(IllegalArgumentException.class, () -> engine.dispatch("automatedPriorityControl", rejected));
            }
            var quiet = grant(main, 1, List.of()); quiet.addProperty("seatId", 2); quiet.addProperty("mode", "Quiet");
            assertThrows(IllegalArgumentException.class, () -> engine.dispatch("automatedPriorityControl", quiet));
            var malformed = grant(main, 1, List.of()); malformed.addProperty("seatId", 2); malformed.remove("phaseStops");
            var invalid = expectThrows(IllegalArgumentException.class, () -> engine.dispatch("automatedPriorityControl", malformed));
            assertFalse(invalid instanceof AutomatedPriorityControlRetired);
            for (int seat : new int[] {0, 3, 999}) {
                var foreign = grant(main, 1, List.of()); foreign.addProperty("seatId", seat);
                var failure = expectThrows(AutomatedPriorityControlRetired.class, () -> engine.dispatch("automatedPriorityControl", foreign));
                var envelope = JSON.toJsonTree(Map.of("id", 10, "method", "automatedPriorityControl", "params", foreign)).getAsJsonObject();
                assertEquals(text(DesktopEngine.errorReply(envelope, failure), "errorCode"), AutomatedPriorityControlRetired.CODE);
            }
            var wrongGame = acknowledgement(fixture, 2); wrongGame.addProperty("sessionId", "foreign-game");
            assertThrows(AutomatedPriorityControlRetired.class, () -> engine.dispatch("automatedPriorityControl", wrongGame));
            var seatRoute = acknowledgement(fixture, 2); seatRoute.addProperty("connectionId", "unowned-local-seat");
            assertThrows(IllegalArgumentException.class, () -> engine.dispatch("automatedPriorityControl", seatRoute));
            var malformedAck = acknowledgement(fixture, 2); malformedAck.addProperty("boardRevision", Long.MAX_VALUE);
            var unproved = expectThrows(IllegalArgumentException.class, () -> engine.dispatch("automatedPriorityControl", malformedAck));
            var ackEnvelope = JSON.toJsonTree(Map.of("id", 11, "method", "automatedPriorityControl", "params", malformedAck)).getAsJsonObject();
            assertFalse(DesktopEngine.errorReply(ackEnvelope, unproved).has("errorCode"));
            assertEquals(text(fixture.state(2).getAsJsonObject("prompt"), "id"), text(main.prompt(), "id"));
            assertEquals(fixture.state(2).getAsJsonObject("priorityContinuation").get("sequence").getAsInt(), 0);
            assertEquals(fixture.state(3).getAsJsonObject("priorityContinuation").get("sequence").getAsInt(), 0);
            assertEquals(fixture.state(2).getAsJsonObject("priorityObservation"), beforeObservation,
                    "Rejected commands cannot register or advance this audience's delivery prefix");
        }
    }

    @Test(timeOut = 90000)
    public void compactPostAdmissionPublicationFailuresRemainUncertainAndRequireANegativeFence() throws Exception {
        try (var fixture = new Fixture("bot-compact-uncertain", false); var engine = fixture.controlEngine()) {
            var main = fixture.main(); fixture.acknowledge(2);
            var armed = new AtomicBoolean();
            fixture.bot().observePlaytest(frame -> { if (armed.get()) throw new IllegalStateException("compact-after-admission"); });
            var command = grant(main, 1, List.of()); command.addProperty("seatId", 2);
            armed.set(true);
            try {
                var failure = expectThrows(IllegalStateException.class, () -> engine.dispatch("automatedPriorityControl", command));
                assertEquals(failure.getMessage(), "compact-after-admission");
                assertEquals(fixture.state(2).getAsJsonObject("priorityContinuation").get("sequence").getAsInt(), 1);
                var envelope = JSON.toJsonTree(Map.of("id", 12, "method", "automatedPriorityControl", "params", command)).getAsJsonObject();
                assertFalse(DesktopEngine.errorReply(envelope, failure).has("errorCode"));
                envelope.getAsJsonObject("params").addProperty("action", "passPriority");
                assertFalse(DesktopEngine.errorReply(envelope, new AutomatedPriorityControlRetired()).has("errorCode"));
            } finally { armed.set(false); fixture.revoke(1); }
        }
    }

    private static JsonObject acknowledgement(Fixture fixture, int seat) {
        var frame = fixture.state(seat).getAsJsonObject("priorityObservation");
        return JSON.toJsonTree(Map.of("sessionId", fixture.table.id, "seatId", seat, "action", "acknowledgePriorityObservation",
                "boardRevision", frame.get("boardRevision"), "publicActivitySequence", frame.get("publicActivitySequence"))).getAsJsonObject();
    }

    private static JsonObject grant(Input input, int sequence, List<String> stops) {
        return request(input, Map.of("action", "grantPriorityContinuation", "sequence", sequence, "grantId", "bot-auto-" + sequence,
                "mode", "NoResponse", "phaseStops", stops, "quietYieldToken", ""));
    }

    private static final class Fixture implements AutoCloseable {
        final MatchTable table;
        final Path evidence;
        private final Map<Integer, String> answered = new HashMap<>();
        Fixture(String name, boolean available) throws Exception {
            this(name, available, false, false);
        }
        Fixture(String name, boolean available, boolean humanAvailable, boolean floatingMana) throws Exception {
            if (StaticData.instance() == null) EngineResources.load(RESOURCES);
            var players = java.util.stream.IntStream.range(0, 4).mapToObj(seat -> {
                var deck = new Deck("Bot continuation seat " + seat);
                deck.getMain().add(StaticData.instance().getCommonCards().getCard("Manalith"), 99);
                deck.getOrCreate(DeckSection.Commander).add(StaticData.instance().getCommonCards().getCard("Progenitus"));
                var player = seat < 2 ? MatchTable.human(deck, "Commander", "Human " + seat) : MatchTable.automated(deck, "Commander", "Policy " + seat);
                player.addExtraCardsOnBattlefield(List.of(StaticData.instance().getCommonCards().getCard("Spellbook"),
                        StaticData.instance().getCommonCards().getCard(available && seat == 2 || humanAvailable && seat == 0 ? "Arid Mesa" : "Forest")));
                if (floatingMana && seat == 2) player.addExtraCardsOnBattlefield(List.of(StaticData.instance().getCommonCards().getCard("Sol Ring")));
                return player;
            }).toList();
            evidence = ApiTestWorkspace.evidence(name);
            table = new MatchTable("Commander", players, RESOURCES, evidence); table.start();
        }
        MatchSession endpoint(int seat) { return seat < 2 ? table.seat(seat) : table.automatedSeat(seat); }
        DesktopEngine controlEngine() throws Exception {
            var engine = new DesktopEngine(new CardCatalog(List.of()), evidence.resolve("desktop-decks"));
            engine.dispatch("automatedEnable", new JsonObject());
            var owner = DesktopEngine.class.getDeclaredField("simulation"); owner.setAccessible(true); owner.set(engine, table);
            return engine;
        }
        MatchSession bot() { return endpoint(2); }
        JsonObject state(int seat) { return JSON.toJsonTree(endpoint(seat).state()).getAsJsonObject(); }
        Input input(int seat) { var state = state(seat); return new Input(seat, state, state.getAsJsonObject("prompt")); }
        Input main() throws Exception { return until(input -> input.seat() == 2 && text(input.prompt(), "inputType").equals("InputPassPriority")
                && text(input.state(), "phaseKey").equals("MAIN1") && input.state().get("activePlayerId").getAsInt() == 2); }
        InputPassPriority actual() { return HeadlessPlatform.query(table, () -> (InputPassPriority)((PlayerControllerHuman)
                table.game.getPlayer(2).getController()).getInputQueue().getInput()); }
        void acknowledge(int seat) {
            acknowledge(seat, state(seat).getAsJsonObject("priorityObservation"));
        }
        void acknowledge(int seat, JsonObject observation) {
            var command = new JsonObject(); command.addProperty("sessionId", table.id); command.addProperty("action", "acknowledgePriorityObservation");
            command.add("boardRevision", observation.get("boardRevision")); command.add("publicActivitySequence", observation.get("publicActivitySequence"));
            endpoint(seat).action(command);
        }
        void revoke(int sequence) {
            var command = new JsonObject(); command.addProperty("sessionId", table.id); command.addProperty("action", "revokePriorityContinuation");
            command.addProperty("throughSequence", sequence); bot().action(command);
        }
        void eliminate(int seat) throws Exception {
            var command = new JsonObject(); command.addProperty("sessionId", table.id);
            endpoint(seat).concede(command);
            long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(10);
            while (System.nanoTime() < deadline) {
                var state = state(seat);
                if (viewer(state).get("eliminated").getAsBoolean() && state.get("prompt").isJsonNull()) return;
                Thread.sleep(2);
            }
            throw new AssertionError("The actual concession did not publish its reader's loss");
        }
        void publicChanges(int count) {
            publicChanges(2, count);
        }
        void publicChanges(int seat, int count) {
            table.game.getAction().withState(() -> {
                var forest = table.game.getPlayer(seat).getCardsIn(ZoneType.Battlefield).stream()
                        .filter(card -> card.getName().equals("Forest")).findFirst().orElseThrow();
                for (int index = 0; index < count; index++) {
                    boolean tapped = index % 2 == 0; forest.setTapped(tapped); table.game.fireEvent(new GameEventCardTapped(forest, tapped));
                }
                table.publish();
            });
        }
        void publicExecutions(int count) {
            publicExecutions(2, count);
        }
        void publicExecutions(int seat, int count) {
            table.game.getAction().withState(() -> {
                var player = table.game.getPlayer(seat);
                var source = player.getCardsIn(ZoneType.Battlefield).stream().filter(card -> card.getName().equals("Forest")).findFirst().orElseThrow();
                var rules = table.game.getExecutions();
                for (int index = 0; index < count; index++) {
                    var ability = new forge.game.spellability.SpellAbility.EmptySa(source, player);
                    rules.prepare(ability); rules.complete(ability, forge.game.ExecutionTracker.Outcome.RESOLVED);
                }
                table.publish();
            });
        }
        Input until(Predicate<Input> stop) throws Exception {
            long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(45);
            while (System.nanoTime() < deadline) {
                for (int seat = 0; seat < 4; seat++) {
                    var state = state(seat); assertNotEquals(text(state, "status"), "error");
                    if (!state.has("prompt") || !state.get("prompt").isJsonObject()) continue;
                    var prompt = state.getAsJsonObject("prompt"); var input = new Input(seat, state, prompt);
                    if (stop.test(input)) return input;
                    String id = text(prompt, "id"); if (id.equals(answered.get(seat))) continue;
                    String type = text(prompt, "inputType"); Map<String, ?> answer;
                    if (state.get("turn").getAsInt() == 0 && prompt.has("playerChoices") && !prompt.getAsJsonArray("playerChoices").isEmpty())
                        answer = Map.of("action", "player", "playerId", 0);
                    else if (type.equals("InputPassPriority")) answer = Map.of("action", "passPriority");
                    else if (type.equals("InputConfirm") || type.contains("Mulligan") || type.equals("InputAttack") || type.equals("InputBlock")
                            || type.equals("InputPayMana") && prompt.get("okEnabled").getAsBoolean()) answer = Map.of("action", "ok");
                    else throw new AssertionError("Unhandled bot continuation fixture choice: " + prompt);
                    try { endpoint(seat).action(request(input, answer)); answered.put(seat, id); }
                    catch (IllegalArgumentException error) {
                        if (!"That choice has changed. Use the current prompt.".equals(error.getMessage())) throw error;
                    }
                }
                Thread.sleep(2);
            }
            throw new AssertionError("The two-human/two-bot fixture did not reach its actual current priority input");
        }
        @Override public void close() { table.close(); }
    }

    private static final class Barrier implements AutoCloseable {
        private final CountDownLatch release = new CountDownLatch(1);
        Barrier(MatchTable table) throws InterruptedException {
            var entered = new CountDownLatch(1);
            HeadlessPlatform.later(() -> {
                entered.countDown();
                try (var suspended = table.game.getAction().suspendState()) {
                    if (!release.await(15, TimeUnit.SECONDS)) throw new AssertionError("Bot continuation input barrier expired");
                } catch (InterruptedException failure) { Thread.currentThread().interrupt(); throw new AssertionError(failure); }
            });
            assertTrue(entered.await(5, TimeUnit.SECONDS));
        }
        void release() { release.countDown(); }
        @Override public void close() { release(); }
    }
}
