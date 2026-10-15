package forge.player;

import forge.ai.AvailableActions;
import forge.game.Game;
import forge.game.card.Card;
import forge.game.card.CardView;
import forge.game.event.GameEventCardChangeZone;
import forge.game.event.GameEventGameFinished;
import forge.game.player.Player;
import forge.game.spellability.AbilityActivated;
import forge.game.spellability.SpellAbility;
import forge.game.zone.ZoneType;
import org.testng.annotations.Test;

import java.util.List;
import java.util.Map;
import java.util.HashMap;
import java.util.Set;

import static org.testng.Assert.*;

public class PriorityAbilityPassShould {
    private static final class Position {
        final HumanControlledGame fixture = new HumanControlledGame();
        final Game game = fixture.game;
        final Player player = fixture.human;
        final PriorityAbilityPass pass = new PriorityAbilityPass();
        final Map<Integer, Card> cards = new HashMap<>();
        final Map<String, SpellAbility> abilities = new HashMap<>();

        Position() {
            game.subscribeToEvents(pass);
        }

        SpellAbility ability(int cardId, long timestamp, int abilityId, String name) {
            String key = cardId + ":" + timestamp + ":" + abilityId;
            if (abilities.containsKey(key)) return abilities.get(key);
            Card card = cards.get(cardId);
            if (card == null || card.getGameTimestamp() != timestamp) {
                if (card != null) card.getZone().remove(card);
                card = new Card(cardId, game);
                card.setOwner(player);
                card.setName(name);
                card.setGameTimestamp(timestamp);
                player.getZone(ZoneType.Battlefield).add(card);
                cards.put(cardId, card);
            }
            SpellAbility ability = new AbilityActivated(card, "0") {
                @Override public void resolve() { }
            };
            ability.setDescription("Pay 1 life, sacrifice " + name + ": Search your library.");
            abilities.put(key, ability);
            return ability;
        }

        void refresh(boolean otherActions, boolean complete, SpellAbility... abilities) {
            Set<CardView> highlights = java.util.Arrays.stream(abilities)
                    .map(ability -> ability.getHostCard().getView()).collect(java.util.stream.Collectors.toSet());
            pass.refresh(player, new AvailableActions.ActionScan(highlights, List.of(abilities), otherActions, complete));
        }

        String idFor(int cardId) {
            return pass.legal(player).stream().filter(ability -> ability.sourceCardId() == cardId).findFirst().orElseThrow().id();
        }
    }

    @Test public void ignoreOneExactAbilityWhileKeepingTheCardPlayableAndHighlighted() {
        Position p = new Position();
        SpellAbility mesa = p.ability(10, 101, 20, "Arid Mesa");
        p.refresh(false, true, mesa);
        String id = p.idFor(10);
        assertTrue(p.pass.hasNonIgnoredActions(p.player));
        assertTrue(p.pass.setIgnored(id, true, p.player, true));
        assertFalse(p.pass.hasNonIgnoredActions(p.player));
        assertTrue(p.pass.legal(p.player).get(0).ignoredUntilNextTurn());

        AvailableActions.ActionScan scan = new AvailableActions.ActionScan(
                Set.of(mesa.getHostCard().getView()), List.of(mesa), false, true);
        assertFalse(scan.hasNonIgnoredActions(ability -> ability == mesa));
        assertTrue(scan.actionableCards().contains(mesa.getHostCard().getView()), "An ignored action remains selectable");

        assertTrue(p.pass.setIgnored(id, false, p.player, false), "Undo is available between priority windows");
        assertTrue(p.pass.hasNonIgnoredActions(p.player));
        assertFalse(p.pass.setIgnored(id, false, p.player, false), "Unknown or already removed policies are rejected");
    }

    @Test public void stopForOtherAbilitiesIncludingANewAbilityOnTheSameCard() {
        Position p = new Position();
        SpellAbility mesa = p.ability(10, 101, 20, "Arid Mesa");
        p.refresh(false, true, mesa);
        String id = p.idFor(10);
        assertTrue(p.pass.setIgnored(id, true, p.player, true));
        SpellAbility secondAbility = p.ability(10, 101, 21, "Arid Mesa");
        p.refresh(false, true, mesa, secondAbility);
        assertTrue(p.pass.hasNonIgnoredActions(p.player), "Ignoring a card's one ability never ignores its other abilities");
        assertEquals(p.pass.legal(p.player).stream().filter(PlayerControllerHuman.PriorityAbility::ignoredUntilNextTurn).count(), 1L);

        SpellAbility anotherMesa = p.ability(11, 102, 20, "Arid Mesa");
        p.refresh(false, true, mesa, anotherMesa);
        assertTrue(p.pass.hasNonIgnoredActions(p.player), "Another copy remains a reason to stop");
        p.refresh(false, true, mesa);
        assertEquals(p.idFor(10), id, "The same ability retains its opaque token across priority windows");
        assertFalse(p.pass.hasNonIgnoredActions(p.player));
    }

