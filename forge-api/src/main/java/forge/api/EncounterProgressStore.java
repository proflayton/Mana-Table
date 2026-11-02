package forge.api;

import com.google.gson.Gson;
import com.google.gson.JsonParseException;
import java.io.IOException;
import java.nio.ByteBuffer;
import java.nio.channels.FileChannel;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.time.Instant;
import java.util.*;
import java.util.concurrent.*;

/** Host-owned encounter journal. I/O is serialized away from transport and rules monitors. */
final class EncounterProgressStore implements AutoCloseable {
    record Participant(String participantId, int index, int playerId, int teamId, String role, String deckSelectionId) { }
    record AttemptOpened(int schemaVersion, String partyId, String encounterId, String gameId, String rulesetId,
                         int partyTeamId, String matchSetupConsent, String tempoRevision,
                         List<Participant> participants, String openedUtc) {
        AttemptOpened { participants = List.copyOf(participants); }
    }
    record Receipt(String receiptId, String partyId, String encounterId, String gameId, String rulesetId,
                   int partyTeamId, MatchOutcomeProjection.Outcome outcome, List<Participant> participants, String completedUtc) {
        Receipt { participants = List.copyOf(participants); }
    }
    record Progress(String partyId, int completedEncounters, int victories, List<String> appliedGameIds, Receipt lastReceipt) {
        Progress { appliedGameIds = List.copyOf(appliedGameIds); }
    }
    private record StoredReceipt(int schemaVersion, Receipt receipt) { }
    interface Writer { void write(Path target, byte[] bytes) throws IOException; }
    private static final Gson JSON = new Gson();
    private static final ConcurrentMap<Path, Object> LOCKS = new ConcurrentHashMap<>();
    private final Path directory;
    private final Writer writer;
    private final ExecutorService io = Executors.newSingleThreadExecutor(task -> {
        var worker = new Thread(task, "mana-encounter-checkpoint"); worker.setDaemon(true); return worker;
    });
    private final ConcurrentMap<String, Progress> progress = new ConcurrentHashMap<>();
    private volatile boolean recovered;
    private final CompletableFuture<Void> startup;

