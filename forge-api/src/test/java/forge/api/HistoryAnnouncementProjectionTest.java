package forge.api;

import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import forge.StaticData;
import forge.deck.Deck;
import forge.game.ability.AbilityFactory;
import forge.game.ability.AbilityUtils;
import forge.game.card.Card;
import forge.game.event.StackAnnouncementReceipt;
import forge.game.spellability.SpellAbility;
import forge.game.spellability.SpellAbilityStackInstance;
import forge.game.zone.ZoneType;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.HashSet;
import java.util.List;

import static forge.api.RulesScenario.text;
import static org.testng.Assert.*;

/** Controlled core stack commits and privacy/freeze guards. These do not claim
 * to pay a spell's cost; AnnouncedTargetHistoryJourneyTest covers that journey. */
public class HistoryAnnouncementProjectionTest {
    private static final Gson JSON = new GsonBuilder().serializeNulls().create();

    @Test
    public void frozenPublicCardPlayerAndSubmodeAllocationsSurviveLaterControlAndZoneChanges() throws Exception {
        try (var table = table()) {
            HeadlessPlatform.query(table, () -> {
                var owner = table.game.getRegisteredPlayers().get(0);
                var opponent = table.game.getRegisteredPlayers().get(1);
                var source = add(table, "Seeds of Strength", 0, ZoneType.Stack);
                var target = add(table, "Grizzly Bears", 1, ZoneType.Battlefield);
                var root = AbilityFactory.getAbility("SP$ Pump | Cost$ G W | NumAtt$ 1 | NumDef$ 1 | ValidTgts$ Creature,Player | TargetMin$ 1 | TargetMax$ 2", source);
                root.setActivatingPlayer(owner); root.setXManaCostPaid(7);
                root.getTargets().add(target); root.getTargets().addDividedAllocation(target, 2);
                root.getTargets().add(opponent); root.getTargets().addDividedAllocation(opponent, 1);
                var child = (forge.game.spellability.AbilitySub)AbilityFactory.getAbility("DB$ Pump | NumAtt$ 1 | NumDef$ 1 | ValidTgts$ Creature", source);
                root.setSubAbility(child); child.getTargets().add(target);
                table.game.getStack().add(root);
                var captured = announcements(table, 0); assertEquals(captured.size(), 1);
                var fact = captured.get(0).getAsJsonObject("announcement");
                assertEquals(fact.get("announcedX").getAsInt(), 7);
                assertEquals(text(fact, "targetKnowledge"), "Known");
                var targets = fact.getAsJsonArray("targets"); assertEquals(targets.size(), 3);
                assertEquals(targets.get(0).getAsJsonObject().get("allocatedAmount").getAsInt(), 2);
                assertEquals(text(targets.get(1).getAsJsonObject(), "kind"), "player");
                assertEquals(targets.get(1).getAsJsonObject().get("allocatedAmount").getAsInt(), 1);
                assertEquals(targets.get(0).getAsJsonObject().get("groupIndex").getAsInt(), 0);
                assertEquals(targets.get(2).getAsJsonObject().get("groupIndex").getAsInt(), 1);
                assertEquals(targets.get(0).getAsJsonObject().get("cardId"), targets.get(2).getAsJsonObject().get("cardId"),
                        "Separate targeted word-groups retain repeated physical recipients");
                assertEquals(announcements(table, 1), captured);
                target.setController(owner, table.game.getNextTimestamp());
                table.game.getAction().moveToGraveyard(target, null);
                assertEquals(announcements(table, 0), captured);
                var old = targets.get(0).getAsJsonObject();
                assertEquals(old.get("controllerId").getAsInt(), opponent.getId());
                assertEquals(text(old, "zone"), "Battlefield"); assertTrue(text(old, "type").contains("Creature"));
                // Creating another view and normal publication are not target commits.
                new SpellAbilityStackInstance(root); table.publish();
                assertEquals(announcements(table, 0), captured);
                return null;
            });
        }
    }

