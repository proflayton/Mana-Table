package forge.api;

import forge.game.combat.CombatInputState;
import org.testng.annotations.Test;
import java.util.*;
import static org.testng.Assert.*;

public class CombatChoiceScopeTest {
    @Test public void onlyTheCurrentOwnedInputSequenceAndRulesVersionCanPublishOffers() {
        var scope = new CombatChoiceScope(); var input = new Object(); var next = new Object();
        var offered = new CombatInputState(false, List.of(), null, Map.of(), List.of(2), Map.of(1, List.of(2)), null);
        scope.bind(input, 7, 12);
        assertNull(scope.current(input, 7, 12, 45), "Unknown is distinct from a certified empty domain");
        assertFalse(scope.publish(next, 7, 12, 45, offered));
        assertFalse(scope.publish(input, 8, 12, 45, offered));
        assertFalse(scope.publish(input, 7, 11, 45, offered));
        assertTrue(scope.publish(input, 7, 12, 45, offered));
        assertSame(scope.current(input, 7, 12, 45), offered);
        assertNull(scope.current(input, 7, 12, 46), "A rules mutation invalidates offers even when the same input and sequence remain");
        assertFalse(scope.publish(input, 7, 11, 46, offered));
        assertSame(scope.current(input, 7, 12, 45), offered, "A stale callback cannot clear a newer coherent publication");
        scope.invalidate(); assertNull(scope.current(input, 7, 12, 45), "Every accepted staging command invalidates the old pair map");
        assertTrue(scope.publish(input, 7, 12, 46, offered)); assertSame(scope.current(input, 7, 12, 46), offered);
        scope.bind(next, 7, 13); assertNull(scope.current(next, 7, 13, 46));
        assertFalse(scope.publish(input, 7, 12, 46, offered));
        var empty = new CombatInputState(false, List.of(), null, Map.of(), List.of(), Map.of(), null);
        assertTrue(scope.publish(next, 7, 13, 46, empty)); assertSame(scope.current(next, 7, 13, 46), empty);
        scope.clear(); assertNull(scope.current(next, 7, 13, 46));
    }
}