    EncounterProgressStore(Path profile) { this(profile, EncounterProgressStore::atomicWrite); }
    EncounterProgressStore(Path profile, Writer writer) {
        directory = profile.resolve("encounters-v1"); this.writer = writer;
        startup = CompletableFuture.runAsync(() -> { try { locked(() -> { recover(); return null; }); }
            catch (IOException error) { throw new CompletionException(error); } }, io);
    }
    CompletableFuture<Void> startup() { return startup; }
    Progress current(String partyId) { return progress.getOrDefault(partyId, new Progress(partyId, 0, 0, List.of(), null)); }
    /** Admission is not evidence that a turn was played or that another owner is dead. */
    CompletableFuture<List<AttemptOpened>> unresolved(String partyId) {
        return CompletableFuture.supplyAsync(() -> {
            try { return locked(() -> {
                var attempts = loadAttempts(); var resolved = new HashSet<String>();
                for (var path : files(directory.resolve("receipts"))) resolved.add(readReceipt(path).gameId());
                return attempts.values().stream().filter(value -> value.partyId().equals(partyId) && !resolved.contains(value.gameId()))
                        .sorted(Comparator.comparing(AttemptOpened::openedUtc).thenComparing(AttemptOpened::encounterId)).toList();
            }); } catch (IOException failure) { throw new CompletionException(failure); }
        }, io);
    }
    CompletableFuture<AttemptOpened> openAttempt(AttemptOpened attempt) {
        return CompletableFuture.supplyAsync(() -> {
            try { return locked(() -> {
                if (!recovered) recover();
                validate(attempt);
                var path = attemptPath(attempt.encounterId());
                if (Files.exists(path)) {
                    if (!attempt.equals(read(path, AttemptOpened.class))) throw new IOException("Conflicting encounter admission");
                } else {
                    if (Files.exists(receiptPath(attempt.gameId()))) throw new IOException("Game already has a terminal receipt");
                    var inventory = files(directory.resolve("attempts"));
                    if (inventory.size() >= 10000) throw new IOException("Encounter admission limit reached");
                    for (var existing : inventory) {
                        var prior = read(existing, AttemptOpened.class); validate(prior);
                        if (prior.gameId().equals(attempt.gameId())) throw new IOException("Game already belongs to another admission");
                    }
                    writer.write(path, JSON.toJson(attempt).getBytes(StandardCharsets.UTF_8));
                }
                return attempt;
            }); } catch (IOException error) { throw new CompletionException(error); }
        }, io);
    }
    CompletableFuture<Progress> commit(Receipt receipt) {
        return CompletableFuture.supplyAsync(() -> {
            try { return locked(() -> {
                if (!recovered) recover();
                validate(receipt);
                Files.createDirectories(directory.resolve("receipts"));
                Files.createDirectories(directory.resolve("parties"));
                // A game has exactly one immutable receipt, even if a caller
                // tries another party/encounter ID after a crash or retry.
                var path = receiptPath(receipt.gameId());
                if (Files.exists(path)) {
                    if (!receipt.equals(readReceipt(path))) throw new IOException("Conflicting receipt for the same game");
                } else {
                    verifyAdmission(receipt, true);
                    if (files(directory.resolve("receipts")).size() >= 10000) throw new IOException("Encounter receipt limit reached");
                    writer.write(path, JSON.toJson(new StoredReceipt(2, receipt)).getBytes(StandardCharsets.UTF_8));
                }
                return apply(receipt);
            }); } catch (IOException error) { throw new CompletionException(error); }
        }, io);
    }
    private void recover() throws IOException {
        Files.createDirectories(directory.resolve("receipts")); Files.createDirectories(directory.resolve("parties"));
        Files.createDirectories(directory.resolve("attempts"));
        loadAttempts();
        var loaded = new HashMap<String, Progress>();
        for (var path : files(directory.resolve("parties"))) {
            var value = read(path, Progress.class);
            if (value == null || !validId(value.partyId()) || !path.getFileName().toString().equals(value.partyId() + ".json")
                    || value.completedEncounters() <= 0 || value.victories() < 0 || value.victories() > value.completedEncounters()
                    || value.appliedGameIds() == null || value.appliedGameIds().size() != value.completedEncounters()
                    || value.appliedGameIds().stream().anyMatch(id -> !validId(id))
                    || value.appliedGameIds().stream().distinct().count() != value.appliedGameIds().size())
                throw new IOException("Invalid encounter checkpoint");
            loaded.put(value.partyId(), value);
        }
        var receipts = new ArrayList<Receipt>();
        for (var path : files(directory.resolve("receipts"))) {
            var receipt = readReceipt(path); validate(receipt);
            if (!path.equals(receiptPath(receipt.gameId()))) throw new IOException("Receipt identity differs from its journal entry");
            receipts.add(receipt);
        }
        var byGame = new HashMap<String, Receipt>();
        for (var receipt : receipts) if (byGame.put(receipt.gameId(), receipt) != null) throw new IOException("Duplicate receipt game");
        for (var checkpoint : loaded.values()) {
            int victories = 0;
            for (var id : checkpoint.appliedGameIds()) {
                var receipt = byGame.get(id);
                if (receipt == null || !receipt.partyId().equals(checkpoint.partyId())) throw new IOException("Checkpoint refers to a missing receipt");
                if (receipt.outcome().winningTeamIds().contains(receipt.partyTeamId())) victories++;
            }
            if (victories != checkpoint.victories() || checkpoint.lastReceipt() == null
                    || !checkpoint.lastReceipt().equals(byGame.get(checkpoint.appliedGameIds().get(checkpoint.appliedGameIds().size() - 1))))
                throw new IOException("Checkpoint differs from its durable receipts");
        }
        progress.clear(); progress.putAll(loaded);
        receipts.sort(Comparator.comparing(Receipt::completedUtc).thenComparing(Receipt::gameId));
        for (var receipt : receipts) apply(receipt);
        recovered = true;
    }
    private Map<String, AttemptOpened> loadAttempts() throws IOException {
        var attempts = new HashMap<String, AttemptOpened>(); var admittedGames = new HashSet<String>();
        for (var path : files(directory.resolve("attempts"))) {
            var value = read(path, AttemptOpened.class); validate(value);
            if (!path.equals(attemptPath(value.encounterId())) || attempts.put(value.encounterId(), value) != null
                    || !admittedGames.add(value.gameId())) throw new IOException("Conflicting admission identity");
        }
        return attempts;
    }
    private Progress apply(Receipt receipt) throws IOException {
        var checkpoint = directory.resolve("parties").resolve(receipt.partyId() + ".json");
        var previous = Files.exists(checkpoint) ? read(checkpoint, Progress.class) : current(receipt.partyId());
        if (previous.appliedGameIds().contains(receipt.gameId())) { progress.put(receipt.partyId(), previous); return previous; }
        if (previous.completedEncounters() >= 10000) throw new IOException("Encounter checkpoint limit reached");
        var applied = new ArrayList<>(previous.appliedGameIds()); applied.add(receipt.gameId());
        var next = new Progress(receipt.partyId(), applied.size(), previous.victories()
                + (receipt.outcome().winningTeamIds().contains(receipt.partyTeamId()) ? 1 : 0), applied, receipt);
        writer.write(checkpoint, JSON.toJson(next).getBytes(StandardCharsets.UTF_8));
        progress.put(receipt.partyId(), next);
        return next;
    }
    private Path receiptPath(String gameId) { return directory.resolve("receipts").resolve(gameId + ".json"); }
    private Path attemptPath(String encounterId) { return directory.resolve("attempts").resolve(encounterId + ".json"); }
    private Receipt readReceipt(Path path) throws IOException {
        var value = read(path, com.google.gson.JsonObject.class);
        if (value == null) throw new IOException("Invalid receipt record");
        boolean versioned = value.has("schemaVersion");
        Receipt receipt;
        try {
            if (versioned) {
                var stored = JSON.fromJson(value, StoredReceipt.class);
                if (stored.schemaVersion() != 2 || value.size() != 2) throw new IOException("Unknown receipt schema");
                receipt = stored.receipt();
            } else {
                // Build 337 wrote the raw receipt. Only this explicit legacy
                // reader may accept a terminal record without prior admission.
                if (value.size() != 9) throw new IOException("Invalid legacy receipt schema");
                receipt = JSON.fromJson(value, Receipt.class);
            }
        } catch (JsonParseException | IllegalArgumentException invalid) { throw new IOException("Invalid receipt record", invalid); }
        validate(receipt);
        if (!path.equals(receiptPath(receipt.gameId()))) throw new IOException("Receipt identity differs from its journal entry");
        verifyAdmission(receipt, versioned); return receipt;
    }
    private void verifyAdmission(Receipt receipt, boolean required) throws IOException {
        var path = attemptPath(receipt.encounterId());
        if (!Files.exists(path)) { if (required) throw new IOException("Receipt has no encounter admission"); return; }
        var attempt = read(path, AttemptOpened.class); validate(attempt);
        if (!attempt.partyId().equals(receipt.partyId()) || !attempt.gameId().equals(receipt.gameId())
                || !attempt.rulesetId().equals(receipt.rulesetId()) || attempt.partyTeamId() != receipt.partyTeamId()
                || !attempt.participants().equals(receipt.participants())) throw new IOException("Receipt differs from its admitted encounter");
    }
    private static void validate(AttemptOpened attempt) throws IOException {
        if (attempt == null || attempt.schemaVersion() != 1 || !validId(attempt.partyId()) || !validId(attempt.encounterId())
                || !validId(attempt.gameId()) || !MatchSetupSpecification.COOPERATIVE.equals(attempt.rulesetId())
                || !validId(attempt.matchSetupConsent()) || !validId(attempt.tempoRevision())) throw new IOException("Invalid encounter admission");
        try { Instant.parse(attempt.openedUtc()); } catch (RuntimeException invalid) { throw new IOException("Invalid admission time", invalid); }
        validateParticipants(attempt.participants(), attempt.rulesetId(), attempt.partyTeamId());
    }
    private static boolean validId(String value) {
        try { return value != null && value.matches("[a-f0-9-]{36}") && UUID.fromString(value).toString().equals(value); }
        catch (IllegalArgumentException invalid) { return false; }
    }
    private static void validate(Receipt receipt) throws IOException {
        if (receipt == null || !validId(receipt.receiptId()) || !validId(receipt.partyId()) || !validId(receipt.encounterId()) || !validId(receipt.gameId())
                || !MatchSetupSpecification.COOPERATIVE.equals(receipt.rulesetId()) || receipt.partyTeamId() < 0
                || receipt.outcome() == null || !receipt.gameId().equals(receipt.outcome().gameId())
                || !List.of("win", "draw").contains(receipt.outcome().kind()) || receipt.outcome().ruleReason() == null
                || receipt.outcome().ruleReason().isBlank() || receipt.outcome().ruleReason().equals("AllHumansLost")
                || receipt.participants() == null || receipt.participants().size() < 2 || receipt.participants().size() > 6)
            throw new IOException("Invalid terminal encounter receipt");
        try { Instant.parse(receipt.completedUtc()); } catch (RuntimeException invalid) { throw new IOException("Invalid receipt time", invalid); }
        validateParticipants(receipt.participants(), receipt.rulesetId(), receipt.partyTeamId());
        var outcome = receipt.outcome();
        if (outcome.winningPlayerIds() == null || outcome.winningTeamIds() == null
                || new HashSet<>(outcome.winningPlayerIds()).size() != outcome.winningPlayerIds().size()
                || new HashSet<>(outcome.winningTeamIds()).size() != outcome.winningTeamIds().size()) throw new IOException("Invalid receipt winners");
        var teams = new HashSet<Integer>();
        for (int player : outcome.winningPlayerIds()) {
            var participant = receipt.participants().stream().filter(p -> p.playerId() == player).findFirst().orElseThrow(() -> new IOException("Unknown receipt winner"));
            teams.add(participant.teamId());
        }
        if (!teams.equals(new HashSet<>(outcome.winningTeamIds())) || outcome.kind().equals("win") && teams.isEmpty()
                || outcome.kind().equals("draw") && !teams.isEmpty()) throw new IOException("Invalid receipt winning teams");
    }
    private static void validateParticipants(List<Participant> participants, String ruleset, int partyTeam) throws IOException {
        if (participants == null || participants.size() < 2 || participants.size() > 6) throw new IOException("Invalid encounter participants");
        var seats = new ArrayList<MatchSetupSpecification.Seat>(); var ids = new HashSet<String>(); var players = new HashSet<Integer>();
        for (var participant : participants) {
            if (!validId(participant.participantId()) || !validId(participant.deckSelectionId()) || !ids.add(participant.participantId())
                    || participant.playerId() < 0 || !players.add(participant.playerId()))
                throw new IOException("Invalid receipt participant");
            seats.add(new MatchSetupSpecification.Seat(participant.index(), participant.teamId(), participant.role()));
        }
        try {
            var setup = new MatchSetupSpecification(ruleset, seats);
            if (setup.partyTeam() != partyTeam) throw new IllegalArgumentException();
        } catch (RuntimeException invalid) { throw new IOException("Invalid receipt setup", invalid); }
    }
    private static List<Path> files(Path directory) throws IOException {
        try (var paths = Files.list(directory)) {
            var inventory = paths.limit(20001).toList();
            if (inventory.size() > 20000) throw new IOException("Encounter journal inventory limit reached");
            var found = inventory.stream().filter(path -> !path.getFileName().toString().matches("[a-f0-9-]{36}\\.json\\.[a-f0-9-]{36}\\.tmp"))
                    .sorted().toList();
            if (found.size() > 10000 || found.stream().anyMatch(path -> Files.isSymbolicLink(path) || !Files.isRegularFile(path) || !path.toString().endsWith(".json")))
                throw new IOException("Invalid encounter journal inventory");
            return found;
        }
    }
    private interface IoCall<T> { T run() throws IOException; }
    private <T> T locked(IoCall<T> action) throws IOException {
        var key = directory.toAbsolutePath().normalize();
        synchronized (LOCKS.computeIfAbsent(key, ignored -> new Object())) {
            Files.createDirectories(directory);
            try (var channel = FileChannel.open(directory.resolve("checkpoint.lock"), StandardOpenOption.CREATE, StandardOpenOption.WRITE);
                 var ignored = channel.lock()) { return action.run(); }
        }
    }
    private static <T> T read(Path path, Class<T> type) throws IOException {
        if (Files.isSymbolicLink(path) || Files.size(path) > 4 * 1024 * 1024) throw new IOException("Invalid encounter journal file");
        try { return JSON.fromJson(Files.readString(path, StandardCharsets.UTF_8), type); }
        catch (JsonParseException | IllegalArgumentException error) { throw new IOException("Invalid encounter journal data", error); }
    }
    static void atomicWrite(Path target, byte[] bytes) throws IOException {
        Files.createDirectories(target.getParent());
        var temporary = target.resolveSibling(target.getFileName() + "." + UUID.randomUUID() + ".tmp");
        try {
            try (var channel = FileChannel.open(temporary, StandardOpenOption.CREATE_NEW, StandardOpenOption.WRITE)) {
                var buffer = ByteBuffer.wrap(bytes); while (buffer.hasRemaining()) channel.write(buffer); channel.force(true);
            }
            Files.move(temporary, target, StandardCopyOption.ATOMIC_MOVE, StandardCopyOption.REPLACE_EXISTING);
        } finally { Files.deleteIfExists(temporary); }
    }
    @Override public void close() { io.shutdown(); }
}
