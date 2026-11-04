package forge.api;

import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.util.List;
import java.util.Map;
import java.util.TreeMap;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

public class EquipmentSnapshotTest {
    private static final Gson JSON = new GsonBuilder().serializeNulls().create();
    private static JsonObject card(JsonObject state, String name, int occurrence) {
        return zone(viewer(state), "Battlefield").getAsJsonArray("cards").asList().stream()
                .map(c -> c.getAsJsonObject()).filter(c -> text(c, "name").equals(name)).skip(occurrence).findFirst().orElseThrow();
    }

    private static RulesScenario.Input equip(RulesScenario game, RulesScenario.Input priority, int occurrence) throws Exception {
        assertTrue(game.answer(priority, Map.of("action", "card", "key", text(card(priority.state(), "Shuko", 0), "key"))));
        var target = game.until(i -> i.seat() == 0);
        if (text(target.prompt(), "kind").equals("choice")) {
            var ability = target.prompt().getAsJsonArray("choices").asList().stream().map(c -> c.getAsJsonObject())
                    .filter(c -> text(c, "label").contains("Equip")).findFirst().orElseThrow();
            assertTrue(game.answer(target, Map.of("choices", List.of(ability.get("index").getAsInt()))));
            target = game.until(i -> i.seat() == 0);
        }
        assertEquals(text(target.prompt(), "inputType"), "InputSelectTargets");
        var creature = card(target.state(), "Wild Cantor", occurrence);
        String targetId = text(creature, "combatId");
        assertTrue(game.answer(target, Map.of("action", "card", "key", text(creature, "key"))));
        return game.until(i -> {
            if (i.seat() != 0 || !text(i.prompt(), "inputType").equals("InputPassPriority") || !i.state().getAsJsonArray("stack").isEmpty()) return false;
            var attached = card(i.state(), "Shuko", 0).get("attachedTo");
            return attached.isJsonObject() && targetId.equals(text(attached.getAsJsonObject(), "id"));
        });
    }

    @Test(timeOut = 90000) public void realEquipReequipAndSacrificeUpdateEverySeatsSnapshot() throws Exception {
        try (var game = new RulesScenario("equipment-indicator", Map.of(0, List.of("Shuko", "Wild Cantor", "Wild Cantor")))) {
            var main = game.until(i -> i.seat() == 0 && text(i.state(), "phaseKey").equals("MAIN1"));
            assertPublicAttachments(game, "before-equip");
            assertTrue(card(main.state(), "Shuko", 0).get("attachedTo").isJsonNull());
            var equipped = equip(game, main, 0);
            assertPublicAttachments(game, "equipped");
            int owner = equipped.state().get("viewerId").getAsInt();
            for (int seat = 0; seat < 4; seat++) {
                var cards = zone(player(game.state(seat), owner), "Battlefield").getAsJsonArray("cards");
                var sword = cards.get(0).getAsJsonObject();
                var first = cards.get(1).getAsJsonObject();
                assertEquals(text(sword.getAsJsonObject("attachedTo"), "id"), text(first, "combatId"));
                assertEquals(text(first.getAsJsonArray("attachments").get(0).getAsJsonObject(), "name"), "Shuko");
                assertEquals(first.get("power").getAsInt(), 2);
            }
            var moved = equip(game, equipped, 1);
            assertPublicAttachments(game, "reequipped");
            assertTrue(card(moved.state(), "Wild Cantor", 0).getAsJsonArray("attachments").isEmpty());
            assertEquals(card(moved.state(), "Wild Cantor", 1).getAsJsonArray("attachments").size(), 1);
            var creature = card(moved.state(), "Wild Cantor", 1);
            assertTrue(game.answer(moved, Map.of("action", "card", "key", text(creature, "key"))));
            var sacrificed = game.until(i -> {
                var field = zone(player(i.state(), owner), "Battlefield");
                return field.get("count").getAsInt() == 2 && field.getAsJsonArray("cards").asList().stream()
                        .map(c -> c.getAsJsonObject()).anyMatch(c -> text(c, "name").equals("Shuko") && c.get("attachedTo").isJsonNull());
            });
            if (text(sacrificed.prompt(), "kind").equals("choice")) {
                assertEquals(sacrificed.prompt().get("min").getAsInt(), 1);
                assertEquals(sacrificed.prompt().get("max").getAsInt(), 1);
                int color = sacrificed.prompt().getAsJsonArray("choices").get(0).getAsJsonObject().get("index").getAsInt();
                assertTrue(game.answer(sacrificed, Map.of("choices", List.of(color))));
                game.until(i -> i.seat() == 0 && text(i.prompt(), "inputType").equals("InputPassPriority")
                        && i.state().getAsJsonArray("stack").isEmpty());
            }
            assertPublicAttachments(game, "host-sacrificed");
            for (int seat = 0; seat < 4; seat++) for (var entry : zone(player(game.state(seat), owner), "Battlefield").getAsJsonArray("cards")) {
                assertTrue(entry.getAsJsonObject().get("attachedTo").isJsonNull());
                assertTrue(entry.getAsJsonObject().getAsJsonArray("attachments").isEmpty());
            }
        }
    }

