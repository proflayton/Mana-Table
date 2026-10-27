package forge.api;

import forge.game.card.CardView;
import forge.game.event.*;
import forge.game.player.PlayerView;
import forge.game.phase.PhaseType;
import forge.game.zone.ZoneType;
import forge.game.zone.ZoneView;
import forge.trackable.TrackableProperty;
import forge.util.Localizer;
import org.testng.annotations.BeforeClass;
import org.testng.annotations.Test;

import static org.testng.Assert.*;

public class MatchActivityTest {
    @Test public void aCurrentVisibleReplacementKeepsItsDisplayAnchorWithoutGrantingHistoricalAuthority() {
        var owner = new PlayerView(1, null); var observer = new PlayerView(2, null);
        var hand = publicCard(211, "Known card", owner); hand.set(TrackableProperty.Zone, ZoneType.Hand);
        var actual = new java.util.concurrent.atomic.AtomicReference<>(hand);
        var identities = new MatchIdentities();
        var own = new MatchActivity(owner, identities, view -> view == actual.get(), ignored -> actual.get());
        var other = new MatchActivity(observer, identities, view -> view == actual.get(), ignored -> actual.get());
        String anchor = own.visualId(hand); assertNull(other.visualId(hand));
        var stack = publicCard(211, "Known card", owner); stack.set(TrackableProperty.Zone, ZoneType.Stack);
        actual.set(stack); own.refreshVisibility(); other.refreshVisibility();
        assertEquals(own.visualId(stack), anchor); assertEquals(other.visualId(stack), anchor);
        assertNull(own.visualId(hand), "An earlier view does not acquire current source authority");
        var battlefield = publicCard(211, "Known card", owner);
        actual.set(battlefield); own.refreshVisibility(); other.refreshVisibility();
        assertEquals(own.visualId(battlefield), anchor); assertEquals(other.visualId(battlefield), anchor);
        stack.set(TrackableProperty.Zone, null);
        own.refreshVisibility(); other.refreshVisibility();
        assertEquals(own.visualId(battlefield), anchor, "Historical metadata cannot revoke the current public replacement");
        var rollback = publicCard(211, "Known card", owner); rollback.set(TrackableProperty.Zone, ZoneType.Hand);
        actual.set(rollback);
        // All audiences reconcile before any one of them projects. A readable
        // owner hand does not prevent the observer from revoking public identity.
        own.refreshVisibility(); other.refreshVisibility();
        String concealed = own.visualId(rollback);
        assertNotEquals(concealed, anchor); assertNull(other.visualId(rollback));
        assertNull(own.visualId(battlefield));
        assertEquals(own.visualId(rollback), concealed);
    }

    @Test public void aDepartingPublicViewKeepsOnlyItsPreviouslyObservedHistoryAnchor() {
        var viewer = new PlayerView(1, null);
        var departing = publicCard(209, "Departing permanent", viewer);
        var current = publicCard(209, "Current exile occurrence", viewer);
        current.set(TrackableProperty.Zone, ZoneType.Exile);
        var actual = new java.util.concurrent.atomic.AtomicReference<>(departing);
        var identities = new MatchIdentities();
        var activity = new MatchActivity(viewer, identities, view -> view == actual.get());
        activity.onGameEvent(new GameEventTurnBegan(viewer, 1));
        String known = activity.visualId(departing);
        actual.set(current);
        activity.onGameEvent(new GameEventCardChangeZone(departing,
                new ZoneView(viewer, ZoneType.Battlefield), new ZoneView(viewer, ZoneType.Exile)));
        var movement = activity.snapshot().get(1);
        assertEquals(movement.cardId(), known, "A genuine public movement retains its event-time animation/history anchor");
        assertNull(activity.visualId(departing), "That readonly history reference cannot become a current snapshot identity");
        assertEquals(activity.visualId(current), known);
        activity.onGameEvent(new GameEventCardDamaged(departing, null, 1, GameEventCardDamaged.DamageType.Normal));
        assertNull(activity.snapshot().get(2).cardId(), "A historical view cannot borrow a newer view's recorded anchor, even with the same physical ID");
        assertNull(activity.snapshot().get(2).impact().cardId());
        departing.set(TrackableProperty.Zone, null);
        activity.refreshVisibility();
        assertEquals(activity.visualId(current), known, "Reading or concealing a historical view cannot alter the live generation");
        var unknown = publicCard(210, "Unobserved historical permanent", viewer);
        activity.onGameEvent(new GameEventCardChangeZone(unknown,
                new ZoneView(viewer, ZoneType.Battlefield), new ZoneView(viewer, ZoneType.Exile)));
        assertNull(activity.snapshot().get(3).cardId(), "An event cannot mint a current identity for an unobserved historical portrait");
        current.set(TrackableProperty.Zone, ZoneType.Library);
        activity.refreshVisibility();
        current.set(TrackableProperty.Zone, ZoneType.Exile);
        assertNotEquals(activity.visualId(current), known, "Real concealment still revokes the current generation");
        assertEquals(movement.cardId(), known, "Retained history is detached and never rewritten to a later generation");
    }

