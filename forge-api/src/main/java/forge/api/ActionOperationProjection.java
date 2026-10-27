package forge.api;

import forge.game.GameObject;
import forge.game.ability.AbilityUtils;
import forge.game.ability.ApiType;
import forge.game.card.Card;
import forge.game.card.CardCollectionView;
import forge.game.card.CardView;
import forge.game.player.Player;
import forge.game.spellability.SpellAbility;
import forge.game.zone.ZoneType;

import java.util.*;
import java.util.function.Function;

/** Finite rules-operation graph. Unknown data is retained, never estimated as a resolved effect. */
final class ActionOperationProjection {
    private static final int MAX_OPERATIONS = 128;
    private static final Set<String> DISTRIBUTED_AMOUNTS = Set.of("DivideEvenly", "DividerOnResolution", "DividedAsYouChoose", "DividedRandomly");
    private static final Set<String> UNMODELED_RECIPIENTS = Set.of("Radiance", "CardChoices", "PlayerChoices", "RelativeTarget", "ExcessDamage");
    private final Player viewer;
    private final Function<CardView, String> identity;
    private final List<SpellAbility> abilities = new ArrayList<>();
    private final IdentityHashMap<SpellAbility, Integer> ids = new IdentityHashMap<>();
    private final List<List<Map<String, Object>>> links = new ArrayList<>();
    private boolean truncated;

    static Map<String, Object> describe(SpellAbility ability, Player viewer, Function<CardView, String> identity) {
        if (ability == null) return map("knowledge", "Unknown", "rootId", 0, "operations", List.of());
        return new ActionOperationProjection(viewer, identity).graph(ability);
    }

    static Map<String, Object> describeDecision(SpellAbility active, Player viewer, Function<CardView, String> identity) {
        if (active == null) return Map.of();
        var projection = new ActionOperationProjection(viewer, identity);
        var graph = projection.graph(active.getRootAbility());
        return map("operationGraph", graph, "operationId", projection.ids.get(active));
    }

    private ActionOperationProjection(Player viewer, Function<CardView, String> identity) {
        this.viewer = viewer;
        this.identity = identity;
    }

    private Map<String, Object> graph(SpellAbility root) {
        add(root);
        for (int index = 0; index < abilities.size(); index++) {
            SpellAbility ability = abilities.get(index);
            link(index, ability.getSubAbility(), "Sequence");
            for (var entry : new TreeMap<>(ability.getAdditionalAbilities()).entrySet())
                link(index, entry.getValue(), switch (entry.getKey()) {
                    case "TrueSubAbility" -> "IfTrue";
                    case "FalseSubAbility" -> "IfFalse";
                    case "RepeatSubAbility" -> "Repeat";
                    default -> "Additional";
                });
            for (var entry : new TreeMap<>(ability.getAdditionalAbilityLists()).entrySet())
                for (SpellAbility child : entry.getValue()) link(index, child, entry.getKey().equals("Choices") ? "Choice" : "Additional");
        }
        var nodes = new ArrayList<Map<String, Object>>();
        for (int index = 0; index < abilities.size(); index++) nodes.add(operation(index, abilities.get(index)));
        boolean known = !truncated && nodes.stream().allMatch(node -> node.get("knowledge").equals("Known"))
                && links.stream().flatMap(Collection::stream).noneMatch(edge -> edge.get("kind").equals("Additional"));
        return map("knowledge", known ? "Known" : "Unknown", "rootId", 0, "operations", List.copyOf(nodes));
    }

    private Integer add(SpellAbility ability) {
        if (ids.containsKey(ability)) return ids.get(ability);
        if (abilities.size() == MAX_OPERATIONS) { truncated = true; return null; }
        int index = abilities.size(); ids.put(ability, index); abilities.add(ability); links.add(new ArrayList<>());
        return index;
    }
    private void link(int from, SpellAbility child, String kind) {
        if (child == null) return;
        Integer to = add(child);
        if (to != null) links.get(from).add(map("operationId", to, "kind", kind));
    }

