package forge.gamemodes.net.server;

import forge.game.GameType;
import forge.game.Game;
import forge.game.GameRules;
import forge.game.Match;
import forge.game.GameView;
import forge.game.event.GameEventGameStarted;
import forge.game.player.PlayerView;
import forge.gamemodes.net.CompatibleObjectEncoder;
import forge.gamemodes.net.DeltaPacket;
import forge.gamemodes.net.ProtocolMethod;
import forge.gamemodes.net.event.GuiGameEvent;
import forge.trackable.TrackableProperty;
import forge.util.Localizer;
import io.netty.buffer.ByteBuf;
import io.netty.buffer.ByteBufAllocator;
import io.netty.buffer.Unpooled;
import io.netty.buffer.UnpooledByteBufAllocator;
import io.netty.channel.Channel;
import io.netty.channel.ChannelFuture;
import io.netty.channel.ChannelPipeline;
import io.netty.channel.EventLoop;
import org.testng.annotations.DataProvider;
import org.testng.annotations.BeforeClass;
import org.testng.annotations.Test;

import java.io.Serializable;
import java.util.ArrayList;
import java.util.Collections;
import java.util.EnumMap;
import java.util.EnumSet;
import java.util.List;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.TimeoutException;
import java.util.concurrent.atomic.AtomicBoolean;

import static org.mockito.ArgumentMatchers.*;
import static org.mockito.Mockito.*;
import static org.testng.Assert.*;

public class DeltaSyncConcurrencyShould {
    @BeforeClass
    public void initializeLanguage() {
        Localizer.getInstance().initialize("en-US", "res/languages/");
    }

    @DataProvider(name = "managerCallbacks")
    public Object[][] managerCallbacks() { return new Object[][] { {"collect"}, {"register"}, {"resync"}, {"reset"} }; }

    @Test(dataProvider = "managerCallbacks", timeOut = 10000)
    public void finishTheCurrentWalkBeforeAnotherCallbackChangesItsBaseline(String callback) throws Exception {
        var manager = new DeltaSyncManager();
        var view = view();
        var entered = new CountDownLatch(1);
        var release = new CountDownLatch(1);
        var attempted = new CountDownLatch(1);
        var first = new AtomicBoolean(true);
        var props = new EnumMap<TrackableProperty, Object>(TrackableProperty.class);
        props.put(TrackableProperty.Title, "Concurrent baseline");
        doAnswer(call -> {
            if (first.compareAndSet(true, false)) {
                entered.countDown();
                assertTrue(release.await(5, TimeUnit.SECONDS), "Release the paused graph walk");
            }
            return props;
        }).when(view).getProps();
        ExecutorService threads = Executors.newFixedThreadPool(2);
        try {
            Future<DeltaPacket> collecting = threads.submit(() -> manager.collectDeltas(view));
            assertTrue(entered.await(5, TimeUnit.SECONDS), "The graph walk reached its controlled pause");
            Future<?> competing = threads.submit(() -> {
                attempted.countDown();
                switch (callback) {
                    case "collect" -> manager.collectDeltas(view);
                    case "register" -> manager.registerNewObjects(view);
                    case "resync" -> manager.onResyncRequested();
                    case "reset" -> manager.reset();
                    default -> throw new AssertionError(callback);
                }
            });
            assertTrue(attempted.await(5, TimeUnit.SECONDS));
            assertThrows(TimeoutException.class, () -> competing.get(150, TimeUnit.MILLISECONDS));
            release.countDown();
            assertEquals(collecting.get(5, TimeUnit.SECONDS).getSequenceNumber(), 1L);
            competing.get(5, TimeUnit.SECONDS);
            assertEquals(manager.getCurrentSequence(), callback.equals("reset") ? 0L : callback.equals("collect") ? 2L : 1L);
            if (callback.equals("reset")) {
                var fresh = manager.collectDeltas(view);
                assertEquals(fresh.getSequenceNumber(), 1L);
                assertFalse(fresh.getNewObjects().isEmpty(), "A completed reset establishes a fresh full-object baseline");
            }
        } finally {
            release.countDown(); threads.shutdownNow();
            assertTrue(threads.awaitTermination(5, TimeUnit.SECONDS));
        }
    }

