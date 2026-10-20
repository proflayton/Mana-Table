package forge.card;

import forge.CardStorageReader;
import forge.StaticData;
import forge.gamesimulationtests.util.CardDatabaseHelper;
import forge.gui.GuiBase;
import forge.gui.interfaces.IGuiBase;
import forge.item.PaperCard;
import forge.localinstance.properties.ForgeConstants;
import forge.model.FModel;
import forge.util.Lang;
import forge.util.Localizer;
import org.mockito.MockedStatic;
import org.mockito.Mockito;
import org.testng.annotations.Test;
import forge.ImageKeys;

import java.lang.reflect.Field;
import java.util.HashMap;
import java.util.Map;

import static org.testng.Assert.assertEquals;
import static org.testng.Assert.assertFalse;
import static org.testng.Assert.assertNotNull;
import static org.testng.Assert.assertNotSame;
import static org.testng.Assert.assertNull;
import static org.testng.Assert.assertSame;
import static org.testng.Assert.assertTrue;

/** Exercises the real fixture lifecycle without inheriting its automatic mocks. */
public class CardMockIsolationTest {
    private static StaticData unfilteredGameData;

    @Test
    public void aCachedGameImageAnswerDoesNotEnterTheMockFixture() throws Exception {
        try (OuterState ignored = new OuterState()) {
            StaticData ordinary;
            // The fixture supplies the same GUI/Lang/Localizer prerequisites as
            // the existing tests while the process-wide helper is first built.
            try (Fixture bootstrap = new Fixture()) {
                bootstrap.open();
                ordinary = CardDatabaseHelper.getStaticDataToPopulateOtherMocks();
            }
            setSingleton(ordinary);
            PaperCard ordinaryCard = ordinary.getCommonCards().getAllCardsNoAlt("Shivan Dragon").stream()
                    .filter(card -> "LEA".equals(card.getEdition()) && card.getArtIndex() == 1)
                    .findFirst().orElse(null);
            assertNotNull(ordinaryCard);
            Field image = field(PaperCard.class, "hasImage");
            Object previousImage = image.get(ordinaryCard);
            try {
                // This scope ends before the fixture opens its own static mock.
                try (MockedStatic<ImageKeys> images = Mockito.mockStatic(ImageKeys.class)) {
                    images.when(() -> ImageKeys.hasImage(Mockito.any(PaperCard.class), Mockito.anyBoolean()))
                            .thenReturn(false);
                    assertFalse(ordinaryCard.hasImage(true));
                }
                try (Fixture fixture = new Fixture()) {
                    fixture.open();
                    PaperCard mockCard = fixture.cardDb.getCard("Shivan Dragon", "LEA", 1);
                    assertNotNull(mockCard);
                    assertNotSame(mockCard, ordinaryCard,
                            "A game card's cached image result must not determine an image-mocked test");
                    assertTrue(mockCard.hasImage(), "The fixture's true-image policy applies to its own card");
                    assertFalse(ordinaryCard.hasImage(), "The ordinary card retains its separate cached answer");
                }
                assertSame(StaticData.instance(), ordinary);
            } finally {
                image.set(ordinaryCard, previousImage);
            }
        }
    }

    @Test
    public void theNextMethodStartsWithDefaultArtEvenAfterAnAbortedMutation() throws Exception {
        try (OuterState ignored = new OuterState(); Fixture fixture = new Fixture()) {
            fixture.open();
            CardDb db = fixture.cardDb;
            CardDb.CardArtPreference previous = db.getCardArtPreference();
            try {
                db.setCardArtPreference(false, true);
                assertEquals(db.getCardArtPreference(), CardDb.CardArtPreference.ORIGINAL_ART_CORE_EXPANSIONS_REPRINT_ONLY);
                // Do not execute the mutation test's successful-tail reset. This
                // models an assertion throwing before that reset is reached.
                fixture.close();
                fixture.open();
                assertEquals(fixture.cardDb.getCardArtPreference(), CardDb.CardArtPreference.LATEST_ART_ALL_EDITIONS,
                        "Each method establishes its own default, including after a failed predecessor");
            } finally {
                db.setCardArtPreference(previous.name());
            }
        }
    }

    @Test
    public void preferredArtEntriesAreRestoredAfterFixtureCleanup() throws Exception {
        try (OuterState ignored = new OuterState(); Fixture fixture = new Fixture()) {
            artPreferences().put("Caller sentinel", "Mountain|3ED|1");
            Map<String, String> before = new HashMap<>(artPreferences());
            fixture.open();
            assertFalse(artPreferences().containsKey("Caller sentinel"),
                    "The caller's art override must not affect fixture card selection");
            artPreferences().clear();
            artPreferences().put("Isolation sentinel", "Island|MIR|3");
            assertEquals(artPreferences().size(), 1);
            fixture.close();
            assertEquals(artPreferences(), before,
                    "Cleanup restores removed entries as well as discarding newly preferred art");
            fixture.close();
            assertEquals(artPreferences(), before, "Repeated cleanup is idempotent");
        }
    }

