package forge.api;

import com.google.common.collect.HashMultiset;
import com.google.gson.GsonBuilder;
import com.google.gson.JsonObject;
import forge.StaticData;
import forge.card.CardStateName;
import forge.card.CardType;
import forge.deck.Deck;
import forge.game.GameStage;
import forge.game.card.Card;
import forge.game.card.CardView;
import forge.game.card.CounterEnumType;
import forge.game.card.CounterType;
import forge.game.event.*;
import forge.game.keyword.KeywordCollection;
import forge.game.player.RegisteredPlayer;
import forge.game.zone.ZoneType;
import forge.trackable.TrackableProperty;
import forge.trackable.TrackableCollection;
import org.testng.annotations.BeforeClass;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.*;

import static forge.api.RulesScenario.*;
import static org.testng.Assert.*;

/** The public seat contract at real publication boundaries and authoritative event emission. */
public class GameplayClarityProjectionTest {
    private static final Path RESOURCES = Path.of("../forge-gui/res");
    private static final com.google.gson.Gson JSON = new GsonBuilder().serializeNulls().create();

    @BeforeClass public void resources() { if (StaticData.instance() == null) EngineResources.load(RESOURCES); }

    private MatchTable table(String name) throws Exception {
        var players = new ArrayList<RegisteredPlayer>();
        for (int i = 0; i < 4; i++) players.add(MatchTable.human(new Deck(), "Constructed", "Seat " + i));
        var table = new MatchTable("Constructed", players, RESOURCES, Files.createTempDirectory(name));
        HeadlessPlatform.activate(table);
        return table;
    }

    @Test public void publicCountersAndAnonymousCurrentCharacteristicsReachEverySeatWithoutPrintedIdentity() throws Exception {
        try (var table = table("mana-public-characteristics")) {
            var owner = table.game.getPlayers().get(0).getView();
            var card = new CardView(10001, null);
            card.set(TrackableProperty.Controller, owner); card.set(TrackableProperty.Owner, owner);
            card.set(TrackableProperty.Zone, ZoneType.Battlefield); card.set(TrackableProperty.Facedown, true);
            card.set(TrackableProperty.DoubleFaced, true); card.set(TrackableProperty.Damage, 1);
            var face = card.new CardStateView(10001, CardStateName.FaceDown, null);
            face.set(TrackableProperty.Name, "Concealed front identity");
            face.set(TrackableProperty.OracleText, "Concealed rules text");
            face.set(TrackableProperty.Type, CardType.parse("Artifact Creature", false));
            face.set(TrackableProperty.Power, 4); face.set(TrackableProperty.Toughness, 5);
            var keywords = new KeywordCollection(); keywords.add("Haste"); keywords.add("Hexproof");
            face.set(TrackableProperty.Keywords, keywords.getView()); card.set(TrackableProperty.CurrentState, face);
            var alternate = card.new CardStateView(10001, CardStateName.Backside, null);
            alternate.set(TrackableProperty.Name, "Concealed alternate identity"); card.set(TrackableProperty.AlternateState, alternate);
            owner.set(TrackableProperty.Battlefield, new TrackableCollection<>(card));
            var counts = HashMultiset.<CounterType>create();
            counts.add(CounterEnumType.POISON, 3); counts.add(CounterEnumType.ENERGY, 2); counts.add(CounterEnumType.EXPERIENCE, 1);
            for (var player : table.game.getRegisteredPlayers()) player.getView().set(TrackableProperty.Counters, HashMultiset.create(counts));
            table.publish();
            for (var view : table.testViews()) {
                var state = JSON.toJsonTree(view).getAsJsonObject();
                for (var value : state.getAsJsonArray("players")) {
                    var projected = value.getAsJsonObject().getAsJsonObject("counters");
                    assertEquals(projected.get(CounterEnumType.POISON.getName()).getAsInt(), 3);
                    assertEquals(projected.get(CounterEnumType.ENERGY.getName()).getAsInt(), 2);
                    assertEquals(projected.get(CounterEnumType.EXPERIENCE.getName()).getAsInt(), 1);
                }
                var current = zone(player(state, owner.getId()), "Battlefield").getAsJsonArray("cards").get(0).getAsJsonObject();
                assertEquals(text(current, "name"), "Face-down card");
                assertEquals(text(current, "type"), "Artifact Creature");
                assertEquals(current.get("power").getAsInt(), 4); assertEquals(current.get("toughness").getAsInt(), 5);
                assertEquals(current.get("damage").getAsInt(), 1);
                assertTrue(current.getAsJsonArray("combatKeywords").asList().stream().anyMatch(k -> k.getAsString().equals("haste")));
                assertTrue(current.getAsJsonArray("combatKeywords").asList().stream().anyMatch(k -> k.getAsString().equals("hexproof")));
                assertEquals(text(current, "text"), ""); assertEquals(text(current, "manaCost"), "");
                assertFalse(current.has("otherFace")); assertFalse(current.has("artName"));
                assertFalse(current.toString().contains("Concealed"));
                assertFalse(current.get("visualId").isJsonNull()); assertEquals(current.get("visualId").getAsString(), "");
                assertFalse(text(current, "combatId").isEmpty());
            }
            var saved = table.seat(1).state();
            for (var player : table.game.getRegisteredPlayers()) player.getView().set(TrackableProperty.Counters, HashMultiset.<CounterType>create());
            owner.set(TrackableProperty.Battlefield, new TrackableCollection<CardView>());
            card.set(TrackableProperty.Zone, ZoneType.Hand); owner.set(TrackableProperty.Hand, new TrackableCollection<>(card));
            table.publish();
            var own = JSON.toJsonTree(table.seat(0).state()).getAsJsonObject();
            var ownFace = zone(player(own, owner.getId()), "Hand").getAsJsonArray("cards").get(0).getAsJsonObject();
            assertEquals(text(ownFace, "type"), ""); assertTrue(ownFace.get("power").isJsonNull());
            for (int seat = 1; seat < 4; seat++) {
                var state = JSON.toJsonTree(table.seat(seat).state()).getAsJsonObject();
                assertTrue(zone(player(state, owner.getId()), "Hand").getAsJsonArray("cards").isEmpty());
                assertTrue(player(state, owner.getId()).getAsJsonObject("counters").isEmpty());
            }
            var historical = JSON.toJsonTree(saved).getAsJsonObject();
            assertEquals(player(historical, owner.getId()).getAsJsonObject("counters").get(CounterEnumType.POISON.getName()).getAsInt(), 3);
        }
    }