    @Test public void aHistoricalEqualIdPortraitCannotRevokeTheCurrentVisibleOccurrence() {
        var viewer = new PlayerView(1, null);
        var current = publicCard(208, "Current permanent", viewer);
        var historical = publicCard(208, "Historical trigger portrait", viewer);
        assertEquals(historical, current, "Forge CardView equality deliberately identifies the physical card only");
        var identities = new MatchIdentities();
        var first = new MatchActivity(viewer, identities, view -> view == current);
        var second = new MatchActivity(new PlayerView(2, null), identities, view -> view == current);
        String currentId = first.visualId(current);
        assertNull(first.visualId(historical), "Even a readable historical face grants no current occurrence ID");
        assertEquals(historical.getCurrentState().getName(), "Historical trigger portrait", "The historical portrait remains readable");
        historical.set(TrackableProperty.Zone, null);
        assertNull(first.visualId(historical));
        assertEquals(first.visualId(current), currentId);
        assertEquals(second.visualId(current), currentId);
        assertNull(second.visualId(historical));
        first.refreshVisibility(); second.refreshVisibility();
        assertEquals(first.visualId(current), currentId);
        assertEquals(second.visualId(current), currentId);
        current.set(TrackableProperty.Zone, ZoneType.Library);
        first.refreshVisibility(); second.refreshVisibility();
        assertNull(first.visualId(current));
        current.set(TrackableProperty.Zone, ZoneType.Battlefield);
        assertNotEquals(first.visualId(current), currentId, "Real concealment still revokes the current occurrence");
    }

    @Test public void aLateAudienceRevocationCannotEraseANewerAuthorizedOccurrence() {
        var owner = new PlayerView(1, null); var observer = new PlayerView(2, null);
        var card = publicCard(207, "Private occurrence", owner);
        var identities = new MatchIdentities();
        var own = new MatchActivity(owner, identities); var other = new MatchActivity(observer, identities);
        String publicId = own.visualId(card);
        assertEquals(other.visualId(card), publicId);
        // A permission can end without a movement event, just as cast rollback
        // restores a zone directly. The two audiences need not refresh together.
        card.set(TrackableProperty.Zone, ZoneType.Library);
        own.refreshVisibility();
        card.set(TrackableProperty.PlayerMayLook, new forge.trackable.TrackableCollection<>(owner));
        String privateId = own.visualId(card);
        assertNotEquals(privateId, publicId, "Concealment still revokes the previously public occurrence");
        other.refreshVisibility();
        assertNull(other.visualId(card));
        assertEquals(own.visualId(card), privateId, "A stale observer must not revoke a different, newly authorized occurrence");
    }