    private Map<String, Object> operation(int index, SpellAbility ability) {
        String effect = ActionSemantics.effect(ability);
        // The scalar field represents a recipient quantity, not an undistributed
        // total. Distribution choices need their own future structural model.
        Integer amount = ability.getMapParams().keySet().stream().anyMatch(DISTRIBUTED_AMOUNTS::contains)
                ? null : ActionSemantics.effectAmount(ability);
        boolean quantified = Set.of("Draw", "Damage", "AddCounters", "RemoveCounters", "LifeGain", "LifeLoss", "Token", "Mill", "Scry", "Proliferate", "MultiplyCounters", "Amass").contains(effect);
        boolean conditional = ability.getMapParams().keySet().stream().anyMatch(key -> key.startsWith("Condition")
                || key.startsWith("Unless") || Set.of("Optional", "OptionalDecider", "UpTo", "Upto", "DividedAsYouChoose", "DividedRandomly").contains(key));
        var recipients = recipients(index, ability, effect);
        var sources = sources(index, ability);
        String origin = canonicalZone(ability, "Origin"), destination = canonicalZone(ability, "Destination");
        var targets = ability.usesTargeting() ? targets(index, ability) : null;
        boolean known = !effect.equals("Unknown") && !conditional && (!quantified || amount != null)
                && sources.get("knowledge").equals("Known")
                && (effect.equals("Choose") || effect.equals("Cleanup") || recipients.get("knowledge").equals("Known"))
                && (targets == null || targets.get("knowledge").equals("Known"))
                && !Set.of("Proliferate", "Conditional", "Repeat", "Token", "Amass").contains(effect)
                && !ability.hasParam("DamageMap") // Damage is staged for a separate DamageResolve operation.
                && (!(ability.getApi() == ApiType.ChangeZone || ability.getApi() == ApiType.ChangeZoneAll) || !origin.isEmpty() && !destination.isEmpty())
                && !ability.getMapParams().keySet().stream().anyMatch(key -> Set.of("CounterType2", "CounterNum2", "CounterTypes",
                    "Placer", "DividedAsYouChoose", "DividedRandomly", "Monstrosity", "Adapt", "Bolster", "Tribute").contains(key));
        var node = map("id", index, "effect", effect, "knowledge", known ? "Known" : "Unknown",
                "actorPlayerId", ability.getActivatingPlayer() == null ? null : ability.getActivatingPlayer().getId(),
                "amount", amount, "amountKnowledge", amount == null ? "Unknown" : "Known",
                "counterType", ability.getParamOrDefault("CounterType", ""), "conditional", conditional,
                "originZone", origin, "destinationZone", destination,
                "sources", sources, "recipients", recipients, "targets", targets,
                "costs", ability.getPayCosts() == null ? List.of() : ability.getPayCosts().getCostParts().stream().map(ActionSemantics::cost).toList(),
                "links", List.copyOf(links.get(index)));
        if (ability.getApi() == ApiType.Charm) {
            Integer maximum = integer(ability.getParamOrDefault("CharmNum", "1"));
            Integer minimum = integer(ability.getParamOrDefault("MinCharmNum", ability.getParamOrDefault("CharmNum", "1")));
            node.put("minChoices", minimum); node.put("maxChoices", maximum);
            node.put("mayRepeatChoices", ability.hasParam("CanRepeatModes"));
            if (minimum == null || maximum == null || ability.hasParam("ChoiceRestriction") || ability.hasParam("Random")
                    || ability.hasParam("Pawprint") || ability.hasParam("Chooser")) node.put("knowledge", "Unknown");
            if (maximum != null && maximum > 1 && ability.hasParam("CanRepeatModes")
                    && links.get(index).stream().filter(edge -> edge.get("kind").equals("Choice"))
                        .anyMatch(edge -> containsTarget((Integer)edge.get("operationId"), new HashSet<>())))
                node.put("knowledge", "Unknown"); // Each chosen instance needs independent target-group bindings.
        } else if (effect.equals("Choose")) node.put("knowledge", "Unknown");
        return node;
    }

