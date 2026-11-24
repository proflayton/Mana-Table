package forge.gamemodes.net;

import forge.game.card.CardView;
import forge.game.card.CounterEnumType;
import forge.game.event.GameEvent;
import forge.game.event.GameEventCardChangeZone;
import forge.game.event.GameEventCardCounters;
import forge.game.event.GameEventCardDamaged;
import forge.game.event.GameEventCause;
import forge.game.event.GameEventPlayerDamaged;
import forge.game.player.PlayerView;
import forge.game.zone.ZoneType;
import forge.game.zone.ZoneView;
import forge.trackable.TrackableProperty;
import forge.trackable.TrackableTypes;
import forge.trackable.Tracker;
import io.netty.handler.codec.serialization.ClassResolvers;
import org.testng.Assert;
import org.testng.annotations.Test;

import java.io.ByteArrayInputStream;
import java.io.ByteArrayOutputStream;
import java.io.InvalidClassException;
import java.io.ObjectOutputStream;
import java.io.Serializable;
import java.lang.reflect.InvocationHandler;
import java.lang.reflect.Method;
import java.lang.reflect.Proxy;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;

/**
 * F-01, at the stream rather than at the predicate.
 *
 * <p>{@link WireClassFilterTest} pins which names the allowlist accepts. This
 * pins that {@link CObjectInputStream} actually consults it, on every route a
 * class can enter the stream — including the one that does not carry a class
 * name at all.
 */
public class WireDeserializationTest {

    /**
     * Stand-in for a gadget. Deliberately not a nested class of this test: that
     * would be named {@code forge.…} and allowlisted by the very prefix under
     * test.
     */
    private static Serializable disallowedObject() {
        return new java.io.File("not-on-the-wire");
    }

    /** Serializable handler so the proxy below can itself be serialized. */
    private static final class Handler implements InvocationHandler, Serializable {
        private static final long serialVersionUID = 1L;

        @Override
        public Object invoke(final Object proxy, final Method method, final Object[] args) {
            return null;
        }
    }

    private static byte[] encode(final Object graph) throws Exception {
        final ByteArrayOutputStream bytes = new ByteArrayOutputStream();
        // The real encoder, so the framing matches what a peer would send.
        try (ObjectOutputStream out = new CObjectOutputStream(bytes, false, null, -1, false)) {
            out.writeObject(graph);
        }
        return bytes.toByteArray();
    }

    private static Object decode(final byte[] frame) throws Exception {
        try (CObjectInputStream in = new CObjectInputStream(
                new ByteArrayInputStream(frame), ClassResolvers.cacheDisabled(null), null)) {
            return in.readObject();
        }
    }

    @Test
    public void testAllowedTrafficStillRoundTrips() throws Exception {
        final HashMap<String, Object> graph = new HashMap<>();
        graph.put("list", new ArrayList<>(java.util.Arrays.asList(1, 2, 3)));
        graph.put("text", "hello");

        final Object decoded = decode(encode(graph));
        Assert.assertEquals(decoded, graph, "Allowlisted traffic must still round-trip");
    }

    private record CausalFixture(Tracker tracker, Tracker receiver, GameEventCause cause, List<GameEvent> events) { }

    private static CausalFixture causalFixture() {
        final Tracker tracker = new Tracker();
        final CardView recipient = new CardView(101, tracker, "public-recipient");
        final CardView source = new CardView(102, tracker, "public-source");
        recipient.set(TrackableProperty.Zone, ZoneType.Battlefield);
        source.set(TrackableProperty.Zone, ZoneType.Battlefield);
        recipient.getCurrentState().set(TrackableProperty.ImageKey, "public-recipient-image");
        source.getCurrentState().set(TrackableProperty.ImageKey, "public-source-image");
        final PlayerView player = new PlayerView(201, tracker);
        player.set(TrackableProperty.Name, "public-player");
        tracker.putObj(TrackableTypes.CardViewType, recipient.getId(), recipient);
        tracker.putObj(TrackableTypes.CardViewType, source.getId(), source);
        tracker.putObj(TrackableTypes.PlayerViewType, player.getId(), player);
        final Tracker receiver = new Tracker();
        for (final CardView publicCard : List.of(recipient, source)) {
            final CardView copy = new CardView(publicCard.getId(), receiver, publicCard.getName());
            copy.set(TrackableProperty.Zone, ZoneType.Battlefield);
            copy.getCurrentState().set(TrackableProperty.ImageKey, publicCard.getCurrentState().getTrackableImageKey());
            receiver.putObj(TrackableTypes.CardViewType, copy.getId(), copy);
            Assert.assertNotSame(copy, publicCard, "The peer's public views are independent of sender memory");
        }
        final PlayerView receivedPlayer = new PlayerView(201, receiver);
        receivedPlayer.set(TrackableProperty.Name, "public-player");
        receiver.putObj(TrackableTypes.PlayerViewType, receivedPlayer.getId(), receivedPlayer);
        Assert.assertNotSame(receivedPlayer, player);
        // These real views belong only to engine-local provenance, never to the
        // public event fields or receiver's tracker.
        final CardView privateCard = new CardView(91001, tracker, "private-cause-card-sentinel");
        privateCard.getCurrentState().set(TrackableProperty.ImageKey, "private-cause-image-sentinel");
        final PlayerView privateController = new PlayerView(92001, tracker);
        privateController.set(TrackableProperty.Name, "private-cause-controller-sentinel");
        privateCard.set(TrackableProperty.Zone, ZoneType.Library);
        privateCard.set(TrackableProperty.Owner, privateController);
        privateCard.set(TrackableProperty.Controller, privateController);
        Assert.assertFalse(privateCard.canBeShownTo(player), "The cause-only source is actually hidden from the public event's player");
        final GameEventCause cause = new GameEventCause(privateCard, "private-cause-ability-sentinel",
                privateController, "private-cause-kind-sentinel", "private-cause-execution-sentinel");
        return new CausalFixture(tracker, receiver, cause, List.of(
                new GameEventCardDamaged(recipient, source, 3, GameEventCardDamaged.DamageType.Deathtouch,
                        201, 202, cause, true),
                new GameEventCardCounters(recipient, CounterEnumType.CHARGE, 2, 5, cause),
                new GameEventPlayerDamaged(player, source, 4, false, true, 201, cause),
                new GameEventCardChangeZone(recipient, new ZoneView(player, ZoneType.Battlefield),
                        new ZoneView(player, ZoneType.Exile), GameEventCardChangeZone.Origin.OTHER, cause, 202)));
    }