    @Test public void actualDamageKeepsItsOwnRecipientAndPublicOccurrenceAcrossDeparture() {
        var viewer = new PlayerView(1, null); var opponent = new PlayerView(2, null);
        var source = publicCard(201, "Damage source", viewer);
        var recipient = publicCard(202, "Damage recipient", opponent);
        var activity = new MatchActivity(viewer);
        String recipientId = activity.visualId(recipient), sourceId = activity.visualId(source);
        activity.onGameEvent(new GameEventCardDamaged(recipient, source, 2, GameEventCardDamaged.DamageType.Normal));
        var recorded = activity.snapshot();
        var damage = recorded.get(0).impact();
        assertEquals(damage.kind(), "damage"); assertEquals(damage.amount(), 2L);
        assertEquals(damage.cardId(), recipientId); assertEquals(damage.cardName(), "Damage recipient");
        assertEquals(damage.sourceCardId(), sourceId); assertNotEquals(damage.cardId(), damage.sourceCardId());
        assertNull(damage.playerId()); assertEquals(damage.damageType(), "normal");
        assertNull(damage.before()); assertNull(damage.after()); assertNull(damage.combat());
        assertNull(recorded.get(0).cause(), "The event supplies no resolving ability identity");
        recipient.set(TrackableProperty.Zone, ZoneType.Graveyard);
        activity.onGameEvent(new GameEventCardChangeZone(recipient,
                new ZoneView(opponent, ZoneType.Battlefield), new ZoneView(opponent, ZoneType.Graveyard)));
        assertEquals(activity.visualId(recipient), recipientId, "A public death keeps the impact anchor resolvable");
        assertEquals(recorded.get(0).impact(), damage, "A published impact is detached event-time data");
        activity.onGameEvent(new GameEventCardChangeZone(recipient,
                new ZoneView(opponent, ZoneType.Graveyard), new ZoneView(opponent, ZoneType.Hand)));
        recipient.set(TrackableProperty.Zone, ZoneType.Hand);
        assertNull(activity.visualId(recipient));
        recipient.set(TrackableProperty.Zone, ZoneType.Battlefield);
        assertNotEquals(activity.visualId(recipient), recipientId, "A later public occurrence cannot inherit an old impact");
    }

    @Test public void damageProjectionMasksAnonymousRecipientsAndHiddenSources() {
        var viewer = new PlayerView(1, null); var opponent = new PlayerView(2, null);
        var source = publicCard(203, "Secret source", opponent);
        source.set(TrackableProperty.Zone, ZoneType.Hand);
        var target = publicCard(204, "Secret recipient", opponent);
        target.set(TrackableProperty.Facedown, true);
        var identities = new MatchIdentities(); var activity = new MatchActivity(viewer, identities);
        var positions = new CombatCardIds(viewer, activity, identities);
        String anonymousId = positions.id(target);
        activity.onGameEvent(new GameEventCardDamaged(target, source, 3, GameEventCardDamaged.DamageType.Normal));
        var entry = activity.snapshot().get(0);
        assertEquals(entry.impact().cardId(), anonymousId);
        assertEquals(entry.impact().cardName(), "Face-down card");
        assertNull(entry.impact().sourceCardId()); assertNull(entry.impact().sourceName());
        assertFalse(entry.toString().contains("Secret source")); assertFalse(entry.toString().contains("Secret recipient"));
        target.set(TrackableProperty.Zone, ZoneType.Hand);
        activity.onGameEvent(new GameEventCardDamaged(target, source, 1, GameEventCardDamaged.DamageType.Normal));
        assertNull(activity.snapshot().get(1).impact(), "A private recipient cannot acquire a public damage anchor");
    }

