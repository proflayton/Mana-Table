package forge.game.player;

import com.google.common.collect.Multiset;
import forge.game.card.CounterEnumType;
import forge.game.card.CounterType;

import java.util.ArrayList;
import java.util.Comparator;
import java.util.HashSet;
import java.util.List;
import java.util.Locale;
import java.util.Objects;
import java.util.function.Function;
import java.util.function.Predicate;
import java.util.function.ToIntFunction;

/** Explicit engine-owned public-state providers. Private engine fields are never discovered or serialized. */
public final class PublicPlayerStateRegistry {
    /** A scalar mechanic, or a named family whose entries share the id prefix. */
    public record Descriptor(String id, String label, PublicPlayerState.Kind kind, Integer maximum,
                             String detail, boolean family) {
        public Descriptor {
            PublicPlayerState.requireText(id, "id");
            PublicPlayerState.requireText(label, "label");
            PublicPlayerState.requireText(detail, "detail");
            Objects.requireNonNull(kind, "kind");
            PublicPlayerState.validateMaximum(kind, maximum);
            if (family && !id.endsWith(":")) throw new IllegalArgumentException("A public-state family needs an id prefix ending in ':'");
        }

        private PublicPlayerState state(Integer value) {
            return new PublicPlayerState(id, label, kind, value, maximum, detail);
        }
    }

    public record Provider(Descriptor descriptor, Function<Player, List<PublicPlayerState>> reader) {
        public Provider { Objects.requireNonNull(descriptor, "descriptor"); Objects.requireNonNull(reader, "reader"); }

        public static Provider progress(Descriptor descriptor, ToIntFunction<Player> value) {
            if (descriptor.kind() != PublicPlayerState.Kind.PROGRESS || descriptor.family())
                throw new IllegalArgumentException("A progress provider needs a scalar progress descriptor");
            Objects.requireNonNull(value, "value");
            return new Provider(descriptor, player -> {
                int current = value.applyAsInt(player);
                return current > 0 ? List.of(descriptor.state(current)) : List.of();
            });
        }

        public static Provider status(Descriptor descriptor, Predicate<Player> active) {
            if (descriptor.kind() != PublicPlayerState.Kind.STATUS || descriptor.family())
                throw new IllegalArgumentException("A status provider needs a scalar status descriptor");
            Objects.requireNonNull(active, "active");
            return new Provider(descriptor, player -> active.test(player) ? List.of(descriptor.state(null)) : List.of());
        }
    }

    private static final PublicPlayerStateRegistry STANDARD = createStandard();
    private final List<Provider> providers;
    private final List<Descriptor> catalog;

    public PublicPlayerStateRegistry(List<Provider> providers) {
        this.providers = List.copyOf(providers);
        var descriptors = new ArrayList<Descriptor>();
        for (var provider : this.providers) {
            var descriptor = provider.descriptor();
            for (var existing : descriptors) {
                if (existing.id().equals(descriptor.id())
                        || existing.family() && descriptor.id().startsWith(existing.id())
                        || descriptor.family() && existing.id().startsWith(descriptor.id()))
                    throw new IllegalArgumentException("Overlapping public-state provider id: " + descriptor.id());
            }
            descriptors.add(descriptor);
        }
        catalog = List.copyOf(descriptors);
    }

    public static PublicPlayerStateRegistry standard() { return STANDARD; }

    /** Declared coverage, including the open-ended counter family; not an inventory of private Player fields. */
    public List<Descriptor> catalog() { return catalog; }

    /** Extending public engine state does not require a transport or renderer mechanic switch. */
    public PublicPlayerStateRegistry with(Provider provider) {
        var extended = new ArrayList<>(providers);
        extended.add(provider);
        return new PublicPlayerStateRegistry(extended);
    }

    /** Capture only at an engine publication boundary, on the same thread as its other public projections. */
    public List<PublicPlayerState> snapshot(Player player) {
        Objects.requireNonNull(player, "player");
        var states = new ArrayList<PublicPlayerState>();
        var ids = new HashSet<String>();
        for (var provider : providers) {
            var descriptor = provider.descriptor();
            for (var state : List.copyOf(provider.reader().apply(player))) {
                boolean ownsId = descriptor.family() ? state.id().startsWith(descriptor.id()) && state.id().length() > descriptor.id().length()
                        : state.id().equals(descriptor.id());
                if (!ownsId || state.kind() != descriptor.kind() || !Objects.equals(state.maximum(), descriptor.maximum()))
                    throw new IllegalStateException("Public-state provider returned an undeclared state: " + state.id());
                if (!ids.add(state.id())) throw new IllegalStateException("Duplicate public-state id: " + state.id());
                states.add(state);
            }
        }
        return List.copyOf(states);
    }

    private static PublicPlayerStateRegistry createStandard() {
        return new PublicPlayerStateRegistry(List.of(
                Provider.progress(new Descriptor("speed", "Speed", PublicPlayerState.Kind.PROGRESS, Player.MAX_SPEED,
                        "Speed starts at 1. Once each of this player's turns, when an opponent loses life, speed increases by 1, up to " + Player.MAX_SPEED + ". "
                                + "It persists after its source leaves. Cards may have abilities at max speed.", false), Player::getSpeed),
                Provider.status(new Descriptor("monarch", "Monarch", PublicPlayerState.Kind.STATUS, null,
                        "At this player's end step, they draw a card. A player whose creature deals combat damage to them becomes the monarch.", false), Player::isMonarch),
                Provider.status(new Descriptor("initiative", "Initiative", PublicPlayerState.Kind.STATUS, null,
                        "When this player takes the initiative and at their upkeep, they venture into Undercity. "
                                + "A player whose creature deals combat damage to them takes the initiative.", false), Player::hasInitiative),
                Provider.status(new Descriptor("citys-blessing", "City's blessing", PublicPlayerState.Kind.STATUS, null,
                        "This player keeps the city's blessing for the rest of the game. It enables abilities that refer to it.", false), Player::hasBlessing),
                new Provider(new Descriptor("counter:", "Player counters", PublicPlayerState.Kind.COUNTER, null,
                        "Every positive counter type on this player, including custom counter types.", true), PublicPlayerStateRegistry::counters)));
    }

    private static List<PublicPlayerState> counters(Player player) {
        return player.getCounters().entrySet().stream().filter(entry -> entry.getCount() > 0)
                .sorted(Comparator.comparing((Multiset.Entry<CounterType> entry) -> !entry.getElement().is(CounterEnumType.POISON))
                        .thenComparing(entry -> entry.getElement().getName().toLowerCase(Locale.ROOT)))
                .map(entry -> {
                    String name = entry.getElement().getName();
                    return new PublicPlayerState("counter:" + name.toLowerCase(Locale.ROOT), name, PublicPlayerState.Kind.COUNTER,
                            entry.getCount(), null, name + " counters on this player. Cards and effects describe how they are used.");
                }).toList();
    }
}
