package forge.api;

import com.google.gson.Gson;
import com.google.gson.JsonObject;
import forge.card.CardType;
import forge.game.Game;
import forge.game.card.Card;
import forge.game.player.Player;
import forge.game.spellability.SpellAbility;
import forge.game.zone.ZoneType;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.concurrent.atomic.AtomicInteger;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Committing a view must retain metadata published by a nested rules query. */
public class PublicationOwnershipTest {
    private static final Gson JSON = new Gson();

    @Test(timeOut = 90000)
    public void aLatePlayAccessQueryCannotReplaceNewerSameBoardPriorityMetadata() throws Exception {
        try (var scenario = new RulesScenario("late-view-metadata", Map.of(0, List.of("Mogg Fanatic")))) {
            scenario.until(input -> input.seat() == 0 && text(input.prompt(), "inputType").equals("InputPassPriority")
                    && text(input.state(), "phaseKey").equals("MAIN1"));
            HeadlessPlatform.query(scenario.table, () -> {
                var actor = scenario.table.game.getRegisteredPlayers().get(0);
                var probe = new PlayAccessProbe(scenario.table.game.nextCardId(), scenario.table.game, actor);
                actor.getZone(ZoneType.Exile).add(probe); probe.setZone(actor.getZone(ZoneType.Exile));
                scenario.table.publish();
                var session = scenario.session(0);
                var before = JSON.toJsonTree(session.state()).getAsJsonObject();
                var ability = before.getAsJsonObject("prompt").getAsJsonArray("priorityAbilities").asList().stream()
                        .map(value -> value.getAsJsonObject())
                        .filter(value -> text(value, "sourceName").equals("Mogg Fanatic")).findFirst().orElseThrow();
                String abilityId = text(ability, "id");
                long board = before.get("boardRevision").getAsLong();
                var nested = new ArrayList<JsonObject>();
                probe.beforeNextAccess = () -> {
                    var setting = request(new Input(0, before, before.getAsJsonObject("prompt")),
                            Map.of("action", "setAbilityAutoPass", "abilityId", abilityId, "ignored", true));
                    var changed = JSON.toJsonTree(session.action(setting)).getAsJsonObject();
                    assertEquals(changed.get("boardRevision").getAsLong(), board,
                            "The actual priority preference is metadata-only on this board");
                    assertTrue(changed.get("revision").getAsLong() > before.get("revision").getAsLong());
                    assertTrue(changed.getAsJsonArray("ignoredPriorityAbilities").asList().stream()
                            .anyMatch(value -> text(value.getAsJsonObject(), "id").equals(abilityId)));
                    nested.add(changed.deepCopy());
                };

                boolean captured = session.publishView(board);
                assertNull(probe.beforeNextAccess, "The real play-access projection must reach its late query");
                assertEquals(nested.size(), 1, "Only the one-shot late query may publish this metadata");
                var after = JSON.toJsonTree(session.state()).getAsJsonObject();
                Files.writeString(scenario.evidence.resolve("reserved-view-metadata.json"), JSON.toJson(Map.of(
                        "before", before, "nested", nested.get(0), "after", after, "outerCaptured", captured)));
                assertEquals(after, nested.get(0),
                        "A reserved outer view cannot overwrite the newer same-board metadata publication");
                assertFalse(captured, "A view that lost commit ownership cannot certify another captured cohort");
                for (int seat = 0; seat < 4; seat++) assertEquals(
                        scenario.state(seat).get("boardRevision").getAsLong(), board,
                        "Retiring a superseded same-board capture cannot create a partial board cohort");
                var owned = JSON.toJsonTree(session.decisionObservation()).getAsJsonObject();
                assertEquals(owned.get("state"), after,
                        "The newer committed metadata retains usable current decision authority");
                var restore = request(new Input(0, after, after.getAsJsonObject("prompt")),
                        Map.of("action", "setAbilityAutoPass", "abilityId", abilityId, "ignored", false));
                var restored = JSON.toJsonTree(session.action(restore)).getAsJsonObject();
                assertTrue(restored.get("revision").getAsLong() > after.get("revision").getAsLong());
                assertTrue(restored.getAsJsonArray("ignoredPriorityAbilities").isEmpty(),
                        "A subsequent explicit current preference still applies to the original real ability");
                scenario.table.publish();
                long recoveredBoard = scenario.state(0).get("boardRevision").getAsLong();
                assertTrue(recoveredBoard > board);
                for (int seat = 0; seat < 4; seat++) assertEquals(
                        scenario.state(seat).get("boardRevision").getAsLong(), recoveredBoard,
                        "A subsequent complete publication retains a coherent four-audience board");
                return null;
            });
        }
    }

