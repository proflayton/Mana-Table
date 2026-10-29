package forge.api;

import forge.game.card.Card;
import forge.game.card.CardView;
import forge.game.player.Player;
import forge.game.spellability.SpellAbility;
import forge.game.trigger.WrappedAbility;
import java.util.*;
import java.util.function.Function;

/** Internal consent evidence. No hidden engine identity is projected on the wire. */
final class FiniteStackWitness {
    record Member(String executionId, Integer controllerId, String kind, String sourceOccurrence,
                  ResolutionBatchProjection.Group repetition, List<String> targetTerms) { }

    static Member capture(SpellAbility ability, String executionId, Player viewer, Function<CardView, String> identity) {
        var host = ability.getHostCard();
        String sourceId = host == null ? null : identity.apply(host.getView());
        if (host == null || ability.getActivatingPlayer() == null || sourceId == null || sourceId.isEmpty()) return null;
        var terms = new ArrayList<String>();
        if (!targets(ability.getRootAbility(), "root", terms, Collections.newSetFromMap(new IdentityHashMap<>()))) return null;
        return new Member(executionId, ability.getActivatingPlayer().getId(), ability.isTrigger() ? "trigger"
                : ability.isSpell() ? "spell" : ability.isActivatedAbility() ? "activation" : "ability",
                sourceId + ":" + card(host) + ":" + host.getController().getId(),
                ResolutionBatchProjection.group(ability, viewer.getView(), identity), List.copyOf(terms));
    }

    private static boolean targets(SpellAbility original, String path, List<String> result, Set<SpellAbility> seen) {
        var ability = original;
        while (ability instanceof WrappedAbility wrapped) ability = wrapped.getWrappedAbility();
        if (!seen.add(ability) || seen.size() > 64 || result.size() > 4096) return false;
        result.add(path);
        for (var target : ability.getTargets()) {
            String id = target instanceof Card card ? card(card) + ":controller:" + card.getController().getId()
                    : target instanceof Player player ? "player:" + player.getId()
                    : target instanceof SpellAbility spell && spell.getExecutionId() != null
                        ? "execution:" + spell.getExecutionId() + ":controller:" + (spell.getActivatingPlayer() == null ? "unknown" : spell.getActivatingPlayer().getId()) : null;
            if (id == null) return false;
            result.add(id + ":allocation:" + ability.getTargets().getDividedValue(target));
        }
        if (ability.getSubAbility() != null && !targets(ability.getSubAbility(), path + "/sub", result, seen)) return false;
        for (var entry : new TreeMap<>(ability.getAdditionalAbilities()).entrySet())
            if (!targets(entry.getValue(), path + "/ability:" + entry.getKey(), result, seen)) return false;
        for (var entry : new TreeMap<>(ability.getAdditionalAbilityLists()).entrySet())
            for (int index = 0; index < entry.getValue().size(); index++)
                if (!targets(entry.getValue().get(index), path + "/list:" + entry.getKey() + ":" + index, result, seen)) return false;
        return result.size() <= 4096;
    }
    private static String card(Card card) { return "card:" + card.getId() + ":" + card.getGameTimestamp(); }
    private FiniteStackWitness() { }
}
