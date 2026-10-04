package forge.api;

import forge.game.card.CardView;
import forge.game.player.PlayerView;
import forge.game.zone.ZoneType;
import forge.trackable.TrackableProperty;
import org.testng.annotations.Test;

import static org.testng.Assert.*;

public class NetworkZoneSnapshotTest {
    @Test public void tableChoiceLinksExcludeRevealedHiddenZones() {
        var owner = new PlayerView(1, null);
        var opponent = new PlayerView(2, null);
        var card = new CardView(3, null);
        card.set(TrackableProperty.Controller, owner);
        card.set(TrackableProperty.Zone, ZoneType.Battlefield);
        assertTrue(MatchSession.isTableChoice(card, opponent));
        card.set(TrackableProperty.Zone, ZoneType.Hand);
        assertTrue(MatchSession.isTableChoice(card, owner));
        assertFalse(MatchSession.isTableChoice(card, opponent));
        card.set(TrackableProperty.Zone, ZoneType.Library);
        assertFalse(MatchSession.isTableChoice(card, owner));
        card.set(TrackableProperty.Zone, ZoneType.Graveyard);
        assertFalse(MatchSession.isTableChoice(card, opponent));
    }
    @Test public void aPlayedLandCannotAppearInItsOldHandDuringDeltaUpdates() {
        var owner = new PlayerView(1, null);
        var opponent = new PlayerView(2, null);
        var land = new CardView(3, null);
        land.set(TrackableProperty.Controller, owner);
        assertFalse(MatchSession.isVisibleInZone(land, ZoneType.Hand, opponent), "An incomplete card is not public");
        land.set(TrackableProperty.Zone, ZoneType.Hand);
        assertTrue(MatchSession.isVisibleInZone(land, ZoneType.Hand, owner));
        assertFalse(MatchSession.isVisibleInZone(land, ZoneType.Hand, opponent));
        land.set(TrackableProperty.Zone, ZoneType.Battlefield);
        assertTrue(land.canBeShownTo(opponent), "The played land is now public");
        assertFalse(MatchSession.isVisibleInZone(land, ZoneType.Hand, opponent), "Its old hand collection may still contain it");
        assertTrue(MatchSession.isVisibleInZone(land, ZoneType.Battlefield, opponent));
        land.set(TrackableProperty.Zone, ZoneType.Hand);
        assertFalse(MatchSession.isVisibleInZone(land, ZoneType.Battlefield, opponent), "A returned card is absent from the old battlefield");
        assertFalse(MatchSession.isVisibleInZone(land, ZoneType.Hand, opponent));
    }
}
