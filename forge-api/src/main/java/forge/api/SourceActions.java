package forge.api;

import forge.game.card.Card;
import forge.game.spellability.SpellAbility;
import forge.game.zone.ZoneType;
import forge.game.player.Player;
import forge.gamemodes.match.input.InputPassPriority;
import forge.gamemodes.match.input.InputPayMana;
import forge.gamemodes.match.input.Input;

import java.util.List;
import java.util.ArrayList;
import java.util.UUID;
import java.util.function.Predicate;
import java.util.LinkedHashSet;

/** Engine objects stay inside one pending input; clients receive opaque IDs and public descriptions only. */
final class SourceActions {
    record Domain(List<Card> sources, boolean complete) { }

    /**
     * Enumerate engine-controlled source zones, independently of the bounded
     * presentation/highlight scan. External-zone access is the engine's own
     * permission collection and includes command, graveyard, exile, top-card
     * permissions, other players' playable cards and stack activations.
     * Discovery never substitutes a strategic AI controller or chooses a route.
     */
    static Domain discover(Input input, Player actor, Predicate<Card> visible) {
        if (!(input instanceof InputPassPriority || input instanceof InputPayMana))
            return new Domain(List.of(), false);
        var candidates = new LinkedHashSet<Card>();
        candidates.addAll(actor.getCardsIn(ZoneType.Hand));
        candidates.addAll(actor.getGame().getCardsIn(ZoneType.Battlefield));
        candidates.addAll(actor.getCardsActivatableInExternalZones(true));
        // Prepared virtual copies are invoked through their creature's catalog,
        // as in the human path. They are not another independently keyed source.
        for (Card battlefield : actor.getGame().getCardsIn(ZoneType.Battlefield)) {
            Card linked = linkedSpell(battlefield);
            if (linked != null && visible.test(battlefield)) candidates.remove(linked);
        }
        var sources = new ArrayList<Card>();
        boolean complete = true;
        for (Card card : candidates) {
            if (card.getZone() == null) continue;
            // The owning caller supplies the exact audience permission. The
            // ordinary source UI excludes face-down cards; private automated
            // seats may know their face through canFaceDownBeShownTo without
            // disclosing that face in the public battlefield projection.
            boolean disclosed = visible.test(card);
            boolean offered = !input.getSourceActions(card).isEmpty();
            boolean linked = disclosed && hasLinkedAction(input, card, visible);
            if (!offered && !linked) continue;
            if (disclosed) sources.add(card);
            else complete = false;
        }
        return new Domain(List.copyOf(sources), complete);
    }
    record Action(String id, SpellAbility ability, String kind, String text, Card host, long timestamp, ZoneType zone) {
        boolean matches(Card source) {
            return ability.getHostCard() == host && host.getGameTimestamp() == timestamp && host.getZone() != null
                    && host.getZone().getZoneType() == zone && (host == source || linkedSpell(source) == host);
        }
    }
    record Catalog(String key, Card source, long timestamp, ZoneType zone, long version, Card linked, long linkedTimestamp, List<Action> actions) {
        boolean matches(Card current, long currentVersion) {
            return current != null && version == currentVersion && current == source && current.getGameTimestamp() == timestamp
                    && current.getZone() != null && current.getZone().getZoneType() == zone
                    && linkedSpell(current) == linked && (linked == null || linked.getGameTimestamp() == linkedTimestamp)
                    && actions.stream().allMatch(action -> action.matches(current));
        }
    }

    static Catalog describe(Input input, Card source, String key, long version, Predicate<Card> visible) {
        var actions = new ArrayList<Action>();
        input.getSourceActions(source).forEach(ability -> actions.add(describe(ability)));
        Card linked = linkedSpell(source);
        if (linked != null && visible.test(linked)) input.getSourceActions(linked).stream().filter(SpellAbility::isSpell)
                .forEach(ability -> actions.add(describe(ability)));
        return new Catalog(key, source, source.getGameTimestamp(), source.getZone().getZoneType(), version,
                linked, linked == null ? 0 : linked.getGameTimestamp(), List.copyOf(actions));
    }
    static Catalog empty(Card source, String key, long version) {
        return new Catalog(key, source, source.getGameTimestamp(), source.getZone().getZoneType(), version, null, 0, List.of());
    }
    static boolean hasLinkedAction(Input input, Card source, Predicate<Card> visible) {
        Card linked = linkedSpell(source);
        return linked != null && visible.test(linked) && input.getSourceActions(linked).stream().anyMatch(SpellAbility::isSpell);
    }
    private static Action describe(SpellAbility ability) {
        var host = ability.getHostCard();
        return new Action(UUID.randomUUID().toString(), ability,
                ability.isLandAbility() ? "play" : ability.isSpell() ? "cast" : "activate", ability.toUnsuppressedString(),
                host, host.getGameTimestamp(), host.getZone().getZoneType());
    }
    private static Card linkedSpell(Card source) {
        if (!source.isInZone(ZoneType.Battlefield) || source.isFaceDown() || source.isPhasedOut()) return null;
        Card prepared = source.getPreparedSpell();
        return prepared != null && prepared.isInZone(ZoneType.Exile) ? prepared : null;
    }
    private SourceActions() { }
}