    @Test public void damageAndLifeChangesRemainDifferentFactsWithExactEngineMetadata() {
        var viewer = new PlayerView(1, null); var opponent = new PlayerView(2, null);
        var source = publicCard(205, "Public source", viewer);
        var activity = new MatchActivity(viewer);
        activity.onGameEvent(new GameEventTurnBegan(viewer, 1));
        activity.onGameEvent(new GameEventPlayerDamaged(opponent, source, 4, true, true));
        var damage = activity.snapshot().get(1);
        assertEquals(damage.impact().amount(), 4L); assertEquals(damage.impact().playerId(), Integer.valueOf(2));
        assertNull(damage.impact().cardId(), "A player damage recipient is not the source card");
        assertEquals(damage.impact().sourceCardId(), activity.visualId(source));
        assertEquals(damage.impact().combat(), Boolean.TRUE); assertEquals(damage.impact().infect(), Boolean.TRUE);
        assertNull(damage.impact().before()); assertNull(damage.impact().after());
        activity.onGameEvent(new GameEventPlayerLivesChanged(opponent, 20, 13));
        var life = activity.snapshot().get(2).impact();
        assertEquals(life.kind(), "life"); assertEquals(life.amount(), -7L);
        assertEquals(life.before(), Integer.valueOf(20)); assertEquals(life.after(), Integer.valueOf(13));
        assertNull(life.sourceCardId()); assertNull(life.sourceName()); assertNull(life.damageType());
        assertNull(life.combat()); assertNull(life.infect());
        activity.onGameEvent(new GameEventPlayerLivesChanged(opponent, Integer.MIN_VALUE, Integer.MAX_VALUE));
        assertEquals(activity.snapshot().get(3).impact().amount(), 4294967295L, "Life deltas must not overflow or become damage");
        var target = publicCard(206, "Public target", opponent);
        var types = GameEventCardDamaged.DamageType.values();
        var names = new String[] { "normal", "m1m1Counters", "deathtouch", "loyaltyLoss" };
        for (int i = 0; i < types.length; i++) {
            activity.onGameEvent(new GameEventCardDamaged(target, source, 1, types[i]));
            var impact = activity.snapshot().get(activity.snapshot().size() - 1).impact();
            assertEquals(impact.damageType(), names[i]); assertEquals(impact.amount(), 1L);
            assertNull(impact.combat(), "Card damage does not report combat in this event");
        }
        var legacy = new MatchActivity.Entry(1, 1, "MAIN1", "damage", 2, "Legacy damage", null, null, "", "", 0);
        assertNull(legacy.impact(), "An old entry must not invent a typed result");
    }

    private static CardView publicCard(int id, String name, PlayerView controller) {
        var card = new CardView(id, null, name);
        card.set(TrackableProperty.Controller, controller); card.set(TrackableProperty.Zone, ZoneType.Battlefield);
        return card;
    }