    @Test(timeOut = 90000)
    public void aLaterSeatLosingViewOwnershipReconcilesTheWholeAudienceCohort() throws Exception {
        try (var scenario = new RulesScenario("late-seat-metadata", Map.of(3, List.of("Mogg Fanatic")))) {
            scenario.until(input -> input.seat() == 3 && text(input.prompt(), "inputType").equals("InputPassPriority")
                    && text(input.state(), "phaseKey").equals("MAIN1"));
            HeadlessPlatform.query(scenario.table, () -> {
                var actor = scenario.table.game.getRegisteredPlayers().get(3);
                var probe = new PlayAccessProbe(scenario.table.game.nextCardId(), scenario.table.game, actor);
                actor.getZone(ZoneType.Exile).add(probe); probe.setZone(actor.getZone(ZoneType.Exile));
                scenario.table.publish();
                var session = scenario.session(3);
                var before = JSON.toJsonTree(session.state()).getAsJsonObject();
                var ability = before.getAsJsonObject("prompt").getAsJsonArray("priorityAbilities").asList().stream()
                        .map(value -> value.getAsJsonObject())
                        .filter(value -> text(value, "sourceName").equals("Mogg Fanatic")).findFirst().orElseThrow();
                String abilityId = text(ability, "id");
                var nested = new ArrayList<JsonObject>();
                probe.beforeNextAccess = () -> {
                    var setting = request(new Input(3, before, before.getAsJsonObject("prompt")),
                            Map.of("action", "setAbilityAutoPass", "abilityId", abilityId, "ignored", true));
                    nested.add(JSON.toJsonTree(session.action(setting)).getAsJsonObject());
                };

                scenario.table.publish();
                assertNull(probe.beforeNextAccess);
                assertEquals(nested.size(), 1);
                var after = JSON.toJsonTree(session.state()).getAsJsonObject();
                var audiences = new ArrayList<JsonObject>();
                for (int seat = 0; seat < 4; seat++) audiences.add(scenario.state(seat));
                Files.writeString(scenario.evidence.resolve("late-seat-metadata.json"), JSON.toJson(Map.of(
                        "before", before, "nested", nested.get(0), "after", after, "audiences", audiences)));
                assertTrue(after.get("revision").getAsLong() > nested.get(0).get("revision").getAsLong(),
                        "Reconciliation must commit after the nested metadata's reserved revision");
                assertEquals(after.getAsJsonArray("ignoredPriorityAbilities"), nested.get(0).getAsJsonArray("ignoredPriorityAbilities"),
                        "A fresh cohort retains the actual accepted preference and its stable source handle");
                long board = after.get("boardRevision").getAsLong();
                assertTrue(board > before.get("boardRevision").getAsLong());
                for (var audience : audiences) assertEquals(audience.get("boardRevision").getAsLong(), board,
                        "The later seat's conflict cannot strand earlier seats in an abandoned cohort");
                var owned = JSON.toJsonTree(session.decisionObservation()).getAsJsonObject();
                assertEquals(owned.get("state"), after, "The replacement cohort retains current decision authority");
                return null;
            });
        }
    }