    @Test
    public void nestedFixtureRefreshRestoresTheExactGameDatabase() throws Exception {
        try (OuterState ignored = new OuterState()) {
            StaticData gameData = unfilteredGameData();
            setSingleton(gameData);
            PaperCard playtestCard = gameData.getCommonCards().getCard("Manakin and Millikin");
            assertNotNull(playtestCard, "The game fixture explicitly includes the nonlegal card used by the AI test");
            try (Fixture fixture = new Fixture()) {
                fixture.open();
                assertNotSame(StaticData.instance(), gameData);
                assertSame(StaticData.instance(), fixture.data);
                assertNull(fixture.cardDb.getCard("Manakin and Millikin"),
                        "The card-test database deliberately filters this Funny-set card");
                // BaseGameSimulationTest refreshes the same fixture part-way
                // through a method. This must close, restore and reopen safely.
                fixture.open();
                assertSame(StaticData.instance(), fixture.data);
            }
            assertSame(StaticData.instance(), gameData, "Cleanup restores the caller's exact database instance");
            assertSame(FModel.getMagicDb(), gameData, "Real FModel lookup sees the game database after mocks close");
            assertSame(FModel.getMagicDb().getCommonCards().getCard("Manakin and Millikin"), playtestCard,
                    "The next real game retains the same available playtest card");
        }
    }

    @Test
    public void aPreviouslyAbsentSingletonIsRestoredAsAbsent() throws Exception {
        try (OuterState ignored = new OuterState()) {
            setSingleton(null);
            try (Fixture fixture = new Fixture()) {
                fixture.open();
                assertNotNull(StaticData.instance());
            }
            assertNull(StaticData.instance(), "An originally null singleton must not become a leaked fixture database");
        }
    }

    private static final class Fixture extends CardDbCardMockTestCase implements AutoCloseable {
        private StaticData data;

        private void open() throws Exception {
            initMocks();
            setup();
            data = FModel.getMagicDb();
        }

        @Override
        public void close() {
            releaseMocks();
        }
    }

    private static StaticData unfilteredGameData() throws Exception {
        if (unfilteredGameData == null) {
            try (Fixture bootstrap = new Fixture()) {
                bootstrap.open();
                unfilteredGameData = new StaticData(
                        new CardStorageReader(ForgeConstants.CARD_DATA_DIR, null, false),
                        new CardStorageReader(ForgeConstants.TOKEN_DATA_DIR, null, false), null, null,
                        ForgeConstants.EDITIONS_DIR, ForgeConstants.USER_CUSTOM_EDITIONS_DIR,
                        ForgeConstants.BLOCK_DATA_DIR, "", "Latest Art All Editions",
                        true, true, false, false);
            }
        }
        return unfilteredGameData;
    }

    /** Keeps even an intentionally failing old-baseline run from poisoning its successor. */
    private static final class OuterState implements AutoCloseable {
        private final StaticData data = StaticData.instance();
        private final Object localizer = get(field(Localizer.class, "instance"));
        private final IGuiBase gui = GuiBase.getInterface();
        private final Object language = get(field(Lang.class, "instance"));
        private final Object english = get(field(Lang.class, "englishInstance"));
        private final Map<String, String> art = new HashMap<>(artPreferences());

        @Override
        public void close() {
            setSingleton(data);
            artPreferences().clear();
            artPreferences().putAll(art);
            set(field(Localizer.class, "instance"), localizer);
            GuiBase.setInterface(gui);
            set(field(Lang.class, "instance"), language);
            set(field(Lang.class, "englishInstance"), english);
        }
    }

    private static void setSingleton(StaticData data) {
        set(field(StaticData.class, "lastInstance"), data);
    }

    @SuppressWarnings("unchecked")
    private static Map<String, String> artPreferences() {
        return (Map<String, String>) get(field(CardDb.class, "artPrefs"));
    }

    private static Field field(Class<?> owner, String name) {
        try {
            Field field = owner.getDeclaredField(name);
            field.setAccessible(true);
            return field;
        } catch (ReflectiveOperationException error) {
            throw new IllegalStateException(error);
        }
    }

    private static Object get(Field field) {
        try {
            return field.get(null);
        } catch (IllegalAccessException error) {
            throw new IllegalStateException(error);
        }
    }

    private static void set(Field field, Object value) {
        try {
            field.set(null, value);
        } catch (IllegalAccessException error) {
            throw new IllegalStateException(error);
        }
    }
}
