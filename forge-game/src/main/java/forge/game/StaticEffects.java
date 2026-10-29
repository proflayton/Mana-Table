/*
 * Forge: Play Magic: the Gathering.
 * Copyright (C) 2011  Forge Team
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 * 
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 * 
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <http://www.gnu.org/licenses/>.
 */
package forge.game;

import java.util.Map;
import java.util.Set;

import com.google.common.collect.Lists;
import com.google.common.collect.Maps;

import forge.game.card.Card;
import forge.game.card.CardCollection;
import forge.game.staticability.StaticAbility;
import forge.game.staticability.StaticAbilityLayer;

/**
 * <p>
 * StaticEffects class.
 * </p>
 * 
 * @author Forge
 * @version $Id$
 */
public class StaticEffects {

    /** The fixed permission changes no battlefield resource or characteristic. */
    public static boolean isReversibleCommandPermission(Map<String, String> params) {
        return params.equals(Map.of("Mode", "Continuous", "EffectZone", "Command", "MayPlay", "True",
                "Affected", "Card.IsCommander+YouOwn", "AffectedZone", "Command"));
    }

    private record EffectShape(Card source, long timestamp, Map<String, String> params,
                               java.util.List<Card> cards, java.util.List<forge.game.player.Player> players) { }
    private record PermissionShape(forge.game.player.Player player, StaticAbility ability, Object payment,
                                   Object alternate, boolean flash, boolean zones) { }

    public static final class ReversiblePermissions {
        private final Map<StaticAbility, StaticEffect> effects;
        private final Map<StaticAbility, EffectShape> shapes;
        private final Map<Card, Map<StaticAbility, forge.game.card.CardPlayOption>> permissions;
        private final Map<Card, Map<StaticAbility, PermissionShape>> permissionShapes;
        private ReversiblePermissions(Map<StaticAbility, StaticEffect> effects,
                Map<StaticAbility, EffectShape> shapes,
                Map<Card, Map<StaticAbility, forge.game.card.CardPlayOption>> permissions,
                Map<Card, Map<StaticAbility, PermissionShape>> permissionShapes) {
            this.effects=effects; this.shapes=shapes; this.permissions=permissions; this.permissionShapes=permissionShapes;
        }
        public boolean sameFootprint(ReversiblePermissions other) {
            return shapes.equals(other.shapes) && permissionShapes.equals(other.permissionShapes);
        }
        public boolean containsRecipient(forge.game.card.CardView card) {
            for (var shape : shapes.values()) for (Card affected : shape.cards())
                if (affected.getView() == card && affected.isInZone(forge.game.zone.ZoneType.Command)) return true;
            return false;
        }
    }

    public ReversiblePermissions captureReversiblePermissions(Game game) {
        var effects = Map.copyOf(staticEffects);
        var shapes = new java.util.HashMap<StaticAbility, EffectShape>();
        for (var item : staticEffects.entrySet()) {
            StaticEffect effect = item.getValue();
            var cards = new java.util.ArrayList<Card>(); for (Card card : effect.getAffectedCards()) cards.add(card);
            shapes.put(item.getKey(), new EffectShape(effect.getSource(), effect.getTimestamp(), Map.copyOf(effect.getParams()),
                    java.util.List.copyOf(cards), java.util.List.copyOf(effect.getAffectedPlayers())));
        }
        var permissions = new java.util.HashMap<Card, Map<StaticAbility, forge.game.card.CardPlayOption>>();
        var permissionShapes = new java.util.HashMap<Card, Map<StaticAbility, PermissionShape>>();
        for (Card card : game.getCardsInGame()) {
            var original = card.getMayPlay();
            if (original.isEmpty()) continue;
            permissions.put(card, Map.copyOf(original));
            var values = new java.util.HashMap<StaticAbility, PermissionShape>();
            for (var item : original.entrySet()) {
                var value = item.getValue();
                values.put(item.getKey(), new PermissionShape(value.getPlayer(), value.getAbility(), value.getPayManaCost(),
                        value.getAltManaCost(), value.isWithFlash(), value.grantsZonePermissions()));
            }
            permissionShapes.put(card, Map.copyOf(values));
        }
        return new ReversiblePermissions(effects, Map.copyOf(shapes), Map.copyOf(permissions), Map.copyOf(permissionShapes));
    }

    /** Restore permission objects and effect ownership without running Continuous again. */
    public void restoreReversiblePermissions(Game game, ReversiblePermissions state) {
        staticEffects.clear(); staticEffects.putAll(state.effects);
        for (Card card : game.getCardsInGame()) card.setMayPlay(new java.util.HashMap<>(state.permissions.getOrDefault(card, Map.of())));
    }

    // **************** StaticAbility system **************************
    private final Map<StaticAbility, StaticEffect> staticEffects = Maps.newHashMap();

    public final void clearStaticEffects(final Set<Card> affectedCards, Map<StaticAbilityLayer, Set<Card>> affectedPerLayer) {
        // remove all static effects
        for (final StaticEffect se : staticEffects.values()) {
            se.remove(affectedPerLayer).forEach(affectedCards::add);
        }
        this.staticEffects.clear();
        updateCaches(affectedPerLayer);
    }

    /**
     * Add a static effect to the list of static effects.
     * 
     * @param staticAbility
     *            a {@link StaticAbility}.
     */
    public final StaticEffect getStaticEffect(final StaticAbility staticAbility) {
        final StaticEffect currentEffect = staticEffects.get(staticAbility);
        if (currentEffect != null) {
            return currentEffect;
        }

        final StaticEffect newEffect = new StaticEffect(staticAbility);
        this.staticEffects.put(staticAbility, newEffect);
        return newEffect;
    }

    public Iterable<StaticEffect> getEffects() {
        return staticEffects.values();
    }

    public boolean removeStaticEffect(final StaticAbility staticAbility, final StaticAbilityLayer layer, final boolean removeFull) {
        final StaticEffect currentEffect;
        if (removeFull) {
            currentEffect = staticEffects.remove(staticAbility);
        } else {
            currentEffect = staticEffects.get(staticAbility);
        }
        if (currentEffect == null) {
            return false;
        }
        Map<StaticAbilityLayer, Set<Card>> affectedPerLayer = Maps.newHashMap();
        currentEffect.remove(affectedPerLayer, Lists.newArrayList(layer));
        updateCaches(affectedPerLayer);
        return true;
    }

    private void updateCaches(Map<StaticAbilityLayer, Set<Card>> affectedPerLayer) {
        CardCollection affectedKeywordsBefore = new CardCollection();
        if (affectedPerLayer.containsKey(StaticAbilityLayer.TEXT)) {
            affectedKeywordsBefore.addAll(affectedPerLayer.get(StaticAbilityLayer.TEXT));
        }
        if (affectedPerLayer.containsKey(StaticAbilityLayer.TYPE)) {
            // setting Basic Land Type case
            affectedKeywordsBefore.addAll(affectedPerLayer.get(StaticAbilityLayer.TYPE));
        }
        if (affectedPerLayer.containsKey(StaticAbilityLayer.ABILITIES)) {
            affectedKeywordsBefore.addAll(affectedPerLayer.get(StaticAbilityLayer.ABILITIES));
        }
        affectedKeywordsBefore.forEach(Card::updateKeywordsCache);
    }
}