    @Test
    public void actualChangeTargetsResolutionAppendsAnAffectedExecutionFactWithoutRewritingTheOriginal() throws Exception {
        try (var table = table()) {
            HeadlessPlatform.query(table, () -> {
                var owner = table.game.getRegisteredPlayers().get(0);
                var oldTarget = add(table, "Grizzly Bears", 1, ZoneType.Battlefield);
                var newTarget = add(table, "Llanowar Elves", 0, ZoneType.Battlefield);
                var shock = add(table, "Shock", 0, ZoneType.Stack).getSpellAbilities().get(0);
                shock.setActivatingPlayer(owner); shock.getTargets().add(oldTarget);
                table.game.getStack().add(shock);
                var first = announcements(table, 0); assertEquals(first.size(), 1);
                var redirectCard = add(table, "Redirect", 0, ZoneType.Stack); redirectCard.addRemembered(newTarget);
                var redirect = AbilityFactory.getAbility("SP$ ChangeTargets | Cost$ U U | TargetType$ Spell | ValidTgts$ Card | DefinedMagnet$ Remembered", redirectCard);
                redirect.setActivatingPlayer(owner); redirect.getTargets().add(shock);
                table.game.getStack().add(redirect);
                String redirectExecution = redirect.getExecutionId();
                var beforeRedirect = announcements(table, 0); assertEquals(beforeRedirect.size(), 2);
                var stackTarget = beforeRedirect.get(1).getAsJsonObject("announcement").getAsJsonArray("targets").get(0).getAsJsonObject();
                assertEquals(text(stackTarget, "kind"), "stack");
                assertEquals(stackTarget.get("stackId"), first.get(0).getAsJsonObject("announcement").get("stackId"));
                try (var execution = table.game.getExecutions().enter(redirect)) { AbilityUtils.resolve(redirect); }
                var after = announcements(table, 0); assertEquals(after.size(), 3);
                assertEquals(after.get(0), first.get(0), "Retargeting cannot mutate the durable original announcement");
                var changedRecord = after.get(2); var changed = changedRecord.getAsJsonObject("announcement");
                assertEquals(text(changed, "kind"), "retargeted");
                assertEquals(text(changedRecord, "executionId"), text(first.get(0), "executionId"));
                assertEquals(text(changed, "causeExecutionId"), redirectExecution);
                assertEquals(text(changed.getAsJsonArray("targets").get(0).getAsJsonObject(), "name"), "Llanowar Elves");
                assertEquals(text(first.get(0).getAsJsonObject("announcement").getAsJsonArray("targets").get(0).getAsJsonObject(), "name"), "Grizzly Bears");
                assertEquals(announcements(table, 1), after);
                var current = StackAnnouncementReceipt.currentStack(table.game, shock);
                current.updateTarget(current.getTargetChoices().clone(), new HashSet<>());
                assertEquals(announcements(table, 0), after, "An unchanged re-selection/view refresh is not another retargeting");
                return null;
            });
        }
    }

    @Test
    public void hiddenFacesAreNotUpgradedByLaterRevealsAndKnownEmptyIsDistinct() throws Exception {
        try (var table = table()) {
            HeadlessPlatform.query(table, () -> {
                var owner = table.game.getRegisteredPlayers().get(0);
                var source = add(table, "Shock", 0, ZoneType.Stack);
                var hiddenHand = add(table, "Dark Ritual", 1, ZoneType.Hand);
                var hiddenLibrary = add(table, "Ancestral Recall", 1, ZoneType.Library);
                var concealed = add(table, "Invisible Stalker", 1, ZoneType.Battlefield); concealed.turnFaceDown(true);
                var guard = new SpellAbility.EmptySa(source, owner);
                guard.getTargets().add(hiddenHand); guard.getTargets().add(hiddenLibrary); guard.getTargets().add(concealed);
                table.game.getExecutions().prepare(guard);
                var receipt = StackAnnouncementReceipt.capture("announced", new SpellAbilityStackInstance(guard));
                assertNotNull(receipt);
                assertTrue(receipt.targets().stream().allMatch(target -> target.card().disclosedTo().isEmpty()));
                assertThrows(UnsupportedOperationException.class, () -> receipt.targets().clear());
                assertThrows(UnsupportedOperationException.class, () -> receipt.targets().get(0).card().disclosedTo().add(owner.getId()));
                // Deliberately delayed delivery is a privacy guard, not a legal
                // target-selection journey. Old disclosure cannot be upgraded.
                table.game.getAction().moveToGraveyard(hiddenHand, null);
                table.game.getAction().moveToGraveyard(hiddenLibrary, null);
                concealed.forceTurnFaceUp();
                table.game.fireEvent(receipt);
                var hidden = announcements(table, 0).get(0).getAsJsonObject("announcement");
                assertEquals(text(hidden, "targetKnowledge"), "Unknown"); assertTrue(hidden.getAsJsonArray("targets").isEmpty());
                String raw = JSON.toJson(hidden);
                for (String secret : List.of("Dark Ritual", "Ancestral Recall", "Invisible Stalker")) assertFalse(raw.contains(secret));
                assertFalse(raw.contains("identity")); assertFalse(raw.contains("disclosedTo"));
                var empty = new SpellAbility.EmptySa(source, owner); table.game.getExecutions().prepare(empty);
                table.game.fireEvent(StackAnnouncementReceipt.capture("announced", new SpellAbilityStackInstance(empty)));
                var known = announcements(table, 0).get(1).getAsJsonObject("announcement");
                assertEquals(text(known, "targetKnowledge"), "Known"); assertTrue(known.getAsJsonArray("targets").isEmpty());
                return null;
            });
        }
    }

