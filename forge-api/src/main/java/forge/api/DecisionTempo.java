package forge.api;

import java.util.*;
import java.util.function.LongSupplier;

/** Monotonic decision time, independent of rules objects, transport and rendering. */
final class DecisionTempo {
    record Settings(String preset, String expiry, int seconds, int graceSeconds, String revision) {
        static Settings off() { return preset("off", "pass"); }
        static Settings preset(String preset, String expiry) {
            if (!List.of("pass", "loss", "warn").contains(expiry)) throw new IllegalArgumentException("Unknown timeout behavior");
            int seconds = switch (preset) {
                case "off" -> 0; case "relaxed" -> 120; case "standard" -> 60; case "fast" -> 30;
                default -> throw new IllegalArgumentException("Unknown tempo preset");
            };
            return new Settings(preset, expiry, seconds, 60, UUID.randomUUID().toString());
        }
        boolean enabled() { return seconds > 0; }
        Map<String, Object> view() { return Map.of("preset", preset, "expiry", expiry, "seconds", seconds,
                "graceSeconds", graceSeconds, "revision", revision); }
    }
    enum Outcome { PASS, LOSS, WARNING }
    record Expired(int player, Object episode, Outcome outcome) { }
    private static final class Clock {
        Object episode;
        String id = "";
        long elapsed, updated, preparation;
        boolean priority, ready, waiting, expired;
        int strikes;
    }
    private final Settings settings;
    private final LongSupplier time;
    private final Map<Integer, Clock> clocks = new LinkedHashMap<>();
    private final Set<Integer> timedOutPlayers = new LinkedHashSet<>();
    DecisionTempo(Settings settings, LongSupplier time) { this.settings = settings; this.time = time; }
    boolean enabled() { return settings.enabled(); }
    void lost(int player) { timedOutPlayers.add(player); }

    void observe(int player, Object episode, boolean priority) {
        if (!settings.enabled()) return;
        long now = time.getAsLong();
        var clock = clocks.computeIfAbsent(player, ignored -> new Clock());
        advance(clock, now);
        if (episode != null && clock.episode != episode) {
            clock.episode = episode; clock.id = UUID.randomUUID().toString();
            clock.elapsed = 0; clock.ready = false; clock.expired = false; clock.preparation = 0;
        }
        if (episode != null && clock.priority != priority && settings.expiry.equals("pass")) clock.expired = false;
        clock.waiting = episode != null;
        if (episode != null) clock.priority = priority;
        clock.updated = now;
    }
    boolean presented(int player, String id) {
        var clock = clocks.get(player);
        if (clock == null || !clock.waiting || clock.expired || !clock.id.equals(id)) return false;
        advance(clock, time.getAsLong());
        clock.ready = true;
        return true;
    }
    void activity(int player) { var clock = clocks.get(player); if (clock != null) clock.strikes = 0; }
    private void advance(Clock clock, long now) {
        if (clock.waiting && !clock.expired) {
            // A bounded presentation allowance excludes initial loading and card
            // motion. Only the authenticated seat can start its decision sooner.
            long delta = Math.max(0, now - clock.updated);
            if (!clock.ready) {
                long allowance = Math.min(delta, Math.max(0, 30_000 - clock.preparation));
                clock.preparation += allowance;
                delta -= allowance;
                clock.ready = clock.preparation >= 30_000;
            }
            if (clock.ready) clock.elapsed += delta;
        }
        clock.updated = now;
    }
    List<Expired> expired() {
        var result = new ArrayList<Expired>();
        for (var entry : clocks.entrySet()) {
            var clock = entry.getValue(); advance(clock, time.getAsLong());
            long limit = settings.seconds * 1000L;
            if (!clock.waiting || clock.expired || !clock.ready || clock.elapsed < limit) continue;
            if (settings.expiry.equals("pass") && !clock.priority && clock.elapsed < limit + settings.graceSeconds * 1000L) continue;
            clock.expired = true;
            Outcome outcome = settings.expiry.equals("warn") ? Outcome.WARNING
                    : settings.expiry.equals("loss") || !clock.priority || ++clock.strikes >= 3 ? Outcome.LOSS : Outcome.PASS;
            result.add(new Expired(entry.getKey(), clock.episode, outcome));
        }
        return result;
    }
    Map<String, Object> view() {
        return view(true);
    }
    Map<String, Object> view(boolean active) {
        var rows = new ArrayList<Object>();
        for (var entry : clocks.entrySet()) {
            var clock = entry.getValue();
            if (!active || !clock.waiting) continue;
            advance(clock, time.getAsLong());
            long base = settings.seconds * 1000L;
            boolean grace = settings.expiry.equals("pass") && !clock.priority && clock.elapsed >= base;
            long limit = grace ? settings.graceSeconds * 1000L : base;
            long remaining = Math.max(0, limit - (grace ? clock.elapsed - base : clock.elapsed));
            rows.add(Map.of("playerId", entry.getKey(), "id", clock.id, "phase", clock.expired ? "expired" : !clock.ready ? "preparing" : grace ? "grace" : "thinking",
                    "remainingMs", remaining, "limitMs", limit, "strikes", clock.strikes));
        }
        return Map.of("settings", settings.view(), "clocks", rows, "timedOutPlayers", new ArrayList<>(timedOutPlayers));
    }
}