    @Test public void authorizedTopCardKeepsItsIdentityThroughDrawingAndCasting() {
        var viewer = new PlayerView(1, null); var opponent = new PlayerView(2, null);
        var card = new CardView(102, null, "Known top card");
        card.set(TrackableProperty.Controller, viewer); card.set(TrackableProperty.Zone, ZoneType.Library);
        card.set(TrackableProperty.PlayerMayLook, new forge.trackable.TrackableCollection<>(viewer));
        var identities = new MatchIdentities(); var own = new MatchActivity(viewer, identities); var other = new MatchActivity(opponent, identities);
        String top = own.visualId(card); assertNull(other.visualId(card));
        identities.moved(new GameEventCardChangeZone(card, new ZoneView(viewer, ZoneType.Library), new ZoneView(viewer, ZoneType.Hand)));
        card.set(TrackableProperty.Zone, ZoneType.Hand);
        assertEquals(own.visualId(card), top); assertNull(other.visualId(card));
        identities.moved(new GameEventCardChangeZone(card, new ZoneView(viewer, ZoneType.Hand), new ZoneView(viewer, ZoneType.Stack)));
        card.set(TrackableProperty.Zone, ZoneType.Stack);
        assertEquals(own.visualId(card), top); assertEquals(other.visualId(card), top);
    }
    @Test public void faceDownPublicObjectsShareOpaqueIdentitiesAcrossSeatsWithoutRevealingPrintedIdentity() {
        var firstViewer = new PlayerView(1, null); var secondViewer = new PlayerView(2, null);
        var card = new CardView(101, null, "Concealed card");
        card.set(TrackableProperty.Controller, firstViewer); card.set(TrackableProperty.Zone, ZoneType.Battlefield);
        var identities = new MatchIdentities();
        var firstActivity = new MatchActivity(firstViewer, identities); var secondActivity = new MatchActivity(secondViewer, identities);
        var first = new CombatCardIds(firstViewer, firstActivity, identities); var second = new CombatCardIds(secondViewer, secondActivity, identities);
        String printed = firstActivity.visualId(card);
        assertEquals(secondActivity.visualId(card), printed);
        card.set(TrackableProperty.Facedown, true);
        String concealed = first.id(card);
        assertEquals(second.id(card), concealed); assertNotEquals(concealed, printed);
        assertNull(firstActivity.visualId(card)); assertNull(secondActivity.visualId(card));
        identities.moved(new GameEventCardChangeZone(card, new ZoneView(firstViewer, ZoneType.Battlefield), new ZoneView(firstViewer, ZoneType.Library)));
        assertNotEquals(first.id(card), concealed, "Hidden transitions revoke the public concealed occurrence");
    }
    @Test public void visibleLibraryHandlesSurvivePollsButNotRevocationOrShuffle() {
        var viewer = new PlayerView(1, null);
        var opponent = new PlayerView(2, null);
        var card = new CardView(10, null, "Private top card");
        card.set(TrackableProperty.Controller, viewer);
        card.set(TrackableProperty.Zone, ZoneType.Library);
        var activity = new MatchActivity(viewer);
        assertNull(activity.visualId(card));
        card.set(TrackableProperty.PlayerMayLook, new forge.trackable.TrackableCollection<>(viewer));
        var visible = activity.visualId(card);
        assertNotNull(visible);
        assertNull(new MatchActivity(opponent).visualId(card));
        activity.refreshVisibility();
        assertEquals(activity.visualId(card), visible);
        card.set(TrackableProperty.PlayerMayLook, null);
        activity.refreshVisibility();
        card.set(TrackableProperty.PlayerMayLook, new forge.trackable.TrackableCollection<>(viewer));
        var revealedAgain = activity.visualId(card);
        assertNotEquals(revealedAgain, visible);
        activity.onGameEvent(new GameEventShuffle(viewer));
        assertNotEquals(activity.visualId(card), revealedAgain);
    }

    @Test public void faceDownCombatPositionsAreDistinctAndBreakAtHiddenZoneTransitions() {
        var viewer = new PlayerView(1, null);
        var owner = new PlayerView(2, null);
        var activity = new MatchActivity(viewer);
        var ids = new CombatCardIds(viewer, activity);
        var first = new CardView(10, null, "Secret one");
        var second = new CardView(11, null, "Secret two");
        for (var card : java.util.List.of(first, second)) {
            card.set(TrackableProperty.Controller, owner);
            card.set(TrackableProperty.Zone, ZoneType.Battlefield);
            card.set(TrackableProperty.Facedown, true);
        }
        var before = ids.id(first);
        assertNotNull(before);
        assertEquals(ids.id(first), before);
        assertNotEquals(before, ids.id(second));
        assertNull(activity.visualId(first));
        ids.changedZone(new GameEventCardChangeZone(first, new ZoneView(owner, ZoneType.Battlefield), new ZoneView(owner, ZoneType.Library)));
        first.set(TrackableProperty.Zone, ZoneType.Library);
        assertNull(ids.id(first));
        first.set(TrackableProperty.Zone, ZoneType.Battlefield);
        assertNotEquals(ids.id(first), before);
        first.set(TrackableProperty.Facedown, false);
        assertEquals(ids.id(first), activity.visualId(first));
    }
    @BeforeClass public void language() { Localizer.getInstance().initialize("en-US", "../forge-gui/res/languages"); }

