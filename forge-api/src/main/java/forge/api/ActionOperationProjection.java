package forge.api;

import forge.game.GameObject;
import forge.game.ability.AbilityUtils;
import forge.game.ability.AbilityFactory;
import forge.game.ability.ApiType;
import forge.game.card.Card;
import forge.game.card.CardCollectionView;
import forge.game.card.CardView;
import forge.game.player.Player;
import forge.game.spellability.SpellAbility;
import forge.game.spellability.StackItemView;
import forge.game.trigger.WrappedAbility;
import forge.game.zone.ZoneType;

import java.util.*;
import java.util.function.Function;
import java.util.function.Supplier;

/** Finite rules-operation graph. Unknown data is retained, never estimated as a resolved effect. */
final class ActionOperationProjection {
    private static final int MAX_OPERATIONS = 128;
    private static final int MAX_LINKS = 512;
    private static final Set<String> DISTRIBUTED_AMOUNTS = Set.of("DivideEvenly", "DividerOnResolution", "DividedAsYouChoose", "DividedRandomly");
    private static final Set<String> UNMODELED_RECIPIENTS = Set.of("Radiance", "CardChoices", "PlayerChoices", "RelativeTarget", "ExcessDamage");
    private final Player viewer;
    private final Function<CardView, String> identity;
    private final boolean committed;
    private sealed interface GraphNode { }
    private record LiveNode(SpellAbility ability) implements GraphNode { }
    private record RecipeNode(ScheduledOperationRecipe recipe) implements GraphNode { }
    private record RecipeKey(String name, boolean trigger, String executeOverride, boolean actorKnown, boolean textChanges) { }
    private final List<GraphNode> operations = new ArrayList<>();
    private final IdentityHashMap<SpellAbility, Integer> ids = new IdentityHashMap<>();
    private final IdentityHashMap<SpellAbility, Map<RecipeKey, Integer>> recipeIds = new IdentityHashMap<>();
    private final List<List<Map<String, Object>>> links = new ArrayList<>();
    private boolean truncated;
    private int linkCount;

    static Map<String, Object> describe(SpellAbility ability, Player viewer, Function<CardView, String> identity) {
        if (ability == null) return unknown();
        return new ActionOperationProjection(viewer, identity, false).graph(ability);
    }

    /** Same grammar as a prospective action, but only the operations and target
     * occurrences actually committed on this stack instance. No legal-target
     * query or unchosen modal recipe may expand this observation. */
    static Map<String, Object> describeCommitted(SpellAbility ability, Player viewer, Function<CardView, String> identity) {
        if (ability == null || viewer == null || ability.getHostCard() == null
                || ability.getHostCard().isFaceDown()
                || !ability.getHostCard().getView().canBeShownTo(viewer.getView())) return unknown();
        return new ActionOperationProjection(viewer, identity, true).graph(ability);
    }

    static Map<String, Object> unknown() {
        return map("knowledge", "Unknown", "rootId", 0, "operations", List.of());
    }

    static Map<String, Object> stackTarget(StackItemView view, Player viewer, Function<CardView, String> identity) {
        if (viewer == null || view == null) return null;
        for (var instance : viewer.getGame().getStack())
            if (instance.getView() == view)
                return new ActionOperationProjection(viewer, identity, true).entity(0, instance.getSpellAbility());
        return null;
    }

    static Map<String, Object> describeDecision(SpellAbility active, Player viewer, Function<CardView, String> identity) {
        if (active == null) return Map.of();
        var projection = new ActionOperationProjection(viewer, identity, false);
        var graph = projection.graph(active.getRootAbility());
        return map("operationGraph", graph, "operationId", ((List<?>)graph.get("operations")).isEmpty() ? null : projection.ids.get(active));
    }

    private ActionOperationProjection(Player viewer, Function<CardView, String> identity, boolean committed) {
        this.viewer = viewer;
        this.identity = identity;
        this.committed = committed;
    }