    private boolean containsTarget(int id, Set<Integer> seen) {
        if (!seen.add(id)) return false;
        return abilities.get(id).usesTargeting() || links.get(id).stream()
                .anyMatch(edge -> containsTarget((Integer)edge.get("operationId"), seen));
    }

    private Map<String, Object> sources(int index, SpellAbility ability) {
        if (ability.hasParam("DamageSource")) {
            String defined = ability.getParam("DamageSource");
            Integer target = targetBinding(ability, defined);
            if (Set.of("Targeted", "ThisTargeted").contains(defined))
                return target == null ? unknownRecipients() : binding(index, "Targets", target, selected(index, abilities.get(target)));
            if (!defined.equals("Self")) return unknownRecipients();
        }
        var source = entity(index, ability.getHostCard());
        return source == null ? unknownRecipients() : binding(index, "Source", null, List.of(source));
    }

    private Map<String, Object> targets(int index, SpellAbility ability) {
        var restrictions = ability.getTargetRestrictions();
        if (viewer == null || ability.getActivatingPlayer() == null)
            return map("knowledge", "Unknown", "min", null, "max", null, "candidates", List.of());
        var candidates = new ArrayList<Map<String, Object>>();
        boolean disclosed = true;
        for (GameObject entity : restrictions.getAllCandidates(ability)) {
            var projected = entity(index, entity);
            if (projected == null) disclosed = false; else candidates.add(projected);
        }
        if (restrictions.getZone().contains(ZoneType.Stack))
            for (var item : ability.getHostCard().getGame().getStack())
                if (ability.canTargetSpellAbility(item.getSpellAbility())) {
                    var projected = entity(index, item.getSpellAbility());
                    if (projected == null) disclosed = false; else candidates.add(projected);
                }
        candidates.sort(Comparator.comparing(candidate -> String.valueOf(candidate.get("id"))));
        Integer minimum = integer(restrictions.getMinTargets()), maximum = integer(restrictions.getMaxTargets());
        // Candidate enumeration is exact for this observation. Joint target-set
        // constraints and future variable cardinalities remain explicitly unknown.
        boolean known = disclosed && minimum != null && maximum != null
                && !restrictions.isUniqueTargets()
                && !ability.hasParam("MaxTotalTargetCMC") && !ability.hasParam("MaxTotalTargetPower")
                && !ability.hasParam("TargetsWithDefinedController") && !ability.hasParam("TargetsWithSharedCardType")
                && !ability.hasParam("TargetsWithRelatedProperty")
                && !restrictions.isWithoutSameCreatureType() && !restrictions.isWithSameCreatureType()
                && !restrictions.isWithSameCardType() && !restrictions.isDifferentCMC() && !restrictions.isDifferentNames()
                && !restrictions.isEqualToughness() && !restrictions.isDifferentControllers() && !restrictions.isForEachPlayer()
                && !restrictions.isSameController() && !restrictions.isRandomTarget() && !restrictions.isRandomNumTargets();
        return map("knowledge", known ? "Known" : "Unknown", "min", minimum, "max", maximum, "candidates", List.copyOf(candidates));
    }

