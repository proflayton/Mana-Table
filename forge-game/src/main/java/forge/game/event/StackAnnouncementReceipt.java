package forge.game.event;

import forge.game.Game;
import forge.game.card.Card;
import forge.game.card.CardView;
import forge.game.player.Player;
import forge.game.spellability.SpellAbility;
import forge.game.spellability.SpellAbilityStackInstance;
import forge.game.zone.ZoneType;

import java.util.*;

/** Actual committed stack targets, copied before subsequent rules work. Engine
 * views are identity/admission witnesses only; all descriptive values and the
 * audience disclosure witness are frozen here, never reconstructed by a reader. */
public record StackAnnouncementReceipt(String kind, String executionId, String stackId,
        String causeExecutionId, CardSnapshot source, boolean complete,
        List<Target> targets, Integer announcedX) implements Event {
    private static final int MAXIMUM_GROUPS = 128, MAXIMUM_TARGETS = 4096;

    public record CardSnapshot(CardView identity, Set<Integer> disclosedTo, String name,
            String type, String zone, Integer controllerId, Integer ownerId) {
        public CardSnapshot { disclosedTo = Set.copyOf(disclosedTo); }
        public boolean disclosedTo(int viewer) { return disclosedTo.contains(viewer); }
    }
    public record Target(String kind, int groupIndex, int targetIndex, CardSnapshot card,
            String stackId, String executionId, Integer playerId, String playerName,
            Integer controllerId, Integer allocatedAmount) { }

    public StackAnnouncementReceipt { targets = List.copyOf(targets); }

    /** Constructors and view refreshes do not announce anything. Only callers
     * which actually commit a stack entry or replace its targets use this port. */
    public static StackAnnouncementReceipt capture(String kind, SpellAbilityStackInstance root) {
        if (!Set.of("announced", "retargeted").contains(kind)) throw new IllegalArgumentException("Unknown stack announcement");
        SpellAbility ability = root.getSpellAbility();
        Game game = ability.getHostCard().getGame();
        String executionId = ability.getExecutionId();
        if (executionId == null) return null;
        String cause = game.getExecutions().currentId();
        if (executionId.equals(cause)) cause = null;
        var targets = new ArrayList<Target>();
        var seen = Collections.newSetFromMap(new IdentityHashMap<SpellAbilityStackInstance, Boolean>());
        boolean complete = true;
        int group = 0;
        for (var current = root; current != null; current = current.getSubInstance(), group++) {
            if (group >= MAXIMUM_GROUPS || !seen.add(current)) { complete = false; break; }
            var choices = current.getTargetChoices();
            for (int index = 0; index < choices.size(); index++) {
                if (targets.size() >= MAXIMUM_TARGETS) { complete = false; break; }
                var target = choices.get(index);
                Integer allocation = choices.getDividedValue(target);
                if (target instanceof Card card) {
                    targets.add(new Target("card", group, index, card(card), null, null, null, null, null, allocation));
                } else if (target instanceof Player player) {
                    targets.add(new Target("player", group, index, null, null, null, player.getId(), player.getName(), player.getId(), allocation));
                } else if (target instanceof SpellAbility spell) {
                    var instance = currentStack(game, spell);
                    targets.add(new Target("stack", group, index, card(spell.getHostCard()),
                            instance == null ? null : String.valueOf(instance.getId()), spell.getExecutionId(), null, null,
                            spell.getActivatingPlayer() == null ? null : spell.getActivatingPlayer().getId(), allocation));
                    complete &= instance != null;
                } else complete = false;
            }
            if (targets.size() >= MAXIMUM_TARGETS && current.getSubInstance() != null) { complete = false; break; }
        }
        return new StackAnnouncementReceipt(kind, executionId, String.valueOf(root.getId()), cause,
                card(ability.getHostCard()), complete, targets, ability.getXManaCostPaid());
    }

    /** Exact execution occurrence, rather than the reusable numeric ability ID. */
    public static SpellAbilityStackInstance currentStack(Game game, SpellAbility ability) {
        String execution = ability.getExecutionId();
        for (var current : game.getStack())
            if (current.getSpellAbility() == ability.getRootAbility()
                    || execution != null && execution.equals(current.getSpellAbility().getExecutionId())) return current;
        return null;
    }

    private static CardSnapshot card(Card card) {
        if (card == null) return null;
        var view = card.getView();
        ZoneType zone = card.getZone() == null ? null : card.getZone().getZoneType();
        boolean publicFace = zone != null && !card.isFaceDown() && switch (zone) {
            case Battlefield, Stack, Graveyard, Exile, Command -> true;
            default -> false;
        };
        var disclosed = new HashSet<Integer>();
        if (publicFace) for (var player : card.getGame().getRegisteredPlayers())
            if (view.canBeShownTo(player.getView())) disclosed.add(player.getId());
        // Hidden faces are not even copied into this receipt's scalar data.
        String name = publicFace ? card.getName() : null;
        String type = publicFace ? card.getType().toString() : null;
        return new CardSnapshot(view, disclosed, name == null || name.isBlank() ? null : name,
                type == null || type.isBlank() ? null : type, zone == null ? null : zone.name(),
                card.getController() == null ? null : card.getController().getId(),
                card.getOwner() == null ? null : card.getOwner().getId());
    }
}
