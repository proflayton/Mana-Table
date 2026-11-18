package forge.api;

import forge.StaticData;
import forge.deck.Deck;
import forge.game.card.Card;
import forge.game.card.CardCollection;
import forge.game.player.LibraryAcquisitionOffer;
import forge.game.player.Player;
import forge.game.spellability.SpellAbility;
import forge.game.zone.ZoneType;
import forge.player.PlayerControllerHuman;
import org.testng.annotations.Test;

import java.nio.file.Path;
import java.util.*;

import static org.testng.Assert.*;

public class LibraryAcquisitionOfferTest {
    @Test(timeOut = 90000)
    public void realDirectAndBundleScriptsRetainOnlyTheirCurrentNominalDestinationFacts() throws Exception {
        try (var fixture = new Fixture("library-offer-real")) {
            var farseek = fixture.ability("Farseek");
            var direct = fixture.offer(farseek, ZoneType.Battlefield, 1);
            assertNotNull(direct, "Farseek needs a direct acquisition offer: " + farseek.getMapParams());
            assertEquals(direct.stage(), "direct"); assertEquals(direct.currentDestination(), "Battlefield");
            assertEquals(direct.plannedDestinations(), List.of(new LibraryAcquisitionOffer.Destination("Battlefield", 1)));
            for (String name : List.of("Cultivate", "Kodama's Reach")) {
                var root = fixture.ability(name);
                var bundle = fixture.offer(root, ZoneType.Library, 2);
                assertNotNull(bundle, name + " needs the exact remembered bundle shape: " + parameters(root));
                assertEquals(bundle.stage(), "select-bundle");
                assertEquals(bundle.plannedDestinations(), List.of(new LibraryAcquisitionOffer.Destination("Battlefield", 1), new LibraryAcquisitionOffer.Destination("Hand", 1)));
                root.getHostCard().addRemembered(fixture.cards);
                var field = fixture.offer(root.getSubAbility(), ZoneType.Battlefield, 1);
                assertNotNull(field); assertEquals(field.stage(), "distribute-bundle");
                var moved = fixture.cards.get(0); fixture.actor().getZone(ZoneType.Library).remove(moved); fixture.actor().getZone(ZoneType.Battlefield).add(moved);
                var hand = LibraryAcquisitionOffer.create(fixture.actor(), fixture.actor(), ZoneType.Hand, List.of(ZoneType.Library),
                        root.getSubAbility().getSubAbility(), new CardCollection(List.of(fixture.cards.get(1))), 1);
                assertNotNull(hand); assertEquals(hand.currentDestination(), "Hand");
                fixture.actor().getZone(ZoneType.Battlefield).remove(moved); fixture.actor().getZone(ZoneType.Library).add(moved);
                root.getHostCard().clearRemembered();
                assertTrue(bundle.matches(fixture.actor(), fixture.cards.stream().map(Card::getView).toList()));
                assertFalse(bundle.matches(fixture.opponent(), fixture.cards.stream().map(Card::getView).toList()));
            }
        }
    }

