package forge.api;

import forge.card.CardType;
import forge.game.card.CardView;
import forge.game.player.PlayerView;
import forge.game.zone.ZoneType;
import forge.trackable.TrackableCollection;
import forge.trackable.TrackableProperty;
import org.testng.annotations.Test;

import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

import static org.testng.Assert.*;

public class AttachmentProjectionTest {
    private final PlayerView viewer = new PlayerView(1, null);
    private final CombatCardIds ids = new CombatCardIds(viewer, new MatchActivity(viewer));

    private CardView card(int id, String name, String type) {
        var card = new CardView(id, null, name);
        card.set(TrackableProperty.Controller, viewer);
        card.set(TrackableProperty.Zone, ZoneType.Battlefield);
        card.getCurrentState().set(TrackableProperty.Type, CardType.parse(type, false));
        return card;
    }

    private Map<String, Object> project(CardView card) {
        var state = new LinkedHashMap<String, Object>();
        AttachmentProjection.add(state, card, viewer, ids);
        return state;
    }

    @Test public void relationshipsUseExactOccurrencesAndReplaceOnReequipAndDetach() {
        var first = card(101, "Llanowar Elves", "Creature Elf Druid");
        var second = card(102, "Llanowar Elves", "Creature Elf Druid");
        var sword = card(103, "Short Sword", "Artifact Equipment");
        var boots = card(104, "Swiftfoot Boots", "Artifact Equipment");
        sword.set(TrackableProperty.EntityAttachedTo, first);
        boots.set(TrackableProperty.EntityAttachedTo, first);
        first.set(TrackableProperty.AttachedCards, new TrackableCollection<>(List.of(sword, boots)));
        assertEquals(((Map<?, ?>) project(sword).get("attachedTo")).get("id"), ids.id(first));
        assertEquals(((List<?>) project(first).get("attachments")).size(), 2);
        assertNotEquals(ids.id(first), ids.id(second));
        first.set(TrackableProperty.PhasedOut, true);
        sword.set(TrackableProperty.PhasedOut, true);
        boots.set(TrackableProperty.PhasedOut, true);
        assertEquals(((List<?>) project(first).get("attachments")).size(), 2, "Phasing preserves physical attachments");
        assertEquals(((Map<?, ?>) project(sword).get("attachedTo")).get("id"), ids.id(first));
        for (var card : List.of(first, sword, boots)) card.set(TrackableProperty.PhasedOut, false);
        sword.set(TrackableProperty.EntityAttachedTo, second);
        first.set(TrackableProperty.AttachedCards, new TrackableCollection<>(boots));
        second.set(TrackableProperty.AttachedCards, new TrackableCollection<>(sword));
        assertEquals(((Map<?, ?>) project(sword).get("attachedTo")).get("id"), ids.id(second));
        assertEquals(((List<?>) project(first).get("attachments")).size(), 1);
        sword.set(TrackableProperty.EntityAttachedTo, null);
        second.set(TrackableProperty.AttachedCards, null);
        assertNull(project(sword).get("attachedTo"));
        assertTrue(((List<?>) project(second).get("attachments")).isEmpty());
    }

    @Test public void hiddenFacesAreRedactedAndHiddenZonesRevokeRelationships() {
        var creature = card(201, "Secret creature", "Creature Elf");
        var sword = card(202, "Short Sword", "Artifact Equipment");
        sword.set(TrackableProperty.EntityAttachedTo, creature);
        creature.set(TrackableProperty.AttachedCards, new TrackableCollection<>(sword));
        creature.set(TrackableProperty.Facedown, true);
        var target = (Map<?, ?>) project(sword).get("attachedTo");
        assertEquals(target.get("name"), "Face-down permanent");
        assertEquals(target.get("type"), "");
        assertEquals(target.get("id"), ids.id(creature));
        sword.set(TrackableProperty.Facedown, true);
        var attachment = (Map<?, ?>) ((List<?>) project(creature).get("attachments")).get(0);
        assertEquals(attachment.get("name"), "Face-down permanent");
        assertEquals(attachment.get("type"), "");
        creature.set(TrackableProperty.Zone, ZoneType.Library);
        assertNull(project(sword).get("attachedTo"));
        assertTrue(((List<?>) project(creature).get("attachments")).isEmpty());
        sword.set(TrackableProperty.Zone, ZoneType.Graveyard);
        assertNull(project(sword).get("attachedTo"));
    }
}
