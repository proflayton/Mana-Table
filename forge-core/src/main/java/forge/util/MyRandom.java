/*
 * Forge: Play Magic: the Gathering.
 * Copyright (C) 2011  Forge Team
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 * 
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 * 
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <http://www.gnu.org/licenses/>.
 */
package forge.util;

import java.security.SecureRandom;
import java.util.Map;
import java.util.Random;
import java.util.Spliterator;
import java.util.Comparator;
import java.util.function.IntConsumer;
import java.util.function.LongConsumer;
import java.util.function.DoubleConsumer;
import java.util.stream.IntStream;
import java.util.stream.LongStream;
import java.util.stream.DoubleStream;
import java.util.stream.StreamSupport;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.atomic.AtomicLong;
import java.util.concurrent.atomic.LongAdder;

/**
 * <p>
 * MyRandom class.<br>
 * Preferably all Random numbers should be retrieved using this wrapper class
 * </p>
 * 
 * @author Forge
 * @version $Id$
 */
public class MyRandom {
    private static final ThreadLocal<Runnable> useObserver = new ThreadLocal<>();

    /** Rules hosts can fence an information-sensitive operation without replacing RNG. */
    public static Runnable exchangeUseObserver(Runnable observer) {
        Runnable previous = useObserver.get();
        if (observer == null) useObserver.remove(); else useObserver.set(observer);
        return previous;
    }

    private static void observeUse() {
        Runnable observer = useObserver.get();
        if (observer != null) observer.run();
    }

    /** Keep the exact provider and its Gaussian cache; observe actual calls,
     * including a Random reference or lazy stream retained before a game action. */
    private static final class ObservedRandom extends Random {
        private static final long serialVersionUID = 1L;
        private final Random provider;
        private final AtomicLong calls = new AtomicLong();
        long callCount() { return calls.get(); }
        private void used() {
            calls.incrementAndGet();
            observeUse();
            if (TRACE) { trace(); }
        }
        ObservedRandom(Random provider) { super(0); this.provider = provider; }
        @Override public void setSeed(long seed) {
            if (provider == null) super.setSeed(seed);
            else { used(); provider.setSeed(seed); }
        }
        @Override protected int next(int bits) { used(); return provider.nextInt() >>> (32 - bits); }
        @Override public int nextInt() { used(); return provider.nextInt(); }
        @Override public int nextInt(int bound) { used(); return provider.nextInt(bound); }
        @Override public int nextInt(int origin, int bound) { used(); return provider.nextInt(origin, bound); }
        @Override public long nextLong() { used(); return provider.nextLong(); }
        @Override public long nextLong(long bound) { used(); return provider.nextLong(bound); }
        @Override public long nextLong(long origin, long bound) { used(); return provider.nextLong(origin, bound); }
        @Override public boolean nextBoolean() { used(); return provider.nextBoolean(); }
        @Override public float nextFloat() { used(); return provider.nextFloat(); }
        @Override public float nextFloat(float bound) { used(); return provider.nextFloat(bound); }
        @Override public float nextFloat(float origin, float bound) { used(); return provider.nextFloat(origin, bound); }
        @Override public double nextDouble() { used(); return provider.nextDouble(); }
        @Override public double nextDouble(double bound) { used(); return provider.nextDouble(bound); }
        @Override public double nextDouble(double origin, double bound) { used(); return provider.nextDouble(origin, bound); }
        @Override public double nextGaussian() { used(); return provider.nextGaussian(); }
        @Override public double nextGaussian(double mean, double deviation) { used(); return provider.nextGaussian(mean, deviation); }
        @Override public double nextExponential() { used(); return provider.nextExponential(); }
        @Override public void nextBytes(byte[] bytes) { used(); provider.nextBytes(bytes); }
        @Override public IntStream ints() { return observed(provider.ints(), this::used); }
        @Override public IntStream ints(long size) { return observed(provider.ints(size), this::used); }
        @Override public IntStream ints(int origin, int bound) { return observed(provider.ints(origin,bound), this::used); }
        @Override public IntStream ints(long size, int origin, int bound) { return observed(provider.ints(size,origin,bound), this::used); }
        @Override public LongStream longs() { return observed(provider.longs(), this::used); }
        @Override public LongStream longs(long size) { return observed(provider.longs(size), this::used); }
        @Override public LongStream longs(long origin, long bound) { return observed(provider.longs(origin,bound), this::used); }
        @Override public LongStream longs(long size, long origin, long bound) { return observed(provider.longs(size,origin,bound), this::used); }
        @Override public DoubleStream doubles() { return observed(provider.doubles(), this::used); }
        @Override public DoubleStream doubles(long size) { return observed(provider.doubles(size), this::used); }
        @Override public DoubleStream doubles(double origin, double bound) { return observed(provider.doubles(origin,bound), this::used); }
        @Override public DoubleStream doubles(long size, double origin, double bound) { return observed(provider.doubles(size,origin,bound), this::used); }
    }

