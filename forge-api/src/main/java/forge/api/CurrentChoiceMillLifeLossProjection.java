package forge.api;

import forge.game.ability.ApiType;
import forge.game.card.Card;
import forge.game.card.CardView;
import forge.game.card.CounterEnumType;
import forge.game.spellability.SpellAbility;

import java.util.*;
import java.util.function.Function;

/** A closed nominal dependency, not a numerical forecast or an expression API.
 * In particular this extractor never reads a library or remembered object. */
final class CurrentChoiceMillLifeLossProjection {
    private static final Set<String> OWNER = Set.of("DB", "ValidTgts", "Choices", "ChoiceAmount", "ChoicePrompt", "SpellDescription", "AILogic");
    private static final Set<String> MILL = Set.of("DB", "Defined", "NumCards", "RememberMilled", "SubAbility", "SpellDescription");
    private static final Set<String> LOSS = Set.of("DB", "Defined", "LifeAmount", "SubAbility", "SpellDescription");
    private static final Set<String> CLEANUP = Set.of("DB", "ClearRemembered", "SpellDescription");

    static Map<String, Object> describe(SpellAbility mill, CurrentEffectChoice choice,
                                         Map<String, Object> graph, Function<CardView, String> identity) {
        if (choice == null || identity == null || choice.offered.size() != 2
                || choice.owner.getAdditionalAbilityList("Choices").size() != 2 || choice.selectedPlayer() == null
                || !OWNER.containsAll(choice.owner.getMapParams().keySet())
                || !choice.owner.getParamOrDefault("ChoiceAmount", "1").equals("1")
                || choice.owner.getSubAbility() != null || !noCost(choice.owner)
                || !choice.owner.getAdditionalAbilities().isEmpty()
                || !choice.owner.getAdditionalAbilityLists().keySet().equals(Set.of("Choices"))
                || !choice.owns(mill) || mill.getParent() != choice.owner || mill.getApi() != ApiType.Mill
                || !plain(mill, MILL) || !mill.getParamOrDefault("Defined", "").equals("You")
                || !mill.getParamOrDefault("RememberMilled", "").equals("True")) return null;
        var loss = mill.getSubAbility();
        var cleanup = loss == null ? null : loss.getSubAbility();
        if (loss == null || cleanup == null || loss.getApi() != ApiType.LoseLife || cleanup.getApi() != ApiType.Cleanup
                || !plain(loss, LOSS) || !plain(cleanup, CLEANUP) || cleanup.getSubAbility() != null
                || !loss.getParamOrDefault("Defined", "").equals("ParentTarget")
                || !cleanup.getParamOrDefault("ClearRemembered", "").equals("True")
                || choice.parentPlayer(loss) == null) return null;
        Card source = mill.getHostCard();
        if (source != choice.owner.getHostCard() || source != loss.getHostCard() || source != cleanup.getHostCard()
                || mill.getActivatingPlayer() == null || mill.getActivatingPlayer() != choice.owner.getActivatingPlayer()
                || mill.getActivatingPlayer() != loss.getActivatingPlayer() || mill.getActivatingPlayer() != cleanup.getActivatingPlayer()
                || mill.getActivatingPlayer() != source.getController()
                // Empty only: never inspect the identities, types, counts or values.
                || source.hasRemembered()) return null;
        String quantity = variable(mill, "NumCards"), aggregate = variable(loss, "LifeAmount");
        if (quantity == null || !quantity.startsWith("Count$CardCounters.") || !"Remembered$CardManaCost".equals(aggregate)) return null;
        CounterEnumType counter;
        try { counter = CounterEnumType.valueOf(quantity.substring("Count$CardCounters.".length())); }
        catch (IllegalArgumentException invalid) { return null; }
        String sourceId = identity.apply(source.getView());
        if (sourceId == null || sourceId.isEmpty()) return null;
        // No native counter-type registry is inferred from an absent zero key.
        // Certify only an exact key already present in the public dictionary.
        if (source.getView().getCounters() == null || !source.getView().getCounters().contains(counter)) return null;
        int observed = source.getView().getCounters(counter);
        if (observed < 0 || observed != source.getCounters(counter)) return null;
        var nodes = nodes(graph);
        if (!"Unknown".equals(graph.get("knowledge")) || nodes.size() != 3) return null;
        var root = nodes.stream().filter(node -> Objects.equals(node.get("id"), graph.get("rootId"))).findFirst().orElse(null);
        var lossNode = nodes.stream().filter(node -> "LifeLoss".equals(node.get("effect"))).findFirst().orElse(null);
        var cleanupNode = nodes.stream().filter(node -> "Cleanup".equals(node.get("effect"))).findFirst().orElse(null);
        if (root == null || lossNode == null || cleanupNode == null || !"Mill".equals(root.get("effect"))
                || !unknownAmount(root) || !unknownAmount(lossNode)
                || !sequence(root, lossNode) || !sequence(lossNode, cleanupNode)
                || !((List<?>)cleanupNode.get("links")).isEmpty()) return null;
        return Map.of("version", 1, "sourceCardId", sourceId, "counterType", counter.getName(),
                "observedCount", observed, "lifeLossOperationId", lossNode.get("id"));
    }

    private static boolean noCost(SpellAbility ability) {
        return CurrentEffectChoice.noCost(ability);
    }
    private static boolean plain(SpellAbility ability, Set<String> allowed) {
        return !ability.isWrapper() && !ability.isReplacementAbility() && !ability.usesTargeting()
                && allowed.containsAll(ability.getMapParams().keySet()) && noCost(ability)
                && ability.getAdditionalAbilities().isEmpty() && ability.getAdditionalAbilityLists().isEmpty();
    }
    private static String variable(SpellAbility ability, String parameter) {
        String name = ability.getParamOrDefault(parameter, "");
        if (!name.matches("[A-Za-z][A-Za-z0-9_]{0,63}")) return null;
        String value = ability.getSVar(name);
        return value == null || value.isBlank() ? ability.getHostCard().getSVar(name) : value;
    }
    private static boolean unknownAmount(Map<String, Object> node) {
        return "Unknown".equals(node.get("knowledge")) && "Unknown".equals(node.get("amountKnowledge")) && node.get("amount") == null;
    }
    private static boolean sequence(Map<String, Object> from, Map<String, Object> to) {
        var links = (List<?>)from.get("links");
        return links.size() == 1 && links.get(0) instanceof Map<?, ?> link
                && "Sequence".equals(link.get("kind")) && Objects.equals(link.get("operationId"), to.get("id"));
    }
    @SuppressWarnings("unchecked")
    private static List<Map<String, Object>> nodes(Map<String, Object> graph) {
        return (List<Map<String, Object>>)graph.get("operations");
    }
    private CurrentChoiceMillLifeLossProjection() { }
}
