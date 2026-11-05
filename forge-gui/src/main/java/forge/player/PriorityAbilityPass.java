package forge.player;

import com.google.common.eventbus.Subscribe;
import forge.ai.AvailableActions;
import forge.game.event.GameEvent;
import forge.game.event.GameEventCardChangeZone;
import forge.game.event.GameEventGameFinished;
import forge.game.card.Card;
import forge.game.card.CardView;
import forge.game.player.Player;
import forge.game.spellability.SpellAbility;
import forge.game.zone.ZoneType;

import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.UUID;

/** Per-controller, per-card-instance priority preferences. The game thread owns
 *  scans; prompt commands only inspect the immutable scan and policy snapshots. */
final class PriorityAbilityPass {
    private record AbilityKey(int cardId, long timestamp, int abilityId, ZoneType zone) {
        static AbilityKey of(SpellAbility ability) {
            Card card = ability.getHostCard();
            return new AbilityKey(card.getId(), card.getGameTimestamp(), ability.getId(), card.getZone().getZoneType());
        }
    }

    private record Candidate(String id, AbilityKey key, Card source, String sourceName, String text,
                             List<Integer> eligibleHandCardIds) {
        PlayerControllerHuman.PriorityAbility view(boolean ignored) {
            return new PlayerControllerHuman.PriorityAbility(id, key.cardId(), sourceName, text, ignored, eligibleHandCardIds);
        }
        PlayerControllerHuman.PriorityAbility policyView() {
            // An undoable pass policy can outlive its legal activation window.
            return new PlayerControllerHuman.PriorityAbility(id, key.cardId(), sourceName, text, true, List.of());
        }
    }

    private record Policy(Candidate candidate, int ownTurns) { }

    private final Map<AbilityKey, String> ids = new LinkedHashMap<>();
    private final Map<String, Policy> ignored = new LinkedHashMap<>();
    private final Map<String, Candidate> candidates = new LinkedHashMap<>();
    private AvailableActions.ActionScan scan;
    private long rulesGeneration;
    private long scanGeneration = -1;

    /** Only the actual scan can certify absence. Publishing or ignoring a
     * candidate does not renew its observation of the rules resources. */
    @Subscribe public synchronized void rulesChanged(GameEvent event) {
        rulesGeneration++;
    }

    synchronized long rulesGeneration() { return rulesGeneration; }

    synchronized boolean scanCurrent() {
        return scan != null && scan.complete() && scanGeneration == rulesGeneration;
    }

    /** One assessed route is positive evidence even when unrelated routes made
     * the overall absence scan incomplete. Both require the same current generation. */
    synchronized boolean hasCurrentFunding(CardView card) {
        return scan != null && scanGeneration == rulesGeneration
                && scan.fundedCards().stream().anyMatch(funded -> funded == card);
    }

    synchronized void clearScan() {
        scan = null;
        scanGeneration = -1;
        candidates.clear();
    }

    /** Zone events run on the game thread, so even another seat's prompt stops
     *  showing a policy immediately after its source leaves the zone. */
    @Subscribe public synchronized void sourceChangedZones(GameEventCardChangeZone event) {
        if (event.card() == null) return;
        int cardId = event.card().getId();
        ignored.values().removeIf(policy -> policy.candidate().key().cardId() == cardId);
        candidates.values().removeIf(candidate -> candidate.key().cardId() == cardId);
        ids.keySet().removeIf(key -> key.cardId() == cardId);
    }

    @Subscribe public synchronized void gameFinished(GameEventGameFinished event) {
        ignored.clear();
        ids.clear();
        clearScan();
    }

    synchronized void refresh(Player player, AvailableActions.ActionScan next, long observedGeneration) {
        pruneTurns(player);
        ignored.values().removeIf(policy -> {
            Candidate candidate = policy.candidate();
            Card current = player.getGame().getCardState(candidate.source(), null);
            return current == null || current.getGameTimestamp() != candidate.key().timestamp()
                    || current.getZone() == null || current.getZone().getZoneType() != candidate.key().zone()
                    || !player.equals(current.getController());
        });
        scan = next;
        candidates.clear();
        for (SpellAbility ability : next.activatedAbilities()) {
            AbilityKey key = AbilityKey.of(ability);
            String id = ids.computeIfAbsent(key, ignoredKey -> UUID.randomUUID().toString());
            Card source = ability.getHostCard();
            candidates.put(id, new Candidate(id, key, source, source.getName(), ability.toUnsuppressedString(),
                    HandPlacements.eligibleCards(player, ability)));
        }
        // Candidate descriptions can also traverse rules objects. Certify only
        // after the entire scan and its cached description capture stayed put.
        scanGeneration = rulesGeneration == observedGeneration ? observedGeneration : -1;
    }

    synchronized boolean setIgnored(String id, boolean value, Player player, boolean currentPriority) {
        pruneTurns(player);
        if (value) {
            Candidate candidate = candidates.get(id);
            if (!currentPriority || candidate == null || scan == null || !scan.complete()) return false;
            ignored.put(id, new Policy(candidate, player.getTurn()));
            return true;
        }
        return ignored.remove(id) != null;
    }

    synchronized List<PlayerControllerHuman.PriorityAbility> legal(Player player) {
        pruneTurns(player);
        if (scan == null || !scan.complete()) return List.of();
        return candidates.values().stream().map(candidate -> candidate.view(ignored.containsKey(candidate.id()))).toList();
    }

    synchronized List<PlayerControllerHuman.PriorityAbility> ignored(Player player) {
        pruneTurns(player);
        return ignored.values().stream().map(policy -> policy.candidate().policyView()).toList();
    }

    synchronized boolean hasHandPlacement(String id, int handCardId) {
        Candidate candidate = candidates.get(id);
        return scan != null && scan.complete() && candidate != null && candidate.eligibleHandCardIds().contains(handCardId);
    }

    synchronized SpellAbility resolveHandPlacement(Player player, String id, int handCardId) {
        if (!hasHandPlacement(id, handCardId)) return null;
        Candidate candidate = candidates.get(id);
        Card current = player.getGame().getCardState(candidate.source(), null);
        if (current == null || current.getGameTimestamp() != candidate.key().timestamp()
                || !current.isInZone(ZoneType.Battlefield) || !player.equals(current.getController())) return null;
        return current.getAllPossibleAbilities(player, true).stream()
                .filter(ability -> ability.getId() == candidate.key().abilityId())
                .filter(ability -> HandPlacements.eligibleCards(player, ability).contains(handCardId))
                .findFirst().orElse(null);
    }

    synchronized boolean hasNonIgnoredActions(Player player) {
        pruneTurns(player);
        if (!scanCurrent() || scan.hasOtherActions()) return true;
        return candidates.keySet().stream().anyMatch(id -> !ignored.containsKey(id));
    }

    private void pruneTurns(Player player) {
        if (player.getGame().isGameOver()) {
            ignored.clear();
            return;
        }
        int ownTurns = player.getTurn();
        ignored.values().removeIf(policy -> policy.ownTurns() != ownTurns);
    }
}
