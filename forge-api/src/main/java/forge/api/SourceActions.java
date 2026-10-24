package forge.api;

import forge.game.card.Card;
import forge.game.spellability.SpellAbility;
import forge.game.zone.ZoneType;
import forge.gamemodes.match.input.Input;

import java.util.List;
import java.util.ArrayList;
import java.util.UUID;
import java.util.function.Predicate;

/** Engine objects stay inside one pending input; clients receive opaque IDs and public descriptions only. */
final class SourceActions {
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
