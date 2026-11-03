package forge.api;

import com.google.common.eventbus.Subscribe;
import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import forge.game.Game;
import forge.game.card.CardView;
import forge.game.event.ManaPaymentReceipt;
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
    record Payment(String kind, int payerId, String origin, String producerExecutionId, String producerMode,
                   List<ManaPaymentReceipt.BillPart> billBefore, List<ManaPaymentReceipt.BillPart> billAfter,
                   Map<String, Integer> produced, Map<String, Integer> spent, Map<String, Integer> poolAfter,
                   Map<String, Integer> sourceCounters, String restoredExecutionId) { }
    record Record(long sequence, int turn, String phaseKey, String exposure,
                  String executionId, String parentId, String sourceCardId, String sourceName,
                  MatchActivity.Entry activity, Execution execution, Payment payment) { }
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
                    occurrence.outcome(), occurrence.ruleKind(), occurrence.ruleResult());
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
        if (after < 0 || after > sequence || limit < 1 || limit > MAXIMUM_CHUNK)
            throw new IllegalArgumentException("Invalid history cursor or chunk limit");
        var result = new ArrayList<Record>();
        int chunkBytes = 0;
        for (var stored : records) if (stored.record().sequence() > after) {
            if (result.size() >= limit || !result.isEmpty() && chunkBytes + stored.bytes() > MAXIMUM_CHUNK_BYTES) break;
            result.add(stored.record()); chunkBytes += stored.bytes();
        }
        long next = result.isEmpty() ? sequence : result.get(result.size() - 1).sequence();
        var chunk = new Chunk(1, gameId, viewer.getId(), firstSequence(), sequence, next, next < sequence, List.copyOf(result));
        return Map.of("schemaVersion", chunk.schemaVersion(), "gameId", chunk.gameId(), "viewerId", chunk.viewerId(),
                "firstSequence", chunk.firstSequence(), "lastSequence", chunk.lastSequence(), "nextSequence", chunk.nextSequence(),
                "hasMore", chunk.hasMore(), "records", chunk.records());
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
        int size = JSON.toJson(record).getBytes(StandardCharsets.UTF_8).length;
        if (size > MAXIMUM_RECORD_BYTES) { records.clear(); bytes = 0; return; }
        records.addLast(new Stored(record, size)); bytes += size;
        while (records.size() > MAXIMUM_RECORDS || bytes > MAXIMUM_BYTES) bytes -= records.removeFirst().bytes();
    }
}