    private static void assertNoPrivateCauseBytes(final byte[] bytes) {
        final String wire = new String(bytes, StandardCharsets.ISO_8859_1);
        for (final String field : List.of("card", "image", "controller", "ability", "kind", "execution"))
            Assert.assertFalse(wire.contains("private-cause-" + field + "-sentinel"),
                    "Engine-only cause " + field + " must never enter the wire bytes");
    }

    private static Object cause(final GameEvent event) throws Exception {
        return event.getClass().getMethod("cause").invoke(event);
    }

    private static void assertPublicEvent(final GameEvent original, final GameEvent received) throws Exception {
        Assert.assertEquals(received.getClass(), original.getClass());
        Assert.assertNull(cause(received), "A legacy peer receives the public event without private engine context");
        for (final var component : original.getClass().getRecordComponents()) {
            if (component.getName().equals("cause")) continue;
            final Object expected = component.getAccessor().invoke(original);
            final Object actual = component.getAccessor().invoke(received);
            Assert.assertEquals(actual, expected, "Public event field: " + component.getName());
            if (expected instanceof CardView card && actual instanceof CardView copied) {
                Assert.assertEquals(copied.getName(), card.getName(), "Public card identity and face survive transport");
                Assert.assertEquals(copied.getCurrentState().getTrackableImageKey(), card.getCurrentState().getTrackableImageKey());
            }
            if (expected instanceof PlayerView player && actual instanceof PlayerView copied)
                Assert.assertEquals(copied.getName(), player.getName(), "Public player identity survives transport");
        }
    }

    private static void assertLocalCauseRetained(final CausalFixture fixture) throws Exception {
        Assert.assertNotSame(fixture.receiver(), fixture.tracker());
        Assert.assertNull(fixture.receiver().getObj(TrackableTypes.CardViewType, 91001),
                "The receiver never obtains the engine-private source view");
        Assert.assertNull(fixture.receiver().getObj(TrackableTypes.PlayerViewType, 92001),
                "The receiver never obtains the engine-private controller view");
        for (final GameEvent event : fixture.events()) Assert.assertSame(cause(event), fixture.cause());
        Assert.assertEquals(fixture.cause().card().getName(), "private-cause-card-sentinel");
        Assert.assertEquals(fixture.cause().card().getCurrentState().getTrackableImageKey(), "private-cause-image-sentinel");
        Assert.assertEquals(fixture.cause().controller().getName(), "private-cause-controller-sentinel");
        Assert.assertEquals(fixture.cause().ability(), "private-cause-ability-sentinel");
        Assert.assertEquals(fixture.cause().kind(), "private-cause-kind-sentinel");
        Assert.assertEquals(fixture.cause().instanceId(), "private-cause-execution-sentinel");
    }

    @Test
    public void testPrivateCausesAreOmittedByActualObjectStreamsInEveryReplacementMode() throws Exception {
        final CausalFixture fixture = causalFixture();
        assertLocalCauseRetained(fixture);
        for (final boolean replace : List.of(false, true)) for (final boolean eventMode : List.of(false, true)) {
            for (final GameEvent event : fixture.events()) {
                final ByteArrayOutputStream bytes = new ByteArrayOutputStream();
                try (CObjectOutputStream out = new CObjectOutputStream(bytes, replace, fixture.tracker(), -1, eventMode)) {
                    out.writeObject(event);
                }
                assertNoPrivateCauseBytes(bytes.toByteArray());
                try (CObjectInputStream in = new CObjectInputStream(new ByteArrayInputStream(bytes.toByteArray()),
                        ClassResolvers.cacheDisabled(null), fixture.receiver())) {
                    assertPublicEvent(event, (GameEvent) in.readObject());
                }
                assertLocalCauseRetained(fixture);
            }
        }
    }

