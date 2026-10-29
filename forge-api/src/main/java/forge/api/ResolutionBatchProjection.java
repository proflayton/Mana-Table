package forge.api;

import com.google.gson.Gson;
import forge.game.card.CardView;
import forge.game.cost.CostPartMana;
import forge.game.spellability.SpellAbility;
import forge.game.trigger.WrappedAbility;
import forge.game.player.PlayerView;
import forge.gamemodes.match.input.InputPayMana;

import java.nio.charset.StandardCharsets;
import java.util.*;
import java.util.function.Function;

/** Current, audience-filtered equivalence facts. No card name, formatted cost,
 * description or historical nearest-stack guess can authorize repetition. */
final class ResolutionBatchProjection {
    record Group(String groupKey, String sourceCardId) { }
    private static final Gson JSON = new Gson();

    static Group group(SpellAbility original, PlayerView viewer, Function<CardView, String> identity) {
        if (original == null || !original.isTrigger()) return null;
        SpellAbility root = unwrap(original.getRootAbility());
        var host = root.getHostCard();
        if (host == null || host.isFaceDown() || !host.getView().canBeShownTo(viewer)
                || root.getActivatingPlayer() == null || host.getController() == null || root.getTrigger() == null) return null;
        String sourceId = identity.apply(host.getView());
        if (sourceId == null || sourceId.isEmpty()) return null;
        var nodes = new ArrayList<Object>();
        if (!shape(root, viewer, identity, nodes, Collections.newSetFromMap(new IdentityHashMap<>()))) return null;
        var shape = List.of(sourceId, host.getId(), host.getGameTimestamp(), host.getController().getId(),
                root.getActivatingPlayer().getId(), root.getTrigger().getClass().getName(), params(root.getTrigger().getMapParams()), nodes);
        String key = token(shape);
        return key == null ? null : new Group(key, sourceId);
    }

    static Map<String, Object> optional(InputPayMana input, PlayerView viewer, Function<CardView, String> identity,
                                        String executionId, MatchExecutions.Frame executions) {
        if (!input.isPaymentActive() || input.isPaymentMandatory() || input.getPayer() == null
                || input.getPayer().getId() != viewer.getId()) return null;
        var ability = input.getPaidForAbility();
        if (ability == null || ability.getPayCosts() == null || input.getRemainingManaCost() == null || input.getRemainingManaCost().isPaid()
                || ability.getPayCosts().getCostParts().isEmpty()
                || ability.getPayCosts().getCostParts().stream().anyMatch(part -> !(part instanceof CostPartMana))) return null;
        var group = group(ability, viewer, identity);
        if (group == null) return null;
        var indexed = new HashMap<String, MatchExecutions.Occurrence>();
        for (var occurrence : executions.occurrences()) indexed.put(occurrence.id(), occurrence);
        var current = indexed.get(executionId);
        var visited = new HashSet<String>();
        while (current != null && visited.add(current.id()) && !current.kind().equals("trigger"))
            current = current.parentId() == null ? null : indexed.get(current.parentId());
        if (current == null || !current.kind().equals("trigger") || !Objects.equals(current.sourceCardId(), group.sourceCardId())) return null;
        String purpose = ability.isTrigger() ? "TriggeredAbilityCost" : input.isEffectPayment() ? "EffectCost" : "Unknown";
        if (purpose.equals("Unknown")) return null;
        var costs = new ArrayList<Object>();
        for (var part : ability.getPayCosts().getCostParts()) {
            var mana = (CostPartMana) part;
            // These variable/external commitments need a separate equivalence
            // proof. The first supported family is literal mana-only costs.
            if (mana.isExiledCreatureCost() || mana.isEnchantedCreatureCost() || mana.isCostPayAnyNumberOfTimes() || mana.getAmountOfX() > 0
                    || mana.getMaxWaterbend() != null || mana.getXMin() != 0) return null;
            costs.add(List.of(part.getClass().getName(), mana.getMana().getGenericCost(),
                    forge.card.mana.ManaCost.serialize(mana.getMana()), params(unwrap(ability).getMapParams())));
        }
        String paymentKey = token(List.of(group.groupKey(), purpose, input.getPayer().getId(), input.isEffectPayment(),
                costs, ManaPaymentFacts.parts(input.getRemainingManaCost()), params(unwrap(ability).getMapParams())));
        if (paymentKey == null) return null;
        return new LinkedHashMap<>(Map.of("groupKey", group.groupKey(), "paymentKey", paymentKey,
                "producingTriggerExecutionId", current.id(), "sourceCardId", group.sourceCardId(),
                "purpose", purpose, "payerId", input.getPayer().getId(), "mandatory", false));
    }

    private static SpellAbility unwrap(SpellAbility ability) {
        while (ability instanceof WrappedAbility wrapped) ability = wrapped.getWrappedAbility();
        return ability;
    }

    private static boolean shape(SpellAbility original, PlayerView viewer, Function<CardView, String> identity,
                                 List<Object> nodes, Set<SpellAbility> seen) {
        SpellAbility ability = unwrap(original);
        if (!seen.add(ability) || nodes.size() >= 64) return false;
        var targets = new ArrayList<Object>();
        if (ability.getTargets() != null) {
            for (var target : ability.getTargets().getTargetCards()) {
                if (!target.getView().canBeShownTo(viewer)) return false;
                String id = identity.apply(target.getView());
                if (id == null || id.isEmpty()) return false;
                targets.add(List.of("card", id, target.getGameTimestamp()));
            }
            for (var target : ability.getTargets().getTargetPlayers()) targets.add(List.of("player", target.getId()));
        }
        nodes.add(List.of(ability.getClass().getName(), params(ability.getMapParams()), variables(ability.getSVars()), targets));
        if (ability.getSubAbility() != null && !shape(ability.getSubAbility(), viewer, identity, nodes, seen)) return false;
        // Additional branches are not equivalent merely because their headline
        // matches. Do not certify an unsupported graph as a simple repetition.
        return ability.getAdditionalAbilities().isEmpty() && ability.getAdditionalAbilityLists().isEmpty();
    }

    private static Map<String, String> params(Map<String, String> original) {
        var result = new TreeMap<>(original);
        result.keySet().removeIf(key -> key.contains("Desc") || key.startsWith("AI"));
        return result;
    }

    private static Map<String, Object> variables(Map<String, String> original) {
        var result = new TreeMap<String, Object>();
        for (var entry : original.entrySet()) {
            if (entry.getKey().startsWith("AI")) continue;
            var fields = new TreeMap<String, String>();
            boolean script = false;
            for (String field : entry.getValue().split("\\|")) {
                int separator = field.indexOf('$');
                if (separator < 0) { fields.clear(); break; }
                script = true;
                fields.put(field.substring(0, separator).trim(), field.substring(separator + 1).trim());
            }
            result.put(entry.getKey(), script && !fields.isEmpty() ? params(fields) : entry.getValue());
        }
        return result;
    }

    private static String token(Object shape) {
        String raw = JSON.toJson(shape);
        if (raw.length() > 65536) return null;
        return UUID.nameUUIDFromBytes(raw.getBytes(StandardCharsets.UTF_8)).toString();
    }
    private ResolutionBatchProjection() { }
}