    @Test public void copiesEventsWithoutLeakingHiddenCardsAndResetsTheirHandles() {
        var viewer = new PlayerView(1, null);
        var opponent = new PlayerView(2, null);
        opponent.set(TrackableProperty.Name, "Verdant");
        var card = new CardView(99, null, "Secret identity");
        card.set(TrackableProperty.Controller, opponent);
        card.set(TrackableProperty.Zone, ZoneType.Hand);
        var activity = new MatchActivity(viewer);
        activity.onGameEvent(new GameEventTurnBegan(opponent, 2));
        activity.onGameEvent(new GameEventCardChangeZone(card, new ZoneView(opponent, ZoneType.Library), new ZoneView(opponent, ZoneType.Hand)));
        assertFalse(activity.snapshot().toString().contains("Secret identity"));
        assertNull(activity.visualId(card));
        card.set(TrackableProperty.Zone, ZoneType.Battlefield);
        activity.onGameEvent(new GameEventLandPlayed(opponent, card));
        var snapshot = activity.snapshot();
        var entry = snapshot.get(snapshot.size() - 1);
        assertEquals(entry.message(), "Verdant played Secret identity.");
        assertEquals(entry.turn(), 2);
        assertEquals(entry.cardId(), activity.visualId(card));
        assertNotEquals(entry.cardId(), "99");
        // Events can arrive before a frozen view updates its zone.
        activity.onGameEvent(new GameEventCardChangeZone(card, new ZoneView(opponent, ZoneType.Battlefield), new ZoneView(opponent, ZoneType.Library)));
        var movement = activity.snapshot().get(activity.snapshot().size() - 1);
        assertNull(movement.cardId());
        assertNull(movement.cardName());
        assertFalse(movement.message().contains("Secret identity"));
        card.set(TrackableProperty.Zone, ZoneType.Library);
        assertNull(activity.visualId(card));
        card.set(TrackableProperty.Zone, ZoneType.Battlefield);
        assertNotEquals(activity.visualId(card), entry.cardId(), "Hidden-zone transitions must break identity correlation");
        card.set(TrackableProperty.Facedown, true);
        activity.onGameEvent(new GameEventLandPlayed(opponent, card));
        var faceDown = activity.snapshot().get(activity.snapshot().size() - 1);
        assertNull(faceDown.cardId());
        assertNull(faceDown.cardName());
        assertFalse(faceDown.message().contains("Secret identity"));
        assertEquals(snapshot.size(), 3, "Published history must be immutable and detached");
    }

    @Test public void retainsOrderedHistoryAcrossPollsWithBoundedMemory() {
        var viewer = new PlayerView(1, null);
        var activity = new MatchActivity(viewer);
        activity.onGameEvent(new GameEventTurnBegan(viewer, 1));
        for (int i = 0; i < 150; i++) activity.onGameEvent(new GameEventPlayerLivesChanged(viewer, 20, 19));
        var history = activity.snapshot();
        assertEquals(history.size(), 120);
        assertEquals(history.get(119).id(), 151L);
        assertEquals(history.get(0).id(), 32L);
        assertEquals(history.get(119).message(), "You lost 1 life (19 remaining).");
        assertEquals(history, activity.snapshot());
    }

    @Test public void copiesPhaseAndTurnWithoutAddingNoisyHistoryEntries() {
        var viewer = new PlayerView(1, null);
        var activity = new MatchActivity(viewer);
        activity.onGameEvent(new GameEventTurnBegan(viewer, 3));
        var before = activity.frame();
        activity.onGameEvent(new GameEventTurnPhase(viewer, PhaseType.COMBAT_DECLARE_BLOCKERS, ""));
        var after = activity.frame();
        assertEquals(after.turn(), 3);
        assertEquals(after.activePlayerId(), Integer.valueOf(1));
        assertEquals(after.phaseKey(), "COMBAT_DECLARE_BLOCKERS");
        assertTrue(after.revision() > before.revision());
        assertEquals(after.entries(), before.entries());
        assertEquals(before.phaseKey(), "UNTAP", "Earlier frames must remain detached");
    }
}