    @Test public void realDrawAndShuffleEventsAreDistinctFromOpeningHandsTutorsAndRepeatedReads() throws Exception {
        try (var table = table("mana-public-draw-events")) {
            var player = table.game.getPlayers().get(0);
            var activities = table.game.getRegisteredPlayers().stream().map(p -> new MatchActivity(p.getView())).toList();
            for (var activity : activities) table.game.subscribeToEvents(activity);
            for (int i = 0; i < 12; i++) player.getZone(ZoneType.Library).add(Card.fromPaperCard(StaticData.instance().getCommonCards().getCard("Forest"), player), null, null);
            table.game.setAge(GameStage.Mulligan); player.drawCards(7);
            assertTrue(activities.stream().allMatch(a -> a.snapshot().stream().noneMatch(e -> e.kind().equals("draw"))));
            table.game.setAge(GameStage.Play);
            table.game.fireEvent(new GameEventTurnBegan(player.getView(), 1));
            table.game.getAction().moveToHand(player.getZone(ZoneType.Library).get(0), null);
            assertTrue(activities.stream().allMatch(a -> a.snapshot().stream().noneMatch(e -> e.kind().equals("draw"))), "A tutor is not a rules draw");
            player.drawCards(2); player.shuffle(null);
            for (int seat = 0; seat < 4; seat++) {
                var activity = activities.get(seat);
                var entries = activity.snapshot();
                assertEquals(entries.stream().filter(e -> e.kind().equals("draw")).mapToInt(MatchActivity.Entry::count).sum(), 2);
                assertEquals(entries.stream().filter(e -> e.kind().equals("shuffle")).count(), 1L);
                assertTrue(entries.stream().filter(e -> e.kind().equals("draw") || e.kind().equals("shuffle")).allMatch(e -> e.playerId() == player.getId() && e.cardId() == null && e.cardName() == null));
                assertEquals(activity.observations().get("cardsDrawn"), Integer.valueOf(seat == 0 ? 2 : 0));
                assertEquals(activity.frame().entries(), entries); assertEquals(activity.snapshot(), entries);
                assertEquals(entries.stream().map(MatchActivity.Entry::id).distinct().count(), (long) entries.size());
                assertFalse(entries.toString().contains("Forest"));
            }
        }
    }

