package forge.api;

import forge.StaticData;
import forge.card.CardRarity;
import forge.card.CardRules;
import forge.deck.DeckSection;
import forge.game.player.RegisteredPlayer;
import forge.item.PaperCard;
import forge.game.ExecutionTracker;
import forge.game.card.CardView;
import forge.game.event.GameEventCardChangeZone;
import forge.game.zone.ZoneType;
import com.google.common.eventbus.Subscribe;
import com.google.gson.Gson;

import java.nio.file.*;
import java.util.*;

/** Private test-classpath launcher only. Setup is synthetic; all subsequent
 * observations, option authority, IPC and zone-change resolution are production.
 * No production command, deck name or process flag enables this fixture. */
public final class LibraryAcquisitionFixtureEngine {
    enum Mode { DIRECT_BATTLEFIELD, DIRECT_HAND, BUNDLE, RENAMED_BUNDLE }
    private LibraryAcquisitionFixtureEngine() { }

    public static void main(String[] args) throws Exception { run(args, Mode.BUNDLE); }
    public static final class DirectBattlefield { public static void main(String[] args) throws Exception { run(args, Mode.DIRECT_BATTLEFIELD); } }
    public static final class DirectHand { public static void main(String[] args) throws Exception { run(args, Mode.DIRECT_HAND); } }
    public static final class Bundle { public static void main(String[] args) throws Exception { run(args, Mode.BUNDLE); } }
    public static final class RenamedBundle { public static void main(String[] args) throws Exception { run(args, Mode.RENAMED_BUNDLE); } }

    private static void run(String[] args, Mode mode) throws Exception {
        DesktopEngine.run(args, (players, resources, profile) -> {
            var printing = configure(players, mode);
            var table = new MatchTable("Commander", players, resources, profile, true);
            try { new PrivateHandAudit(table, profile, printing).start(); return table; }
            catch (RuntimeException error) { table.close(); throw error; }
        });
    }

    /** Private test-only measurement. No observer callback performs I/O,
     * serializes, allocates an opaque identity, or changes an execution ACK. */
    private static final class PrivateHandAudit {
        private static final int MAXIMUM_MOVEMENTS = 16;
        private final MatchTable table;
        private final Path file;
        private final PaperCard printing;
        private final Object guard = new Object();
        private final Map<String, Root> roots = new LinkedHashMap<>();
        private final java.lang.reflect.Field identities;
        private final long expires = System.nanoTime() + java.util.concurrent.TimeUnit.SECONDS.toNanos(100);
        private String failure;
        private record Movement(int engineCardId, int cardOwnerId, int fromOwnerId, int toOwnerId,
                                String fromZone, String toZone, String executionId, String budgetRootId) { }
        private static final class Root {
            final String id; final int sourceEngineCardId; final int actorPlayerId;
            final List<Movement> movements = new ArrayList<>(); boolean completed;
            Root(String id, int source, int actor) { this.id = id; sourceEngineCardId = source; actorPlayerId = actor; }
        }
        private record PendingRoot(String id, int sourceEngineCardId, int actorPlayerId, boolean completed, List<Movement> movements) { }

        PrivateHandAudit(MatchTable table, Path profile, PaperCard printing) {
            this.table = table; this.printing = printing; file = profile.resolve("private-library-acquisition-audit.json");
            // Test-only read access: unlike id(), Map.get never creates a new
            // occurrence. A changed private implementation fails this fixture.
            try { identities = MatchIdentities.class.getDeclaredField("ids"); identities.setAccessible(true); }
            catch (ReflectiveOperationException error) { throw new IllegalStateException("The private fixture cannot read the existing identity registry.", error); }
        }
        void start() {
            table.game.subscribeToEvents(this);
            var writer = new Thread(this::writeWhenCompleted, "Private acquisition audit"); writer.setDaemon(true); writer.start();
        }