    private Map<String, Object> graph(SpellAbility root) {
        add(root);
        for (int index = 0; index < operations.size(); index++) {
            if (operations.get(index) instanceof RecipeNode recipe) {
                expandRecipe(index, recipe.recipe());
                continue;
            }
            SpellAbility ability = live(index);
            link(index, ability.getSubAbility(), "Sequence");
            for (var entry : new TreeMap<>(ability.getAdditionalAbilities()).entrySet()) {
                if (entry.getKey().equals("Execute") && (ability.getApi() == ApiType.DelayedTrigger || ability.getApi() == ApiType.ImmediateTrigger)) {
                    var child = entry.getValue();
                    boolean knownActor = !ability.hasParam("TriggerController");
                    recipeLink(index, child, new RecipeKey("", false, "", knownActor, false),
                            () -> ScheduledOperationRecipe.copied(child, knownActor), "Scheduled");
                    continue;
                }
                link(index, entry.getValue(), switch (entry.getKey()) {
                    case "TrueSubAbility" -> "IfTrue";
                    case "FalseSubAbility" -> "IfFalse";
                    case "RepeatSubAbility" -> "Repeat";
                    default -> "Additional";
                });
            }
            for (var entry : new TreeMap<>(ability.getAdditionalAbilityLists()).entrySet()) {
                // CharmEffect has already copied chosen modes into the linear
                // subability chain. Choices still contains every unused recipe.
                if (committed && entry.getKey().equals("Choices")) continue;
                for (SpellAbility child : entry.getValue()) link(index, child, entry.getKey().equals("Choices") ? "Choice" : "Additional");
            }
            if ((ability.getApi() == ApiType.DelayedTrigger || ability.getApi() == ApiType.ImmediateTrigger)
                    && ability.hasParam("Execute") && !ability.hasAdditionalAbility("Execute"))
                recipeLink(index, ability, ability.getParam("Execute"), false, "", !ability.hasParam("TriggerController"), true, "Scheduled");
            expandScheduled(index, ability, parameters(ability), ability.getApi(), true);
        }
        if (truncated || !acyclic(0, new HashSet<>(), new HashSet<>()))
            return map("knowledge", "Unsupported", "rootId", 0, "operations", List.of());
        var nodes = new ArrayList<Map<String, Object>>();
        for (int index = 0; index < operations.size(); index++) nodes.add(operations.get(index) instanceof RecipeNode recipe
                ? recipeOperation(index, recipe.recipe()) : operation(index, live(index)));
        boolean known = !truncated && nodes.stream().allMatch(node -> node.get("knowledge").equals("Known"))
                && links.stream().flatMap(Collection::stream).noneMatch(edge -> Set.of("Additional", "Scheduled").contains(edge.get("kind")));
        return map("knowledge", known ? "Known" : "Unknown", "rootId", 0, "operations", List.copyOf(nodes));
    }

    private boolean acyclic(int operation, Set<Integer> complete, Set<Integer> active) {
        if (complete.contains(operation)) return true;
        if (!active.add(operation)) return false;
        for (var edge : links.get(operation))
            if (!acyclic((Integer)edge.get("operationId"), complete, active)) return false;
        active.remove(operation); complete.add(operation); return true;
    }

    private Integer add(SpellAbility ability) {
        if (ids.containsKey(ability)) return ids.get(ability);
        if (operations.size() == MAX_OPERATIONS) { truncated = true; return null; }
        int index = operations.size(); ids.put(ability, index); operations.add(new LiveNode(ability)); links.add(new ArrayList<>());
        return index;
    }
    private void link(int from, SpellAbility child, String kind) {
        if (child == null) return;
        Integer to = add(child);
        if (to != null) appendLink(from, to, kind);
    }

    private void appendLink(int from, int to, String kind) {
        var edge = map("operationId", to, "kind", kind);
        if (links.get(from).contains(edge)) return;
        if (linkCount == MAX_LINKS) { truncated = true; return; }
        links.get(from).add(edge); linkCount++;
    }

    private SpellAbility live(int index) { return ((LiveNode) operations.get(index)).ability(); }

    private void recipeLink(int from, SpellAbility context, RecipeKey key, Supplier<ScheduledOperationRecipe> read, String kind) {
        var scoped = recipeIds.computeIfAbsent(context, ignored -> new HashMap<>());
        Integer to = scoped.get(key);
        if (to == null) {
            if (operations.size() == MAX_OPERATIONS) { truncated = true; return; }
            to = operations.size(); scoped.put(key, to);
            operations.add(new RecipeNode(read.get())); links.add(new ArrayList<>());
        }
        appendLink(from, to, kind);
    }