    @Test(timeOut = 10000)
    public void enqueueTheFirstPacketBeforeAConcurrentInputPublicationCanSendTheNext() throws Exception {
        var firstSend = new CountDownLatch(1);
        var release = new CountDownLatch(1);
        var attempted = new CountDownLatch(1);
        var sequences = Collections.synchronizedList(new ArrayList<Long>());
        var channel = mock(Channel.class);
        var pipeline = mock(ChannelPipeline.class);
        var eventLoop = mock(EventLoop.class);
        var encoded = mock(CompatibleObjectEncoder.class);
        var write = mock(ChannelFuture.class);
        when(channel.pipeline()).thenReturn(pipeline);
        when(channel.eventLoop()).thenReturn(eventLoop);
        when(eventLoop.inEventLoop()).thenReturn(true);
        when(pipeline.get(CompatibleObjectEncoder.class)).thenReturn(encoded);
        when(channel.isWritable()).thenReturn(true);
        when(channel.alloc()).thenReturn(UnpooledByteBufAllocator.DEFAULT);
        when(channel.writeAndFlush(any())).thenAnswer(call -> {
            ((ByteBuf) call.getArgument(0)).release();
            return write;
        });
        when(encoded.encodeToBuf(any(Serializable.class), any(ByteBufAllocator.class))).thenAnswer(call -> {
            var event = (GuiGameEvent) call.getArgument(0);
            if (event.getMethod() == ProtocolMethod.applyDelta) {
                long sequence = ((DeltaPacket) event.getObjects()[0]).getSequenceNumber();
                if (sequence == 2L) {
                    firstSend.countDown();
                    assertTrue(release.await(5, TimeUnit.SECONDS), "Release the first outbound publication");
                }
                sequences.add(sequence);
            }
            return Unpooled.buffer(1).writeByte(0);
        });
        var gui = new RemoteClientGuiGame(new RemoteClient(channel), false);
        gui.setGameView(view());
        gui.sendFullState();
        gui.handleGameEvents(List.of(new GameEventGameStarted(GameType.Constructed, (PlayerView) null, List.<PlayerView>of())));
        assertEquals(sequences, List.of(1L), "The initial delta baseline was published");
        sequences.clear();
        ExecutorService threads = Executors.newFixedThreadPool(2);
        try {
            Future<?> events = threads.submit(() -> gui.handleGameEvents(List.of()));
            assertTrue(firstSend.await(5, TimeUnit.SECONDS), "The first packet is paused between collection and enqueue");
            Future<?> input = threads.submit(() -> { attempted.countDown(); gui.updateGameView(); });
            assertTrue(attempted.await(5, TimeUnit.SECONDS));
            assertThrows(TimeoutException.class, () -> input.get(150, TimeUnit.MILLISECONDS));
            release.countDown();
            events.get(5, TimeUnit.SECONDS); input.get(5, TimeUnit.SECONDS);
            assertEquals(sequences, List.of(2L, 3L), "Concurrent event and input publications retain their delta sequence order on the wire");
        } finally {
            release.countDown(); threads.shutdownNow();
            assertTrue(threads.awaitTermination(5, TimeUnit.SECONDS));
        }
    }

    private static GameView view() {
        var rules = new GameRules(GameType.Constructed);
        var game = new Game(List.of(), rules, new Match(rules, List.of(), "Concurrent baseline"));
        var view = spy(game.getView());
        var props = new EnumMap<TrackableProperty, Object>(TrackableProperty.class);
        props.put(TrackableProperty.Title, "Stable public table");
        doReturn(props).when(view).getProps();
        when(view.getAndClearDirtyProps(anyInt())).thenAnswer(call -> EnumSet.of(TrackableProperty.Title));
        return view;
    }
}
