package forge.api;

import com.google.common.eventbus.Subscribe;
import com.google.gson.Gson;
import com.google.gson.JsonObject;
import forge.StaticData;
import forge.deck.Deck;
import forge.game.ability.AbilityFactory;
import forge.game.card.Card;
import forge.game.event.GameEventTurnBegan;
import forge.game.event.ManaPaymentReceipt;
import forge.game.mana.ManaCostBeingPaid;
import forge.game.zone.ZoneType;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;

import static org.testng.Assert.*;

/** Projection/transport fixtures; ScalingManaPaymentTest supplies actual human
 * Auto/tap/payment completion, rather than claiming these are whole matches. */
public class MatchHistoryTest {
    private static final Gson JSON = new Gson();

    @Test public void onlyNegotiatedFeatureAbsenceProducesTheOptionalHistoryErrorCode() {
        try (var server = new forge.api.sync.SyncTransport.Server((seat, method, args) -> Map.of(), () -> 1);
             var connection = new SeatConnection(server.local(0))) {
            assertThrows(HistoryUnavailableException.class, () -> connection.history(query("match", 0, 128)));
        }
        var request = new JsonObject(); request.addProperty("method", "matchHistory");
        assertEquals(DesktopEngine.errorReply(request, new HistoryUnavailableException()).get("errorCode").getAsString(), "history-unavailable");
        assertFalse(DesktopEngine.errorReply(request, new IllegalArgumentException("Unknown seat command")).has("errorCode"),
                "Malformed/ordinary engine failures cannot masquerade as absent optional history");
        request.addProperty("method", "matchAction");
        assertFalse(DesktopEngine.errorReply(request, new HistoryUnavailableException()).has("errorCode"));
    }

    @Test public void historyCapturesBeforeBothLiveTailsAndQueriesWithoutAnActiveDecision() throws Exception {
        try (var table = table()) {
            var owner = table.game.getRegisteredPlayers().get(0);
            HeadlessPlatform.query(table, () -> {
                var source = add(table, "Island", ZoneType.Battlefield);
                var mana = source.getManaAbilities().get(0); mana.setActivatingPlayer(owner);
                for (int i = 1; i <= 300; i++) {
                    table.game.fireEvent(new GameEventTurnBegan(owner.getView(), i));
                    table.game.getExecutions().prepare(mana);
                    table.game.getExecutions().complete(mana, forge.game.ExecutionTracker.Outcome.RESOLVED);
                }
                assertEquals(table.game.getExecutions().lastSequence(), 600L);
                assertTrue(table.game.getExecutions().firstSequence() > 1);
                return null;
            });
            var all = history(table.seat(0), table.id);
            assertEquals(all.stream().filter(record -> record.activity() != null).count(), 300L);
            assertEquals(all.stream().filter(record -> record.execution() != null).count(), 600L);
            assertEquals(all.get(0).sequence(), 1L);
            try (var connection = SeatConnection.local(table.seat(0))) {
                var query = query(table.id, 0, 1);
                var chunk = connection.history(query);
                assertEquals(((Number) chunk.get("nextSequence")).longValue(), 1L);
                assertEquals(chunk.get("hasMore"), true);
                assertEquals(JSON.toJson(chunk), JSON.toJson(table.seat(0).history(query)), "Loopback uses the same audience-bound query");
            }
        }
    }

    @Test public void publicProductionDoesNotDisclosePrivateBillOrPreparationToAnotherSeat() throws Exception {
        try (var table = table()) {
            var owner = table.game.getRegisteredPlayers().get(0);
            var hiddenId = HeadlessPlatform.query(table, () -> {
                var hand = add(table, "Spellbook", ZoneType.Hand);
                var paid = AbilityFactory.getAbility("AB$ Draw | Cost$ 3 | NumCards$ 1", hand); paid.setActivatingPlayer(owner);
                String hidden = table.game.getExecutions().prepare(paid);
                var island = add(table, "Island", ZoneType.Battlefield);
                var mana = island.getManaAbilities().get(0); mana.setActivatingPlayer(owner);
                owner.pushPaidForSA(paid);
                try (var execution = table.game.getExecutions().enter(paid)) {
                    table.game.getExecutions().prepare(mana);
                    mana.getManaPart().produceMana("U", owner, mana);
                    owner.getManaPool().tryPayCostWithColor(forge.card.MagicColor.BLUE, paid,
                            new ManaCostBeingPaid(paid.getPayCosts().getCostMana().getMana()), new ArrayList<>());
                } finally { owner.popPaidForSA(); }
                return hidden;
            });
            var own = history(table.seat(0), table.id);
            assertTrue(own.stream().anyMatch(record -> record.exposure().equals("seatPrivate") && record.executionId() != null
                    && record.executionId().equals(hiddenId)));
            var opponent = history(table.seat(1), table.id);
            String publicJson = JSON.toJson(opponent);
            assertFalse(publicJson.contains(hiddenId));
            assertFalse(publicJson.contains("Spellbook"));
            var produced = opponent.stream().filter(record -> record.payment() != null && record.payment().kind().equals("produced"))
                    .findFirst().orElseThrow();
            assertEquals(produced.exposure(), "public");
            assertEquals(produced.payment().produced(), Map.of("U", 1));
            assertNull(produced.payment().billBefore()); assertNull(produced.payment().billAfter());
            assertTrue(opponent.stream().noneMatch(record -> record.payment() != null && record.payment().kind().equals("spent")));
        }
    }

