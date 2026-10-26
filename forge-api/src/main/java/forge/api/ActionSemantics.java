package forge.api;

import forge.game.ability.ApiType;
import forge.game.card.Card;
import forge.game.card.CardView;
import forge.game.player.Player;
import forge.game.player.LibrarySearchOffer;
import forge.game.cost.*;
import forge.game.spellability.SpellAbility;
import forge.game.zone.ZoneType;
import forge.gamemodes.match.input.Input;
import forge.gamemodes.match.input.InputPayMana;

import java.nio.charset.StandardCharsets;
import java.util.*;
import java.util.function.Function;

/** Rules metadata for policies. Never derive strategy from printed names or localized prose. */
final class ActionSemantics {
    static Map<String, Object> describe(SourceActions.Action action) {
        var result = describeAbility(action.ability());
        result.put("actionId", action.id());
        return result;
    }

    static Map<String, Object> describe(SourceActions.Action action, Input input) {
        return describe(action, input, null, null);
    }

    static Map<String, Object> describe(SourceActions.Action action, Input input, Player viewer, Function<CardView, String> identity) {
        var result = describeAbility(action.ability(), viewer, identity);
        result.put("actionId", action.id());
        if (input instanceof InputPayMana payment)
            result.put("paymentManaColors", ((Number) result.get("manaAmount")).intValue() <= 0
                    ? List.of() : payment.getUsableManaColors(action.ability()));
        return result;
    }

    static Map<String, Object> describeAbility(SpellAbility ability) {
        return describeAbility(ability, null, null);
    }

    static Map<String, Object> describeAbility(SpellAbility ability, Player viewer, Function<CardView, String> identity) {
        var effects = new ArrayList<String>();
        var seen = Collections.newSetFromMap(new IdentityHashMap<SpellAbility, Boolean>());
        for (SpellAbility part = ability; part != null && seen.add(part); part = part.getSubAbility())
            effects.add(effect(part));
        var costs = new ArrayList<Map<String, Object>>();
        if (ability.getPayCosts() != null)
            for (CostPart part : ability.getPayCosts().getCostParts()) costs.add(cost(part));

        String affordability = "Unknown";
        // Cost.canPay deliberately does not test mana. Preserve that distinction;
        // never omit a route because a legacy planner cannot pay it.
        if (ability.getPayCosts() != null && ability.getActivatingPlayer() != null
                && !ability.getPayCosts().canPay(ability, ability.getActivatingPlayer(), false))
            affordability = "Unavailable";
        else if (ability.isLandAbility() || ability.getPayCosts() == null
                || ability.getPayCosts().getCostParts().isEmpty()) affordability = "Payable";
        else if (ability.getActivatingPlayer() != null && ability.getPayCosts().getCostParts().size() == 1
                && ability.getPayCosts().getCostParts().stream().allMatch(part -> part instanceof CostTap
                    || part instanceof CostUntap || part instanceof CostPayLife)) affordability = "Payable";

        var colors = new ArrayList<String>();
        int produced = 0;
        if (ability.isManaAbility()) {
            for (String color : List.of("W", "U", "B", "R", "G", "C"))
                if (ability.canProduce(color)) colors.add(color);
            var manaParts = Collections.newSetFromMap(new IdentityHashMap<SpellAbility, Boolean>());
            for (SpellAbility part = ability; part != null && manaParts.add(part); part = part.getSubAbility())
                produced += Math.max(0, part.amountOfManaGenerated(true));
        }
        var result = new LinkedHashMap<String, Object>();
        result.put("effect", effects.isEmpty() ? "Unknown" : effects.get(0));
        result.put("effects", List.copyOf(effects));
        result.put("costs", List.copyOf(costs));
        result.put("affordability", affordability);
        result.put("stableId", stableId(ability));
        result.put("manaAbility", ability.isManaAbility());
        result.put("manaValue", ability.getPayCosts() == null ? 0 : ability.getPayCosts().getTotalMana().getCMC());
        result.put("manaColors", List.copyOf(colors));
        result.put("manaAmount", produced);
        result.put("targeted", ability.usesTargeting());
        result.put("targetsAvailable", targetsAvailable(ability));
        // Costs/once-per-turn restrictions still remain authority at invocation.
        result.put("repeatable", ability.isActivatedAbility());
        result.put("effectAmount", effectAmount(ability));
        result.put("operationGraph", ActionOperationProjection.describe(ability, viewer, identity));
        result.put("production", ManaProductionProjection.describe(ability));
        result.put("manaCostKnowledge", "Unknown");
        return result;
    }

    private static Boolean targetsAvailable(SpellAbility ability) {
        boolean targeted = false;
        var seen = Collections.newSetFromMap(new IdentityHashMap<SpellAbility, Boolean>());
        for (SpellAbility part = ability; part != null && seen.add(part); part = part.getSubAbility()) {
            if (!part.usesTargeting()) continue;
            targeted = true;
            if (part.getActivatingPlayer() == null) continue;
            if (part.getTargetRestrictions().getNumCandidates(part) < part.getMinTargets()) return false;
        }
        return targeted ? null : true;
    }

