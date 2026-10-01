package forge.player;

import forge.game.player.PlayerView;
import forge.gamemodes.match.input.InputPassPriority;
import forge.gamemodes.match.input.InputSyncronizedBase;
import forge.gui.GuiBase;
import forge.gui.interfaces.IGuiBase;
import org.testng.annotations.Test;

import static org.mockito.Mockito.*;
import static org.testng.Assert.*;

public class AutomaticPriorityShould {
    @Test public void rejectUnverifiedAvailableOldForeignAndDuplicatePasses() {
        IGuiBase original = GuiBase.getInterface();
        GuiBase.setInterface(mock(IGuiBase.class));
        try {
            var game = new HumanControlledGame();
            var viewer = game.human.getView();
            var proxy = game.controller.getInputProxy();
            var queue = game.controller.getInputQueue();
            assertFalse(new InputPassPriority(game.controller).canAutoPass(), "An unscanned input defaults to false");
            var first = mock(InputPassPriority.class);
            when(first.getOwner()).thenReturn(viewer);
            queue.setInput(first);

            assertFalse(proxy.passPriorityIfNoResponse(1), "An unverified input cannot authorize Auto");
            when(first.canAutoPass()).thenReturn(true);
            viewer.setHasAvailableActions(true);
            assertFalse(proxy.passPriorityIfNoResponse(1), "Host must reject a stale client permission");
            verify(first, never()).passPriority();
            viewer.setHasAvailableActions(false);
            assertTrue(proxy.passPriorityIfNoResponse(1));
            assertFalse(proxy.passPriorityIfNoResponse(1), "A delayed completion cannot consume a second pass");
            verify(first, times(1)).passPriority();

            queue.removeInput(first);
            var second = mock(InputPassPriority.class);
            when(second.getOwner()).thenReturn(new PlayerView(999, null));
            when(second.canAutoPass()).thenReturn(true);
            queue.setInput(second); // sequence 3; sequence 2 was the locked input
            assertFalse(proxy.passPriorityIfNoResponse(1), "A late request cannot pass a newer priority window");
            assertFalse(proxy.passPriorityIfNoResponse(3), "A controller cannot pass another player's input");
            verify(second, never()).passPriority();
            when(second.getOwner()).thenReturn(viewer);
            viewer.setHasAvailableActions(true);
            assertFalse(proxy.passPriorityIfNoResponse(3));
            viewer.setHasAvailableActions(false);
            assertTrue(proxy.passPriorityIfNoResponse(3));
            verify(second, times(1)).passPriority();

            queue.removeInput(second);
            var choice = mock(InputSyncronizedBase.class);
            when(choice.getOwner()).thenReturn(viewer);
            queue.setInput(choice);
            assertFalse(proxy.passPriorityIfNoResponse(5), "Required choices never become automatic passes");
        } finally {
            GuiBase.setInterface(original);
        }
    }
}