    @Test(timeOut = 90000)
    public void repeatedMetadataLossHasABoundedFailureWithoutInstallingAPartialCohort() throws Exception {
        try (var scenario = new RulesScenario("repeated-view-metadata", Map.of(3, List.of("Mogg Fanatic")))) {
            scenario.until(input -> input.seat() == 3 && text(input.prompt(), "inputType").equals("InputPassPriority")
                    && text(input.state(), "phaseKey").equals("MAIN1"));
            HeadlessPlatform.query(scenario.table, () -> {
                var actor = scenario.table.game.getRegisteredPlayers().get(3);
                var probe = new PlayAccessProbe(scenario.table.game.nextCardId(), scenario.table.game, actor);
                actor.getZone(ZoneType.Exile).add(probe); probe.setZone(actor.getZone(ZoneType.Exile));
                scenario.table.publish();
                var session = scenario.session(3);
                var before = JSON.toJsonTree(session.state()).getAsJsonObject();
                var ability = before.getAsJsonObject("prompt").getAsJsonArray("priorityAbilities").asList().stream()
                        .map(value -> value.getAsJsonObject())
                        .filter(value -> text(value, "sourceName").equals("Mogg Fanatic")).findFirst().orElseThrow();
                String abilityId = text(ability, "id");
                long board = before.get("boardRevision").getAsLong();
                var original = new ArrayList<JsonObject>();
                var completions = new AtomicInteger();
                for (int seat = 0; seat < 4; seat++) {
                    original.add(scenario.state(seat));
                    scenario.session(seat).observePlaytest(frame -> completions.incrementAndGet());
                }
                completions.set(0);
                var nested = new ArrayList<JsonObject>();
                Runnable[] replaceMetadata = new Runnable[1];
                replaceMetadata[0] = () -> {
                    for (int seat = 0; seat < 4; seat++) assertEquals(
                            scenario.state(seat).get("boardRevision").getAsLong(), board,
                            "A callback during capture must see the prior complete cohort for every audience");
                    var current = JSON.toJsonTree(session.state()).getAsJsonObject();
                    var setting = request(new Input(3, current, current.getAsJsonObject("prompt")),
                            Map.of("action", "setAbilityAutoPass", "abilityId", abilityId, "ignored", nested.isEmpty()));
                    nested.add(JSON.toJsonTree(session.action(setting)).getAsJsonObject());
                    probe.beforeNextAccess = replaceMetadata[0];
                };
                probe.beforeNextAccess = replaceMetadata[0];

                assertThrows(MatchTable.ViewPublicationSupersededException.class, scenario.table::publish);
                assertEquals(nested.size(), 2, "One initial capture and one fresh capture are the entire attempt allowance");
                assertEquals(completions.get(), 0, "An abandoned cohort cannot notify an observer or queue automatic passes");
                var after = JSON.toJsonTree(session.state()).getAsJsonObject();
                assertEquals(after, nested.get(1), "The last accepted metadata survives bounded capture failure exactly");
                var audiences = new ArrayList<JsonObject>();
                for (int seat = 0; seat < 4; seat++) {
                    var audience = scenario.state(seat); audiences.add(audience);
                    assertEquals(audience.get("boardRevision").getAsLong(), board, "No earlier seat installed a partial board");
                    assertEquals(audience.get("priorityObservation"), original.get(seat).get("priorityObservation"),
                            "Neither failed cut certified observation delivery");
                }
                Files.writeString(scenario.evidence.resolve("repeated-view-metadata.json"), JSON.toJson(Map.of(
                        "before", before, "nested", nested, "after", after, "audiences", audiences, "completions", completions.get())));
                var owned = JSON.toJsonTree(session.decisionObservation()).getAsJsonObject();
                assertEquals(owned.get("state"), after, "Prior response authority is still usable on its actual retained board");
                probe.beforeNextAccess = null;
                scenario.table.publish();
                assertEquals(completions.get(), 4, "A later explicitly requested stable publication still completes normally");
                long recovered = scenario.state(3).get("boardRevision").getAsLong();
                assertTrue(recovered > board);
                for (int seat = 0; seat < 4; seat++) assertEquals(scenario.state(seat).get("boardRevision").getAsLong(), recovered);
                return null;
            });
        }
    }

