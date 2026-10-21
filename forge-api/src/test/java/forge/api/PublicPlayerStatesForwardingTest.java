package forge.api;

import com.google.gson.GsonBuilder;
import forge.StaticData;
import forge.deck.Deck;
import forge.game.card.CounterEnumType;
import forge.game.card.CounterType;
import forge.game.player.Player;
import forge.game.player.PublicPlayerState;
import forge.game.player.PublicPlayerStateRegistry;
import forge.game.player.RegisteredPlayer;
import org.testng.annotations.BeforeClass;
import org.testng.annotations.Test;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;

import static org.testng.Assert.*;

/** A mechanic unknown to the API still uses the public wire schema and generic activity transitions. */
public class PublicPlayerStatesForwardingTest {
    private static final Path RESOURCES = Path.of("../forge-gui/res");

    @BeforeClass public void resources() { if (StaticData.instance() == null) EngineResources.load(RESOURCES); }

    @Test public void engineProviderExtensionsAndCustomCountersForwardWithoutApiMechanicCases() throws Exception {
        var players = new ArrayList<RegisteredPlayer>();
        for (int seat = 0; seat < 4; seat++) players.add(MatchTable.human(new Deck(), "Constructed", "Seat " + seat));
        try (var table = new MatchTable("Constructed", players, RESOURCES, Files.createTempDirectory("engine-public-state-forwarding"))) {
            HeadlessPlatform.activate(table);
            var owner = table.game.getPlayers().get(0);
            owner.setSpeed(2);
            var custom = CounterType.getType("Scenario signal");
            owner.setCounters(custom, 3, null, false);
            owner.setCounters(CounterEnumType.POISON, 1, null, false);
            var descriptor = new PublicPlayerStateRegistry.Descriptor("future-engine-progress", "Future progress",
                    PublicPlayerState.Kind.PROGRESS, 7, "An engine extension with a public maximum.", false);
            var registry = PublicPlayerStateRegistry.standard().with(
                    PublicPlayerStateRegistry.Provider.progress(descriptor, player -> player.getSpeed() + 3));
            var engineSnapshot = registry.snapshot(owner);
            var wire = PublicPlayerStates.of(owner, registry);
            var json = new GsonBuilder().serializeNulls().create().toJsonTree(wire).getAsJsonArray();
            var future = json.asList().stream().map(value -> value.getAsJsonObject())
                    .filter(value -> value.get("id").getAsString().equals(descriptor.id())).findFirst().orElseThrow();
            assertEquals(future.get("kind").getAsString(), "progress");
            assertEquals(future.get("value").getAsInt(), 5);
            assertEquals(future.get("maximum").getAsInt(), 7);
            assertEquals(future.get("detail").getAsString(), descriptor.detail());
            assertEquals(wire.stream().filter(state -> state.kind().equals("counter")).map(PublicPlayerStates.State::id).toList(),
                    List.of("counter:poison", "counter:scenario signal"));
            assertEquals(wire.stream().filter(state -> state.id().equals("counter:scenario signal")).findFirst().orElseThrow().value(), Integer.valueOf(3));
            expectThrows(UnsupportedOperationException.class, () -> wire.clear());
            expectThrows(UnsupportedOperationException.class, () -> engineSnapshot.clear());

            var activity = new MatchActivity(owner.getView());
            activity.observePlayerStates(owner.getView(), wire);
            assertTrue(activity.snapshot().isEmpty(), "Registering current state does not invent past transitions");
            owner.setSpeed(Player.MAX_SPEED);
            owner.setCounters(custom, 0, null, false);
            var updated = PublicPlayerStates.of(owner, registry);
            activity.observePlayerStates(owner.getView(), updated);
            assertTrue(activity.snapshot().stream().anyMatch(entry -> entry.message().contains("Future progress reached the maximum: 7")),
                    "An unknown progress provider gets the generic public transition cue");
            assertTrue(activity.snapshot().stream().anyMatch(entry -> entry.message().contains("Scenario signal decreased from 3 to 0")));
            var entries = activity.snapshot();
            activity.observePlayerStates(owner.getView(), updated);
            assertEquals(activity.snapshot(), entries, "Unchanged engine snapshots do not replay activity");
            assertEquals(wire.stream().filter(state -> state.id().equals("speed")).findFirst().orElseThrow().value(), Integer.valueOf(2));
            assertEquals(engineSnapshot.stream().filter(state -> state.id().equals("counter:scenario signal")).findFirst().orElseThrow().value(), Integer.valueOf(3),
                    "Captured values do not follow mutable engine counters");
            assertTrue(updated.stream().noneMatch(state -> state.id().equals("counter:scenario signal")), "Inactive counters leave the active state list");

            var duplicate = new PublicPlayerStateRegistry.Provider(new PublicPlayerStateRegistry.Descriptor("future:", "Future family",
                    PublicPlayerState.Kind.COUNTER, null, "A deliberately invalid provider.", true), player -> List.of(
                    new PublicPlayerState("future:same", "Future", PublicPlayerState.Kind.COUNTER, 1, null, "Public counter."),
                    new PublicPlayerState("future:same", "Future", PublicPlayerState.Kind.COUNTER, 2, null, "Public counter.")));
            expectThrows(IllegalStateException.class, () -> PublicPlayerStates.of(owner, registry.with(duplicate)));
        }
    }
}
