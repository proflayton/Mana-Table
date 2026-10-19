package forge.player;

import forge.game.ability.AbilityUtils;
import forge.game.ability.ApiType;
import forge.game.card.Card;
import forge.game.player.Player;
import forge.game.spellability.SpellAbility;
import forge.game.zone.ZoneType;

import java.util.List;
import java.util.Set;

/** Conservative presentation metadata from a legal engine ability, never Oracle text. */
final class HandPlacements {
    private static final Set<String> SIMPLE_PARAMS = Set.of("AB", "Cost", "Origin", "Destination", "ChangeType",
            "ChangeNum", "SorcerySpeed", "ActivationLimit", "ActivationZone", "ActivationPhases",
            "PlayerTurn", "OpponentTurn", "SpellDescription", "StackDescription");

    private HandPlacements() { }

    static List<Integer> eligibleCards(Player player, SpellAbility ability) {
        Card source = ability.getHostCard();
        if (!ability.isActivatedAbility() || ability.isManaAbility() || ability.getApi() != ApiType.ChangeZone
                || !source.isInZone(ZoneType.Battlefield) || source.isPhasedOut() || !player.equals(source.getController())
                || ability.usesTargeting() || ability.getSubAbility() != null
                || ability.getPayCosts() == null || !ability.getPayCosts().isFree()
                || !"Hand".equals(ability.getParam("Origin"))
                || !"Battlefield".equals(ability.getParam("Destination"))
                || !"1".equals(ability.getParamOrDefault("ChangeNum", "1"))
                || !SIMPLE_PARAMS.containsAll(ability.getMapParams().keySet())
                || ability.getParamOrDefault("ChangeType", "").startsWith("EACH")) return List.of();
        // ChangeZoneEffect uses this same engine predicate at resolution. Restrict
        // the hint to the acting player's own visible hand and actual land cards.
        return AbilityUtils.filterListByType(player.getCardsIn(ZoneType.Hand), ability.getParam("ChangeType"), ability)
                .stream().filter(Card::isLand).map(Card::getId).toList();
    }
}
