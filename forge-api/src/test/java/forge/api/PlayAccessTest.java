package forge.api;

import com.google.gson.Gson;
import com.google.gson.JsonObject;
import forge.game.zone.ZoneType;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.function.Predicate;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Real grants and casts, read through the same per-viewer snapshot as the game. */
public class PlayAccessTest {
    private static final Gson JSON = new Gson();

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void distinctExileGrantsAndFlashbackSurviveTimingChangesAndRevokeAtTheirRealBoundary() throws Exception {
        String firstSource = "Urabrask, Heretic Praetor", secondSource = "Visions of Phyrexia", spell = "Lingering Souls";
        var battlefield = new ArrayList<>(List.of(firstSource, secondSource));
        for (int i = 0; i < 8; i++) battlefield.add("Plains");
        battlefield.add("Swamp"); battlefield.add("Swamp");
        try (var game = new RulesScenario("play-access-routes", Map.of(0, battlefield), spell, "Rhys the Redeemed", true)) {
            var input = main(game);
            assertEquals(exileAccesses(input.state()).size(), 2, "Both actual upkeep triggers grant access");
            input = cast(game, input, card(input.state(), "Hand", spell), "W", "W", "W");
            var grave = card(input.state(), "Graveyard", spell);
            var flashback = access(input.state(), grave);
            assertNotNull(flashback, "An actual flashback route is discovered independently of hand permissions");
            String flashbackId = text(route(flashback), "id");
            assertEquals(text(route(flashback), "detail"), "Flashback {1}{B}",
                    "The route uses its actual alternate cost, not the printed {2}{W} cost or effect prose");
            assertTrue(route(flashback).get("canStart").getAsBoolean());

            var first = exileAccesses(input.state()).stream()
                    .filter(entry -> text(route(entry), "sourceName").equals(firstSource)).findFirst().orElseThrow();
            assertEquals(text(route(first), "sourceCardId"), text(card(input.state(), "Battlefield", firstSource), "visualId"));
            assertEquals(text(route(first), "detail"), "Cast {2}{W}",
                    "Granting source is separate from a concise route cost, without internal card IDs or effect paragraphs");
            String firstRoute = text(route(first), "id");

            // Freeze the actual input dispatcher to inspect the accepted action's
            // resolving observation before the next prompt replaces it.
            var untapped = zone(viewer(input.state()), "Battlefield").getAsJsonArray("cards").asList().stream()
                    .map(value -> value.getAsJsonObject()).filter(value -> text(value, "name").equals("Plains")
                            && !value.get("tapped").getAsBoolean()).findFirst().orElseThrow();
            var source = query(game, input, untapped);
            var ability = action(source, candidate -> text(candidate, "kind").equals("activate"));
            var entered = new CountDownLatch(1); var release = new CountDownLatch(1);
            try {
                HeadlessPlatform.later(() -> { entered.countDown(); await(release); });
                assertTrue(entered.await(5, TimeUnit.SECONDS));
                assertTrue(game.answer(input, invoke(source, ability)));
                var resolving = game.state(0);
                assertEquals(text(resolving, "status"), "resolving");
                assertNotNull(access(resolving, grave));
                assertTrue(accesses(resolving).stream().flatMap(entry -> entry.getAsJsonArray("routes").asList().stream())
                        .noneMatch(value -> value.getAsJsonObject().get("canStart").getAsBoolean()),
                        "Resolving retains access but cannot retain any current action cue");
            } finally { release.countDown(); }
            input = settled(game);
            assertEquals(exileAccesses(input.state()).size(), 2);
            var second = exileAccesses(input.state()).stream()
                    .filter(entry -> text(route(entry), "sourceName").equals(secondSource)).findFirst().orElseThrow();
            assertNotEquals(text(route(second), "id"), firstRoute);
            assertTrue(exileAccesses(input.state()).stream().allMatch(entry -> route(entry).get("canStart").getAsBoolean()));
            Files.writeString(game.evidence.resolve("two-exile-sources-and-flashback.json"), JSON.toJson(input.state()));

            input = until(game, next -> next.seat() == 0 && priority(next)
                    && text(next.state(), "phaseKey").equals("COMBAT_BEGIN"));
            assertEquals(text(route(access(input.state(), grave)), "id"), flashbackId);
            assertEquals(exileAccesses(input.state()).size(), 2);
            assertTrue(accesses(input.state()).stream().noneMatch(entry -> route(entry).get("canStart").getAsBoolean()),
                    "Sorcery access remains visible during combat without advertising a current cast");
            assertTrue(query(game, input, card(input.state(), "Graveyard", spell)).getAsJsonArray("actions").isEmpty());
            assertTrue(exileAccesses(input.state()).stream().anyMatch(entry -> text(route(entry), "id").equals(firstRoute)));

            input = until(game, next -> next.seat() == 0 && priority(next) && text(next.state(), "phaseKey").equals("MAIN2"));
            input = cast(game, input, card(input.state(), "Graveyard", spell), "W", "B");
            assertEquals(count(viewer(input.state()), "Exile"), 3);
            assertEquals(exileAccesses(input.state()).size(), 2,
                    "The permanently exiled flashback card does not acquire another copy's play permission");
            assertNull(access(input.state(), card(input.state(), "Graveyard", spell)));
            var granted = exileAccesses(input.state()).get(0);
            String consumed = text(granted, "cardId");
            var face = zone(viewer(input.state()), "Exile").getAsJsonArray("cards").asList().stream()
                    .map(value -> value.getAsJsonObject()).filter(value -> text(value, "visualId").equals(consumed)).findFirst().orElseThrow();
            input = cast(game, input, face, "W", "W", "W");
            assertEquals(exileAccesses(input.state()).size(), 1);
            assertTrue(accesses(input.state()).stream().noneMatch(entry -> text(entry, "cardId").equals(consumed)
                    && text(entry, "zone").equals("Exile")), "A played occurrence loses its old exile route");

            int caster = input.state().get("viewerId").getAsInt();
            input = until(game, next -> next.seat() != 0 && priority(next)
                    && next.state().get("activePlayerId").getAsInt() != caster);
            var afterTurn = game.state(0);
            assertTrue(exileAccesses(afterTurn).isEmpty(), "This-turn permissions expire in the rules engine");
            assertTrue(accesses(afterTurn).stream().anyMatch(entry -> text(entry, "zone").equals("Graveyard")),
                    "Independent flashback access remains while another player has priority");
            assertTrue(accesses(afterTurn).stream().noneMatch(entry -> route(entry).get("canStart").getAsBoolean()));
            for (int seat = 1; seat < 4; seat++) assertTrue(accesses(game.state(seat)).isEmpty(),
                    "A public exiled face does not give another viewer its owner's private play grant");
        }
    }

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void aLandOnlyTopPermissionFindsAndPlaysTheAlternateLandFace() throws Exception {
        String front = "Bala Ged Recovery", back = "Bala Ged Sanctuary";
        try (var game = new RulesScenario("play-access-alternate-face", Map.of(0,
                List.of("Courser of Kruphix")), front, "Rhys the Redeemed")) {
            var input = main(game);
            var top = zone(viewer(input.state()), "Library").getAsJsonObject("topCard");
            assertEquals(text(top, "name"), front, "The disclosed occurrence is currently its sorcery front");
            var access = access(input.state(), top);
            assertNotNull(access, "The engine evaluates Courser's land-only permission on the alternate host");
            assertEquals(access.getAsJsonArray("routes").size(), 1, "Courser does not grant casting the sorcery front");
            assertEquals(route(access).get("kind").getAsInt(), 1);
            assertEquals(text(route(access), "detail"), "Play land");
            assertTrue(route(access).get("canStart").getAsBoolean());
            var actions = query(game, input, top);
            assertTrue(actions.getAsJsonArray("actions").asList().stream()
                    .noneMatch(value -> text(value.getAsJsonObject(), "kind").equals("cast")));
            assertTrue(game.answer(input, invoke(actions, action(actions, candidate -> text(candidate, "kind").equals("play")))));
            input = settled(game);
            assertNotNull(card(input.state(), "Battlefield", back), "The actual alternate land face enters the battlefield");
            assertEquals(viewer(input.state()).get("landPlaysUsed").getAsInt(), 1);
            assertFalse(accesses(input.state()).stream().flatMap(entry -> entry.getAsJsonArray("routes").asList().stream())
                    .anyMatch(value -> value.getAsJsonObject().get("canStart").getAsBoolean()));
            Files.writeString(game.evidence.resolve("alternate-land-played.json"), JSON.toJson(input.state()));
        }
    }

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void aFaceUpPlottedSpellHasAccessBeforeItsLaterTurnCastingWindow() throws Exception {
        String spell = "Outcaster Trailblazer";
        try (var game = new RulesScenario("play-access-plot", Map.of(0,
                List.of("Forest", "Forest", "Forest")), spell, "Rhys the Redeemed")) {
            var input = main(game);
            for (int i = 0; i < 3; i++) input = floatMana(game, input, "G");
            var actions = query(game, input, card(input.state(), "Hand", spell));
            assertTrue(game.answer(input, invoke(actions, action(actions,
                    candidate -> text(candidate, "kind").equals("activate") && text(candidate, "text").startsWith("Plot")))));
            input = settled(game);
            var face = card(input.state(), "Exile", spell);
            assertNotNull(face, "The real Plot action exiles this card face up");
            assertFalse(face.get("faceDown").getAsBoolean());
            var access = access(input.state(), face);
            assertNotNull(access, "The ongoing plotted permission exists before its later-turn timing window");
            assertEquals(route(access).get("kind").getAsInt(), 0);
            assertTrue(text(route(access), "detail").startsWith("Plotted"));
            assertFalse(route(access).get("canStart").getAsBoolean());
            assertTrue(query(game, input, face).getAsJsonArray("actions").isEmpty(),
                    "Discovery does not change the legacy action catalog's prohibition on casting during the plotted turn");
            String routeId = text(route(access), "id");
            int caster = input.state().get("viewerId").getAsInt();
            until(game, next -> next.seat() != 0 && priority(next)
                    && next.state().get("activePlayerId").getAsInt() != caster);
            var retained = access(game.state(0), face);
            assertNotNull(retained);
            assertEquals(text(route(retained), "id"), routeId);
            assertFalse(route(retained).get("canStart").getAsBoolean());
            Files.writeString(game.evidence.resolve("plotted-before-next-turn.json"), JSON.toJson(input.state()));
        }
    }

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void exiledLandAndDisclosedLibraryTopKeepAccessWhenTheLandLimitIsUsed() throws Exception {
        try (var game = new RulesScenario("play-access-land", Map.of(0,
                List.of("Urabrask, Heretic Praetor", "Courser of Kruphix")))) {
            var input = main(game);
            var exile = exileAccesses(input.state()).get(0);
            var top = accesses(input.state()).stream().filter(entry -> text(entry, "zone").equals("Library")).findFirst().orElseThrow();
            assertEquals(route(exile).get("kind").getAsInt(), 1);
            assertEquals(route(top).get("kind").getAsInt(), 1);
            assertEquals(text(route(top), "sourceName"), "Courser of Kruphix");
            assertEquals(text(route(exile), "detail"), "Play land");
            assertTrue(route(exile).get("canStart").getAsBoolean());
            assertTrue(route(top).get("canStart").getAsBoolean());
            String topId = text(top, "cardId"), routeId = text(route(top), "id");
            var face = zone(viewer(input.state()), "Exile").getAsJsonArray("cards").get(0).getAsJsonObject();
            var actions = query(game, input, face);
            assertTrue(game.answer(input, invoke(actions, action(actions, candidate -> text(candidate, "kind").equals("play")))));
            input = settled(game);
            assertEquals(viewer(input.state()).get("landPlaysUsed").getAsInt(), 1);
            assertTrue(exileAccesses(input.state()).isEmpty(), "Playing a real exiled land consumes its exile occurrence");
            var retained = accesses(input.state()).stream().filter(entry -> text(entry, "zone").equals("Library")).findFirst().orElseThrow();
            assertEquals(text(retained, "cardId"), topId);
            assertEquals(text(route(retained), "id"), routeId);
            assertFalse(route(retained).get("canStart").getAsBoolean(),
                    "Courser still grants access after the land play limit removes the current action");
            assertTrue(query(game, input, zone(viewer(input.state()), "Library").getAsJsonObject("topCard"))
                    .getAsJsonArray("actions").isEmpty());
            Files.writeString(game.evidence.resolve("top-land-after-land-limit.json"), JSON.toJson(input.state()));
        }
    }

