package forge.api;

import com.google.common.eventbus.Subscribe;
import com.google.gson.GsonBuilder;
import forge.game.ability.AbilityFactory;
import forge.game.ability.AbilityKey;
import forge.game.card.Card;
import forge.game.card.CardCopyService;
import forge.game.event.GameEventCardStatsChanged;
import forge.game.spellability.SpellAbility;
import forge.game.zone.ZoneType;
import forge.gamemodes.match.input.InputPassPriority;
import forge.player.PlayerControllerHuman;
import org.testng.annotations.DataProvider;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.atomic.AtomicLong;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** LKI eligibility may inspect another face; it does not mutate the live game. */
public class MorphSourceQueryPurityTest {
    @DataProvider
    public Object[][] reads() { return new Object[][] {{false}, {true}}; }

    @Test(dataProvider = "reads", timeOut = 90000)
    public void anOwnedMorphReadCannotPublishCopyStatsOrRevokeItsCurrentInput(boolean directLki) throws Exception {
        inspect(directLki ? "lki-producer" : "actual-source-query", directLki, false);
    }

    @Test(timeOut = 90000)
    public void anActualLiveFaceChangeStillPublishesStatsAndRevokesTheCurrentInput() throws Exception {
        inspect("live-producer-control", false, true);
    }

    private static void inspect(String name, boolean directLki, boolean live) throws Exception {
        try (var scenario = new RulesScenario("morph-query-purity-" + name,
                Map.of(0, List.of("Island", "Island")), "Willbender", "Rhys the Redeemed")) {
            scenario.until(next -> next.seat() == 0 && text(next.prompt(), "inputType").equals("InputPassPriority")
                    && text(next.state(), "phaseKey").equals("MAIN1"));
            var actor = scenario.table.game.getRegisteredPlayers().get(0);
            HeadlessPlatform.query(scenario.table, () -> {
                // Controlled setup uses the real operation used by Reality
                // Shift, not an invented ability, input or eligibility model.
                var cause = AbilityFactory.getAbility("DB$ Manifest | DefinedPlayer$ You", actor.getCommanders().get(0));
                cause.setActivatingPlayer(actor);
                var source = actor.getCardsIn(ZoneType.Library).get(0).manifest(actor, cause, AbilityKey.newMap());
                assertTrue(source.isManifested());
                assertTrue(source.isFaceDown());
                scenario.table.publish();
                return null;
            });
            HeadlessPlatform.query(scenario.table, () -> {
                var controller = (PlayerControllerHuman)actor.getController();
                var input = controller.getInputQueue().getInput();
                assertTrue(input instanceof InputPassPriority);
                assertSame(controller.getInputProxy().getInput(), input);
                assertTrue(((InputPassPriority)input).isPriorityActive());
                var source = actor.getCardsIn(ZoneType.Battlefield).stream().filter(Card::isManifested).findFirst().orElseThrow();
                assertFalse(source.isLKI());
                var versionField = MatchSession.class.getDeclaredField("sourceActionVersion");
                versionField.setAccessible(true);
                var version = (AtomicLong)versionField.get(scenario.session(0));
                long before = version.get();
                var trace = new Stats(source);
                var copy = directLki ? CardCopyService.getLKICopy(source) : null;
                if (copy != null) {
                    assertTrue(copy.isLKI()); assertNotSame(copy.getView(), source.getView());
                }
                List<SpellAbility> offers = List.of();
                scenario.table.game.subscribeToEvents(trace);
                try {
                    if (live) source.forceTurnFaceUp();
                    else if (copy != null) copy.forceTurnFaceUp();
                    else offers = input.getSourceActions(source);
                    var proof = new LinkedHashMap<String, Object>(Map.of("kind", name, "beforeSourceVersion", before, "afterSourceVersion", version.get(),
                            "sameActualInput", controller.getInputQueue().getInput() == input && controller.getInputProxy().getInput() == input,
                            "physicalFaceDown", source.isFaceDown(), "physicalPower", source.getNetPower(),
                            "physicalToughness", source.getNetToughness(), "copyWasLki", copy != null && copy.isLKI(),
                            "offeredRoutes", offers.stream().map(ability -> Map.of("morph", ability.isMorphUp(),
                                    "manifest", ability.isManifestUp(), "queryCopy", ability.isQueryCopy())).toList(),
                            "statsEvents", trace.events));
                    boolean publicName = new GsonBuilder().create().toJson(SpectatorObservation.project(scenario.table, 1, null)).contains("Willbender");
                    proof.put("publicProjectionContainsPrintedName", publicName);
                    Files.writeString(scenario.evidence.resolve("source-query-event-proof.json"),
                            new GsonBuilder().setPrettyPrinting().create().toJson(proof));
                    assertSame(controller.getInputQueue().getInput(), input);
                    assertSame(controller.getInputProxy().getInput(), input);
                    if (live) {
                        assertFalse(source.isFaceDown());
                        assertTrue(trace.events.stream().anyMatch(event -> Boolean.TRUE.equals(event.get("actualPhysicalView"))),
                                "An actual live face change still publishes its real physical view");
                        assertTrue(version.get() > before, "A real live mutation inside the same state owner still revokes source authority");
                    } else {
                        assertTrue(source.isFaceDown());
                        assertEquals(source.getNetPower(), 2);
                        assertEquals(source.getNetToughness(), 2);
                        assertFalse(publicName, "An eligibility read cannot reveal the physical creature's printed face in public projection");
                        if (!directLki) {
                            assertTrue(offers.stream().anyMatch(SpellAbility::isMorphUp), "The actual printed morph route remains eligible");
                            assertTrue(offers.stream().allMatch(SpellAbility::isQueryCopy));
                        }
                        assertTrue(trace.events.isEmpty(), "Detached eligibility must not announce copied characteristics as a live game mutation: " + trace.events);
                        assertEquals(version.get(), before, "A read cannot revoke its own exact current source input");
                        if (!directLki) {
                            var owned = new GsonBuilder().create().toJsonTree(scenario.session(0).decisionObservation(true)).getAsJsonObject();
                            assertEquals(text(owned.getAsJsonObject("domain"), "coverage"), "Complete",
                                    "The actual fenced owned observation must finish with its complete morph domain");
                            assertEquals(owned.getAsJsonObject("state").get("viewerId").getAsInt(), actor.getId());
                            assertEquals(version.get(), before, "Rich eligibility and descriptions keep the same current authority too");
                            assertTrue(trace.events.isEmpty());
                            Files.writeString(scenario.evidence.resolve("complete-owned-morph-observation.json"),
                                    new GsonBuilder().setPrettyPrinting().create().toJson(owned));
                        }
                    }
                } finally { scenario.table.game.unsubscribeFromEvents(trace); }
                return null;
            });
        }
    }

    public static final class Stats {
        private final Card physical;
        final List<Map<String, Object>> events = new ArrayList<>();
        Stats(Card physical) { this.physical = physical; }
        @Subscribe
        public void changed(GameEventCardStatsChanged event) {
            for (var view : event.cards()) events.add(Map.of("actualPhysicalView", view == physical.getView(),
                    "viewIdentity", System.identityHashCode(view), "physicalViewIdentity", System.identityHashCode(physical.getView()),
                    "sameEngineId", view.getId() == physical.getId(), "reportedFaceDown", view.isFaceDown(),
                    "reportedName", view.getCurrentState().getName(), "producer", Arrays.stream(Thread.currentThread().getStackTrace())
                            .filter(frame -> frame.getClassName().startsWith("forge.")).limit(16).map(Object::toString).toList()));
        }
    }
}
