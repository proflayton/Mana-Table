package forge.api;

import com.google.common.eventbus.Subscribe;
import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import forge.game.Game;
import forge.game.card.CardView;
import forge.game.event.ManaPaymentReceipt;
import forge.game.event.StackAnnouncementReceipt;
import forge.game.player.PlayerView;

import java.nio.charset.StandardCharsets;
import java.util.*;

/** Audience-owned immutable facts, captured before the live projection tails
 * evict them. The ordinal orders this ledger, not rules transitions or polls. */
final class MatchHistory {
    static final int MAXIMUM_RECORDS = 16384;
    static final int MAXIMUM_BYTES = 8 * 1024 * 1024;
    static final int MAXIMUM_RECORD_BYTES = 64 * 1024;
    static final int MAXIMUM_CHUNK = 128;
    private static final int MAXIMUM_CHUNK_BYTES = 512 * 1024;
    private static final Gson JSON = new GsonBuilder().serializeNulls().create();
    record Execution(MatchExecutions.Occurrence occurrence, MatchExecutions.Transition transition) { }
    // Schemas1/2 predate live route correlation. A separate wire value omits
    // that property even when the engine serializer includes null fields.
    private record LegacyOccurrence(String id, String parentId, String budgetRootId, String kind, Integer actorPlayerId,
                                    String sourceCardId, String sourceName, String step, String outcome, String ruleKind, String ruleResult) { }
    private record LegacyExecution(LegacyOccurrence occurrence, MatchExecutions.Transition transition) { }
    record Payment(String kind, int payerId, String origin, String producerExecutionId, String producerMode,
                   List<ManaPaymentReceipt.BillPart> billBefore, List<ManaPaymentReceipt.BillPart> billAfter,
                   Map<String, Integer> produced, Map<String, Integer> spent, Map<String, Integer> poolAfter,
                   Map<String, Integer> sourceCounters, String restoredExecutionId) { }
    record Record(long sequence, int turn, String phaseKey, String exposure,
                  String executionId, String parentId, String sourceCardId, String sourceName,
                  MatchActivity.Entry activity, Execution execution, Payment payment,
                  HistoryAnnouncementProjection.Announcement announcement) {
        Record(long sequence, int turn, String phaseKey, String exposure, String executionId, String parentId,
               String sourceCardId, String sourceName, MatchActivity.Entry activity, Execution execution, Payment payment) {
            this(sequence, turn, phaseKey, exposure, executionId, parentId, sourceCardId, sourceName, activity, execution, payment, null);
        }
    }
    record Chunk(int schemaVersion, String gameId, int viewerId, long firstSequence, long lastSequence,
                 long nextSequence, boolean hasMore, List<Record> records) { }
    private record Stored(Record record, int bytes) { }
    private final String gameId;
    private final PlayerView viewer;
    private final Game game;
    private final MatchActivity activity;
    private final Deque<Stored> records = new ArrayDeque<>();
    private final LinkedHashSet<String> publicExecutions = new LinkedHashSet<>();
    private final LinkedHashSet<String> admittedExecutions = new LinkedHashSet<>();
    private long sequence;
    private int bytes;

    MatchHistory(String gameId, PlayerView viewer, Game game, MatchActivity activity) {
        this.gameId = gameId; this.viewer = viewer; this.game = game; this.activity = activity;
    }

    synchronized void activity(MatchActivity.Entry entry, boolean publicFact) {
        boolean discloseExecution = !publicFact || publicExecutions.contains(entry.executionId());
        var cause = entry.cause();
        if (publicFact && cause != null && !publicExecutions.contains(cause.id()))
            cause = new MatchActivity.PublicEffectCause(cause.cardId(), cause.name(), cause.ability(), cause.controllerId(), cause.kind(), "");
        var safe = new MatchActivity.Entry(entry.id(), entry.turn(), entry.phaseKey(), entry.kind(),
                entry.playerId(), entry.message(), entry.cardId(), entry.cardName(), publicFact ? "" : entry.detail(), entry.outcome(), entry.count(),
                entry.change(), cause, entry.interruptsQuietYield(), entry.impact(), entry.stackId(),
                discloseExecution ? entry.executionId() : null, discloseExecution ? entry.executionStep() : null, entry.historicSpell());
        append(new Record(++sequence, entry.turn(), entry.phaseKey(), publicFact ? "public" : "seatPrivate",
                safe.executionId(), null, safe.cardId(), safe.cardName(), safe, null, null));
    }