    @Test(groups = "canonical-interaction", timeOut = 90000)
    public void foreignOwnedExileUsesTheGrantingSourceWhileLookOnlyAndGraveyardActivationsStayOut() throws Exception {
        String hostage = "Hostage Taker", skeleton = "Reassembling Skeleton";
        try (var game = new RulesScenario("play-access-foreign", Map.of(0,
                List.of(hostage, "Courser of Kruphix", skeleton, "Ashnod's Altar", "Plains", "Plains", "Plains"),
                1, List.of("Spellbook")), "Cloudshift", "Rhys the Redeemed")) {
            var input = main(game);
            assertNotNull(zone(viewer(input.state()), "Library").get("topCard"), "The look-only top card is actually disclosed");
            assertTrue(accesses(input.state()).isEmpty(), "Looking at a nonland is not Courser's permission to play it");

            var altar = query(game, input, card(input.state(), "Battlefield", "Ashnod's Altar"));
            assertTrue(game.answer(input, invoke(altar, action(altar, candidate -> text(candidate, "kind").equals("activate")))));
            var sacrifice = until(game, next -> next.seat() == 0 && text(next.prompt(), "inputType").equals("InputSelectCardsFromList"));
            assertTrue(game.answer(sacrifice, Map.of("action", "card", "key", text(card(sacrifice.state(), "Battlefield", skeleton), "key"))));
            input = settled(game);
            var skeletonFace = card(input.state(), "Graveyard", skeleton);
            assertNotNull(skeletonFace);
            assertFalse(query(game, input, skeletonFace).getAsJsonArray("actions").isEmpty(),
                    "This is a real available graveyard activation, not an inert fixture");
            assertNull(access(input.state(), skeletonFace), "An activation that returns a creature is not casting a card");

            input = floatMana(game, input, "W");
            var blink = query(game, input, card(input.state(), "Hand", "Cloudshift"));
            var actor = game.table.game.getPlayers().get(0);
            var original = actor.getCardsIn(ZoneType.Hand).get(0).getSpellAbilities().get(0);
            assertSame(original.getActivatingPlayer(), actor,
                    "The offered hand spell retains its actual casting player");
            var beforeDiscovery = game.state(0);
            for (var other : game.table.game.getPlayers()) if (other != actor) {
                assertTrue(original.getHostCard().getPlayAccessAbilities(other).isEmpty());
                assertSame(original.getActivatingPlayer(), actor,
                        "Another viewer's discovery must not mutate a base ability retained by the source-action catalog");
            }
            assertEquals(game.state(0), beforeDiscovery, "Access discovery does not advance or republish game state");
            // Commit the exact pre-discovery handle, without refreshing the
            // catalog and accidentally repairing a corrupted activating player.
            assertTrue(game.answer(input, invoke(blink, action(blink, candidate -> text(candidate, "kind").equals("cast")))));
            var target = until(game, next -> next.seat() == 0 && text(next.prompt(), "inputType").equals("InputSelectTargets"));
            assertTrue(game.answer(target, Map.of("action", "card", "key", text(card(target.state(), "Battlefield", hostage), "key"))));
            target = until(game, next -> next.seat() == 0 && text(next.prompt(), "inputType").equals("InputSelectTargets"));
            var victim = target.state().getAsJsonArray("players").asList().stream().map(value -> value.getAsJsonObject())
                    .filter(player -> zone(player, "Battlefield").getAsJsonArray("cards").asList().stream()
                            .anyMatch(value -> text(value.getAsJsonObject(), "name").equals("Spellbook"))).findFirst().orElseThrow();
            var book = zone(victim, "Battlefield").getAsJsonArray("cards").get(0).getAsJsonObject();
            assertTrue(game.answer(target, Map.of("action", "card", "key", text(book, "key"))));
            input = settled(game);
            var foreign = accesses(input.state()).stream().filter(entry -> text(entry, "zone").equals("Exile")).findFirst().orElseThrow();
            assertEquals(foreign.get("playerId").getAsInt(), victim.get("id").getAsInt());
            assertNotEquals(foreign.get("playerId").getAsInt(), input.state().get("viewerId").getAsInt());
            assertEquals(text(route(foreign), "sourceName"), hostage);
            assertTrue(route(foreign).get("canStart").getAsBoolean());
            assertTrue(accesses(game.state(1)).isEmpty(), "Ownership alone does not inherit the thief's permission");
            Files.writeString(game.evidence.resolve("foreign-owned-exile.json"), JSON.toJson(input.state()));
            var exiled = zone(player(input.state(), victim.get("id").getAsInt()), "Exile").getAsJsonArray("cards").get(0).getAsJsonObject();
            input = cast(game, input, exiled);
            assertTrue(accesses(input.state()).isEmpty());
            assertNotNull(card(input.state(), "Battlefield", "Spellbook"), "The actual granted foreign-owned cast completes");
        }
    }

