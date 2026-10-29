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
    /** Constant <code>random</code>. */
    private static Random random = new ObservedRandom(new SecureRandom());
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
        ObservedRandom(Random provider) { super(0); this.provider = provider; }
        @Override public void setSeed(long seed) {
            if (provider == null) super.setSeed(seed);
            else { observeUse(); provider.setSeed(seed); }
        }
        @Override protected int next(int bits) { observeUse(); return provider.nextInt() >>> (32 - bits); }
        @Override public int nextInt() { observeUse(); return provider.nextInt(); }
        @Override public int nextInt(int bound) { observeUse(); return provider.nextInt(bound); }
        @Override public int nextInt(int origin, int bound) { observeUse(); return provider.nextInt(origin, bound); }
        @Override public long nextLong() { observeUse(); return provider.nextLong(); }
        @Override public long nextLong(long bound) { observeUse(); return provider.nextLong(bound); }
        @Override public long nextLong(long origin, long bound) { observeUse(); return provider.nextLong(origin, bound); }
        @Override public boolean nextBoolean() { observeUse(); return provider.nextBoolean(); }
        @Override public float nextFloat() { observeUse(); return provider.nextFloat(); }
        @Override public float nextFloat(float bound) { observeUse(); return provider.nextFloat(bound); }
        @Override public float nextFloat(float origin, float bound) { observeUse(); return provider.nextFloat(origin, bound); }
        @Override public double nextDouble() { observeUse(); return provider.nextDouble(); }
        @Override public double nextDouble(double bound) { observeUse(); return provider.nextDouble(bound); }
        @Override public double nextDouble(double origin, double bound) { observeUse(); return provider.nextDouble(origin, bound); }
        @Override public double nextGaussian() { observeUse(); return provider.nextGaussian(); }
        @Override public double nextGaussian(double mean, double deviation) { observeUse(); return provider.nextGaussian(mean, deviation); }
        @Override public double nextExponential() { observeUse(); return provider.nextExponential(); }
        @Override public void nextBytes(byte[] bytes) { observeUse(); provider.nextBytes(bytes); }
        @Override public IntStream ints() { return observed(provider.ints()); }
        @Override public IntStream ints(long size) { return observed(provider.ints(size)); }
        @Override public IntStream ints(int origin, int bound) { return observed(provider.ints(origin,bound)); }
        @Override public IntStream ints(long size, int origin, int bound) { return observed(provider.ints(size,origin,bound)); }
        @Override public LongStream longs() { return observed(provider.longs()); }
        @Override public LongStream longs(long size) { return observed(provider.longs(size)); }
        @Override public LongStream longs(long origin, long bound) { return observed(provider.longs(origin,bound)); }
        @Override public LongStream longs(long size, long origin, long bound) { return observed(provider.longs(size,origin,bound)); }
        @Override public DoubleStream doubles() { return observed(provider.doubles()); }
        @Override public DoubleStream doubles(long size) { return observed(provider.doubles(size)); }
        @Override public DoubleStream doubles(double origin, double bound) { return observed(provider.doubles(origin,bound)); }
        @Override public DoubleStream doubles(long size, double origin, double bound) { return observed(provider.doubles(size,origin,bound)); }
    }

    // Preserve the provider's stream implementation and SIZED optimizations:
    // e.g. Random.ints(3).count() must not consume three random values. A cached
    // stream observes its consuming state boundary, not its creation boundary.
    private static IntStream observed(IntStream stream) {
        boolean parallel = stream.isParallel();
        return StreamSupport.intStream(observed(stream.spliterator()),parallel).onClose(stream::close);
    }
    private static Spliterator.OfInt observed(Spliterator.OfInt values) {
        return new Spliterator.OfInt() {
            public boolean tryAdvance(IntConsumer action) { if (values.estimateSize()!=0) observeUse(); return values.tryAdvance(action); }
            public void forEachRemaining(IntConsumer action) { if (values.estimateSize()!=0) observeUse(); values.forEachRemaining(action); }
            public Spliterator.OfInt trySplit() { observeUse(); var split=values.trySplit(); return split==null?null:observed(split); }
            public long estimateSize() { return values.estimateSize(); }
            public int characteristics() { return values.characteristics(); }
            public Comparator<? super Integer> getComparator() { return values.getComparator(); }
        };
    }
    private static LongStream observed(LongStream stream) {
        boolean parallel = stream.isParallel();
        return StreamSupport.longStream(observed(stream.spliterator()),parallel).onClose(stream::close);
    }
    private static Spliterator.OfLong observed(Spliterator.OfLong values) {
        return new Spliterator.OfLong() {
            public boolean tryAdvance(LongConsumer action) { if (values.estimateSize()!=0) observeUse(); return values.tryAdvance(action); }
            public void forEachRemaining(LongConsumer action) { if (values.estimateSize()!=0) observeUse(); values.forEachRemaining(action); }
            public Spliterator.OfLong trySplit() { observeUse(); var split=values.trySplit(); return split==null?null:observed(split); }
            public long estimateSize() { return values.estimateSize(); }
            public int characteristics() { return values.characteristics(); }
            public Comparator<? super Long> getComparator() { return values.getComparator(); }
        };
    }
    private static DoubleStream observed(DoubleStream stream) {
        boolean parallel = stream.isParallel();
        return StreamSupport.doubleStream(observed(stream.spliterator()),parallel).onClose(stream::close);
    }
    private static Spliterator.OfDouble observed(Spliterator.OfDouble values) {
        return new Spliterator.OfDouble() {
            public boolean tryAdvance(DoubleConsumer action) { if (values.estimateSize()!=0) observeUse(); return values.tryAdvance(action); }
            public void forEachRemaining(DoubleConsumer action) { if (values.estimateSize()!=0) observeUse(); values.forEachRemaining(action); }
            public Spliterator.OfDouble trySplit() { observeUse(); var split=values.trySplit(); return split==null?null:observed(split); }
            public long estimateSize() { return values.estimateSize(); }
            public int characteristics() { return values.characteristics(); }
            public Comparator<? super Double> getComparator() { return values.getComparator(); }
        };
    }

    /**
     * <p>
     * percentTrue.<br>
     * If percent is like 30, then 30% of the time it will be true.
     * </p>
     * 
     * @param percent an int.
     * @return a boolean.
     */
    public static boolean percentTrue(final int percent) {
        return percent > MyRandom.getRandom().nextInt(100);
    }

    /**
     * Gets the random.
     * 
     * @return the random
     */
    public static Random getRandom() {
        return MyRandom.random;
    }

    /**
     * Sets the random provider. Used for deterministic simulation.
     * @param random the random
     */
    public static void setRandom(Random random) {
        observeUse();
        MyRandom.random = random instanceof ObservedRandom ? random : new ObservedRandom(random);
    }

    public static int[] splitIntoRandomGroups(final int value, final int numGroups) {
        int[] groups = new int[numGroups];

        for (int i = 0; i < value; i++) {
            groups[random.nextInt(numGroups)]++;
        }

        return groups;
    }
}
