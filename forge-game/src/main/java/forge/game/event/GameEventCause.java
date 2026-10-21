package forge.game.event;

import forge.game.card.CardView;
import forge.game.player.PlayerView;
import forge.game.spellability.SpellAbility;
import forge.game.trigger.WrappedAbility;

import java.util.UUID;

/** The actual effect supplied by a rules mutation, copied before its ability is reused.
 * This is engine event context, not a public wire view: each viewer must authorize
 * disclosure of the source before publishing its name, description or identity. */
public record GameEventCause(CardView card, String ability, PlayerView controller, String kind, String instanceId) {
    public static GameEventCause from(SpellAbility ability) {
        if (ability == null || ability.getHostCard() == null) return null;
        SpellAbility root = unwrap(ability.getRootAbility());
        String instanceId = null;
        String description = ability.getView().getDescription();
        // A trigger resolves its wrapped ability, while the wrapper is the object
        // on the stack. Match that exact relationship, never a recent stack item.
        for (var instance : ability.getHostCard().getGame().getStack()) {
            SpellAbility stacked = instance.getSpellAbility();
            if (unwrap(stacked).getId() != root.getId()) continue;
            instanceId = "stack:" + instance.getId();
            break;
        }
        if (description == null || description.isBlank()) description = root.getView().getDescription();
        if ((description == null || description.isBlank()) && root.getTrigger() != null) {
            // Use its human rules description, not the augmented wrapper stack
            // text which can append remembered or otherwise private objects.
            description = root.getTrigger().toString(false).replace("ABILITY", "its ability");
        }
        String kind = root.isTrigger() ? "trigger" : root.isSpell() ? "spell"
                : root.isReplacementAbility() ? "replacement" : root.isActivatedAbility() ? "activated" : "effect";
        return new GameEventCause(CardView.get(ability.getHostCard()), description == null ? "" : description,
                PlayerView.get(root.getActivatingPlayer()), kind, instanceId == null ? UUID.randomUUID().toString() : instanceId);
    }

    private static SpellAbility unwrap(SpellAbility ability) {
        while (ability instanceof WrappedAbility wrapped) ability = wrapped.getWrappedAbility();
        return ability.getRootAbility();
    }
}