    @Test(timeOut = 90000) public void actualAttachmentDestructionWithdrawsBothPublicRelationshipDirections() throws Exception {
        try (var game = new RulesScenario("equipment-spectator-destruction",
                Map.of(0, List.of("Shuko", "Wild Cantor", "Seal of Primordium")))) {
            var main = game.until(i -> i.seat() == 0 && text(i.state(), "phaseKey").equals("MAIN1"));
            var equipped = equip(game, main, 0);
            assertPublicAttachments(game, "before-attachment-destruction");
            assertTrue(game.answer(equipped, Map.of("action", "card", "key", text(card(equipped.state(), "Seal of Primordium", 0), "key"))));
            var target = game.until(i -> i.seat() == 0);
            if (text(target.prompt(), "kind").equals("choice")) {
                var ability = target.prompt().getAsJsonArray("choices").asList().stream().map(value -> value.getAsJsonObject())
                        .filter(value -> text(value, "label").contains("Destroy target artifact")).findFirst().orElseThrow();
                assertTrue(game.answer(target, Map.of("choices", List.of(ability.get("index").getAsInt()))));
                target = game.until(i -> i.seat() == 0);
            }
            assertEquals(text(target.prompt(), "inputType"), "InputSelectTargets");
            assertTrue(game.answer(target, Map.of("action", "card", "key", text(card(target.state(), "Shuko", 0), "key"))));
            var resolved = game.until(i -> i.seat() == 0 && text(i.prompt(), "inputType").equals("InputPassPriority")
                    && i.state().getAsJsonArray("stack").isEmpty()
                    && zone(viewer(i.state()), "Graveyard").getAsJsonArray("cards").asList().stream()
                        .anyMatch(value -> text(value.getAsJsonObject(), "name").equals("Shuko")));
            assertEquals(zone(viewer(resolved.state()), "Battlefield").get("count").getAsInt(), 1);
            assertTrue(card(resolved.state(), "Wild Cantor", 0).getAsJsonArray("attachments").isEmpty());
            assertEquals(card(resolved.state(), "Wild Cantor", 0).get("power").getAsInt(), 1);
            assertPublicAttachments(game, "attachment-destroyed");
        }
    }

    private static void assertPublicAttachments(RulesScenario game, String label) throws Exception {
        var facts = HeadlessPlatform.query(game.table, () -> {
            long board = ((Number)game.session(0).state().get("boardRevision")).longValue();
            var json = JSON;
            var spectator = json.toJsonTree(SpectatorObservation.project(game.table, board, null)).getAsJsonObject();
            var expected = relationships(spectator);
            for (int seat = 0; seat < 4; seat++) {
                var state = json.toJsonTree(game.session(seat).state()).getAsJsonObject();
                assertEquals(state.get("boardRevision").getAsLong(), board);
                assertEquals(relationships(state), expected, "The spectator and each seat must share current battlefield/graveyard attachment topology: " + label);
            }
            return Map.of("boardRevision", board, "relationships", expected);
        });
        Files.writeString(game.evidence.resolve("public-attachments-" + label + ".json"), JSON.toJson(facts));
    }

    private static Map<String, Object> relationships(JsonObject state) {
        var result = new TreeMap<String, Object>();
        for (var entry : state.getAsJsonArray("players")) {
            var player = entry.getAsJsonObject();
            for (String name : List.of("Battlefield", "Graveyard")) for (var value : zone(player, name).getAsJsonArray("cards")) {
                var card = value.getAsJsonObject();
                assertTrue(card.has("attachedTo") && card.has("attachments"));
                String id = text(card, "combatId");
                if (id.isEmpty()) id = text(card, "visualId");
                assertFalse(id.isEmpty(), "A public attachment fixture card retains a physical occurrence");
                String parent = card.get("attachedTo").isJsonObject() ? text(card.getAsJsonObject("attachedTo"), "id") : "";
                var children = card.getAsJsonArray("attachments").asList().stream()
                        .map(child -> text(child.getAsJsonObject(), "id")).sorted().toList();
                result.put(player.get("id").getAsInt() + ":" + name + ":" + id, Map.of("parent", parent, "children", children));
            }
        }
        return result;
    }
}
