package forge.api;

import org.testng.annotations.Test;
import java.util.*;
import java.util.concurrent.atomic.AtomicLong;
import static org.testng.Assert.*;

public class DecisionTempoTest {
    @SuppressWarnings("unchecked")
    private static Map<String, Object> clock(DecisionTempo tempo, int player) {
        return ((List<Map<String, Object>>)tempo.view().get("clocks")).stream()
                .filter(row -> row.get("playerId").equals(player)).findFirst().orElseThrow();
    }
    private static void present(DecisionTempo tempo, int player) {
        assertTrue(tempo.presented(player, (String)clock(tempo, player).get("id")));
    }
    @Test public void readsRefreshesPartialSelectionsAndProcessingCannotRefundTime() {
        var time = new AtomicLong(); var tempo = new DecisionTempo(DecisionTempo.Settings.preset("standard", "pass"), time::get);
        Object input = new Object(); tempo.observe(4, input, false);
        time.set(12_000); present(tempo, 4);
        time.set(32_000); tempo.observe(4, input, false);
        assertEquals(clock(tempo, 4).get("remainingMs"), 40_000L);
        assertTrue(tempo.presented(4, (String)clock(tempo, 4).get("id")));
        tempo.observe(4, null, false); time.set(900_000); assertTrue(((List<?>)tempo.view().get("clocks")).isEmpty());
        tempo.observe(4, input, false); assertEquals(clock(tempo, 4).get("remainingMs"), 40_000L);
        time.addAndGet(40_000); assertEquals(clock(tempo, 4).get("phase"), "grace");
        assertTrue(tempo.expired().isEmpty(), "Required choices must never be guessed on the base deadline");
        time.addAndGet(60_000); assertEquals(tempo.expired().get(0).outcome(), DecisionTempo.Outcome.LOSS);
        assertTrue(tempo.expired().isEmpty(), "A timeout applies once");
        tempo.lost(4);
        assertTrue(((List<?>)tempo.view(false).get("clocks")).isEmpty());
        assertEquals(tempo.view(false).get("timedOutPlayers"), List.of(4), "Terminal observations retain the confirmed loss reason without a running clock");
    }
    @Test public void presentationIsBoundedAndOtherSeatsCannotArmOrResetAClock() {
        var time = new AtomicLong(); var tempo = new DecisionTempo(DecisionTempo.Settings.preset("fast", "loss"), time::get);
        tempo.observe(7, new Object(), true); String id = (String)clock(tempo, 7).get("id");
        assertFalse(tempo.presented(8, id)); assertFalse(tempo.presented(7, "old-id"));
        time.set(29_999); assertTrue(tempo.expired().isEmpty()); assertEquals(clock(tempo, 7).get("phase"), "preparing");
        time.set(35_000); assertEquals(clock(tempo, 7).get("remainingMs"), 25_000L);
        assertTrue(tempo.presented(7, id)); assertEquals(clock(tempo, 7).get("remainingMs"), 25_000L);
        time.set(60_000); assertEquals(tempo.expired().get(0).outcome(), DecisionTempo.Outcome.LOSS);
    }
    @Test public void nestedChoicesAndCancellationKeepTheirParentBudgetAndRequiredChoiceGrace() {
        var time = new AtomicLong(); var tempo = new DecisionTempo(DecisionTempo.Settings.preset("fast", "pass"), time::get);
        Object root = new Object(); tempo.observe(1, root, true); present(tempo, 1);
        time.set(20_000); tempo.observe(1, root, false);
        time.set(25_000); tempo.observe(1, root, true);
        assertEquals(clock(tempo, 1).get("remainingMs"), 5_000L, "Cancelling a menu cannot buy a fresh decision budget");
        time.set(30_000); assertEquals(tempo.expired().get(0).outcome(), DecisionTempo.Outcome.PASS);
        // A normal pass can itself need confirmation of floating mana loss.
        tempo.observe(1, null, false); time.addAndGet(3_000); tempo.observe(1, root, false);
        assertEquals(clock(tempo, 1).get("phase"), "grace");
        assertEquals(clock(tempo, 1).get("remainingMs"), 60_000L);
        assertTrue(tempo.expired().isEmpty());
        time.addAndGet(60_000); assertEquals(tempo.expired().get(0).outcome(), DecisionTempo.Outcome.LOSS);
    }
    @Test public void priorityTimeoutPassesButRepeatedUnattendedWindowsLoseTheSeat() {
        var time = new AtomicLong(); var tempo = new DecisionTempo(DecisionTempo.Settings.preset("fast", "pass"), time::get);
        for (int window = 1; window <= 3; window++) {
            var input = new Object(); tempo.observe(1, input, true); present(tempo, 1);
            time.addAndGet(30_000); var expired = tempo.expired();
            assertEquals(expired.size(), 1); assertSame(expired.get(0).episode(), input);
            assertEquals(expired.get(0).outcome(), window == 3 ? DecisionTempo.Outcome.LOSS : DecisionTempo.Outcome.PASS);
        }
        tempo.activity(1); tempo.observe(1, new Object(), true); present(tempo, 1);
        time.addAndGet(30_000); assertEquals(tempo.expired().get(0).outcome(), DecisionTempo.Outcome.PASS);
    }
    @Test public void independentSimultaneousChoicesWarningsAndOffNeverInventRulesActions() {
        var time = new AtomicLong(); var tempo = new DecisionTempo(DecisionTempo.Settings.preset("fast", "warn"), time::get);
        tempo.observe(1, new Object(), true); tempo.observe(2, new Object(), false); present(tempo, 1);
        time.set(20_000); present(tempo, 2);
        time.set(30_000); var first = tempo.expired();
        assertEquals(first.size(), 1); assertEquals(first.get(0).player(), 1); assertEquals(first.get(0).outcome(), DecisionTempo.Outcome.WARNING);
        assertEquals(clock(tempo, 2).get("remainingMs"), 20_000L);
        time.set(50_000); assertEquals(tempo.expired().get(0).player(), 2);
        var off = new DecisionTempo(DecisionTempo.Settings.off(), time::get);
        off.observe(1, new Object(), true); time.addAndGet(1_000_000);
        assertTrue(off.expired().isEmpty()); assertTrue(((List<?>)off.view().get("clocks")).isEmpty());
        assertThrows(IllegalArgumentException.class, () -> DecisionTempo.Settings.preset("custom", "pass"));
        assertThrows(IllegalArgumentException.class, () -> DecisionTempo.Settings.preset("fast", "guess"));
    }
}