    // Preserve the provider's stream implementation and SIZED optimizations:
    // e.g. Random.ints(3).count() must not consume three random values. A cached
    // stream observes its consuming state boundary, not its creation boundary.
    private static IntStream observed(IntStream stream, Runnable use) {
        boolean parallel = stream.isParallel();
        return StreamSupport.intStream(observed(stream.spliterator(), use),parallel).onClose(stream::close);
    }
    private static Spliterator.OfInt observed(Spliterator.OfInt values, Runnable use) {
        return new Spliterator.OfInt() {
            public boolean tryAdvance(IntConsumer action) { if (values.estimateSize()!=0) use.run(); return values.tryAdvance(action); }
            public void forEachRemaining(IntConsumer action) { if (values.estimateSize()!=0) use.run(); values.forEachRemaining(action); }
            public Spliterator.OfInt trySplit() { use.run(); var split=values.trySplit(); return split==null?null:observed(split, use); }
            public long estimateSize() { return values.estimateSize(); }
            public int characteristics() { return values.characteristics(); }
            public Comparator<? super Integer> getComparator() { return values.getComparator(); }
        };
    }
    private static LongStream observed(LongStream stream, Runnable use) {
        boolean parallel = stream.isParallel();
        return StreamSupport.longStream(observed(stream.spliterator(), use),parallel).onClose(stream::close);
    }
    private static Spliterator.OfLong observed(Spliterator.OfLong values, Runnable use) {
        return new Spliterator.OfLong() {
            public boolean tryAdvance(LongConsumer action) { if (values.estimateSize()!=0) use.run(); return values.tryAdvance(action); }
            public void forEachRemaining(LongConsumer action) { if (values.estimateSize()!=0) use.run(); values.forEachRemaining(action); }
            public Spliterator.OfLong trySplit() { use.run(); var split=values.trySplit(); return split==null?null:observed(split, use); }
            public long estimateSize() { return values.estimateSize(); }
            public int characteristics() { return values.characteristics(); }
            public Comparator<? super Long> getComparator() { return values.getComparator(); }
        };
    }
    private static DoubleStream observed(DoubleStream stream, Runnable use) {
        boolean parallel = stream.isParallel();
        return StreamSupport.doubleStream(observed(stream.spliterator(), use),parallel).onClose(stream::close);
    }
    private static Spliterator.OfDouble observed(Spliterator.OfDouble values, Runnable use) {
        return new Spliterator.OfDouble() {
            public boolean tryAdvance(DoubleConsumer action) { if (values.estimateSize()!=0) use.run(); return values.tryAdvance(action); }
            public void forEachRemaining(DoubleConsumer action) { if (values.estimateSize()!=0) use.run(); values.forEachRemaining(action); }
            public Spliterator.OfDouble trySplit() { use.run(); var split=values.trySplit(); return split==null?null:observed(split, use); }
            public long estimateSize() { return values.estimateSize(); }
            public int characteristics() { return values.characteristics(); }
            public Comparator<? super Double> getComparator() { return values.getComparator(); }
        };
    }

    /**
     * Per-thread random source. Thread-local so concurrent games in one JVM
     * (headless simulation workers, parallel AI harnesses) that seed their
     * stream via {@link #setRandom(Random)} cannot contaminate each other's
     * sequences. Inheritable so threads spawned from a seeded thread (AI
     * evaluation helpers) continue the parent's stream instead of silently
     * falling back to an unseeded one. Single-threaded behavior is unchanged.
     */
    private static final ThreadLocal<ObservedRandom> random = new InheritableThreadLocal<ObservedRandom>() {
        @Override
        protected ObservedRandom initialValue() {
            return new ObservedRandom(new SecureRandom());
        }
    };

    /** Monotonic use counter on the current stream. Undo needs to detect use,
     * including cached values and stream traversal, rather than count entropy bits. */
    public static long callCount() { return random.get().callCount(); }

    // Debug only (-Dforge.undo.debug=true): tally draws by thread and call site, to find who consumes randomness.
    private static final boolean TRACE = Boolean.getBoolean("forge.undo.debug");
    private static final Map<String, LongAdder> SITES = new ConcurrentHashMap<>();

    private static void trace() {
        final StringBuilder key = new StringBuilder(Thread.currentThread().getName()).append(" <- ");
        int shown = 0;
        for (final StackTraceElement e : new Throwable().getStackTrace()) {
            final String c = e.getClassName();
            if (c.startsWith("java.") || c.startsWith("jdk.") || c.startsWith("forge.util.MyRandom")) { continue; }
            key.append(c.substring(c.lastIndexOf('.') + 1)).append('.').append(e.getMethodName())
                    .append(':').append(e.getLineNumber()).append(shown < 3 ? " < " : "");
            if (++shown == 4) { break; }
        }
        SITES.computeIfAbsent(key.toString(), k -> new LongAdder()).increment();
    }

    /** Debug only: the {@code top} busiest draw sites since the last call, as text; clears the tally. */
    public static String drainTrace(final int top) {
        final StringBuilder sb = new StringBuilder();
        SITES.entrySet().stream()
                .sorted((a, b) -> Long.compare(b.getValue().sum(), a.getValue().sum()))
                .limit(top)
                .forEach(e -> sb.append("\n    ").append(e.getValue().sum()).append("x ").append(e.getKey()));
        SITES.clear();
        return sb.toString();
    }

    public static boolean percentTrue(final int percent) {
        return percent > MyRandom.getRandom().nextInt(100);
    }

    /**
     * Gets the random.
     * 
     * @return the random for the current thread
     */
    public static Random getRandom() {
        return random.get();
    }

    /**
     * Sets the random provider for the current thread (and threads it spawns
     * afterwards). Used for deterministic simulation.
     * @param random the random
     */
    public static void setRandom(Random random) {
        observeUse();
        MyRandom.random.set(random instanceof ObservedRandom ? (ObservedRandom) random : new ObservedRandom(random));
    }

    public static int[] splitIntoRandomGroups(final int value, final int numGroups) {
        int[] groups = new int[numGroups];

        for (int i = 0; i < value; i++) {
            groups[getRandom().nextInt(numGroups)]++;
        }

        return groups;
    }
}
