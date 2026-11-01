package forge.game.mana;

import forge.game.ability.ApiType;
import forge.game.card.CounterEnumType;
import forge.game.cost.CostPartMana;
import forge.game.cost.CostTap;
import forge.game.replacement.ReplacementType;
import forge.game.spellability.SpellAbility;
import forge.game.trigger.TriggerType;
import forge.game.zone.ZoneType;

import java.util.Set;

/** Conservative current-source reads, without resolving an ability or evaluating the amount DSL. */
public final class ManaSourceFacts {
    /** Null is unknown, distinct from a known zero. This is not payment permission. */
    public static Integer currentAmount(SpellAbility ability) {
        String value = ability.getParamOrDefault("Amount", "1");
        try { return Integer.valueOf(value); } catch (NumberFormatException notLiteral) { }
        String expression = value.contains("$") ? value : ability.getSVar(value);
        String prefix = "Count$CardCounters.";
        if (!expression.startsWith(prefix)) return null;
        String counter = expression.substring(prefix.length());
        if (!counter.matches("[A-Z][A-Z0-9_]*") || !simpleTapSourceIsStable(ability)) return null;
        long count;
        if (counter.equals("ALL")) count = ability.getHostCard().getCounters().entrySet().stream().mapToLong(entry -> entry.getCount()).sum();
        else {
            try { count = ability.getHostCard().getCounters(CounterEnumType.valueOf(counter)); }
            catch (IllegalArgumentException unsupported) { return null; }
        }
        return count >= 0 && count <= 1024 ? (int) count : null;
    }

    /** Exact nominal units for interchangeable, unconditional tap-only modes; null keeps legacy ordering. */
    public static Integer comparableTapOutput(SpellAbility ability) {
        if (ability.getApi() != ApiType.Mana || !simpleTapSourceIsStable(ability)
                || ability.getMapParams().keySet().stream().anyMatch(key -> key.startsWith("Condition") || key.startsWith("Unless")
                    || Set.of("Optional", "Chooser").contains(key))) return null;
        String produced = ability.getManaPart().getOrigProduced();
        if (!produced.equals("Any") && !produced.matches("[WUBRGC]( [WUBRGC])*")) return null;
        Integer amount = currentAmount(ability);
        if (amount == null || amount < 0 || amount > 1024) return null;
        // Each multiplies Combo choices in the resolver; it does not multiply
        // these fixed/Any outputs. Combo remains outside this ordering fact.
        long units = (long) amount * (produced.equals("Any") ? 1 : produced.split(" ").length);
        return units <= 1024 ? (int) units : null;
    }

    /** Current public source-state stability, separate from timing and payment legality. */
    public static boolean simpleTapSourceIsStable(SpellAbility ability) {
        var source = ability.getHostCard();
        var actor = ability.getActivatingPlayer();
        var mana = ability.getManaPart();
        if (actor == null || source == null || source.isFaceDown() || !source.isInZone(ZoneType.Battlefield)
                || source.getController() != actor || ability.getRootAbility() != ability || ability.getSubAbility() != null
                || !ability.getAdditionalAbilities().isEmpty() || !ability.getAdditionalAbilityLists().isEmpty()
                || mana == null || mana.getSourceCard() != source || mana.addsCounters(ability) || mana.addKeywords(ability)
                || !mana.getManaRestrictions().isEmpty() || !mana.getExtraManaRestriction().isEmpty()
                || mana.getTriggersWhenSpent() || mana.isCannotCounterPaidWith() || mana.isCombatMana() || mana.isPersistentMana()
                || ability.hasParam("Defined") && !ability.getParam("Defined").equals("You") || ability.getPayCosts() == null
                || ability.getPayCosts().getCostParts().stream().filter(CostTap.class::isInstance).count() != 1
                || ability.getPayCosts().getCostParts().stream().anyMatch(cost -> !(cost instanceof CostTap)
                    && !(cost instanceof CostPartMana part && part.getMana().isZero()))) return false;
        // Concealed originals are not inputs. Granted current traits remain
        // relevant even on a face-down permanent, just as they are in the rules.
        for (ZoneType zone : new ZoneType[] { ZoneType.Battlefield, ZoneType.Command })
            for (var card : source.getGame().getCardsIn(zone)) {
                if (!card.getView().canBeShownTo(actor.getView())) return false;
                var current = card.getCurrentState();
                if (current.getReplacementEffects(false).stream().anyMatch(replacement -> replacement.zonesCheck(card.getZone())
                        && (replacement.getMode() == ReplacementType.ProduceMana || replacement.getMode() == ReplacementType.Tap))) return false;
                if (current.getTriggers().stream().anyMatch(trigger -> trigger.zonesCheck(card.getZone())
                        && Set.of(TriggerType.Taps, TriggerType.TapAll, TriggerType.TapsForMana, TriggerType.ManaAdded).contains(trigger.getMode()))) return false;
            }
        return true;
    }

    private ManaSourceFacts() { }
}