    static String effect(SpellAbility ability) {
        if (ability.isLandAbility()) return "Land";
        ApiType api = ability.getApi();
        if (api == null) return "Unknown";
        return switch (api) {
            case PermanentCreature, PermanentNoncreature -> "Permanent";
            case Mana, ManaReflected -> "Mana";
            case Draw -> "Draw";
            case DealDamage, DamageAll, EachDamage -> "Damage";
            case Destroy, DestroyAll -> "Destroy";
            case Counter -> "CounterSpell";
            case PutCounter, PutCounterAll -> "AddCounters";
            case Proliferate -> "Proliferate";
            case MultiplyCounter -> "MultiplyCounters";
            case RemoveCounter, RemoveCounterAll -> "RemoveCounters";
            case GainLife -> "LifeGain";
            case LoseLife -> "LifeLoss";
            case Token, Investigate -> "Token";
            case Amass -> "Amass";
            case Tap, TapAll -> "Tap";
            case Untap, UntapAll -> "Untap";
            case Discard -> "Discard";
            case Mill -> "Mill";
            case GainControl, GainControlVariant, ExchangeControl, ExchangeControlVariant -> "ChangeControl";
            case Scry -> "Scry";
            case Charm, ChooseCard, ChoosePlayer, ChooseColor, ChooseNumber, ChooseType, GenericChoice -> "Choose";
            case Branch -> "Conditional";
            case Repeat, RepeatEach -> "Repeat";
            case Cleanup -> "Cleanup";
            case ChangeZone, ChangeZoneAll -> zoneEffect(ability);
            default -> "Unknown";
        };
    }

    private static String zoneEffect(SpellAbility ability) {
        // These are scripting parameters interpreted by the rules engine, not
        // card wording. Multi-zone/multi-destination scripts stay Unknown.
        if (LibrarySearchOffer.targeting(ability) != null) return "Search";
        return switch (ability.getParamOrDefault("Destination", "")) {
            case "Exile" -> "Exile";
            case "Hand" -> ability.getParamOrDefault("Origin", "").equals(ZoneType.Battlefield.name()) ? "Bounce" : "MoveCard";
            default -> "MoveCard";
        };
    }

    static Integer effectAmount(SpellAbility ability) {
        String key = switch (effect(ability)) {
            case "Draw" -> "NumCards";
            case "Damage" -> "NumDmg";
            case "AddCounters", "RemoveCounters" -> "CounterNum";
            case "LifeGain", "LifeLoss" -> "LifeAmount";
            case "Token" -> ability.getApi() == ApiType.Investigate ? "Num" : "TokenAmount";
            case "Amass" -> "Num";
            case "Mill" -> "NumCards";
            case "Scry" -> "ScryNum";
            case "Proliferate" -> "Amount";
            case "MultiplyCounters" -> "Multiplier";
            default -> "";
        };
        if (key.isEmpty()) return null;
        // Variable amounts can depend on future choices (X, targets, paid costs).
        // Project only a literal fixed quantity rather than evaluating prematurely.
        return integer(ability.getParamOrDefault(key, key.equals("Multiplier") ? "2" : "1"));
    }

    static Map<String, Object> cost(CostPart part) {
        var result = new LinkedHashMap<String, Object>();
        String kind = part instanceof CostPartMana ? "Mana"
                : part instanceof CostTap || part instanceof CostTapType ? "Tap"
                : part instanceof CostUntap || part instanceof CostUntapType ? "Untap"
                : part instanceof CostPayLife ? "Life"
                : part instanceof CostSacrifice ? "Sacrifice"
                : part instanceof CostDiscard ? "Discard"
                : part instanceof CostExile || part instanceof CostExileFromStack ? "Exile"
                : part instanceof CostRemoveCounter || part instanceof CostRemoveAnyCounter ? "RemoveCounters"
                : part instanceof CostPutCounter || part instanceof CostPutCounterYou ? "AddCounters" : "Other";
        result.put("kind", kind);
        result.put("amount", part instanceof CostPartMana mana ? mana.getMana().getGenericCost() : integer(part.getAmount()));
        result.put("fromSource", part instanceof CostTap || part instanceof CostUntap || part.payCostFromSource());
        result.put("counterType", part instanceof CostRemoveCounter remove && remove.counter != null ? remove.counter.toString()
                : part instanceof CostRemoveAnyCounter remove && remove.counter != null ? remove.counter.toString()
                : part instanceof CostPutCounter put && put.getCounter() != null ? put.getCounter().toString() : "");
        var symbols = new ArrayList<String>();
        if (part instanceof CostPartMana mana) mana.getMana().forEach(shard -> symbols.add(shard.toString()));
        result.put("manaSymbols", List.copyOf(symbols));
        return result;
    }

    private static String stableId(SpellAbility ability) {
        Card host = ability.getHostCard();
        var params = new TreeMap<>(ability.getMapParams());
        // Display strings do not define a strategic route. Identical routes may
        // intentionally share a retry guard; opaque action IDs remain distinct.
        params.keySet().removeIf(key -> key.contains("Desc") || key.equals("AILogic"));
        String shape = host.getId() + ":" + host.getGameTimestamp() + ":" + ability.getClass().getName()
                + ":" + params + ":" + (ability.getPayCosts() == null ? "" : ability.getPayCosts().getCostParts().stream().map(ActionSemantics::cost).toList());
        return UUID.nameUUIDFromBytes(shape.getBytes(StandardCharsets.UTF_8)).toString();
    }

    private static Integer integer(String text) {
        try { return Integer.valueOf(text); }
        catch (NumberFormatException ignored) { return null; }
    }
    private ActionSemantics() { }
}
