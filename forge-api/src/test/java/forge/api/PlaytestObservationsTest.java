package forge.api;

import com.google.gson.JsonObject;
import forge.StaticData;
import forge.deck.Deck;
import forge.game.GameStage;
import forge.game.card.Card;
import forge.game.card.CardView;
import forge.game.event.*;
import forge.game.player.PlayerView;
import forge.game.spellability.SpellAbilityStackInstance;
import forge.game.zone.ZoneType;
import forge.trackable.TrackableProperty;
import forge.util.Localizer;
import org.testng.annotations.BeforeClass;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.List;
import java.util.Map;

import static forge.api.RulesScenario.text;
import static org.testng.Assert.*;

public class PlaytestObservationsTest {
    @BeforeClass public void resources() {
        if (StaticData.instance() == null) EngineResources.load(Path.of("../forge-gui/res"));
        Localizer.getInstance().initialize("en-US", "../forge-gui/res/languages");
    }

    @Test public void aggregateFactsOutliveTheFeedAndExcludeEveryOtherSeat() {
        var viewer = new PlayerView(1, null);
        var opponent = new PlayerView(2, null);
        var card = new CardView(99, null, "An opponent's hidden card");
        card.set(TrackableProperty.Controller, opponent);
        card.set(TrackableProperty.Zone, ZoneType.Hand);
        var activity = new MatchActivity(viewer);
        activity.onGameEvent(new GameEventTurnBegan(viewer, 1));
        for (int i = 0; i < 150; i++) {
            activity.onGameEvent(new GameEventCardDrawn(viewer));
            activity.onGameEvent(new GameEventLandPlayed(viewer, null));
            activity.onGameEvent(new GameEventCardDrawn(opponent));
            activity.onGameEvent(new GameEventLandPlayed(opponent, card));
        }
        activity.onGameEvent(new GameEventMulligan(opponent));
        activity.onGameEvent(new GameEventMulligan(viewer));
        activity.onGameEvent(new GameEventTurnBegan(opponent, 2));
        assertEquals(activity.snapshot().size(), 120);
        assertEquals(activity.observations().get("cardsDrawn"), Integer.valueOf(150));
        assertEquals(activity.observations().get("landsPlayed"), Integer.valueOf(150));
        assertEquals(activity.observations().get("ownTurns"), Integer.valueOf(1));
        assertEquals(activity.observations().get("mulligans"), Integer.valueOf(1));
        assertFalse(activity.observations().toString().contains("hidden card"));
        var retained = activity.observations();
        activity.onGameEvent(new GameEventCardDrawn(viewer));
        assertEquals(retained.get("cardsDrawn"), Integer.valueOf(150));
        assertThrows(UnsupportedOperationException.class, () -> retained.put("cardsDrawn", 0));
    }

    @Test public void realDrawsAreDistinctFromOpeningHandsAndTutors() throws Exception {
        var resources = Path.of("../forge-gui/res");
        var players = List.of(MatchTable.human(new Deck(), "Constructed", "Host"), MatchTable.human(new Deck(), "Constructed", "Guest"));
        try (var table = new MatchTable("Constructed", players, resources, Files.createTempDirectory("mana-draw-facts"))) {
            HeadlessPlatform.activate(table);
            var player = table.game.getPlayers().get(0);
            var activity = new MatchActivity(player.getView());
            table.game.subscribeToEvents(activity);
            for (int i = 0; i < 10; i++) {
                var card = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard("Forest"), player);
                player.getZone(ZoneType.Library).add(card, null, null);
            }
            table.game.setAge(GameStage.Mulligan);
            player.drawCards(7);
            assertEquals(activity.observations().get("cardsDrawn"), Integer.valueOf(0), "Opening hands are not rules draws");
            table.game.setAge(GameStage.Play);
            table.game.getAction().moveToHand(player.getZone(ZoneType.Library).get(0), null);
            assertEquals(activity.observations().get("cardsDrawn"), Integer.valueOf(0), "A tutor moving library to hand is not a draw");
            player.drawCard();
            assertEquals(activity.observations().get("cardsDrawn"), Integer.valueOf(1));
            player.drawCards(2);
            assertEquals(activity.observations().get("cardsDrawn"), Integer.valueOf(2), "An empty library cannot manufacture another successful draw");
            var shock = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard("Shock"), player);
            var spell = shock.getSpellAbilities().get(0);
            spell.setActivatingPlayer(player);
            var stack = new SpellAbilityStackInstance(spell);
            activity.onGameEvent(new GameEventSpellAbilityCast(spell.getView(), stack.getView(), 0, "", true));
            assertEquals(activity.observations().get("spellsCast"), Integer.valueOf(0), "A copied spell is not cast");
            activity.onGameEvent(new GameEventSpellAbilityCast(spell.getView(), stack.getView(), 0, "", false));
            assertEquals(activity.observations().get("spellsCast"), Integer.valueOf(1));
            assertFalse(activity.observations().toString().contains("Forest"));
        }
    }

    @Test(timeOut = 90000) public void concedingPersistsTerminalFactsWithoutAnyClientPoll() throws Exception {
        Path directory = Files.createTempDirectory("mana-observed-concede");
        var store = new DeckPlaytests(directory);
        var test = store.begin("deck", "immutable-version", "Deck", "Commander", List.of(), "game", "Opening land test", List.of("green"));
        try (var scenario = new RulesScenario("playtest-observations-concede", Map.of())) {
            store.attach(test.id(), scenario.table.seat(0));
            var input = scenario.until(i -> i.seat() == 0 && text(i.state(), "phaseKey").equals("MAIN1"));
            var before = store.get("deck", test.id());
            assertEquals(before.observations().get("ownTurns"), Integer.valueOf(1));
            assertEquals(before.observations().get("cardsDrawn"), Integer.valueOf(1), "Commander multiplayer first player draws on turn one");
            var request = new JsonObject();
            request.addProperty("sessionId", text(input.state(), "id"));
            scenario.table.seat(0).concede(request);
            assertEquals(store.get("deck", test.id()).status(), "conceded");
            var restarted = new DeckPlaytests(directory);
            assertEquals(restarted.get("deck", test.id()).status(), "conceded");
            assertEquals(restarted.get("deck", test.id()).versionId(), "immutable-version");
            assertNotNull(restarted.get("deck", test.id()).ended());
        }
    }
}
