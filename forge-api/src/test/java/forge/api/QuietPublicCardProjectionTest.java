package forge.api;

import org.testng.annotations.Test;

import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

import static org.testng.Assert.*;

/** Closed public-value controls, not a second rules or visibility evaluator. */
public class QuietPublicCardProjectionTest {
    @Test public void privateCardFactsCannotChangeOrRevokeThePublicQuietBaseline() {
        var card = card();
        card.put("playAffordability", "Payable"); card.put("futurePrivateInput", Map.of("key", "old-input"));
        var face = new LinkedHashMap<>(Map.of("name", "Public back", "manaCost", "{2}", "privateOffer", "old"));
        var attached = new LinkedHashMap<>(Map.of("id", "public-host", "name", "Host", "type", "Creature", "privateOffer", "old"));
        card.put("otherFace", face); card.put("attachedTo", attached); card.put("attachments", List.of(attached));
        var baseline = new QuietYieldBaseline();
        String token = baseline.observe(state(card), 4);
        card.put("key", "different-input"); card.put("selectable", false); card.put("highlighted", true);
        card.put("playAffordability", "Unknown"); card.put("futurePrivateInput", Map.of("key", "new-input"));
        face.put("privateOffer", "new"); attached.put("privateOffer", "new");
        assertEquals(baseline.observe(state(card), 4), token, "Private input changes cannot replace public permission");
        baseline.observeCard(card);
        assertTrue(baseline.permits(token, 4), "The event-time card projection must equal the baseline's projection");
    }

    @Test public void everyPublicCharacteristicIdentityAndRelationshipStillInterrupts() {
        var changes = new LinkedHashMap<String, Object>();
        changes.put("name", "Changed"); changes.put("type", "Artifact Creature"); changes.put("gamePieceType", "token");
        changes.put("manaCost", "{5}"); changes.put("commanderTax", 2); changes.put("power", 3); changes.put("toughness", 4);
        changes.put("text", "Changed rules"); changes.put("tapped", true); changes.put("phasedOut", true); changes.put("sick", true);
        changes.put("damage", 1); changes.put("attacking", true); changes.put("blocking", true); changes.put("counters", Map.of("Blight", 1));
        changes.put("defenderId", 2); changes.put("defender", "Player 2"); changes.put("faceDown", true);
        changes.put("combatKeywords", List.of("Flying")); changes.put("blockingCapacity", Map.of("maximum", 2, "unlimited", false));
        changes.put("states", List.of(Map.of("key", "different-public-mechanic", "value", 2)));
        changes.put("visualId", "another-visible-occurrence"); changes.put("combatId", "another-physical-occurrence");
        changes.put("attachedTo", Map.of("id", "host", "name", "Host", "type", "Creature"));
        changes.put("attachments", List.of(Map.of("id", "equipment", "name", "Equipment", "type", "Artifact")));
        changes.put("otherFace", Map.of("name", "Public back", "manaCost", "{2}"));
        changes.put("artName", "Public art"); changes.put("artFace", "back"); changes.put("artSet", "abc");
        changes.put("edition", "ABC"); changes.put("collectorNumber", "5"); changes.put("artIndex", 2); changes.put("foil", true);
        for (var change : changes.entrySet()) {
            var card = card(); var baseline = new QuietYieldBaseline();
            String token = baseline.observe(state(card), 4);
            card.put(change.getKey(), change.getValue());
            assertNotEquals(baseline.observe(state(card), 4), token, "Public field must remain meaningful: " + change.getKey());
            var event = new QuietYieldBaseline(); var original = card();
            String eventToken = event.observe(state(original), 4);
            original.put(change.getKey(), change.getValue()); event.observeCard(original);
            assertFalse(event.permits(eventToken, 4), "Event refresh must retain the same public field: " + change.getKey());
        }
    }

    @Test public void retainedPublicContainersAreDetachedAndTransientPublicChangesRemainInterruptions() {
        var card = card(); var counters = new LinkedHashMap<String, Integer>(); counters.put("Blight", 1);
        var states = new ArrayList<>(List.of(Map.of("key", "public-mechanic", "value", 2)));
        card.put("counters", counters); card.put("states", states);
        var baseline = new QuietYieldBaseline(); String token = baseline.observe(state(card), 4);
        counters.put("Blight", 2); baseline.observeCard(card);
        counters.put("Blight", 1); baseline.observeCard(card);
        assertFalse(baseline.permits(token, 4), "A real public change cannot disappear between polls");
        String next = baseline.observe(state(card), 4);
        states.set(0, Map.of("key", "different-public-mechanic", "value", 2));
        assertNotEquals(baseline.observe(state(card), 4), next, "Nested public mechanic keys are not private command keys");
    }

    private static Map<String, Object> state(Map<String, Object> card) {
        return Map.of("turn", 2, "activePlayerId", 1, "players", List.of(Map.of("id", 0,
                "zones", List.of(Map.of("name", "Battlefield", "cards", List.of(card))))), "stack", List.of());
    }

    private static LinkedHashMap<String, Object> card() {
        var card = new LinkedHashMap<String, Object>();
        card.put("visualId", "visible-occurrence"); card.put("combatId", "physical-occurrence");
        card.put("name", "Public card"); card.put("type", "Creature"); card.put("gamePieceType", "card");
        card.put("manaCost", "{2}"); card.put("commanderTax", 0); card.put("power", 2); card.put("toughness", 2);
        card.put("text", "Public rules"); card.put("tapped", false); card.put("phasedOut", false); card.put("sick", false);
        card.put("damage", 0); card.put("attacking", false); card.put("blocking", false); card.put("counters", Map.of());
        card.put("defenderId", null); card.put("defender", null); card.put("faceDown", false); card.put("combatKeywords", List.of());
        card.put("blockingCapacity", null); card.put("states", List.of(Map.of("key", "public-mechanic", "value", 2)));
        card.put("attachedTo", null); card.put("attachments", List.of());
        return card;
    }
}