        @Subscribe public void committed(ExecutionTracker.Committed committed) {
            try {
                if (!(committed.event() instanceof GameEventCardChangeZone movement) || committed.execution() == null
                        || movement.from() == null || movement.to() == null || movement.from().zoneType() != ZoneType.Library
                        || movement.to().zoneType() != ZoneType.Hand) return;
                var occurrence = table.game.getExecutions().occurrence(committed.execution().id());
                var root = occurrence == null ? null : table.game.getExecutions().occurrence(occurrence.budgetRootId());
                if (!fixtureRoot(root)) return;
                var from = movement.from().player(); var to = movement.to().player(); var owner = movement.card().getOwner();
                if (from == null || to == null || owner == null || from.getId() != root.actorPlayerId()
                        || to.getId() != root.actorPlayerId() || owner.getId() != root.actorPlayerId()) { failed("The private hand movement crossed its fixture owner."); return; }
                synchronized (guard) {
                    Root entry = root(root);
                    if (entry.movements.size() >= MAXIMUM_MOVEMENTS) { failure = "Private hand audit exceeded its finite movement capacity."; return; }
                    entry.movements.add(new Movement(movement.card().getId(), owner.getId(), from.getId(), to.getId(),
                            "Library", "Hand", committed.execution().id(), root.id()));
                }
            } catch (Exception error) { failed("Private hand callback unavailable: " + error.getClass().getSimpleName()); }
        }

        @Subscribe public void transitioned(ExecutionTracker.Transition transition) {
            try {
                var occurrence = transition.occurrence();
                if (!occurrence.id().equals(occurrence.budgetRootId()) || !"resolved".equals(occurrence.outcome()) || !fixtureRoot(occurrence)) return;
                synchronized (guard) { root(occurrence).completed = true; }
            } catch (Exception error) { failed("Private completion callback unavailable: " + error.getClass().getSimpleName()); }
        }

        private boolean fixtureRoot(ExecutionTracker.Occurrence root) {
            if (root == null || root.actorPlayerId() == null || root.source() == null || !"activation".equals(root.kind())) return false;
            var actor = table.game.getRegisteredPlayers().get(0);
            if (root.actorPlayerId() != actor.getId()) return false;
            return actor.getCardsIn(ZoneType.Battlefield).stream().anyMatch(card -> card.getView().equals(root.source())
                    && card.getPaperCard() != null && card.getPaperCard().getRules() == printing.getRules());
        }

        private Root root(ExecutionTracker.Occurrence occurrence) {
            var entry = roots.get(occurrence.id());
            if (entry == null) {
                if (roots.size() >= 2) throw new IllegalStateException("Private fixture received too many roots");
                entry = new Root(occurrence.id(), occurrence.source().getId(), occurrence.actorPlayerId()); roots.put(occurrence.id(), entry);
            }
            return entry;
        }
        private void failed(String reason) { synchronized (guard) { if (failure == null) failure = reason; } }

        private void writeWhenCompleted() {
            String terminalFailure = null;
            try {
                while (System.nanoTime() < expires && !Thread.currentThread().isInterrupted()) {
                    List<PendingRoot> pending;
                    synchronized (guard) {
                        if (failure != null) { terminalFailure = failure; break; }
                        pending = roots.values().stream().filter(root -> root.completed).map(root -> new PendingRoot(root.id,
                                root.sourceEngineCardId, root.actorPlayerId, true, List.copyOf(root.movements))).toList();
                    }
                    if (!pending.isEmpty()) {
                        Object proof = table.game.getAction().readState(() -> {
                            synchronized (table.gate) { return bridge(pending); }
                        });
                        if (proof != null) { publish(proof); return; }
                    }
                    if (table.game.isGameOver()) { terminalFailure = "The fixture ended before an owned identity bridge was available."; break; }
                    Thread.sleep(20);
                }
                if (terminalFailure == null) terminalFailure = "The finite private audit deadline expired without a complete owned identity bridge.";
            } catch (Exception error) { terminalFailure = "Private audit writer unavailable: " + error.getClass().getSimpleName(); }
            finally { table.game.unsubscribeFromEvents(this); }
            try { publish(Map.of("schemaVersion", 1, "kind", "private-fixture-library-acquisition-audit", "gameId", table.id,
                    "complete", false, "error", terminalFailure, "roots", List.of())); }
            catch (Exception ignored) { System.err.println("Private acquisition audit could not be preserved."); }
        }

