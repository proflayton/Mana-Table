package forge.api;

import com.google.gson.JsonObject;
import org.testng.annotations.Test;

import java.util.List;
import java.util.Map;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

public class EquipmentSnapshotTest {
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
            assertTrue(card(main.state(), "Shuko", 0).get("attachedTo").isJsonNull());
            var equipped = equip(game, main, 0);
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
            assertTrue(card(moved.state(), "Wild Cantor", 0).getAsJsonArray("attachments").isEmpty());
            assertEquals(card(moved.state(), "Wild Cantor", 1).getAsJsonArray("attachments").size(), 1);
            var creature = card(moved.state(), "Wild Cantor", 1);
            assertTrue(game.answer(moved, Map.of("action", "card", "key", text(creature, "key"))));
            game.until(i -> {
                var field = zone(player(i.state(), owner), "Battlefield");
                return field.get("count").getAsInt() == 2 && field.getAsJsonArray("cards").asList().stream()
                        .map(c -> c.getAsJsonObject()).anyMatch(c -> text(c, "name").equals("Shuko") && c.get("attachedTo").isJsonNull());
            });
            for (int seat = 0; seat < 4; seat++) for (var entry : zone(player(game.state(seat), owner), "Battlefield").getAsJsonArray("cards")) {
                assertTrue(entry.getAsJsonObject().get("attachedTo").isJsonNull());
                assertTrue(entry.getAsJsonObject().getAsJsonArray("attachments").isEmpty());
            }
        }
    }
}