    private static Input cast(RulesScenario game, Input input, JsonObject card, String... mana) throws Exception {
        for (String color : mana) input = floatMana(game, input, color);
        // Floating mana refreshes the prompt-scoped source key.
        String id = text(card, "visualId");
        card = input.state().getAsJsonArray("players").asList().stream().map(value -> value.getAsJsonObject())
                .flatMap(player -> player.getAsJsonArray("zones").asList().stream()).map(value -> value.getAsJsonObject())
                .flatMap(zone -> zone.getAsJsonArray("cards").asList().stream()).map(value -> value.getAsJsonObject())
                .filter(value -> text(value, "visualId").equals(id)).findFirst().orElseThrow();
        var actions = query(game, input, card);
        assertTrue(game.answer(input, invoke(actions, action(actions, candidate -> text(candidate, "kind").equals("cast")))));
        return settled(game);
    }
    private static Input floatMana(RulesScenario game, Input input, String color) throws Exception {
        String name = color.equals("B") ? "Swamp" : color.equals("G") ? "Forest" : "Plains";
        var land = zone(viewer(input.state()), "Battlefield").getAsJsonArray("cards").asList().stream()
                .map(value -> value.getAsJsonObject()).filter(value -> text(value, "name").equals(name)
                        && !value.get("tapped").getAsBoolean()).findFirst().orElseThrow();
        var actions = query(game, input, land);
        assertTrue(game.answer(input, invoke(actions, action(actions, candidate -> text(candidate, "kind").equals("activate")))));
        return settled(game);
    }
    private static Input main(RulesScenario game) throws Exception {
        return until(game, next -> next.seat() == 0 && priority(next) && text(next.state(), "phaseKey").equals("MAIN1"));
    }
    private static Input settled(RulesScenario game) throws Exception {
        return until(game, next -> next.seat() == 0 && priority(next) && next.state().getAsJsonArray("stack").isEmpty());
    }
    private static Input until(RulesScenario game, Predicate<Input> ready) throws Exception {
        for (int count = 0; count < 40; count++) {
            var next = game.until(input -> text(input.prompt(), "kind").equals("reveal")
                    || input.prompt().has("ordered") && input.prompt().get("ordered").getAsBoolean()
                    || text(input.prompt(), "inputType").startsWith("InputPayMana") || ready.test(input));
            if (text(next.prompt(), "kind").equals("reveal")) game.answer(next, Map.of("action", "ok"));
            else if (next.prompt().has("ordered") && next.prompt().get("ordered").getAsBoolean()) game.answer(next, Map.of("choices",
                    next.prompt().getAsJsonArray("choices").asList().stream().map(value -> value.getAsJsonObject().get("index").getAsInt()).toList()));
            else if (text(next.prompt(), "inputType").startsWith("InputPayMana")) {
                assertTrue(next.prompt().get("okEnabled").getAsBoolean(), "The deliberately floated pool pays the real cost");
                game.answer(next, Map.of("action", "ok"));
            } else return next;
        }
        throw new AssertionError("Too many auxiliary payment/reveal prompts");
    }
    private static boolean priority(Input input) { return text(input.prompt(), "inputType").equals("InputPassPriority"); }
    private static JsonObject card(JsonObject state, String zone, String name) {
        return zone(viewer(state), zone).getAsJsonArray("cards").asList().stream().map(value -> value.getAsJsonObject())
                .filter(card -> text(card, "name").equals(name)).findFirst().orElse(null);
    }
    private static List<JsonObject> accesses(JsonObject state) {
        return state.has("playAccesses") ? state.getAsJsonArray("playAccesses").asList().stream().map(value -> value.getAsJsonObject()).toList() : List.of();
    }
    private static List<JsonObject> exileAccesses(JsonObject state) {
        return accesses(state).stream().filter(entry -> text(entry, "zone").equals("Exile")).toList();
    }
    private static JsonObject access(JsonObject state, JsonObject card) {
        return card == null ? null : accesses(state).stream().filter(entry -> text(entry, "cardId").equals(text(card, "visualId"))).findFirst().orElse(null);
    }
    private static JsonObject route(JsonObject access) { return access.getAsJsonArray("routes").get(0).getAsJsonObject(); }
    private static JsonObject query(RulesScenario game, Input input, JsonObject card) {
        assertNotNull(card);
        return JSON.toJsonTree(game.table.seat(input.seat()).sourceActions(request(input, Map.of("key", text(card, "key"))))).getAsJsonObject();
    }
    private static JsonObject action(JsonObject set, Predicate<JsonObject> choose) {
        return set.getAsJsonArray("actions").asList().stream().map(value -> value.getAsJsonObject())
                .filter(choose).findFirst().orElseThrow(() -> new AssertionError("No matching current action: " + set));
    }
    private static Map<String, ?> invoke(JsonObject set, JsonObject action) {
        return Map.of("action", "useSourceAction", "key", text(set, "sourceCardKey"), "actionId", text(action, "id"));
    }
    private static void await(CountDownLatch latch) {
        var game = forge.game.GameAction.currentGame();
        try (var suspended = game == null ? null : game.getAction().suspendState()) {
            if (!latch.await(10, TimeUnit.SECONDS)) throw new AssertionError("Input dispatcher was not released");
        }
        catch (InterruptedException failure) { Thread.currentThread().interrupt(); throw new AssertionError(failure); }
    }
}