        @SuppressWarnings("unchecked") private Object bridge(List<PendingRoot> pending) {
            var results = new ArrayList<Object>();
            for (var root : pending) {
                var actor = table.game.getRegisteredPlayers().stream().filter(player -> player.getId() == root.actorPlayerId).findFirst().orElseThrow();
                var state = table.automatedSeat(actor.getId()).state();
                if (!table.id.equals(state.get("id")) || !"playing".equals(state.get("status"))) return null;
                var player = ((List<Map<String, Object>>)state.get("players")).stream().filter(value -> Objects.equals(value.get("id"), actor.getId())).findFirst().orElseThrow();
                var zones = (List<Map<String, Object>>)player.get("zones");
                var hand = zones.stream().filter(value -> "Hand".equals(value.get("name"))).findFirst().orElseThrow();
                var field = zones.stream().filter(value -> "Battlefield".equals(value.get("name"))).findFirst().orElseThrow();
                var handIds = visibleIds(hand); var fieldIds = visibleIds(field);
                if (((Number)hand.get("count")).intValue() != actor.getCardsIn(ZoneType.Hand).size()
                        || handIds.size() != actor.getCardsIn(ZoneType.Hand).size()) return null;
                var source = actor.getCardsIn(ZoneType.Battlefield).stream().filter(card -> card.getId() == root.sourceEngineCardId).findFirst().orElse(null);
                if (source == null) return null;
                String sourceNative = existingId(source.getView());
                if (sourceNative == null || !fieldIds.contains(sourceNative)) return null;
                var movements = new ArrayList<Object>();
                for (var movement : root.movements) {
                    var actual = actor.getCardsIn(ZoneType.Hand).stream().filter(card -> card.getId() == movement.engineCardId).findFirst().orElse(null);
                    if (actual == null || actual.getZone().getPlayer() != actor) return null;
                    String nativeId = existingId(actual.getView());
                    if (nativeId == null || !handIds.contains(nativeId)) return null;
                    movements.add(Map.of("engineCardId", movement.engineCardId, "nativeCardId", nativeId,
                            "cardOwnerId", movement.cardOwnerId, "fromOwnerId", movement.fromOwnerId, "toOwnerId", movement.toOwnerId,
                            "fromZone", movement.fromZone, "toZone", movement.toZone, "executionId", movement.executionId,
                            "budgetRootId", movement.budgetRootId));
                }
                results.add(Map.of("sourceExecutionId", root.id, "sourceEngineCardId", root.sourceEngineCardId,
                        "sourceNativeCardId", sourceNative, "actorPlayerId", root.actorPlayerId, "resolved", true,
                        "bridgeBoardRevision", state.get("boardRevision"), "handMovements", List.copyOf(movements)));
            }
            return Map.of("schemaVersion", 1, "kind", "private-fixture-library-acquisition-audit", "gameId", table.id,
                    "complete", true, "error", "", "roots", List.copyOf(results));
        }

        @SuppressWarnings("unchecked") private String existingId(CardView card) {
            synchronized (table.identities) {
                try { return ((Map<CardView, String>)identities.get(table.identities)).get(card); }
                catch (IllegalAccessException error) { throw new IllegalStateException(error); }
            }
        }
        @SuppressWarnings("unchecked") private static Set<String> visibleIds(Map<String, Object> zone) {
            var ids = new HashSet<String>();
            for (var card : (List<Map<String, Object>>)zone.get("cards")) {
                Object id = card.get("visualId"); if (id instanceof String text && !text.isEmpty()) ids.add(text);
            }
            return ids;
        }
        private void publish(Object value) throws Exception {
            byte[] bytes = new Gson().toJson(value).getBytes(java.nio.charset.StandardCharsets.UTF_8);
            if (bytes.length > 65536) throw new IllegalStateException("Private audit exceeded its byte cap");
            Files.createDirectories(file.getParent());
            Path staging = Files.createTempFile(file.getParent(), "private-acquisition-audit-", ".tmp");
            try { Files.write(staging, bytes); Files.move(staging, file, StandardCopyOption.ATOMIC_MOVE, StandardCopyOption.REPLACE_EXISTING); }
            finally { Files.deleteIfExists(staging); }
        }
    }