    @Test
    public void negotiatedTcpHistoryPreservesLegacyOrdinalsAndExplicitlyDowngradesAnOlderHost() throws Exception {
        try (var table = table()) {
            HeadlessPlatform.query(table, () -> {
                var card = add(table, "Shock", 0, ZoneType.Stack); var owner = table.game.getRegisteredPlayers().get(0);
                var ability = new SpellAbility.EmptySa(card, owner); table.game.getExecutions().prepare(ability);
                table.game.fireEvent(StackAnnouncementReceipt.capture("announced", new SpellAbilityStackInstance(ability)));
                return null;
            });
            try (var server = new forge.api.sync.SyncTransport.Server((seat, method, args) -> table.seat(1).history(args),
                    () -> 1, ignored -> { }, java.util.Set.of("match-history", "history-announcements"))) {
                int port = server.listen();
                try (var remote = new SeatConnection(new forge.api.sync.SyncTransport.Remote("127.0.0.1:" + port))) {
                    var modern = AnnouncedTargetHistoryJourneyTest.history(remote, table.id, 2);
                    var legacy = AnnouncedTargetHistoryJourneyTest.history(remote, table.id, 1);
                    assertEquals(modern.stream().map(row -> row.get("sequence")).toList(), legacy.stream().map(row -> row.get("sequence")).toList());
                    assertTrue(modern.stream().anyMatch(row -> row.has("announcement") && !row.get("announcement").isJsonNull()));
                    assertTrue(legacy.stream().noneMatch(row -> row.has("announcement")));
                }
            }
            try (var server = new forge.api.sync.SyncTransport.Server((seat, method, args) -> {
                assertFalse(args.has("historySchemaVersion"));
                return table.seat(1).history(args);
            }, () -> 1, ignored -> { }, java.util.Set.of("match-history"));
                 var old = new SeatConnection(server.local(0))) {
                var query = new JsonObject(); query.addProperty("gameId", table.id); query.addProperty("historySchemaVersion", 2);
                assertEquals(((Number)old.history(query).get("schemaVersion")).intValue(), 1);
                assertEquals(query.get("historySchemaVersion").getAsInt(), 2, "Relay downgrade must not mutate the caller's request");
                query.addProperty("historySchemaVersion", 3);
                assertThrows(IllegalArgumentException.class, () -> old.history(query));
            }
        }
    }

    private static List<JsonObject> announcements(MatchTable table, int seat) {
        return AnnouncedTargetHistoryJourneyTest.history(table.seat(seat), table.id, 2).stream()
                .filter(row -> row.has("announcement") && !row.get("announcement").isJsonNull()).toList();
    }
    private static Card add(MatchTable table, String name, int seat, ZoneType zone) {
        var player = table.game.getRegisteredPlayers().get(seat);
        var card = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard(name), player);
        if (zone == ZoneType.Stack) table.game.getStackZone().add(card);
        else player.getZone(zone).add(card);
        card.setSickness(false); return card;
    }
    private static MatchTable table() throws Exception {
        var resources = Path.of("../forge-gui/res"); if (StaticData.instance() == null) EngineResources.load(resources);
        var table = new MatchTable("Constructed", List.of(MatchTable.human(new Deck(), "Constructed", "Owner"),
                MatchTable.human(new Deck(), "Constructed", "Other")), resources, Files.createTempDirectory("announced-target-history"));
        HeadlessPlatform.activate(table); return table;
    }
}