    @Test(timeOut = 90000)
    public void unsupportedCostsDestinationsActorsChainsAndChangedChoicesPublishNoAcquisition() throws Exception {
        try (var fixture = new Fixture("library-offer-negative")) {
            var original = fixture.ability("Cultivate");
            for (String key : List.of("Condition", "UnlessCost", "Chooser", "DefinedPlayer", "LibraryPosition", "DestinationAlternative", "UnknownFutureMechanic")) {
                var ability = fixture.ability("Cultivate"); ability.getMapParams().put(key, "Fixture");
                assertNull(fixture.offer(ability, ZoneType.Library, 2), key + " cannot acquire context");
            }
            for (String destination : List.of("Exile", "Graveyard", "Library")) {
                var ability = fixture.ability("Farseek"); ability.getMapParams().put("Destination", destination);
                assertNull(fixture.offer(ability, ZoneType.valueOf(destination), 1));
            }
            var repeat = fixture.ability("Cultivate"); repeat.getMapParams().put("ChangeNum", "X");
            assertNull(fixture.offer(repeat, ZoneType.Library, 2));
            var coupled = fixture.ability("Cultivate"); coupled.getSubAbility().getMapParams().put("UnlessCost", "1");
            assertNull(fixture.offer(coupled, ZoneType.Library, 2));
            var childCost = fixture.ability("Cultivate"); childCost.getSubAbility().setPayCosts(new forge.game.cost.Cost("1", false));
            assertNull(fixture.offer(childCost, ZoneType.Library, 2));
            var altered = fixture.ability("Cultivate"); altered.getSubAbility().getSubAbility().getMapParams().put("Destination", "Exile");
            assertNull(fixture.offer(altered, ZoneType.Library, 2));
            var incomplete = fixture.ability("Cultivate"); incomplete.getSubAbility().setSubAbility(null);
            assertNull(fixture.offer(incomplete, ZoneType.Library, 2));
            var cyclic = fixture.ability("Cultivate"); cyclic.getSubAbility().setParent(cyclic.getSubAbility());
            assertNull(fixture.offer(cyclic.getSubAbility(), ZoneType.Battlefield, 1), "A cyclic parent walk cannot exceed the bounded recognizer");
            assertNull(LibraryAcquisitionOffer.create(fixture.actor(), fixture.opponent(), ZoneType.Library, List.of(ZoneType.Library), original, fixture.cards, 2));
            assertNull(LibraryAcquisitionOffer.create(fixture.opponent(), fixture.opponent(), ZoneType.Library, List.of(ZoneType.Library), original, fixture.cards, 2));
            assertNull(LibraryAcquisitionOffer.create(fixture.actor(), fixture.actor(), ZoneType.Library, List.of(ZoneType.Graveyard), original, fixture.cards, 2));
            assertNull(fixture.offer(original, ZoneType.Library, 3));
            var foreign = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard("Plains"), fixture.opponent());
            fixture.opponent().getZone(ZoneType.Library).add(foreign);
            assertNull(LibraryAcquisitionOffer.create(fixture.actor(), fixture.actor(), ZoneType.Library, List.of(ZoneType.Library), original,
                    new CardCollection(List.of(fixture.cards.get(0), foreign)), 2), "Actual library zone ownership defeats misleading card/control metadata");
            var valid = fixture.offer(original, ZoneType.Library, 2); assertNotNull(valid);
            original.getHostCard().addRemembered(fixture.cards.get(0));
            assertNull(fixture.offer(original, ZoneType.Library, 2), "A new bundle cannot borrow preexisting remembered occurrences");
            assertFalse(valid.matches(fixture.actor(), fixture.cards.stream().map(Card::getView).toList()));
            original.getHostCard().clearRemembered();
            assertFalse(valid.matches(fixture.actor(), List.of(fixture.cards.get(0).getView())));
            original.getMapParams().put("Shuffle", "True");
            assertFalse(valid.matches(fixture.actor(), fixture.cards.stream().map(Card::getView).toList()), "Changed mechanics retire the old contextual witness");
            var fresh = fixture.offer(fixture.ability("Farseek"), ZoneType.Battlefield, 1); assertNotNull(fresh);
            var moved = fixture.cards.get(0); fixture.actor().getZone(ZoneType.Library).remove(moved); fixture.actor().getZone(ZoneType.Hand).add(moved);
            assertFalse(fresh.matches(fixture.actor(), fixture.cards.stream().map(Card::getView).toList()), "A moved option cannot retain its earlier library context");
            assertNull(fixture.offer(fixture.ability("Farseek"), ZoneType.Battlefield, 1));
        }
    }

    @Test(timeOut = 90000)
    public void nestedCallbacksAndExceptionsRestoreOnlyTheExactLiveScopedOffer() throws Exception {
        try (var fixture = new Fixture("library-offer-scope")) {
            var controller = (PlayerControllerHuman)fixture.actor().getController();
            var originalGui = controller.getGui();
            var ability = fixture.ability("Farseek");
            var entries = new java.util.concurrent.atomic.AtomicInteger();
            var stale = new java.util.concurrent.atomic.AtomicReference<LibraryAcquisitionOffer>();
            var failure = new IllegalStateException("Controlled nested acquisition callback failure");
            controller.setGui((forge.gui.interfaces.IGuiGame)java.lang.reflect.Proxy.newProxyInstance(
                    forge.gui.interfaces.IGuiGame.class.getClassLoader(), new Class<?>[] { forge.gui.interfaces.IGuiGame.class }, (proxy, method, arguments) -> {
                        if (method.getName().equals("chooseSingleEntityForEffect")) {
                            int entry = entries.incrementAndGet();
                            if (entry == 2) { assertNull(controller.getLibraryAcquisitionOffer(), "An unsupported nested callback cannot inherit its parent's provenance"); return null; }
                            assertEquals(entry, 1);
                            var outer = controller.getLibraryAcquisitionOffer(); assertNotNull(outer); stale.set(outer);
                            assertTrue(outer.matches(fixture.actor(), fixture.cards.stream().map(Card::getView).toList()));
                            assertNull(controller.chooseSingleCardForZoneChange(ZoneType.Exile, List.of(ZoneType.Library), ability,
                                    fixture.cards, null, "Unrelated nested text", true, fixture.actor()));
                            assertSame(controller.getLibraryAcquisitionOffer(), outer, "Completing the nested callback restores its live outer lease");
                            throw failure;
                        }
                        try { return method.invoke(originalGui, arguments); }
                        catch (java.lang.reflect.InvocationTargetException error) { throw error.getCause(); }
                    }));
            try {
                assertSame(expectThrows(IllegalStateException.class, () -> controller.chooseSingleCardForZoneChange(
                        ZoneType.Battlefield, List.of(ZoneType.Library), ability, fixture.cards, null, "Arbitrary prompt", true, fixture.actor())), failure);
            } finally { controller.setGui(originalGui); }
            assertEquals(entries.get(), 2); assertNull(controller.getLibraryAcquisitionOffer());
            assertNotNull(stale.get(), "The test retained the old read-only value; it cannot reinstall the controller's retired lease");
        }
    }

    private static List<Map<String, String>> parameters(SpellAbility root) {
        var result = new ArrayList<Map<String, String>>();
        for (var node = root; node != null && result.size() < 8; node = node.getSubAbility()) result.add(node.getMapParams());
        return result;
    }

    private static final class Fixture implements AutoCloseable {
        final MatchTable table; final CardCollection cards = new CardCollection();
        Fixture(String name) throws Exception {
            var resources = Path.of("../forge-gui/res"); if (StaticData.instance() == null) EngineResources.load(resources);
            table = new MatchTable("Constructed", List.of(MatchTable.automated(new Deck(), "Constructed", "Actor"),
                    MatchTable.automated(new Deck(), "Constructed", "Opponent")), resources, ApiTestWorkspace.profile(name), true);
            HeadlessPlatform.activate(table);
            for (String land : List.of("Forest", "Plains")) {
                var card = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard(land), actor());
                actor().getZone(ZoneType.Library).add(card); cards.add(card);
            }
        }
        Player actor() { return table.game.getRegisteredPlayers().get(0); }
        Player opponent() { return table.game.getRegisteredPlayers().get(1); }
        SpellAbility ability(String name) {
            var card = Card.fromPaperCard(StaticData.instance().getCommonCards().getCard(name), actor());
            actor().getZone(ZoneType.Hand).add(card);
            var ability = card.getSpellAbilities().get(0); ability.setActivatingPlayer(actor()); return ability;
        }
        LibraryAcquisitionOffer offer(SpellAbility ability, ZoneType destination, int maximum) {
            return LibraryAcquisitionOffer.create(actor(), actor(), destination, List.of(ZoneType.Library), ability, cards, maximum);
        }
        public void close() { table.close(); }
    }
}
