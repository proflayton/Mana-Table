package forge.api;

import forge.game.Game;
import forge.game.player.Player;
import forge.game.player.PlayerController;
import forge.player.LobbyPlayerHuman;
import forge.player.PlayerControllerHuman;
import forge.util.GuiDisplayUtil;

import java.util.EnumSet;

/**
 * A host-owned policy seat using the ordinary authoritative input controller.
 * The explicit role is separate from Forge's isAI flag: engine code uses that
 * flag to select (and sometimes cast to) its strategic PlayerControllerAi.
 */
final class AutomatedLobbyPlayer extends LobbyPlayerHuman {
    AutomatedLobbyPlayer(String name) { super(name); }

    @Override
    public Player createIngamePlayer(Game game, int id) {
        var player = new Player(GuiDisplayUtil.personalizeHuman(getName()), game, id);
        player.setFirstController(configure(new PlayerControllerHuman(game, player, this) {
            @Override public boolean isExternallyAutomated() { return true; }
        }));
        return player;
    }

    @Override
    public PlayerController createMindSlaveController(Player master, Player slave) {
        return configure(new PlayerControllerHuman(slave, this, (PlayerControllerHuman) master.getController()) {
            @Override public boolean isExternallyAutomated() { return true; }
        });
    }

    private static PlayerControllerHuman configure(PlayerControllerHuman controller) {
        controller.getFullControl().addAll(EnumSet.of(PlayerController.FullControlFlag.ChooseCostOrder,
                PlayerController.FullControlFlag.ChooseCostReductionOrderAndVariableAmount,
                PlayerController.FullControlFlag.ChooseManaPoolShard,
                PlayerController.FullControlFlag.LayerTimestampOrder));
        return controller;
    }
}
