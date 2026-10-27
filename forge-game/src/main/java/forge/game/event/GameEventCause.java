package forge.game.event;

import forge.game.card.CardView;
import forge.game.player.PlayerView;
import forge.game.spellability.SpellAbility;
import forge.game.trigger.WrappedAbility;

/** The actual effect supplied by a rules mutation, copied before its ability is reused.
 * This is engine event context, not a public wire view: each viewer must authorize
 * disclosure of the source before publishing its name, description or identity. */
public record GameEventCause(CardView card, String ability, PlayerView controller, String kind, String instanceId) {
    public static GameEventCause from(SpellAbility ability) {
        if (ability == null || ability.getHostCard() == null) return null;
        SpellAbility root = unwrap(ability.getRootAbility());
        var execution = ability.getHostCard().getGame().getExecutions().occurrence(ability);
        String instanceId = execution == null || execution.step().equals("complete") ? null : execution.id();
        // Unmigrated effects have no execution evidence. Do not manufacture a
        // correlation by searching whatever happens to be on the live stack.
        if (instanceId == null) return null;
        String description = ability.getView().getDescription();
        if (description == null || description.isBlank()) description = root.getView().getDescription();
        if ((description == null || description.isBlank()) && root.getTrigger() != null) {
            // Use its human rules description, not the augmented wrapper stack
            // text which can append remembered or otherwise private objects.
            description = root.getTrigger().toString(false).replace("ABILITY", "its ability");
        }
        String kind = root.isTrigger() ? "trigger" : root.isSpell() ? "spell"
                : root.isReplacementAbility() ? "replacement" : root.isActivatedAbility() ? "activated" : "effect";
        return new GameEventCause(CardView.get(ability.getHostCard()), description == null ? "" : description,
                PlayerView.get(root.getActivatingPlayer()), kind, instanceId);
    }

    private static SpellAbility unwrap(SpellAbility ability) {
        while (ability instanceof WrappedAbility wrapped) ability = wrapped.getWrappedAbility();
        return ability.getRootAbility();
    }
}
