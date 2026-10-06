package forge.gamemodes.net.server;

import org.testng.annotations.Test;
import static org.testng.Assert.*;

public class PortMappingStateTest {
    @Test public void lateRepliesCannotChangeANewLobbyOrUndoASuccessfulMapping() {
        var state = new PortMappingState();
        assertEquals(state.status(), "disabled");
        long first = state.begin();
        assertEquals(state.status(), "searching");
        state.complete(first, false);
        assertEquals(state.status(), "failed");
        state.complete(first, true);
        assertEquals(state.status(), "mapped", "A slow gateway can succeed after discovery timed out");
        state.complete(first, false);
        assertEquals(state.status(), "mapped");
        state.reset();
        assertFalse(state.complete(first, true));
        assertEquals(state.status(), "disabled");
        long second = state.begin();
        assertFalse(state.complete(first, false));
        assertEquals(state.status(), "searching");
        assertTrue(state.complete(second, false));
        assertEquals(state.status(), "failed");
    }
}
