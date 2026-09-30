package forge.api;

import forge.deck.Deck;
import forge.game.Game;
import forge.game.GameRules;
import forge.game.GameType;
import forge.game.Match;
import forge.game.phase.PhaseType;
import forge.game.player.Player;
import forge.game.player.PlayerView;
import forge.game.player.RegisteredPlayer;
import forge.player.LobbyPlayerHuman;
import forge.player.PlayerControllerHuman;
import forge.trackable.TrackableCollection;
import forge.trackable.TrackableProperty;
import forge.util.Lang;
import forge.util.Localizer;
import org.testng.annotations.Test;

import java.util.List;
import java.util.Map;

import static org.testng.Assert.*;

public class NetworkAutoPassTest {
    @Test public void usesLiveAvailabilityAfterOpeningWithADetachedNetworkPlayer() {
        Localizer.getInstance().initialize("en-US", "../forge-gui/res/languages");
        Lang.createInstance("en-US");
        var seats = List.of(seat("Host"), seat("Guest"));
        var rules = new GameRules(GameType.Constructed);
        var game = new Game(seats, rules, new Match(rules, seats, "Auto regression"));
        var guest = game.getPlayers().get(1);
        var live = guest.getView();
        var view = game.getView();
        view.set(TrackableProperty.Turn, 1);
        view.set(TrackableProperty.Phase, PhaseType.MAIN1);
        view.set(TrackableProperty.PlayerTurn, live);

        var session = new NetworkMatchSession();
        session.setGameView(view);
        session.setOriginalGameController(live, (PlayerControllerHuman) guest.getController());
        // openView sends full serialized players, not the tracker-owned instances
        // that applyDelta subsequently updates.
        var openingSnapshot = new PlayerView(live.getId(), game.getTracker());
        var localPlayers = new TrackableCollection<PlayerView>();
        localPlayers.add(openingSnapshot);
        session.openView(localPlayers);

        live.setHasAvailableActions(true);
        showPriority(session, live, 1);
        assertEquals(prompt(session).get("canAutoPass"), false,
                "A new playable action must stop Auto even if the opening snapshot says false");
        live.setHasAvailableActions(false);
        showPriority(session, live, 2);
        assertEquals(prompt(session).get("canAutoPass"), true,
                "Auto must resume once the live player has no actions");
        live.setHasAvailableActions(true);
        showPriority(session, live, 3);
        assertEquals(prompt(session).get("canAutoPass"), false,
                "Future action availability must continue to update");
        live.setHasAvailableActions(false);
        session.setInputState(live, "InputPassPriority", 4, true, false);
        session.updateButtons(live, "Continue", "Cancel", true, true, true);
        session.publishInput();
        assertEquals(prompt(session).get("canAutoPass"), false, "No host permission means no automatic pass");
        session.setInputState(live, "InputSelectTargets", 5, true, true);
        session.updateButtons(live, "Continue", "Cancel", true, true, true);
        session.publishInput();
        assertEquals(prompt(session).get("canAutoPass"), false, "Required choices ignore contradictory permission");
    }

    private static void showPriority(NetworkMatchSession session, PlayerView player, long sequence) {
        session.setInputState(player, "InputPassPriority", sequence, true, true);
        session.updateButtons(player, "Continue", "Cancel", true, true, true);
        session.publishInput();
    }

    @SuppressWarnings("unchecked")
    private static Map<String, Object> prompt(NetworkMatchSession session) {
        var prompt = (Map<String, Object>) session.state().get("prompt");
        assertNotNull(prompt);
        return prompt;
    }

    private static RegisteredPlayer seat(String name) {
        return new RegisteredPlayer(new Deck()).setPlayer(new LobbyPlayerHuman(name) {
            @Override public Player createIngamePlayer(Game game, int id) {
                var player = new Player(getName(), game, id);
                player.setFirstController(new PlayerControllerHuman(game, player, this));
                return player;
            }
        });
    }
}