    private void recipeLink(int from, SpellAbility context, String name, boolean trigger, String executeOverride,
                            boolean actorKnown, boolean textChanges, String kind) {
        recipeLink(from, context, new RecipeKey(name, trigger, executeOverride, actorKnown, textChanges),
                () -> ScheduledOperationRecipe.read(context, name, trigger, executeOverride, actorKnown, textChanges), kind);
    }

    private void expandScheduled(int index, SpellAbility context, Map<String, String> params, ApiType api, boolean actorKnown) {
        if (api == ApiType.AddTurn && (params.containsKey("ExtraTurnDelayedTrigger") || params.containsKey("ExtraTurnDelayedTriggerExecute")))
            recipeLink(index, context, params.getOrDefault("ExtraTurnDelayedTrigger", ""), true,
                    params.getOrDefault("ExtraTurnDelayedTriggerExecute", ""), actorKnown, false, "Scheduled");
        if (api == ApiType.Effect && params.containsKey("Triggers")) {
            boolean knownOwner = actorKnown && (!params.containsKey("EffectOwner") || params.get("EffectOwner").equals("You"));
            for (String trigger : params.get("Triggers").split(",", MAX_OPERATIONS + 1))
                recipeLink(index, context, trigger.trim(), true, "", knownOwner, true, "Scheduled");
        }
    }

    private void expandRecipe(int index, ScheduledOperationRecipe recipe) {
        var params = recipe.parameters();
        var api = recipe.api();
        if (recipe.trigger()) {
            String execute = recipe.executeOverride().isEmpty() ? params.getOrDefault("Execute", "") : recipe.executeOverride();
            boolean knownActor = recipe.actorKnown() && !params.containsKey("TriggerController");
            // A missing Execute is an explicit unknown future recipe, never a
            // certified empty effect. No trigger is instantiated or installed.
            recipeLink(index, recipe.context(), execute, false, "", knownActor, true, "Scheduled");
            return;
        }
        if (params.containsKey("SubAbility"))
            recipeLink(index, recipe.context(), params.get("SubAbility"), false, "", recipe.actorKnown(), true, "Sequence");
        for (String key : AbilityFactory.additionalAbilityKeys) if (params.containsKey(key)) {
            String kind = switch (key) {
                case "TrueSubAbility" -> "IfTrue";
                case "FalseSubAbility" -> "IfFalse";
                case "RepeatSubAbility" -> "Repeat";
                case "Execute" -> api == ApiType.DelayedTrigger || api == ApiType.ImmediateTrigger ? "Scheduled" : "Additional";
                default -> "Additional";
            };
            recipeLink(index, recipe.context(), params.get(key), false, "", recipe.actorKnown() && !params.containsKey("TriggerController"), true, kind);
        }
        if (params.containsKey("Choices")) for (String choice : params.get("Choices").split(",", MAX_OPERATIONS + 1))
            recipeLink(index, recipe.context(), choice.trim(), false, "", recipe.actorKnown(), true, "Choice");
        expandScheduled(index, recipe.context(), params, api, recipe.actorKnown());
    }

    private Map<String, Object> recipeOperation(int index, ScheduledOperationRecipe recipe) {
        var params = recipe.parameters();
        var actor = recipe.actorKnown() ? recipe.context().getActivatingPlayer() : null;
        // SourceController/remembered/future targets remain unknown. A present
        // controller must not substitute for a future recipient binding.
        var recipients = params.getOrDefault("Defined", "").equals("You") && actor != null
                ? binding(index, "Actor", null, List.of(entity(index, actor))) : unknownRecipients();
        var targets = params.containsKey("ValidTgts")
                ? map("knowledge", "Unknown", "min", null, "max", null, "candidates", List.of()) : null;
        return map("id", index, "effect", ActionSemantics.effect(recipe.api()), "knowledge", "Unknown",
                "actorPlayerId", actor == null ? null : actor.getId(), "amount", null, "amountKnowledge", "Unknown",
                "counterType", params.getOrDefault("CounterType", ""), "conditional", recipe.conditional(),
                "originZone", "", "destinationZone", "", "sources", unknownRecipients(), "recipients", recipients,
                "targets", targets, "costs", List.of(), "links", List.copyOf(links.get(index)));
    }

