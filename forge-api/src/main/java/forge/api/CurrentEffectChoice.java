package forge.api;

import forge.game.ability.ApiType;
import forge.game.player.Player;
import forge.game.spellability.SpellAbility;
import forge.game.spellability.SpellAbilityView;
import forge.game.zone.ZoneType;
import forge.player.PlayerControllerHuman.EffectChoiceContext;

import java.util.*;

/** Producer-local references for one actual blocking effect choice. These are
 * not a wire capability and never expand an option's operation graph. */
final class CurrentEffectChoice {
    final EffectChoiceContext context;
    final SpellAbility owner;
    final Player chooser;
    final List<SpellAbility> offered;

    static CurrentEffectChoice capture(List<?> choices, EffectChoiceContext context, Player viewer) {
        if (viewer == null || choices.size() > 128 || context == null || context.owner() == null || context.chooser() != viewer || context.count() != 1
                || context.owner().getApi() != ApiType.GenericChoice || context.owner().isWrapper()
                || context.owner().getHostCard() == null || context.owner().getHostCard().getGame() != viewer.getGame()
                || context.owner().hasParam("Secretly")) return null;
        var owner = context.owner();
        var source = owner.getHostCard();
        var current = source.getGame().getCardState(source, null);
        if (source.isFaceDown() || source.isPhasedOut() || !source.isInZone(ZoneType.Battlefield)
                || !source.getView().canBeShownTo(viewer.getView()) || current == null
                || !source.equalsWithGameTimestamp(current)) return null;
        var offered = new ArrayList<SpellAbility>();
        var seen = Collections.newSetFromMap(new IdentityHashMap<SpellAbility, Boolean>());
        for (Object value : choices) {
            if (!(value instanceof SpellAbilityView view)) return null;
            var actual = context.abilities().get(view);
            if (actual == null || actual.getView() != view || actual.getParent() != owner || !seen.add(actual)
                    || actual.getHostCard() != owner.getHostCard()
                    || owner.getAdditionalAbilityList("Choices").stream().noneMatch(item -> item == actual)) return null;
            offered.add(actual);
        }
        if (offered.isEmpty() || offered.size() != context.abilities().size()) return null;
        var result = new CurrentEffectChoice(context, List.copyOf(offered));
        return result.selectedPlayer() == null ? null : result;
    }

    private CurrentEffectChoice(EffectChoiceContext context, List<SpellAbility> offered) {
        this.context = context; owner = context.owner(); chooser = context.chooser(); this.offered = offered;
    }

    Player selectedPlayer() {
        if (owner.getTargets().size() != 1) return null;
        var players = owner.getTargets().getTargetPlayers().iterator();
        if (!players.hasNext()) return null;
        var player = players.next();
        if (players.hasNext()) return null;
        return player == chooser && player.isInGame() && player.getGame() == owner.getHostCard().getGame()
                && player.getGame().getRegisteredPlayers().stream().anyMatch(actual -> actual == player) ? player : null;
    }

    boolean owns(SpellAbility ability) {
        var seen = Collections.newSetFromMap(new IdentityHashMap<SpellAbility, Boolean>());
        for (var part = ability; part != null && seen.size() < 128 && seen.add(part); ) {
            var current = part;
            if (offered.stream().anyMatch(option -> option == current)) return part.getParent() == owner;
            var parent = part.getParent();
            if (parent == null || parent.getSubAbility() != part) return false;
            part = parent;
        }
        return false;
    }

    Player parentPlayer(SpellAbility ability) {
        return owns(ability) && ability.getParentTargetingPlayer() == owner ? selectedPlayer() : null;
    }
}