    @Test public void actualReplacementOutputIsRecordedAndTestPaymentsAreNotSpending() throws Exception {
        try (var table = table()) {
            var collector = new Receipts(); table.game.subscribeToEvents(collector);
            HeadlessPlatform.query(table, () -> {
                var owner = table.game.getRegisteredPlayers().get(0);
                add(table, "Mana Reflection", ZoneType.Battlefield);
                var island = add(table, "Island", ZoneType.Battlefield);
                var mana = island.getManaAbilities().get(0); mana.setActivatingPlayer(owner);
                table.game.getExecutions().prepare(mana);
                mana.getManaPart().produceMana("U", owner, mana);
                var actual = collector.receipts.stream().filter(receipt -> receipt.kind().equals("produced")).findFirst().orElseThrow();
                assertEquals(actual.produced(), Map.of("U", 2), "Record post-replacement production, not the requested U");
                var host = add(table, "Spellbook", ZoneType.Hand);
                var paid = AbilityFactory.getAbility("AB$ Draw | Cost$ 1 | NumCards$ 1", host); paid.setActivatingPlayer(owner);
                table.game.getExecutions().prepare(paid);
                var originalPool = ManaPaymentReceipt.pool(owner);
                var spent = new ArrayList<forge.game.mana.Mana>();
                assertTrue(owner.getManaPool().payManaCostFromPool(new ManaCostBeingPaid(paid.getPayCosts().getCostMana().getMana()), paid, true, spent));
                assertEquals(ManaPaymentReceipt.pool(owner), originalPool);
                assertTrue(collector.receipts.stream().noneMatch(receipt -> receipt.kind().equals("spent")), "A remove/refund preview is not committed spending");
                assertEquals(collector.receipts.stream().filter(receipt -> receipt.kind().equals("produced")).count(), 1L,
                        "Refunding a preview does not produce another mana receipt");
                try (var preview = table.game.getExecutions().enterPaymentPreview(true)) {
                    var borrowed = new ArrayList<forge.game.mana.Mana>();
                    assertTrue(owner.getManaPool().tryPayCostWithColor(forge.card.MagicColor.BLUE, paid,
                            new ManaCostBeingPaid(paid.getPayCosts().getCostMana().getMana()), borrowed));
                    owner.getManaPool().refundMana(borrowed);
                }
                assertTrue(collector.receipts.stream().noneMatch(receipt -> receipt.kind().equals("spent")),
                        "A nested legacy helper cannot lose the surrounding preview boundary");
                assertTrue(owner.getManaPool().tryPayCostWithColor(forge.card.MagicColor.BLUE, paid,
                        new ManaCostBeingPaid(paid.getPayCosts().getCostMana().getMana()), paid.getPayingMana()));
                new forge.game.mana.ManaRefundService(paid).refundManaPaid();
                var restored = collector.receipts.stream().filter(receipt -> receipt.kind().equals("restored")).findFirst().orElseThrow();
                assertEquals(restored.restoredExecutionId(), paid.getExecutionId());
                assertEquals(restored.poolAfter(), originalPool);
                assertEquals(collector.receipts.stream().filter(receipt -> receipt.kind().equals("produced")).count(), 1L,
                        "An actual cancelled-payment refund is a correction, not new production");
                return null;
            });
        }
    }