    static PaperCard configure(List<RegisteredPlayer> players, Mode mode) {
        if (players.size() != 4 || players.stream().anyMatch(player -> !(player.getPlayer() instanceof AutomatedLobbyPlayer)))
            throw new IllegalArgumentException("The private library-acquisition fixture requires four automated players");
        var paper = paper(mode);
        StaticData.instance().getCommonCards().addCard(paper);
        for (var player : players) {
            player.getDeck().getMain().clear();
            player.getDeck().getMain().add(StaticData.instance().getCommonCards().getCard("Plains"), 99);
        }
        players.get(0).addExtraCardsOnBattlefield(List.of(paper));
        var powerPlay = StaticData.instance().getVariantCards().getCard("Power Play");
        if (powerPlay == null) throw new IllegalArgumentException("Missing private fixture starting-player card");
        players.get(0).getDeck().getOrCreate(DeckSection.Conspiracy).add(powerPlay);
        players.get(0).assignConspiracies();
        return paper;
    }

    static PaperCard paper(Mode mode) {
        String name = mode == Mode.RENAMED_BUNDLE ? "Unrelated lantern fixture" : "Library acquisition fixture " + mode;
        String root = switch (mode) {
            case DIRECT_BATTLEFIELD -> "AB$ ChangeZone | Cost$ 0 | Origin$ Library | Destination$ Battlefield | ChangeType$ Plains,Island,Swamp,Mountain | Tapped$ True | ChangeNum$ 1 | SpellDescription$ Unrelated text.";
            case DIRECT_HAND -> "AB$ ChangeZone | Cost$ 0 | Origin$ Library | Destination$ Hand | ChangeType$ Land.Basic | ChangeNum$ 1 | SpellDescription$ Unrelated text.";
            case BUNDLE, RENAMED_BUNDLE -> "AB$ ChangeZone | Cost$ 0 | Origin$ Library | Destination$ Library | ChangeType$ Land.Basic | ChangeTypeDesc$ basic land | ChangeNum$ 2 | RememberChanged$ True | Reveal$ True | Shuffle$ False | StackDescription$ SpellDescription | SubAbility$ DBField | SpellDescription$ Unrelated text.";
        };
        String tail = mode == Mode.BUNDLE || mode == Mode.RENAMED_BUNDLE ? "\nSVar:DBField:DB$ ChangeZone | Origin$ Library | Destination$ Battlefield | ChangeType$ Land.IsRemembered | ChangeNum$ 1 | Mandatory$ True | NoLooking$ True | SelectPrompt$ Arbitrary first prompt | Tapped$ True | Shuffle$ False | SubAbility$ DBHand | StackDescription$ None"
                + "\nSVar:DBHand:DB$ ChangeZone | Origin$ Library | Destination$ Hand | ChangeType$ Land.IsRemembered | Mandatory$ True | NoLooking$ True | SelectPrompt$ Arbitrary second prompt | StackDescription$ None | SubAbility$ DBCleanup"
                + "\nSVar:DBCleanup:DB$ Cleanup | ClearRemembered$ True" : "";
        return new PaperCard(new CardRules.Reader().readCard(Arrays.asList(("Name:" + name
                + "\nManaCost:0\nTypes:Artifact\nA:" + root + tail + "\nOracle:Private synthetic acquisition fixture.").split("\n"))), "TST", CardRarity.Common);
    }
}
