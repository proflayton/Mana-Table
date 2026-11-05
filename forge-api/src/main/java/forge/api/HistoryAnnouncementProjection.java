package forge.api;

import forge.game.event.StackAnnouncementReceipt;

import java.util.ArrayList;
import java.util.List;
import java.util.function.Function;

/** Durable descriptors retain their event-time disclosure scope. These values
 * are history, never live card identities, target offers or action permissions. */
final class HistoryAnnouncementProjection {
    record Target(String kind, int groupIndex, int targetIndex, String cardId, String stackId,
                  Integer playerId, String name, String type, String zone, Integer controllerId,
                  Integer ownerId, Integer allocatedAmount) { }
    record Announcement(String kind, String stackId, String causeExecutionId,
                        String targetKnowledge, List<Target> targets, Integer announcedX) {
        Announcement { targets = List.copyOf(targets); }
    }

    static Announcement describe(StackAnnouncementReceipt receipt, int viewer,
            Function<StackAnnouncementReceipt.CardSnapshot, String> identity, String publicCause) {
        var targets = new ArrayList<Target>();
        boolean complete = receipt.complete();
        for (var target : receipt.targets()) {
            if (target.kind().equals("player")) {
                targets.add(new Target("player", target.groupIndex(), target.targetIndex(), null, null,
                        target.playerId(), target.playerName(), null, null, null, null, target.allocatedAmount()));
                continue;
            }
            var card = target.card();
            boolean disclosed = card != null && card.disclosedTo(viewer);
            String id = disclosed ? identity.apply(card) : null;
            if (target.kind().equals("stack") && target.stackId() != null) {
                // A face-down spell still occupies a public stack occurrence;
                // that never discloses its concealed source face or card ID.
                targets.add(new Target("stack", target.groupIndex(), target.targetIndex(), id, target.stackId(),
                        null, disclosed ? card.name() : null, disclosed ? card.type() : null, "Stack",
                        target.controllerId(), disclosed ? card.ownerId() : null, target.allocatedAmount()));
            } else if (target.kind().equals("card") && disclosed && id != null && !id.isBlank()) {
                targets.add(new Target("card", target.groupIndex(), target.targetIndex(), id, null, null,
                        card.name(), card.type(), card.zone(), card.controllerId(), card.ownerId(), target.allocatedAmount()));
            } else complete = false;
        }
        return new Announcement(receipt.kind(), receipt.stackId(), publicCause,
                complete ? "Known" : "Unknown", targets, receipt.announcedX());
    }

    private HistoryAnnouncementProjection() { }
}