    @Test(timeOut = 90000)
    public void aNestedRealStackSourceReadRetainsItsNewOpaqueHandleAndExecutableDomain() throws Exception {
        var library = List.of("Lightning Storm", "Mountain", "Mountain", "Mountain", "Mountain", "Mountain", "Mountain", "Mountain");
        try (var scenario = new RulesScenario("nested-stack-handle", Map.of(0, List.of("Mountain", "Mountain", "Mountain")),
                "Mountain", "Rhys the Redeemed", Map.of(0, library))) {
            var main = scenario.until(input -> input.seat() == 0 && text(input.prompt(), "inputType").equals("InputPassPriority")
                    && text(input.state(), "phaseKey").equals("MAIN1"));
            var spell = namedCard(main.state(), "Hand", "Lightning Storm");
            var offered = JSON.toJsonTree(scenario.session(0).sourceActions(request(main, Map.of("key", text(spell, "key"))))).getAsJsonObject();
            var cast = offered.getAsJsonArray("actions").asList().stream().map(value -> value.getAsJsonObject())
                    .filter(action -> text(action, "kind").equals("cast")).findFirst().orElseThrow();
            assertTrue(scenario.answer(main, Map.of("action", "useSourceAction", "key", text(offered, "sourceCardKey"), "actionId", text(cast, "id"))));
            Input stacked = null;
            for (int step = 0; step < 8; step++) {
                var current = scenario.until(input -> input.seat() == 0 && (text(input.prompt(), "inputType").equals("InputSelectTargets")
                        || text(input.prompt(), "inputType").startsWith("InputPayMana")
                        || text(input.prompt(), "inputType").equals("InputPassPriority") && !input.state().getAsJsonArray("stack").isEmpty()));
                if (text(current.prompt(), "inputType").equals("InputPassPriority")) { stacked = current; break; }
                assertTrue(scenario.answer(current, text(current.prompt(), "inputType").equals("InputSelectTargets")
                        ? Map.of("action", "player", "playerId", 1) : Map.of("action", "ok")));
            }
            assertNotNull(stacked, "The real spell must finish its target and mana inputs before the retained-domain test");
            var retained = new ArrayList<JsonObject>();
            HeadlessPlatform.query(scenario.table, () -> {
                var actor = scenario.table.game.getRegisteredPlayers().get(0);
                var probe = new PlayAccessProbe(scenario.table.game.nextCardId(), scenario.table.game, actor);
                actor.getZone(ZoneType.Exile).add(probe); probe.setZone(actor.getZone(ZoneType.Exile));
                scenario.table.publish();
                var session = scenario.session(0);
                var before = JSON.toJsonTree(session.state()).getAsJsonObject();
                probe.beforeNextAccess = () -> retained.add(JSON.toJsonTree(session.decisionObservation()).getAsJsonObject());
                assertFalse(session.publishView(before.get("boardRevision").getAsLong()),
                        "An additional handle exposed by a nested rich read supersedes the staged allocation frontier");
                assertEquals(retained.size(), 1);
                assertEquals(JSON.toJsonTree(session.state()), before, "The rich read keeps the actual committed ordinary view intact");
                var source = stackSource(retained.get(0));
                assertFalse(text(source, "sourceCardKey").isBlank());
                scenario.table.publish();
                var current = JSON.toJsonTree(session.decisionObservation()).getAsJsonObject();
                assertEquals(current.get("domain"), retained.get(0).get("domain"),
                        "The next coherent publication retains the exact exposed action IDs and source handle");
                assertEquals(current.getAsJsonObject("state").get("actionSources"), retained.get(0).getAsJsonObject("state").get("actionSources"));
                Files.writeString(scenario.evidence.resolve("nested-stack-handle.json"), JSON.toJson(Map.of(
                        "before", before, "retained", retained.get(0), "current", current)));
                return null;
            });
            var state = scenario.state(0);
            var source = stackSource(retained.get(0));
            var action = source.getAsJsonArray("actions").get(0).getAsJsonObject();
            assertTrue(scenario.answer(new Input(0, state, state.getAsJsonObject("prompt")),
                    Map.of("action", "useSourceAction", "key", text(source, "sourceCardKey"), "actionId", text(action, "id"))),
                    "The exact retained domain still invokes its real stack activation");
            var discard = scenario.until(input -> input.seat() == 0 && text(input.prompt(), "inputType").equals("InputSelectCardsFromList"));
            assertTrue(scenario.answer(discard, Map.of("action", "card", "key", text(namedCard(discard.state(), "Hand", "Mountain"), "key"))));
            scenario.until(input -> input.seat() == 0 && text(input.prompt(), "inputType").equals("InputPassPriority")
                    && input.state().getAsJsonArray("stack").size() == 2);
        }
    }

    private static JsonObject namedCard(JsonObject state, String zone, String name) {
        return zone(viewer(state), zone).getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                .filter(card -> text(card, "name").equals(name)).findFirst().orElseThrow();
    }

    private static JsonObject stackSource(JsonObject observation) {
        var portrait = observation.getAsJsonObject("state").getAsJsonArray("actionSources").asList().stream()
                .map(value -> value.getAsJsonObject()).filter(value -> text(value, "zone").equals("Stack")
                        && text(value.getAsJsonObject("card"), "name").equals("Lightning Storm")).findFirst().orElseThrow();
        return observation.getAsJsonObject("domain").getAsJsonArray("sources").asList().stream()
                .map(value -> value.getAsJsonObject()).filter(value -> text(value, "sourceCardKey")
                        .equals(text(portrait.getAsJsonObject("card"), "key"))).findFirst().orElseThrow();
    }

    // Existing rules-query polymorphism is the scheduling seam; production
    // publication code receives no test hook or alternate metadata writer.
    private static final class PlayAccessProbe extends Card {
        private final Player actor;
        Runnable beforeNextAccess;
        PlayAccessProbe(int id, Game game, Player actor) {
            super(id, game);
            this.actor = actor;
            setOwner(actor); setName("Publication query probe");
            setType(CardType.parse("Artifact", false));
        }
        @Override public List<SpellAbility> getPlayAccessAbilities(Player player) {
            if (player == actor) {
                var callback = beforeNextAccess; beforeNextAccess = null;
                if (callback != null) callback.run();
            }
            return super.getPlayAccessAbilities(player);
        }
    }
}