    @Test public void nonResolutionOutcomeIsExplicitWithoutGuessingItsCauseOrLeakingHiddenFaces() throws Exception {
        try (var table = table("mana-resolution-outcome")) {
            var player = table.game.getPlayers().get(0);
            var card = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard("Shock"), player);
            table.game.getStackZone().add(card, null, null);
            var spell = card.getSpellAbilities().get(0); spell.setActivatingPlayer(player);
            var activity = new MatchActivity(table.game.getPlayers().get(1).getView());
            activity.onGameEvent(new GameEventSpellResolved(spell, true));
            var failed = activity.snapshot().get(0);
            assertEquals(failed.kind(), "resolved"); assertEquals(failed.outcome(), "notResolved");
            assertTrue(failed.message().contains("did not resolve"));
            assertFalse(failed.message().toLowerCase(Locale.ROOT).contains("countered"));
            activity.onGameEvent(new GameEventSpellResolved(spell, false));
            assertEquals(activity.snapshot().get(1).outcome(), "resolved");
            card.getView().set(TrackableProperty.Facedown, true);
            activity.onGameEvent(new GameEventSpellResolved(spell, true));
            var hidden = activity.snapshot().get(2);
            assertEquals(hidden.outcome(), "notResolved"); assertEquals(hidden.detail(), "");
            assertNull(hidden.cardName()); assertNull(hidden.cardId()); assertFalse(hidden.message().contains("Shock"));
        }
    }

    @Test(timeOut = 90000) public void publicDecisionOwnerDiffersFromActiveTurnAndNeverExportsAnotherSeatsChoices() throws Exception {
        try (var game = new RulesScenario("public-decision-ownership", Map.of())) {
            var input = game.until(i -> i.seat() == 1 && text(i.state(), "phaseKey").equals("MAIN1") && text(i.prompt(), "inputType").equals("InputPassPriority"));
            int active = game.state(0).get("viewerId").getAsInt(), owner = input.state().get("viewerId").getAsInt();
            assertNotEquals(active, owner);
            for (var raw : game.table.testViews()) {
                var state = JSON.toJsonTree(raw).getAsJsonObject();
                assertEquals(state.get("activePlayerId").getAsInt(), active);
                assertEquals(state.get("priorityPlayerId").getAsInt(), owner);
                assertEquals(state.get("decisionOwnerId").getAsInt(), owner);
                assertEquals(text(state, "actionClass"), "priority");
                if (state.get("viewerId").getAsInt() != owner) assertTrue(state.get("prompt").isJsonNull());
            }
            var observed = game.table.seat(2).state();
            assertEquals(game.table.seat(2).state(), observed, "Polling does not manufacture another public decision");
        }
    }

    @Test(timeOut = 90000) public void resolvingSpellPublishesTheForeignPrivateChooserWithoutSharingTheirHand() throws Exception {
        try (var game = new RulesScenario("foreign-private-decision", Map.of(0, List.of("Swamp", "Swamp", "Swamp", "Swamp")), "Syphon Mind", "Rhys the Redeemed")) {
            var main = game.until(i -> i.seat() == 0 && text(i.state(), "phaseKey").equals("MAIN1"));
            var card = zone(viewer(main.state()), "Hand").getAsJsonArray("cards").get(0).getAsJsonObject();
            assertTrue(game.answer(main, Map.of("action", "card", "key", text(card, "key"))));
            var payment = game.until(i -> !i.state().getAsJsonArray("stack").isEmpty()
                    || i.seat() == 0 && text(i.prompt(), "inputType").startsWith("InputPayMana"));
            if (payment.state().getAsJsonArray("stack").isEmpty()) {
                assertTrue(game.answer(payment, Map.of("action", "ok")));
                long deadline = System.nanoTime() + java.time.Duration.ofSeconds(5).toNanos();
                JsonObject latest;
                do {
                    latest = game.state(0);
                    var prompt = latest.get("prompt");
                    if (!latest.getAsJsonArray("stack").isEmpty()
                            || prompt != null && !prompt.isJsonNull() && !text(prompt.getAsJsonObject(), "inputType").startsWith("InputPayMana")) break;
                    Thread.sleep(5);
                } while (System.nanoTime() < deadline);
                assertFalse(latest.getAsJsonArray("stack").isEmpty(), "One accepted Auto must publish the spell: " + latest);
            }
            var choice = game.until(i -> i.seat() == 1 && text(i.prompt(), "inputType").startsWith("InputSelect"));
            int chooser = choice.state().get("viewerId").getAsInt();
            int caster = game.state(0).get("viewerId").getAsInt();
            assertNotEquals(chooser, caster);
            assertFalse(zone(viewer(choice.state()), "Hand").getAsJsonArray("cards").isEmpty());
            for (var view : game.table.testViews()) {
                var state = JSON.toJsonTree(view).getAsJsonObject();
                assertEquals(state.get("activePlayerId").getAsInt(), caster);
                assertEquals(state.get("decisionOwnerId").getAsInt(), chooser);
                assertEquals(text(state, "actionClass"), "selection");
                if (state.get("viewerId").getAsInt() != chooser) {
                    assertTrue(state.get("prompt").isJsonNull(), "Only the actual chooser receives private decision contents");
                    assertTrue(zone(player(state, chooser), "Hand").getAsJsonArray("cards").isEmpty());
                }
            }
        }
    }
}
