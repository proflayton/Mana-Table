package forge.api;

import com.google.gson.JsonObject;
import forge.StaticData;
import forge.deck.Deck;
import forge.game.ability.AbilityFactory;
import forge.game.card.Card;
import forge.game.player.LibrarySearchOffer;
import org.testng.annotations.DataProvider;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.*;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** Actual Partner-with targeting, foreign confirmation and optional library search. */
public class PartnerChoiceExperienceTest {
    private static final String TOOTHY = "Toothy, Imaginary Friend";
    private static final String PIR = "Pir, Imaginative Rascal";

    @DataProvider public Object[][] partnerChoices() {
        return new Object[][] {{0, true, true}, {1, true, true}, {1, false, true}, {1, true, false}};
    }

    @Test
    public void aLibraryOnlyOfferCannotDescribeMixedOriginsOrDefinedCardMoves() throws Exception {
        if (StaticData.instance() == null) EngineResources.load(Path.of("../forge-gui/res"));
        var players = List.of(MatchTable.human(new Deck(), "Constructed", "Seat 0"),
                MatchTable.human(new Deck(), "Constructed", "Seat 1"));
        // No rules thread is started: these descriptors examine public script
        // parameters without making a rules choice or opening a hidden zone.
        try (var table = new MatchTable("Constructed", players, Path.of("../forge-gui/res"),
                Files.createTempDirectory("search-offer-descriptor"))) {
            HeadlessPlatform.activate(table);
            var owner = table.game.getPlayers().get(0);
            var source = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard("Solemn Simulacrum"), owner);
            for (String scope : List.of("Origin$ Library,Graveyard", "Origin$ Graveyard,Hand,Library",
                    "Origin$ Library | OriginAlternative$ Graveyard", "Origin$ Library | Defined$ TopOfLibrary",
                    "Origin$ Library | NoLooking$ True")) {
                var ability = AbilityFactory.getAbility("DB$ ChangeZone | " + scope + " | Destination$ Hand | ChangeType$ Card", source);
                ability.setActivatingPlayer(owner);
                assertNull(LibrarySearchOffer.targeting(ability), scope + " needs its complete native decision rather than a library-only offer");
                assertNull(LibrarySearchOffer.optionalTrigger(ability), scope);
            }
            var search = AbilityFactory.getAbility("DB$ ChangeZone | Origin$ Library | Destination$ Hand | ChangeType$ Card.namedPir; Imaginative Rascal", source);
            search.setActivatingPlayer(owner);
            assertNull(LibrarySearchOffer.targeting(search).libraryOwnerId(), "Describing a target never preselects the recipient");
            assertEquals(LibrarySearchOffer.optionalTrigger(search).libraryOwnerId(), Integer.valueOf(owner.getId()));
            assertEquals(LibrarySearchOffer.optionalTrigger(search).cardName(), PIR);
        }
    }

    @Test(groups = "canonical-interaction", dataProvider = "partnerChoices", timeOut = 90000)
    public void partnerTargetsAndSearchBelongToTheActualChosenPlayer(int targetSeat, boolean accept, boolean pirInLibrary) throws Exception {
        // All-Toothy or all-Pir libraries remove random opening-hand availability.
        // An absent Pir is a real commander, not a pretend library candidate.
        try (var game = new RulesScenario("partner-choice-" + targetSeat + "-" + accept + "-" + pirInLibrary,
                Map.of(0, List.of("Island", "Island", "Island", "Island", "Leyline of Sanctity"),
                        3, List.of("Leyline of Sanctity")),
                pirInLibrary ? PIR : TOOTHY, pirInLibrary ? TOOTHY : PIR)) {
            var main = game.until(i -> i.seat() == 0 && text(i.state(), "phaseKey").equals("MAIN1")
                    && text(i.prompt(), "inputType").equals("InputPassPriority"));
            int caster = main.state().get("viewerId").getAsInt();
            int recipient = game.state(targetSeat).get("viewerId").getAsInt();
            var cast = zone(viewer(main.state()), pirInLibrary ? "Command" : "Hand").getAsJsonArray("cards").asList().stream()
                    .map(c -> c.getAsJsonObject()).filter(c -> text(c, "name").equals(TOOTHY)).findFirst().orElseThrow();
            assertTrue(game.answer(main, Map.of("action", "card", "key", text(cast, "key"))));
            var payment = game.until(i -> text(i.prompt(), "inputType").startsWith("InputPayMana")
                    || text(i.prompt(), "inputType").equals("InputSelectTargets"));
            if (text(payment.prompt(), "inputType").startsWith("InputPayMana")) {
                assertEquals(payment.seat(), 0);
                assertTrue(game.answer(payment, Map.of("action", "ok")));
                awaitPayment(game);
            }
            var targeting = text(payment.prompt(), "inputType").equals("InputSelectTargets") ? payment
                    : game.until(i -> text(i.prompt(), "inputType").equals("InputSelectTargets"));
            assertEquals(targeting.seat(), 0);
            assertSource(targeting, caster);
            assertSearch(targeting.prompt(), null, "Hand", PIR);
            var legalPlayers = new HashSet<Integer>();
            targeting.prompt().getAsJsonArray("playerChoices").forEach(p -> legalPlayers.add(p.getAsInt()));
            assertEquals(legalPlayers, Set.of(caster, game.state(1).get("viewerId").getAsInt(), game.state(2).get("viewerId").getAsInt()),
                    "Partner can target self or another legal player, including self with hexproof");
            assertFalse(targeting.prompt().get("okEnabled").getAsBoolean(), "Choosing a recipient is mandatory");
            game.rejected(targeting, Map.of("action", "player", "playerId", game.state(3).get("viewerId").getAsInt()),
                    "That player is not selectable in this decision");
            assertTrue(game.answer(targeting, Map.of("action", "player", "playerId", recipient)));
            var confirm = game.until(i -> text(i.prompt(), "inputType").equals("InputConfirm"));
            assertEquals(confirm.seat(), targetSeat, "The targeted player decides whether to search");
            assertSource(confirm, caster);
            assertSearch(confirm.prompt(), recipient, "Hand", PIR);
            assertEquals(text(confirm.prompt(), "ok"), "Search library");
            assertEquals(text(confirm.prompt(), "cancel"), "Don’t search");
            assertTrue(text(confirm.prompt(), "message").toLowerCase(Locale.ROOT).contains("library"));
            assertTrue(confirm.prompt().get("okEnabled").getAsBoolean());
            assertTrue(confirm.prompt().get("cancelEnabled").getAsBoolean());
            assertPrivateOwner(game, recipient, "confirm");
            int handBefore = count(viewer(confirm.state()), "Hand"), libraryBefore = count(viewer(confirm.state()), "Library");
            int drawsBefore = eventCount(confirm.state(), "draw"), shufflesBefore = eventCount(confirm.state(), "shuffle");
            assertTrue(game.answer(confirm, Map.of("action", accept ? "ok" : "cancel")));

            boolean chosePir = false, sawLibrary = false;
            RulesScenario.Input done = null;
            for (int step = 0; step < 12; step++) {
                var next = game.until(i -> text(i.prompt(), "kind").equals("choice") || text(i.prompt(), "kind").equals("reveal")
                        || text(i.prompt(), "inputType").equals("InputPassPriority") && i.state().getAsJsonArray("stack").isEmpty());
                if (text(next.prompt(), "inputType").equals("InputPassPriority")) { done = next; break; }
                if (text(next.prompt(), "context").equals("librarySearch")) {
                    assertTrue(accept, "Declining the optional search never opens a library");
                    assertEquals(next.seat(), targetSeat);
                    sawLibrary = true;
                    assertPrivateOwner(game, recipient, text(next.prompt(), "kind"));
                    for (int seat = 0; seat < 4; seat++) if (seat != targetSeat)
                        assertTrue(zone(player(game.state(seat), recipient), "Library").getAsJsonArray("cards").isEmpty());
                    if (text(next.prompt(), "kind").equals("choice")) {
                        var pir = next.prompt().getAsJsonArray("choices").asList().stream().map(c -> c.getAsJsonObject())
                                .filter(c -> c.has("card") && text(c.getAsJsonObject("card"), "name").equals(PIR)).findFirst().orElseThrow();
                        assertTrue(pirInLibrary, "A commander outside the library is not a search option");
                        assertFalse(pir.has("cardId"));
                        assertFalse(pir.getAsJsonObject("card").has("key"), "A private search face grants no persistent action key");
                        assertTrue(game.answer(next, Map.of("choices", List.of(pir.get("index").getAsInt()))));
                        chosePir = true;
                        continue;
                    }
                }
                assertEquals(text(next.prompt(), "kind"), "reveal", "Only actual engine reveals remain after the search");
                assertTrue(game.answer(next, Map.of("choices", List.of())));
            }
            assertNotNull(done, "The actual Partner ability must finish and restore ordinary priority");
            assertEquals(chosePir, accept && pirInLibrary);
            assertEquals(sawLibrary, accept);
            int moved = accept && pirInLibrary ? 1 : 0;
            for (int seat = 0; seat < 4; seat++) {
                var state = game.state(seat);
                assertEquals(count(player(state, recipient), "Hand"), handBefore + moved);
                assertEquals(count(player(state, recipient), "Library"), libraryBefore - moved);
                assertEquals(eventCount(state, "draw"), drawsBefore, "Searching into hand is not a draw");
                assertEquals(eventCount(state, "shuffle"), shufflesBefore + (accept ? 1 : 0),
                        "Only an accepted real search shuffles; declining does not");
            }
            if (!pirInLibrary) assertTrue(zone(viewer(game.state(targetSeat)), "Command").getAsJsonArray("cards").asList().stream()
                    .anyMatch(c -> text(c.getAsJsonObject(), "name").equals(PIR)), "The absent partner remains in Command");
            var stable = game.table.seat(2).state();
            assertEquals(game.table.seat(2).state(), stable, "Reading the result does not replay a shuffle");
        }
    }

    private static void assertSource(RulesScenario.Input input, int caster) {
        assertEquals(text(input.prompt().getAsJsonObject("sourceCard"), "name"), TOOTHY);
        assertEquals(input.prompt().get("sourceControllerId").getAsInt(), caster);
    }

    @Test(groups = "canonical-interaction", timeOut = 60000)
    public void optionalSearchTriggerCarriesItsOwnLibraryWithoutAPlayerPicker() throws Exception {
        try (var game = new RulesScenario("optional-search-solemn", Map.of(0, List.of("Island", "Island", "Island", "Island")),
                "Solemn Simulacrum", "Rhys the Redeemed")) {
            var main = game.until(i -> i.seat() == 0 && text(i.state(), "phaseKey").equals("MAIN1")
                    && text(i.prompt(), "inputType").equals("InputPassPriority"));
            int owner = main.state().get("viewerId").getAsInt();
            var card = zone(viewer(main.state()), "Hand").getAsJsonArray("cards").get(0).getAsJsonObject();
            assertTrue(game.answer(main, Map.of("action", "card", "key", text(card, "key"))));
            var payment = game.until(i -> text(i.prompt(), "inputType").startsWith("InputPayMana"));
            assertTrue(game.answer(payment, Map.of("action", "ok")));
            awaitPayment(game);
            // until deliberately rejects unhandled player inputs: the search
            // cannot insert a redundant mandatory singleton player picker.
            var offer = game.until(i -> text(i.prompt(), "inputType").equals("InputConfirm"));
            assertEquals(offer.seat(), 0);
            assertSearch(offer.prompt(), owner, "Battlefield", "");
            assertEquals(text(offer.prompt().getAsJsonObject("sourceCard"), "name"), "Solemn Simulacrum");
            assertEquals(text(offer.prompt(), "ok"), "Search library");
            assertEquals(text(offer.prompt(), "cancel"), "Don’t search");
            assertPrivateOwner(game, owner, "confirm");
            int shuffles = eventCount(offer.state(), "shuffle");
            assertTrue(game.answer(offer, Map.of("action", "cancel")));
            var done = game.until(i -> text(i.prompt(), "inputType").equals("InputPassPriority")
                    && i.state().getAsJsonArray("stack").isEmpty());
            assertEquals(eventCount(done.state(), "shuffle"), shuffles, "Declining the optional trigger does not search or shuffle");
        }
    }

    private static void assertSearch(JsonObject prompt, Integer owner, String destination, String cardName) {
        var search = prompt.getAsJsonObject("librarySearch");
        assertNotNull(search, "The engine identifies the search separately from player targeting");
        if (owner == null) assertTrue(search.get("libraryOwnerId").isJsonNull(), "Targeting does not imply a chosen library");
        else assertEquals(search.get("libraryOwnerId").getAsInt(), owner.intValue());
        assertEquals(text(search, "destination"), destination);
        assertEquals(text(search, "cardName"), cardName);
        assertFalse(prompt.has("libraryCards"), "Search permission has not yet been accepted");
        assertFalse(search.has("available"));
        assertFalse(search.has("count"), "Public search intent never examines a hidden library");
    }

    private static void assertPrivateOwner(RulesScenario game, int owner, String actionClass) {
        for (int seat = 0; seat < 4; seat++) {
            var state = game.state(seat);
            assertEquals(state.get("decisionOwnerId").getAsInt(), owner);
            assertEquals(text(state, "actionClass"), actionClass);
            if (state.get("viewerId").getAsInt() != owner) assertTrue(state.get("prompt").isJsonNull(), "Private decision contents stay with the chooser");
        }
    }

    private static int eventCount(JsonObject state, String kind) {
        return state.getAsJsonArray("activity").asList().stream().map(e -> e.getAsJsonObject())
                .filter(e -> text(e, "kind").equals(kind)).mapToInt(e -> e.get("count").getAsInt()).sum();
    }

    private static void awaitPayment(RulesScenario game) throws Exception {
        long deadline = System.nanoTime() + java.time.Duration.ofSeconds(5).toNanos();
        JsonObject last;
        do {
            last = game.state(0);
            var prompt = last.get("prompt");
            if (!last.getAsJsonArray("stack").isEmpty() || prompt != null && !prompt.isJsonNull()
                    && !text(prompt.getAsJsonObject(), "inputType").startsWith("InputPayMana")) return;
            Thread.sleep(5);
        } while (System.nanoTime() < deadline);
        throw new AssertionError("One accepted Auto must finish before another semantic decision: " + last);
    }
}