    @Test public void publicCounterResultsDoNotExportAnUnadmittedCauseIdentity() throws Exception {
        try (var table = table()) {
            HeadlessPlatform.query(table, () -> {
                var owner = table.game.getRegisteredPlayers().get(0);
                var source = add(table, "Sol Ring", ZoneType.Battlefield);
                var target = add(table, "Island", ZoneType.Battlefield);
                var cause = new forge.game.event.GameEventCause(source.getView(), "Add a charge counter.", owner.getView(), "activated", "unadmitted-private-cause");
                table.game.fireEvent(new forge.game.event.GameEventCardCounters(target.getView(),
                        forge.game.card.CounterEnumType.CHARGE, 0, 1, cause));
                var mana = source.getManaAbilities().get(0); mana.setActivatingPlayer(owner);
                table.game.fireEvent(new forge.game.event.GameEventSpellResolved(mana.getView(), false, "INTERNAL-REMEMBERED-HIDDEN-CARD", 73));
                return null;
            });
            var entries = history(table.seat(1), table.id);
            var counter = entries.stream().filter(record -> record.activity() != null && record.activity().change() != null).findFirst().orElseThrow();
            assertEquals(counter.exposure(), "public");
            assertEquals(counter.activity().cause().id(), "");
            assertEquals(counter.activity().cause().name(), "Sol Ring");
            assertEquals(counter.activity().change().before(), 0);
            assertEquals(counter.activity().change().after(), 1);
            assertFalse(JSON.toJson(entries).contains("unadmitted-private-cause"));
            assertFalse(JSON.toJson(entries).contains("INTERNAL-REMEMBERED-HIDDEN-CARD"),
                    "A public source does not certify arbitrary augmented stack description prose");
            var resolved = entries.stream().filter(record -> record.activity() != null && record.activity().kind().equals("resolved")).findFirst().orElseThrow();
            assertEquals(resolved.activity().detail(), "");
            assertEquals(resolved.activity().outcome(), "resolved");
        }
    }

    @Test public void retainedFencesAndQueryScopeMakeLossAndInvalidRequestsExplicit() throws Exception {
        try (var table = table()) {
            var owner = table.game.getRegisteredPlayers().get(0);
            HeadlessPlatform.query(table, () -> {
                for (int i = 1; i <= MatchHistory.MAXIMUM_RECORDS + 3; i++)
                    table.game.fireEvent(new GameEventTurnBegan(owner.getView(), i));
                return null;
            });
            var chunk = table.seat(0).history(query(table.id, 0, 128));
            assertTrue(((Number) chunk.get("firstSequence")).longValue() > 1, "Eviction is a retention fence, not a false complete history");
            assertThrows(IllegalArgumentException.class, () -> table.seat(0).history(query("another-match", 0, 128)));
            assertThrows(IllegalArgumentException.class, () -> table.seat(0).history(query(table.id, -1, 128)));
            assertThrows(IllegalArgumentException.class, () -> table.seat(0).history(query(table.id, 0, 129)));
            assertThrows(IllegalArgumentException.class, () -> table.seat(0).history(query(table.id, Long.MAX_VALUE, 128)));
        }
    }

    private static List<MatchHistory.Record> history(MatchEndpoint endpoint, String id) {
        var result = new ArrayList<MatchHistory.Record>(); long after = 0;
        for (;;) {
            var chunk = endpoint.history(query(id, after, 128));
            @SuppressWarnings("unchecked") var records = (List<MatchHistory.Record>) chunk.get("records");
            result.addAll(records); after = ((Number) chunk.get("nextSequence")).longValue();
            if (!Boolean.TRUE.equals(chunk.get("hasMore"))) return List.copyOf(result);
        }
    }
    private static JsonObject query(String id, long after, int limit) {
        var query = new JsonObject(); query.addProperty("gameId", id); query.addProperty("afterSequence", after); query.addProperty("limit", limit); return query;
    }
    private static Card add(MatchTable table, String name, ZoneType zone) {
        var player = table.game.getRegisteredPlayers().get(0);
        var card = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard(name), player);
        player.getZone(zone).add(card); card.setSickness(false); return card;
    }
    private static MatchTable table() throws Exception {
        var resources = Path.of("../forge-gui/res");
        if (StaticData.instance() == null) EngineResources.load(resources);
        var table = new MatchTable("Constructed", List.of(MatchTable.human(new Deck(), "Constructed", "Owner"),
                MatchTable.human(new Deck(), "Constructed", "Observer")), resources, Files.createTempDirectory("match-history"));
        HeadlessPlatform.activate(table); return table;
    }
    public static final class Receipts {
        final List<ManaPaymentReceipt> receipts = new ArrayList<>();
        @Subscribe public void event(ManaPaymentReceipt receipt) { receipts.add(receipt); }
    }
}