    private Map<String, Object> operation(int index, SpellAbility ability) {
        String effect = ActionSemantics.effect(ability);
        // The scalar field represents a recipient quantity, not an undistributed
        // total. Distribution choices need their own future structural model.
        Integer amount = parameters(ability).keySet().stream().anyMatch(DISTRIBUTED_AMOUNTS::contains)
                ? null : ActionSemantics.effectAmount(ability);
        boolean quantified = Set.of("Draw", "Damage", "AddCounters", "RemoveCounters", "LifeGain", "LifeLoss", "Token", "Mill", "Scry", "Proliferate", "MultiplyCounters", "Amass").contains(effect);
        boolean conditional = parameters(ability).keySet().stream().anyMatch(key -> key.startsWith("Condition")
                || key.startsWith("Unless") || Set.of("Optional", "OptionalDecider", "UpTo", "Upto", "DividedAsYouChoose", "DividedRandomly").contains(key));
        if (committed && ability.getTrigger() != null)
            conditional |= ability instanceof WrappedAbility wrapped && wrapped.getDecider() != null
                    || ability.getTrigger().getMapParams().keySet().stream().anyMatch(key -> key.startsWith("Condition")
                        || key.startsWith("CheckSVar") || key.startsWith("IsPresent")
                        || Set.of("InterveningIf", "ResolvingCheck", "ResolvedLimit", "ResolvedLimitPerTurn").contains(key));
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
                && !parameters(ability).keySet().stream().anyMatch(key -> Set.of("CounterType2", "CounterNum2", "CounterTypes",
                    "Placer", "DividedAsYouChoose", "DividedRandomly", "Monstrosity", "Adapt", "Bolster", "Tribute").contains(key));
        var node = map("id", index, "effect", effect, "knowledge", known ? "Known" : "Unknown",
                "actorPlayerId", ability.getActivatingPlayer() == null ? null : ability.getActivatingPlayer().getId(),
                "amount", amount, "amountKnowledge", amount == null ? "Unknown" : "Known",
                "counterType", ability.getParamOrDefault("CounterType", ""), "conditional", conditional,
                "originZone", origin, "destinationZone", destination,
                "sources", sources, "recipients", recipients, "targets", targets,
                "costs", committed || ability.getPayCosts() == null ? List.of() : ability.getPayCosts().getCostParts().stream().map(ActionSemantics::cost).toList(),
                "links", List.copyOf(links.get(index)));
        if (ability.getApi() == ApiType.Charm && !committed) {
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
        } else if (effect.equals("Choose") && ability.getApi() != ApiType.Charm) node.put("knowledge", "Unknown");
        return node;
    }

    private boolean containsTarget(int id, Set<Integer> seen) {
        if (!seen.add(id)) return false;
        return (operations.get(id) instanceof LiveNode live ? live.ability().usesTargeting()
                : ((RecipeNode) operations.get(id)).recipe().parameters().containsKey("ValidTgts")) || links.get(id).stream()
                .anyMatch(edge -> containsTarget((Integer)edge.get("operationId"), seen));
    }

    private Map<String, Object> sources(int index, SpellAbility ability) {
        if (ability.hasParam("DamageSource")) {
            String defined = ability.getParam("DamageSource");
            Integer target = targetBinding(ability, defined);
            if (Set.of("Targeted", "ThisTargeted").contains(defined))
                return target == null ? unknownRecipients() : selectedBinding(index, target, live(target));
            if (!defined.equals("Self")) return unknownRecipients();
        }
        var source = entity(index, ability.getHostCard());
        return source == null ? unknownRecipients() : binding(index, "Source", null, List.of(source));
    }