    @Test public void neverIgnoreSpellsLandPlaysOrIncompleteScans() {
        Position p = new Position();
        SpellAbility mesa = p.ability(10, 101, 20, "Arid Mesa");
        p.refresh(false, true, mesa);
        String id = p.idFor(10);
        assertTrue(p.pass.setIgnored(id, true, p.player, true));
        p.refresh(true, true, mesa);
        assertTrue(p.pass.hasNonIgnoredActions(p.player), "Other action types always stop Auto");
        p.refresh(false, false, mesa);
        assertTrue(p.pass.hasNonIgnoredActions(p.player), "A scan timeout must never authorize passing");
        assertTrue(p.pass.legal(p.player).isEmpty(), "A partial candidate list is not offered as complete");
        assertFalse(p.pass.setIgnored(id, true, p.player, true));
        AvailableActions.ActionScan incomplete = new AvailableActions.ActionScan(Set.of(), List.of(), false, false);
        assertTrue(incomplete.hasNonIgnoredActions(ability -> true));
    }

    @Test public void retainThroughOpponentsTurnsAndExpireAtTheNextOwnTurn() {
        Position p = new Position();
        SpellAbility mesa = p.ability(10, 101, 20, "Arid Mesa");
        for (int turn = 0; turn < 3; turn++) p.player.incrementTurn();
        p.refresh(false, true, mesa);
        String id = p.idFor(10);
        assertTrue(p.pass.setIgnored(id, true, p.player, true));
        // Opponents taking any number of turns does not change this player's turns played.
        for (int opponent = 0; opponent < 4; opponent++) {
            p.fixture.opponent.incrementTurn();
            p.refresh(false, true, mesa);
            assertFalse(p.pass.hasNonIgnoredActions(p.player));
        }
        p.player.incrementTurn();
        assertTrue(p.pass.ignored(p.player).isEmpty(), "Expiry does not wait until the end of the next own turn");
        p.refresh(false, true, mesa);
        assertTrue(p.pass.hasNonIgnoredActions(p.player), "The first next-turn priority stops normally");
        assertTrue(p.pass.setIgnored(id, true, p.player, true));
        p.player.incrementTurn();
        p.refresh(false, true, mesa);
        assertTrue(p.pass.hasNonIgnoredActions(p.player), "An immediate extra turn also expires the policy");
    }

    @Test public void rejectStaleSettingsAndExpireWhenTheSourceChangesZonesOrController() {
        Position p = new Position();
        SpellAbility mesa = p.ability(10, 101, 20, "Arid Mesa");
        p.refresh(false, true, mesa);
        String id = p.idFor(10);
        assertFalse(p.pass.setIgnored(id, true, p.player, false), "A stale prompt cannot create a policy");
        assertFalse(p.pass.setIgnored("unknown", true, p.player, true));
        assertTrue(p.pass.setIgnored(id, true, p.player, true));
        SpellAbility returnedMesa = p.ability(10, 202, 20, "Arid Mesa");
        p.refresh(false, true, returnedMesa);
        assertTrue(p.pass.ignored(p.player).isEmpty());
        assertNotEquals(p.idFor(10), id, "A new zone-change timestamp receives a new ability identity");
        assertTrue(p.pass.hasNonIgnoredActions(p.player));

        String returnedId = p.idFor(10);
        assertTrue(p.pass.setIgnored(returnedId, true, p.player, true));
        returnedMesa.getHostCard().setController(p.fixture.opponent, 303);
        p.refresh(false, true);
        assertTrue(p.pass.ignored(p.player).isEmpty(), "Control changes remove the old player's policy");
        p.pass.clearScan();
        assertFalse(p.pass.setIgnored(returnedId, true, p.player, true));
    }

    @Test public void clearPreferencesWhenTheGameEnds() {
        Position p = new Position();
        SpellAbility mesa = p.ability(10, 101, 20, "Arid Mesa");
        p.refresh(false, true, mesa);
        assertTrue(p.pass.setIgnored(p.idFor(10), true, p.player, true));
        p.game.fireEvent(new GameEventGameFinished());
        assertTrue(p.pass.ignored(p.player).isEmpty());
    }

    @Test public void clearASacrificedCardsPolicyImmediatelyWithoutWaitingForItsOwnersPriority() {
        Position p = new Position();
        SpellAbility mesa = p.ability(10, 101, 20, "Arid Mesa");
        p.refresh(false, true, mesa);
        String id = p.idFor(10);
        assertTrue(p.pass.setIgnored(id, true, p.player, true));
        p.game.fireEvent(new GameEventCardChangeZone(mesa.getHostCard().getView(), null, null));
        assertTrue(p.pass.ignored(p.player).isEmpty(), "The policy clears before another seat's next input");
        assertFalse(p.pass.setIgnored(id, true, p.player, true), "The departed instance cannot be re-ignored");

        SpellAbility returnedMesa = p.ability(10, 202, 20, "Arid Mesa");
        p.refresh(false, true, returnedMesa);
        String returnedId = p.idFor(10);
        assertNotEquals(returnedId, id);
        assertTrue(p.pass.setIgnored(returnedId, true, p.player, true));
        p.pass.gameFinished(new GameEventGameFinished());
        assertTrue(p.pass.ignored(p.player).isEmpty());
        assertFalse(p.pass.setIgnored(returnedId, true, p.player, true));
    }
}