    synchronized void execution(MatchExecutions.Occurrence occurrence, MatchExecutions.Transition transition, boolean publicSource) {
        admittedExecutions.add(occurrence.id());
        while (admittedExecutions.size() > MAXIMUM_RECORDS) admittedExecutions.remove(admittedExecutions.iterator().next());
        boolean disclosed = publicSource || publicExecutions.contains(occurrence.id());
        boolean publicFact = disclosed && (occurrence.parentId() == null || publicExecutions.contains(occurrence.parentId()));
        // A public source can descend from private preparation. Its actual
        // occurrence becomes public without disclosing that private caller.
        if (disclosed) {
            publicExecutions.add(occurrence.id());
            while (publicExecutions.size() > MAXIMUM_RECORDS) publicExecutions.remove(publicExecutions.iterator().next());
        }
        var safe = occurrence;
        if (publicFact) {
            safe = new MatchExecutions.Occurrence(occurrence.id(), publicReference(occurrence.parentId()),
                    publicExecutions.contains(occurrence.budgetRootId()) ? occurrence.budgetRootId() : occurrence.id(),
                    occurrence.kind(), occurrence.actorPlayerId(), occurrence.sourceCardId(), occurrence.sourceName(), occurrence.step(),
                    occurrence.outcome(), occurrence.ruleKind(), occurrence.ruleResult(), occurrence.stableRouteId());
        }
        append(new Record(++sequence, turn(), phase(), publicFact ? "public" : "seatPrivate", safe.id(), safe.parentId(),
                safe.sourceCardId(), safe.sourceName(), null, new Execution(safe, transition), null));
    }

    @Subscribe public void payment(ManaPaymentReceipt receipt) {
        boolean produced = receipt.kind().equals("produced");
        boolean publicSource = publicCard(receipt.source());
        boolean publicBill = publicCard(receipt.paidForSource());
        // Other seats' hidden preparation and bills are never admitted.
        boolean own = receipt.payer().getId() == viewer.getId();
        if (!own && (!publicSource || !produced && !publicBill)) return;
        boolean publicFact = produced ? publicSource : publicBill && (receipt.source() == null || publicSource);
        String sourceId = visible(receipt.source()) ? activity.visualId(receipt.source()) : null;
        String sourceName = visible(receipt.source()) ? receipt.source().getCurrentState().getName() : null;
        synchronized (this) {
            String execution = admittedReference(produced ? receipt.producerExecutionId() : receipt.executionId());
            if (publicFact) execution = publicReference(execution);
            String producer = publicFact ? publicReference(receipt.producerExecutionId()) : admittedReference(receipt.producerExecutionId());
            var payment = new Payment(receipt.kind(), receipt.payer().getId(), receipt.origin(), producer,
                    visible(receipt.source()) ? receipt.producerMode() : null,
                    produced ? null : receipt.billBefore(), produced ? null : receipt.billAfter(),
                    receipt.produced(), receipt.spent(), receipt.poolAfter(), visible(receipt.source()) ? receipt.sourceCounters() : null,
                    publicFact ? publicReference(receipt.restoredExecutionId()) : admittedReference(receipt.restoredExecutionId()));
            append(new Record(++sequence, turn(), phase(), publicFact ? "public" : "seatPrivate", execution, null,
                    sourceId, sourceName, null, null, payment));
        }
    }

    @Subscribe public void announcement(StackAnnouncementReceipt receipt) {
        // Reaching the public stack is the disclosure of this occurrence, not
        // its private preparation or the identity of an unadmitted caller.
        String publicCause;
        synchronized (this) {
            admittedExecutions.add(receipt.executionId());
            publicExecutions.add(receipt.executionId());
            while (admittedExecutions.size() > MAXIMUM_RECORDS) admittedExecutions.remove(admittedExecutions.iterator().next());
            while (publicExecutions.size() > MAXIMUM_RECORDS) publicExecutions.remove(publicExecutions.iterator().next());
            publicCause = publicReference(receipt.causeExecutionId());
        }
        // Activity admission may take its own monitor. Never invert the
        // Activity -> History ordering used by ordinary committed events.
        var announced = HistoryAnnouncementProjection.describe(receipt, viewer.getId(), activity::announcementIdentity, publicCause);
        var source = receipt.source();
        boolean disclosed = source != null && source.disclosedTo(viewer.getId());
        String sourceId = disclosed ? activity.announcementIdentity(source) : null;
        synchronized (this) {
            append(new Record(++sequence, turn(), phase(), "public", receipt.executionId(), null,
                    sourceId, disclosed ? source.name() : null, null, null, null, announced));
        }
    }

    synchronized void restored(int actorId, String executionId, Map<String, Integer> pool) {
        var payment = new Payment("restored", actorId, "manual", null, null, null, null, null, null, pool, null,
                publicReference(executionId));
        append(new Record(++sequence, turn(), phase(), "public", publicReference(executionId), null, null, null, null, null, payment));
    }