    private Map<String, Object> recipients(int index, SpellAbility ability, String effect) {
        if (ability.getMapParams().keySet().stream().anyMatch(UNMODELED_RECIPIENTS::contains)) return unknownRecipients();
        if (ability.getApi() != null && Set.of(ApiType.DestroyAll, ApiType.PutCounterAll, ApiType.RemoveCounterAll).contains(ability.getApi()))
            return globalCards(index, ability);
        if (ability.getApi() != null && Set.of(ApiType.ChangeZoneAll, ApiType.DamageAll, ApiType.TapAll, ApiType.UntapAll,
                ApiType.EachDamage, ApiType.Proliferate, ApiType.Amass).contains(ability.getApi())) return unknownRecipients();
        String parameter = ability.getApi() == ApiType.Token ? "TokenOwner" : "Defined";
        boolean definedFirst = ability.getApi() == ApiType.Token || ability.getApi() == ApiType.Mana;
        if (ability.usesTargeting() && (!definedFirst || !ability.hasParam(parameter)))
            return binding(index, "Targets", index, selected(index, ability));
        boolean playerOperation = Set.of("Draw", "LifeGain", "LifeLoss", "Token", "Mill", "Scry", "Mana").contains(effect);
        String defined = ability.getParamOrDefault(parameter, playerOperation ? "You" : "Self");
        if (effect.equals("Search") && !ability.hasParam(parameter)) return unknownRecipients();
        Integer target = targetBinding(ability, defined);
        if (Set.of("Targeted", "ThisTargeted", "ParentTarget").contains(defined))
            return target == null ? unknownRecipients() : binding(index, "Targets", target, selected(index, abilities.get(target)));
        if (Set.of("TargetedController", "ThisTargetedController", "TargetedOwner", "ThisTargetedOwner").contains(defined))
            return target == null ? unknownRecipients() : binding(index, defined.endsWith("Owner") ? "TargetOwners" : "TargetControllers", target, List.of());
        if (defined.equals("You") && ability.getActivatingPlayer() != null)
            return binding(index, "Actor", null, List.of(entity(index, ability.getActivatingPlayer())));
        if (defined.equals("Self") && !playerOperation) {
            var source = entity(index, ability.getHostCard());
            return source == null ? unknownRecipients() : binding(index, "Source", null, List.of(source));
        }
        // Only public, deterministic player sets are resolved here. Remembered,
        // chosen and secret bindings require their own explicit knowledge model.
        if (Set.of("Player", "Opponent", "Player.Opponent", "Each", "EachOtherPlayer").contains(defined)) {
            var entities = AbilityUtils.getDefinedPlayers(ability.getHostCard(), defined, ability).stream()
                    .map(player -> entity(index, player)).toList();
            return binding(index, "Explicit", null, entities);
        }
        return unknownRecipients();
    }

    private Map<String, Object> globalCards(int index, SpellAbility ability) {
        if (viewer == null || ability.getActivatingPlayer() == null || ability.usesTargeting()
                || ability.hasParam("Defined") || ability.hasParam("ValidCards2") || ability.hasParam("CounterType2")
                || ability.hasParam("CounterNum2")) return unknownRecipients();
        String valid = ability.getParamOrDefault("ValidCards", "");
        if (valid.contains("X") || valid.contains("Remembered") || valid.contains("Chosen")) return unknownRecipients();
        ZoneType zone = ability.hasParam("ValidZone") ? ZoneType.smartValueOf(ability.getParam("ValidZone")) : ZoneType.Battlefield;
        if (zone != ZoneType.Battlefield) return unknownRecipients();
        CardCollectionView cards = AbilityUtils.filterListByType(ability.getHostCard().getGame().getCardsIn(zone), valid, ability);
        var entities = new ArrayList<Map<String, Object>>();
        for (Card card : cards) {
            var projected = entity(index, card);
            if (projected == null) return unknownRecipients();
            entities.add(projected);
        }
        entities.sort(Comparator.comparing(entity -> String.valueOf(entity.get("id"))));
        return binding(index, "Explicit", null, List.copyOf(entities));
    }

