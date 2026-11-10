package forge.game.spellability;

import forge.game.card.Card;
import forge.game.cost.*;

import java.nio.charset.StandardCharsets;
import java.util.*;

/** A rules-route correlation, not an occurrence or permission to execute it. */
public final class SpellAbilityRouteIdentity {
    public static String stableId(SpellAbility ability) {
        Card host = ability.getHostCard();
        var params = new TreeMap<>(ability.getMapParams());
        // Display strings do not define a strategic route. Identical routes may
        // intentionally share a retry guard; opaque action IDs remain distinct.
        params.keySet().removeIf(key -> key.contains("Desc") || key.equals("AILogic"));
        String shape = host.getId() + ":" + host.getGameTimestamp() + ":" + ability.getClass().getName()
                + ":" + params + ":" + (ability.getPayCosts() == null ? "" : ability.getPayCosts().getCostParts().stream().map(SpellAbilityRouteIdentity::cost).toList());
        return UUID.nameUUIDFromBytes(shape.getBytes(StandardCharsets.UTF_8)).toString();
    }

    // Insertion order is part of the existing stable-ID shape. Keep the same
    // fresh value copy for action metadata and execution correlation.
    public static Map<String, Object> cost(CostPart part) {
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

    private static Integer integer(String text) {
        try { return Integer.valueOf(text); }
        catch (NumberFormatException ignored) { return null; }
    }
    private SpellAbilityRouteIdentity() { }
}