    @Test
    public void testAllFourPublicEventsSurviveWrappingWithoutPrivateCausesOrSilentDrops() throws Exception {
        final CausalFixture fixture = causalFixture();
        assertLocalCauseRetained(fixture);
        final List<Object> wrapped = TrackableSerializer.wrapEvents(fixture.events(), fixture.tracker());
        Assert.assertEquals(wrapped.size(), 4, "No serialization error may silently drop a cause-bearing event");
        for (final Object item : wrapped) {
            Assert.assertTrue(item instanceof TrackableSerializer.WrappedEvent);
            assertNoPrivateCauseBytes(((TrackableSerializer.WrappedEvent) item).data());
        }
        final byte[] outerFrame = encode(wrapped);
        assertNoPrivateCauseBytes(outerFrame);
        final List<?> carried = (List<?>) decode(outerFrame);
        final List<Object> items = new ArrayList<>(carried);
        final List<GameEvent> received = TrackableSerializer.unwrapEvents(items, fixture.receiver());
        Assert.assertEquals(received.size(), 4, "The actual wrapped-event receiver must retain all four events");
        for (int index = 0; index < received.size(); index++) assertPublicEvent(fixture.events().get(index), received.get(index));
        assertLocalCauseRetained(fixture);
    }

    /**
     * Nested rather than top-level, which strictly subsumes the simpler case: a
     * filter applied only to the first descriptor would pass this and fail only
     * here.
     */
    @Test
    public void testRejectsAClassNestedInsideAllowedCollections() throws Exception {
        final HashMap<String, Object> graph = new HashMap<>();
        graph.put("innocuous", "text");
        graph.put("payload", disallowedObject());

        try {
            decode(encode(graph));
            Assert.fail("A disallowed class nested inside an allowed map was deserialized");
        } catch (final InvalidClassException expected) {
            Assert.assertTrue(expected.getMessage().contains("java.io.File"), expected.getMessage());
        }
    }

    /**
     * The route that carries no class name: a proxy descriptor reaches
     * {@code resolveProxyClass} alone, so a filter on the other two would let
     * it through — and that is where the best-known chains start.
     */
    @Test
    public void testRejectsDynamicProxies() throws Exception {
        final Runnable proxy = (Runnable) Proxy.newProxyInstance(
                getClass().getClassLoader(), new Class<?>[] { Runnable.class }, new Handler());

        final byte[] frame = encode(proxy);
        try {
            decode(frame);
            Assert.fail("A dynamic proxy was deserialized from the wire");
        } catch (final InvalidClassException expected) {
            Assert.assertTrue(expected.getMessage().toLowerCase().contains("proxy"),
                    "Rejection should identify the proxy: " + expected.getMessage());
        }
    }

    /**
     * Lengths and depths are data, not names, so the class filter cannot see
     * them. Both bounds share one pattern string, so dropping a term from it is
     * a silent regression — hence checking two. The reference bound is not
     * checked: tripping it legitimately needs a million-object graph.
     */
    @Test
    public void testRejectsResourceExhaustingFrames() throws Exception {
        // Nesting past the depth bound.
        java.util.List<Object> deep = new ArrayList<>();
        java.util.List<Object> cursor = deep;
        for (int i = 0; i < 150; i++) {
            final java.util.List<Object> next = new ArrayList<>();
            cursor.add(next);
            cursor = next;
        }
        try {
            decode(encode(deep));
            Assert.fail("A frame nested 150 deep was accepted");
        } catch (final InvalidClassException expected) {
            // WireStreamLimits refused it.
        }

        final byte[] frame = encode(new int[] { 1, 2, 3, 4 });
        // Rewrite the declared length to 200 million (~762 MB if allocated) and
        // cut the frame short: nothing past the length is ever read.
        final int lengthAt = frame.length - 4 * 4 - 4;
        final int huge = 200_000_000;
        frame[lengthAt] = (byte) (huge >>> 24);
        frame[lengthAt + 1] = (byte) (huge >>> 16);
        frame[lengthAt + 2] = (byte) (huge >>> 8);
        frame[lengthAt + 3] = (byte) huge;

        try {
            decode(frame);
            Assert.fail("A frame declaring int[" + huge + "] was accepted");
        } catch (final InvalidClassException expected) {
            // WireStreamLimits refused the length before anything was allocated.
        } catch (final Throwable other) {
            // Anything else — EOF from reading elements, or OutOfMemoryError —
            // means the length was accepted and the array already allocated.
            Assert.fail("The declared array was allocated (" + other.getClass().getSimpleName()
                    + "): the wire stream limits are not in place");
        }
    }
}
