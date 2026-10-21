package forge.api;

import forge.game.card.Card;
import forge.game.spellability.SpellAbility;
import forge.game.zone.ZoneType;
import forge.gamemodes.match.input.Input;

import java.util.List;
import java.util.UUID;

/** Engine objects stay inside one pending input; clients receive opaque IDs and public descriptions only. */
final class SourceActions {
    record Action(String id, SpellAbility ability, String kind, String text) { }
    record Catalog(String key, Card source, long timestamp, ZoneType zone, long version, List<Action> actions) {
        boolean matches(Card current, long currentVersion) {
            return current != null && version == currentVersion && current == source && current.getGameTimestamp() == timestamp
                    && current.getZone() != null && current.getZone().getZoneType() == zone;
        }
    }

    static Catalog describe(Input input, Card source, String key, long version) {
        var actions = input.getSourceActions(source).stream().map(ability -> new Action(UUID.randomUUID().toString(), ability,
                ability.isLandAbility() ? "play" : ability.isSpell() ? "cast" : "activate", ability.toUnsuppressedString())).toList();
        return new Catalog(key, source, source.getGameTimestamp(), source.getZone().getZoneType(), version, List.copyOf(actions));
    }
    private SourceActions() { }
}