    synchronized Map<String, Object> read(JsonObject request) {
        if (!request.has("gameId") || !request.get("gameId").isJsonPrimitive()
                || !request.get("gameId").getAsJsonPrimitive().isString() || !gameId.equals(request.get("gameId").getAsString()))
            throw new IllegalArgumentException("History belongs to another match");
        long after = number(request, "afterSequence", 0);
        long limit = number(request, "limit", MAXIMUM_CHUNK);
        int schema = requestedSchema(request);
        if (after < 0 || after > sequence || limit < 1 || limit > MAXIMUM_CHUNK)
            throw new IllegalArgumentException("Invalid history cursor or chunk limit");
        var result = new ArrayList<Record>();
        int chunkBytes = 0;
        for (var stored : records) if (stored.record().sequence() > after) {
            if (result.size() >= limit || !result.isEmpty() && chunkBytes + stored.bytes() > MAXIMUM_CHUNK_BYTES) break;
            result.add(stored.record()); chunkBytes += stored.bytes();
        }
        long next = result.isEmpty() ? sequence : result.get(result.size() - 1).sequence();
        var chunk = new Chunk(schema, gameId, viewer.getId(), firstSequence(), sequence, next, next < sequence, List.copyOf(result));
        return Map.of("schemaVersion", chunk.schemaVersion(), "gameId", chunk.gameId(), "viewerId", chunk.viewerId(),
                "firstSequence", chunk.firstSequence(), "lastSequence", chunk.lastSequence(), "nextSequence", chunk.nextSequence(),
                "hasMore", chunk.hasMore(), "records", chunk.records().stream().map(record -> wireRecord(record, schema)).toList());
    }

    static int requestedSchema(JsonObject request) {
        long schema = number(request, "historySchemaVersion", 1);
        if (schema != 1 && schema != 2) throw new IllegalArgumentException("Unsupported history schema");
        return (int) schema;
    }

    private static Map<String, Object> wireRecord(Record record, int schema) {
        var activity = record.activity();
        if (schema == 1 && record.announcement() != null) {
            var announced = record.announcement();
            // Compatibility facts use this ledger ordinal as their own event
            // identity. No unrelated live Activity entry is borrowed or edited.
            activity = new MatchActivity.Entry(record.sequence(), record.turn(), record.phaseKey(), "announcement", null,
                    announced.kind().equals("retargeted") ? "Targets changed." : "Targets announced.",
                    record.sourceCardId(), record.sourceName(), "", "", 0, null, null, false,
                    null, announced.stackId(), record.executionId(), null);
        }
        var value = new LinkedHashMap<String, Object>();
        value.put("sequence", record.sequence()); value.put("turn", record.turn()); value.put("phaseKey", record.phaseKey());
        value.put("exposure", record.exposure()); value.put("executionId", record.executionId()); value.put("parentId", record.parentId());
        value.put("sourceCardId", record.sourceCardId()); value.put("sourceName", record.sourceName());
        value.put("activity", activity); value.put("execution", legacyExecution(record.execution())); value.put("payment", record.payment());
        if (schema == 2) value.put("announcement", record.announcement());
        return Collections.unmodifiableMap(value);
    }

    private static LegacyExecution legacyExecution(Execution execution) {
        if (execution == null) return null;
        var occurrence = execution.occurrence();
        return new LegacyExecution(new LegacyOccurrence(occurrence.id(), occurrence.parentId(), occurrence.budgetRootId(),
                occurrence.kind(), occurrence.actorPlayerId(), occurrence.sourceCardId(), occurrence.sourceName(), occurrence.step(),
                occurrence.outcome(), occurrence.ruleKind(), occurrence.ruleResult()), execution.transition());
    }

    private static long number(JsonObject request, String key, long fallback) {
        if (!request.has(key)) return fallback;
        try {
            var value = request.get(key);
            if (!value.isJsonPrimitive() || !value.getAsJsonPrimitive().isNumber()) throw new NumberFormatException();
            return value.getAsBigDecimal().longValueExact();
        } catch (ArithmeticException | NumberFormatException invalid) { throw new IllegalArgumentException("Invalid history integer"); }
    }
    private long firstSequence() { return records.isEmpty() ? sequence + 1 : records.peekFirst().record().sequence(); }
    private String publicReference(String id) { return id != null && publicExecutions.contains(id) ? id : null; }
    private String admittedReference(String id) { return id != null && admittedExecutions.contains(id) ? id : null; }
    private int turn() { return game.getPhaseHandler().getTurn(); }
    private String phase() { return game.getPhaseHandler().getPhase() == null ? "PREGAME" : game.getPhaseHandler().getPhase().name(); }
    private boolean visible(CardView card) {
        return activity.historyVisible(card);
    }
    private boolean publicCard(CardView card) {
        return activity.historyPublic(card);
    }
    private void append(Record record) {
        int size = JSON.toJson(wireRecord(record, 2)).getBytes(StandardCharsets.UTF_8).length;
        if (size > MAXIMUM_RECORD_BYTES) { records.clear(); bytes = 0; return; }
        records.addLast(new Stored(record, size)); bytes += size;
        while (records.size() > MAXIMUM_RECORDS || bytes > MAXIMUM_BYTES) bytes -= records.removeFirst().bytes();
    }
}
