package forge.game.player;

import org.testng.annotations.Test;

import java.util.List;

import static org.testng.Assert.*;

public class PublicPlayerStateRegistryTest {
    private static PublicPlayerStateRegistry.Descriptor progress(String id, Integer maximum) {
        return new PublicPlayerStateRegistry.Descriptor(id, "Future progress", PublicPlayerState.Kind.PROGRESS,
                maximum, "Public progress supplied by the engine.", false);
    }

    @Test public void unrenderableProgressAndInvalidScalarValuesAreRejectedBeforePublication() {
        expectThrows(IllegalArgumentException.class, () -> progress("future-progress", null));
        expectThrows(IllegalArgumentException.class, () -> progress("future-progress", 0));
        expectThrows(IllegalArgumentException.class, () -> new PublicPlayerState("future-progress", "Progress",
                PublicPlayerState.Kind.PROGRESS, 8, 7, "Public progress."));
        expectThrows(IllegalArgumentException.class, () -> new PublicPlayerState("future-status", "Status",
                PublicPlayerState.Kind.STATUS, 1, null, "Public status."));
        expectThrows(IllegalArgumentException.class, () -> new PublicPlayerState("counter:future", "Counter",
                PublicPlayerState.Kind.COUNTER, 0, null, "Public counter."));
    }

    @Test public void ambiguousProviderOwnershipIsRejectedWhenTheRegistryIsBuilt() {
        var provider = PublicPlayerStateRegistry.Provider.progress(progress("future-progress", 7), player -> 1);
        expectThrows(IllegalArgumentException.class, () -> new PublicPlayerStateRegistry(List.of(provider, provider)));
        var counterClaim = new PublicPlayerStateRegistry.Provider(new PublicPlayerStateRegistry.Descriptor(
                "counter:future", "Future counter", PublicPlayerState.Kind.COUNTER, null, "Public counter.", false), player -> List.of());
        expectThrows(IllegalArgumentException.class, () -> PublicPlayerStateRegistry.standard().with(counterClaim));
    }

    @Test public void catalogIsImmutableAndUsesTheSameSpeedLimitAsTheRules() {
        var standard = PublicPlayerStateRegistry.standard();
        var speed = standard.catalog().stream().filter(entry -> entry.id().equals("speed")).findFirst().orElseThrow();
        assertEquals(speed.maximum(), Integer.valueOf(Player.MAX_SPEED));
        assertEquals(speed.kind(), PublicPlayerState.Kind.PROGRESS);
        assertTrue(standard.catalog().stream().anyMatch(entry -> entry.id().equals("counter:") && entry.family()),
                "The counter family covers custom names without registering each name");
        int originalSize = standard.catalog().size();
        var extended = standard.with(PublicPlayerStateRegistry.Provider.progress(progress("future-progress", 7), player -> 1));
        assertEquals(standard.catalog().size(), originalSize, "Extending a registry never mutates the standard registry");
        assertEquals(extended.catalog().size(), originalSize + 1);
        expectThrows(UnsupportedOperationException.class, () -> extended.catalog().clear());
    }
}
