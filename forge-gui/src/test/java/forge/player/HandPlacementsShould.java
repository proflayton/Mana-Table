package forge.player;

import forge.ai.AvailableActions;
import forge.card.CardType;
import forge.game.ability.AbilityFactory;
import forge.game.card.Card;
import forge.game.spellability.SpellAbility;
import forge.game.zone.ZoneType;
import org.testng.annotations.Test;

import java.util.List;
import java.util.Set;

import static org.testng.Assert.*;

public class HandPlacementsShould {
    private static final String SIMPLE = "AB$ ChangeZone | Cost$ 0 | Origin$ Hand | Destination$ Battlefield | ChangeType$ Forest.Basic+YouCtrl | ChangeNum$ 1 | SorcerySpeed$ True | ActivationLimit$ 1 | SpellDescription$ Put a basic Forest onto the battlefield.";

    private static final class Position {
        final HumanControlledGame game = new HumanControlledGame();
        final Card source = game.card(game.human, "A differently named source", "Types:Enchantment");
        final PriorityAbilityPass pass = new PriorityAbilityPass();
        Position() {
            // This asset-free fixture declares the subtype registry needed by
            // the same engine predicate that production initializes from files.
            CardType.Helper.parseTypes("BasicTypes", List.of("Plains", "Island", "Swamp", "Mountain", "Forest"));
            game.human.getZone(ZoneType.Battlefield).add(source);
        }
        Card hand(String name, String type) {
            Card card = game.card(game.human, name, "Types:" + type);
            game.human.getZone(ZoneType.Hand).add(card);
            return card;
        }
        SpellAbility ability(String script) {
            SpellAbility ability = AbilityFactory.getAbility(script, source);
            ability.setActivatingPlayer(game.human);
            return ability;
        }
    }

    @Test public void useEngineCardPredicatesAndOnlyTheActorsOwnHandWithoutNameOrOracleHeuristics() {
        Position p = new Position();
        Card forest = p.hand("Ordinary wood", "Basic Land Forest");
        Card snow = p.hand("Snowy wood", "Basic Snow Land Forest");
        p.hand("Gaea's Touch", "Basic Land Island");
        p.hand("Forest", "Land Forest");
        p.hand("Forest spell", "Creature Elf");
        Card opponentForest = p.game.card(p.game.opponent, "Opponent wood", "Types:Basic Land Forest");
        p.game.opponent.getZone(ZoneType.Hand).add(opponentForest);
        var eligible = HandPlacements.eligibleCards(p.game.human, p.ability(SIMPLE));
        assertEquals(Set.copyOf(eligible), Set.of(forest.getId(), snow.getId()));
        assertFalse(eligible.contains(opponentForest.getId()), "Another player's private hand never enters presentation metadata");
        assertThrows(UnsupportedOperationException.class, () -> eligible.add(999));
    }

    @Test public void unsupportedShapesAndNonfreeCostsNeverAdvertiseACardRoute() {
        Position p = new Position(); p.hand("Forest", "Basic Land Forest");
        List<String> unsupported = List.of(
                SIMPLE.replace("Cost$ 0", "Cost$ 1"),
                SIMPLE.replace("Cost$ 0", "Cost$ T"),
                SIMPLE.replace("Cost$ 0", "Cost$ Sac<1/CARDNAME>"),
                SIMPLE.replace("Origin$ Hand", "Origin$ Graveyard"),
                SIMPLE.replace("Destination$ Battlefield", "Destination$ Exile"),
                SIMPLE.replace("ChangeNum$ 1", "ChangeNum$ 2"),
                SIMPLE + " | DefinedPlayer$ Opponent",
                SIMPLE + " | Chooser$ Opponent",
                SIMPLE + " | AtRandom$ True",
                SIMPLE + " | WithTotalCMC$ 0",
                SIMPLE + " | ChooseFromDefined$ YouCtrl",
                SIMPLE + " | ValidTgts$ Player");
        for (String script : unsupported)
            assertTrue(HandPlacements.eligibleCards(p.game.human, p.ability(script)).isEmpty(), script);
        assertTrue(HandPlacements.eligibleCards(p.game.opponent, p.ability(SIMPLE)).isEmpty(), "Another controller's source is not offered");
    }

    @Test public void cachedPairsAreImmutableAndIncompleteScansOrMovedSourcesCannotDispatch() {
        Position p = new Position(); Card forest = p.hand("Forest", "Basic Land Forest");
        SpellAbility ability = p.ability(SIMPLE);
        p.pass.refresh(p.game.human, new AvailableActions.ActionScan(Set.of(p.source.getView()), List.of(ability), false, true));
        var view = p.pass.legal(p.game.human).get(0);
        assertEquals(view.eligibleHandCardIds(), List.of(forest.getId()));
        assertTrue(p.pass.hasHandPlacement(view.id(), forest.getId()));
        assertFalse(p.pass.hasHandPlacement("unknown", forest.getId()));
        assertFalse(p.pass.hasHandPlacement(view.id(), p.source.getId()));
        assertTrue(p.pass.setIgnored(view.id(), true, p.game.human, true));
        assertTrue(p.pass.legal(p.game.human).get(0).eligibleHandCardIds().contains(forest.getId()), "Auto-pass policy does not change legal hand routes");
        assertTrue(p.pass.ignored(p.game.human).get(0).eligibleHandCardIds().isEmpty(), "An undoable policy is not evidence of a currently legal hand route");
        assertThrows(UnsupportedOperationException.class, () -> view.eligibleHandCardIds().clear());
        p.game.human.getZone(ZoneType.Hand).remove(forest);
        p.game.human.getZone(ZoneType.Battlefield).add(forest);
        assertNull(p.pass.resolveHandPlacement(p.game.human, view.id(), forest.getId()), "An old hand membership cannot dispatch after moving");
        assertEquals(view.eligibleHandCardIds(), List.of(forest.getId()), "A previously published view never mutates");
        p.pass.refresh(p.game.human, new AvailableActions.ActionScan(Set.of(), List.of(ability), false, false));
        assertTrue(p.pass.legal(p.game.human).isEmpty());
        assertFalse(p.pass.hasHandPlacement(view.id(), forest.getId()));
        p.pass.clearScan();
        assertNull(p.pass.resolveHandPlacement(p.game.human, view.id(), forest.getId()));
    }
}