    private Integer targetBinding(SpellAbility ability, String defined) {
        if (defined.startsWith("ThisTargeted")) return ability.usesTargeting() ? ids.get(ability) : null;
        if (defined.equals("ParentTarget")) {
            SpellAbility parent = ability.getParentTargetingCard();
            return parent == null ? null : ids.get(parent);
        }
        var groups = new LinkedHashSet<SpellAbility>();
        var linearSeen = Collections.newSetFromMap(new IdentityHashMap<SpellAbility, Boolean>());
        for (SpellAbility part = ability.getRootAbility(); part != null && linearSeen.add(part); part = part.getSubAbility())
            if (part.usesTargeting()) groups.add(part);
        // An unchosen mode is not yet attached to its root's linear chain. Its
        // own structural ancestors still define the hypothetical mode's target.
        var seen = Collections.newSetFromMap(new IdentityHashMap<SpellAbility, Boolean>());
        for (SpellAbility part = ability; part != null && seen.add(part); part = part.getParent())
            if (part.usesTargeting()) groups.add(part);
        return groups.size() == 1 ? ids.get(groups.iterator().next()) : null;
    }
    private List<Map<String, Object>> selected(int index, SpellAbility ability) {
        var result = new ArrayList<Map<String, Object>>();
        for (GameObject target : ability.getTargets()) {
            var value = entity(index, target); if (value != null) result.add(value);
        }
        return List.copyOf(result);
    }
    private Map<String, Object> entity(int operation, GameObject target) {
        String prefix = "op" + operation + ":";
        if (target instanceof Player player)
            return map("id", prefix + "player:" + player.getId(), "kind", "Player", "cardId", "", "playerId", player.getId(),
                    "controllerId", player.getId(), "ownerId", null);
        Card card = target instanceof Card actual ? actual : target instanceof SpellAbility spell ? spell.getHostCard() : null;
        if (card == null || viewer == null || identity == null || card.getZone() == null || !card.getView().canBeShownTo(viewer.getView())) return null;
        // Equal physical IDs do not make an old LKI incarnation a current
        // target/source. Canonicalize only the same engine timestamp, before
        // asking the audience for a current occurrence identity.
        Card current = card.getGame().getCardState(card, null);
        if (current == null || !card.equalsWithGameTimestamp(current) || current.getZone() == null) return null;
        card = current;
        String cardId = identity.apply(card.getView());
        if (cardId == null || cardId.isEmpty()) return null;
        String id = prefix + "card:" + cardId, kind = "Card";
        if (target instanceof SpellAbility spell) {
            var stack = card.getGame().getStack().getInstanceMatchingSpellAbilityID(spell);
            if (stack == null) return null;
            id = prefix + "spell:" + stack.getId(); kind = "Spell";
        }
        return map("id", id, "kind", kind, "cardId", cardId, "playerId", null,
                "controllerId", target instanceof SpellAbility spell && spell.getActivatingPlayer() != null
                        ? spell.getActivatingPlayer().getId() : card.getController() == null ? null : card.getController().getId(),
                "ownerId", card.getOwner() == null ? null : card.getOwner().getId());
    }
    private static Map<String, Object> binding(int index, String kind, Integer target, List<Map<String, Object>> entities) {
        return map("knowledge", "Known", "binding", kind, "targetOperationId", target, "entities", entities);
    }
    private static Map<String, Object> unknownRecipients() {
        return map("knowledge", "Unknown", "binding", "Unknown", "targetOperationId", null, "entities", List.of());
    }
    private static Integer integer(String text) {
        try { return Integer.valueOf(text); } catch (NumberFormatException ignored) { return null; }
    }
    private static String canonicalZone(SpellAbility ability, String parameter) {
        if (!ability.hasParam(parameter) || parameter.equals("Origin") && ability.hasParam("OriginAlternative")) return "";
        try {
            var zones = ZoneType.listValueOf(ability.getParam(parameter));
            if (zones.size() != 1) return "";
            return switch (zones.get(0)) {
                case Battlefield, Hand, Library, Graveyard, Exile, Command, Stack, Sideboard -> zones.get(0).name();
                default -> "";
            };
        } catch (IllegalArgumentException invalid) { return ""; }
    }
    private static LinkedHashMap<String, Object> map(Object... fields) {
        var result = new LinkedHashMap<String, Object>();
        for (int index = 0; index < fields.length; index += 2) result.put((String)fields[index], fields[index + 1]);
        return result;
    }
    private ActionOperationProjection() { this(null, null); }
}