    private Map<String, Object> targets(int index, SpellAbility ability) {
        if (committed) {
            var selected = selected(index, ability);
            boolean disclosed = selected.size() == ability.getTargets().size();
            // In a committed graph this finite domain is exactly the chosen
            // set; min == max excludes choosing another hypothetical target.
            return map("knowledge", disclosed ? "Known" : "Unknown", "min", disclosed ? selected.size() : null,
                    "max", disclosed ? selected.size() : null, "candidates", selected);
        }
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
        if (parameters(ability).keySet().stream().anyMatch(UNMODELED_RECIPIENTS::contains)) return unknownRecipients();
        if (ability.getApi() != null && Set.of(ApiType.DestroyAll, ApiType.PutCounterAll, ApiType.RemoveCounterAll).contains(ability.getApi()))
            return globalCards(index, ability);
        if (ability.getApi() != null && Set.of(ApiType.ChangeZoneAll, ApiType.DamageAll, ApiType.TapAll, ApiType.UntapAll,
                ApiType.EachDamage, ApiType.Proliferate, ApiType.Amass).contains(ability.getApi())) return unknownRecipients();
        String parameter = ability.getApi() == ApiType.Token ? "TokenOwner" : "Defined";
        boolean definedFirst = ability.getApi() == ApiType.Token || ability.getApi() == ApiType.Mana;
        if (ability.usesTargeting() && (!definedFirst || !ability.hasParam(parameter)))
            return selectedBinding(index, index, ability);
        boolean playerOperation = Set.of("Draw", "LifeGain", "LifeLoss", "LoseGame", "WinGame", "Token", "Mill", "Scry", "Mana").contains(effect);
        String defined = ability.getParamOrDefault(parameter, playerOperation ? "You" : "Self");
        if (effect.equals("Search") && !ability.hasParam(parameter)) return unknownRecipients();
        Integer target = targetBinding(ability, defined);
        if (Set.of("Targeted", "ThisTargeted", "ParentTarget").contains(defined))
            return target == null ? unknownRecipients() : selectedBinding(index, target, live(target));
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
    private Map<String, Object> selectedBinding(int index, int target, SpellAbility ability) {
        var selected = selected(index, ability);
        return committed && selected.size() != ability.getTargets().size() ? unknownRecipients()
                : binding(index, "Targets", target, selected);
    }
    private Map<String, Object> entity(int operation, GameObject target) {
        String prefix = "op" + operation + ":";
        if (target instanceof Player player)
            return map("id", prefix + "player:" + player.getId(), "kind", "Player", "cardId", "", "playerId", player.getId(),
                    "controllerId", player.getId(), "ownerId", null);
        if (target instanceof SpellAbility spell) {
            // A stack occurrence outlives its source permanent. Keep its exact
            // public stack identity even when no current card identity exists.
            var stack = spell.getHostCard().getGame().getStack().getInstanceMatchingSpellAbilityID(spell);
            if (stack == null || viewer == null) return null;
            var actual = stack.getSpellAbility();
            Card host = actual.getHostCard();
            boolean disclosed = host != null && host.getView().canBeShownTo(viewer.getView());
            Card current = host == null ? null : host.getGame().getCardState(host, null);
            String cardId = disclosed && current != null && host.equalsWithGameTimestamp(current) && current.getZone() != null && identity != null
                    ? Objects.toString(identity.apply(current.getView()), "") : "";
            String stackId = String.valueOf(stack.getId());
            return map("id", prefix + "spell:" + stackId, "kind", "Spell", "cardId", cardId, "stackItemId", stackId,
                    "playerId", null, "controllerId", actual.getActivatingPlayer() == null ? null : actual.getActivatingPlayer().getId(),
                    "ownerId", !disclosed || host.getOwner() == null ? null : host.getOwner().getId());
        }
        Card card = target instanceof Card actual ? actual : null;
        if (card == null || viewer == null || identity == null || card.getZone() == null || !card.getView().canBeShownTo(viewer.getView())) return null;
        // Equal physical IDs do not make an old LKI incarnation a current
        // target/source. Canonicalize only the same engine timestamp, before
        // asking the audience for a current occurrence identity.
        Card current = card.getGame().getCardState(card, null);
        if (current == null || !card.equalsWithGameTimestamp(current) || current.getZone() == null) return null;
        card = current;
        String cardId = identity.apply(card.getView());
        if (cardId == null || cardId.isEmpty()) return null;
        return map("id", prefix + "card:" + cardId, "kind", "Card", "cardId", cardId, "stackItemId", "", "playerId", null,
                "controllerId", card.getController() == null ? null : card.getController().getId(),
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
    private static Map<String, String> parameters(SpellAbility ability) {
        while (ability instanceof WrappedAbility wrapped) ability = wrapped.getWrappedAbility();
        return ability.getMapParams();
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
    private ActionOperationProjection() { this(null, null, false); }
}
