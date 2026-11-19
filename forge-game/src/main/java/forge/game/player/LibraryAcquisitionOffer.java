package forge.game.player;

import forge.game.ability.ApiType;
import forge.game.card.Card;
import forge.game.card.CardCollectionView;
import forge.game.card.CardView;
import forge.game.spellability.SpellAbility;
import forge.game.zone.ZoneType;

import java.util.*;

/**
 * Read-only provenance for one blocking zone-change choice. It neither grants
 * access to a library nor predicts replacement effects or completed movement.
 * Recognition is deliberately limited to direct acquisition and one exact
 * two-card remembered bundle. Unsupported mechanics publish no offer.
 */
public final class LibraryAcquisitionOffer {
    public record Destination(String destination, int maximumCount) { }
    private record Shape(String stage, ZoneType currentDestination, List<Destination> destinations) { }
    private static final Set<String> DISPLAY = Set.of("SpellDescription", "StackDescription", "SelectPrompt");
    private static final Set<String> DIRECT = Set.of("SP", "AB", "DB", "Cost", "Origin", "Destination", "ChangeType", "ChangeTypeDesc", "ChangeNum", "Tapped");
    private static final Set<String> BUNDLE = Set.of("SP", "AB", "DB", "Cost", "Origin", "Destination", "ChangeType", "ChangeTypeDesc", "ChangeNum", "RememberChanged", "Reveal", "Shuffle", "SubAbility");
    private static final Set<String> DISTRIBUTE = Set.of("DB", "Origin", "Destination", "ChangeType", "ChangeNum", "Mandatory", "NoLooking", "Tapped", "Shuffle", "SubAbility");
    private static final Set<String> CLEANUP = Set.of("DB", "ClearRemembered");

    private final Player actor;
    private final PlayerController controller;
    private final SpellAbility ability;
    private final ZoneType destination;
    private final List<Card> choices;
    private final List<SpellAbility> chain;
    private final List<Map<String, String>> parameters;
    private final Shape shape;

    private LibraryAcquisitionOffer(Player actor, SpellAbility ability, ZoneType destination,
                                    List<Card> choices, List<SpellAbility> chain, Shape shape) {
        this.actor = actor; this.controller = actor.getController(); this.ability = ability; this.destination = destination;
        this.choices = List.copyOf(choices); this.chain = List.copyOf(chain); this.shape = shape;
        parameters = chain.stream().map(node -> Map.copyOf(node.getMapParams())).toList();
    }

    public static LibraryAcquisitionOffer create(Player actor, Player decider, ZoneType destination,
                                                 List<ZoneType> origin, SpellAbility ability,
                                                 CardCollectionView choices, int maximum) {
        if (actor == null || decider != actor || actor.isControlled() || actor.hasLost() || ability == null
                || !List.of(ZoneType.Library).equals(origin) || maximum < 1 || maximum > 2
                || actor.getGame().costPaymentStack.peek() != null || choices.isEmpty() || choices.size() > 256) return null;
        var chain = boundedChain(ability);
        if (chain == null || chain.get(0).getActivatingPlayer() != actor) return null;
        var shape = recognize(chain, ability, destination);
        if (shape == null || shape.destinations.stream().mapToInt(Destination::maximumCount).sum() < maximum) return null;
        var cards = new ArrayList<Card>();
        var distinct = new HashSet<Card>();
        for (Card card : choices) {
            if (card == null || card.getZone() == null || card.getZone().getZoneType() != ZoneType.Library
                    || card.getZone().getPlayer() != actor || card.getOwner() != actor || !distinct.add(card)) return null;
            cards.add(card);
        }
        if (!memoryMatches(actor, chain, shape, cards)) return null;
        return new LibraryAcquisitionOffer(actor, ability, destination, cards, chain, shape);
    }

    /** Exact original list and live scoped controller lease are checked by the
     * caller; this independently rejects moved cards or changed mechanics. */
    public boolean matches(Player viewer, List<?> views) {
        if (viewer != actor || actor.getController() != controller || actor.isControlled() || actor.hasLost() || actor.getGame().isGameOver()
                || actor.getGame().costPaymentStack.peek() != null || views.size() != choices.size()) return false;
        var expected = new HashSet<CardView>();
        for (Card card : choices) {
            if (card.getZone() == null || card.getZone().getZoneType() != ZoneType.Library
                    || card.getZone().getPlayer() != actor || card.getOwner() != actor) return false;
            expected.add(card.getView());
        }
        if (expected.size() != views.size() || !expected.equals(new HashSet<>(views))) return false;
        var current = boundedChain(ability);
        if (current == null || !current.equals(chain) || current.get(0).getActivatingPlayer() != actor) return false;
        for (int index = 0; index < chain.size(); index++)
            if (!parameters.get(index).equals(chain.get(index).getMapParams())) return false;
        return shape.equals(recognize(current, ability, destination)) && memoryMatches(actor, current, shape, choices);
    }

    public int actorPlayerId() { return actor.getId(); }
    public String stage() { return shape.stage; }
    public String currentDestination() { return shape.currentDestination.name(); }
    public List<Destination> plannedDestinations() { return shape.destinations; }

    private static List<SpellAbility> boundedChain(SpellAbility ability) {
        var seen = Collections.newSetFromMap(new IdentityHashMap<SpellAbility, Boolean>());
        var root = ability;
        while (root.getParent() != null) {
            if (!seen.add(root) || seen.size() >= 8) return null;
            root = root.getParent();
        }
        var result = new ArrayList<SpellAbility>(); seen.clear();
        for (var node = root; node != null; node = node.getSubAbility()) {
            if (!seen.add(node) || result.size() >= 8 || node.isWrapper() || node.usesTargeting()
                    || node.getHostCard() != root.getHostCard()
                    || !node.getAdditionalAbilities().isEmpty() || !node.getAdditionalAbilityLists().isEmpty()
                    || node != root && node.getPayCosts() != null && !node.getPayCosts().isFree()) return null;
            if (node != root && node.getParent() != result.get(result.size() - 1)) return null;
            result.add(node);
        }
        return result.contains(ability) ? result : null;
    }

    private static Shape recognize(List<SpellAbility> chain, SpellAbility active, ZoneType destination) {
        var root = chain.get(0);
        if (chain.size() == 1 && root == active && zoneChange(root, DIRECT, "Library", destination.name())
                && (destination == ZoneType.Hand || destination == ZoneType.Battlefield)
                && Set.of("Land.Basic", "Plains,Island,Swamp,Mountain").contains(root.getParamOrDefault("ChangeType", ""))) {
            int count = literal(root.getParamOrDefault("ChangeNum", "1"));
            return count < 1 ? null : new Shape("direct", destination, List.of(new Destination(destination.name(), count)));
        }
        if (chain.size() != 4 || !zoneChange(root, BUNDLE, "Library", "Library")
                || !"Land.Basic".equals(root.getParam("ChangeType")) || !"2".equals(root.getParam("ChangeNum"))
                || !"True".equals(root.getParam("RememberChanged")) || !"True".equals(root.getParam("Reveal"))
                || !"False".equals(root.getParam("Shuffle"))) return null;
        var field = chain.get(1); var hand = chain.get(2); var cleanup = chain.get(3);
        if (!distribution(field, "Battlefield") || !"1".equals(field.getParam("ChangeNum")) || !"True".equals(field.getParam("Tapped"))
                || !"False".equals(field.getParam("Shuffle"))
                || !distribution(hand, "Hand") || hand.hasParam("Tapped") || hand.hasParam("ChangeNum") || hand.hasParam("Shuffle")
                || cleanup.getApi() != ApiType.Cleanup || !allowed(cleanup, CLEANUP)
                || !"True".equals(cleanup.getParam("ClearRemembered"))) return null;
        if (active == root && destination == ZoneType.Library)
            return new Shape("select-bundle", destination, List.of(new Destination("Battlefield", 1), new Destination("Hand", 1)));
        if (active == field && destination == ZoneType.Battlefield || active == hand && destination == ZoneType.Hand)
            return new Shape("distribute-bundle", destination, List.of(new Destination(destination.name(), 1)));
        return null;
    }

    private static boolean distribution(SpellAbility ability, String destination) {
        return zoneChange(ability, DISTRIBUTE, "Library", destination)
                && "Land.IsRemembered".equals(ability.getParam("ChangeType"))
                && "True".equals(ability.getParam("Mandatory")) && "True".equals(ability.getParam("NoLooking"));
    }

    private static boolean zoneChange(SpellAbility ability, Set<String> allowed, String origin, String destination) {
        return ability.getApi() == ApiType.ChangeZone && allowed(ability, allowed)
                && origin.equals(ability.getParam("Origin")) && destination.equals(ability.getParam("Destination"))
                && (!ability.hasParam("Tapped") || "True".equals(ability.getParam("Tapped")));
    }

    private static boolean allowed(SpellAbility ability, Set<String> allowed) {
        return ability.getMapParams().size() <= 64 && ability.getMapParams().keySet().stream().allMatch(key -> allowed.contains(key) || DISPLAY.contains(key));
    }

    private static boolean memoryMatches(Player actor, List<SpellAbility> chain, Shape shape, List<Card> choices) {
        var source = chain.get(0).getHostCard();
        if (shape.stage.equals("direct")) return true;
        if (shape.stage.equals("select-bundle")) return !source.hasRemembered();
        if (source.getRememberedCount() < 1 || source.getRememberedCount() > 2) return false;
        var inLibrary = new HashSet<Card>();
        int inField = 0;
        for (Object value : source.getRemembered()) {
            if (!(value instanceof Card card) || card.getOwner() != actor || card.getZone() == null || card.getZone().getPlayer() != actor) return false;
            if (card.isInZone(ZoneType.Library)) inLibrary.add(card);
            else if (shape.currentDestination == ZoneType.Hand && card.isInZone(ZoneType.Battlefield)) inField++;
            else return false;
        }
        return inLibrary.equals(new HashSet<>(choices)) && (shape.currentDestination != ZoneType.Hand || inField == 1);
    }

    private static int literal(String value) { return "1".equals(value) ? 1 : "2".equals(value) ? 2 : -1; }
}
