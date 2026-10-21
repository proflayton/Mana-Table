package forge.game.keyword;

import java.io.Serializable;
import java.util.Collection;
import java.util.ArrayList;
import java.util.Iterator;
import java.util.List;
import forge.game.PublicState;

import com.google.common.collect.Multimap;
import com.google.common.collect.MultimapBuilder;

public class KeywordCollectionView implements Iterable<KeywordView>, Serializable {
    private static final long serialVersionUID = 1L;
    public static KeywordCollectionView EMPTY = new KeywordCollectionView(List.of());
    // don't use enumKeys it causes a slow down
    private final Multimap<Keyword, KeywordView> map = MultimapBuilder.hashKeys()
            .linkedHashSetValues().build();
    private final List<PublicState> publicStates;

    public KeywordCollectionView(Iterable<KeywordView> list) {
        var effective = new ArrayList<KeywordView>();
        for (KeywordView k : list) {
            effective.add(k);
            map.put(k.keyword(), k);
        }
        // Copy before the view's set collapses equal keywords: two identical
        // nonredundant Ward abilities still trigger and pay separately.
        publicStates = PublicKeywordStates.snapshot(effective);
    }

    public List<PublicState> getPublicStates() {
        // Older serialized desktop views predate this additive copied field.
        return publicStates == null ? PublicKeywordStates.snapshot(map.values()) : publicStates;
    }

    public boolean contains(Keyword keyword) {
        return map.containsKey(keyword);
    }

    public boolean isEmpty() {
        return map.isEmpty();
    }

    public int size() {
        return map.values().size();
    }

    public Collection<KeywordView> getValues() {
        return map.values();
    }

    public Collection<KeywordView> getValues(final Keyword keyword) {
        return map.get(keyword);
    }

    @Override
    public Iterator<KeywordView> iterator() {
        return this.map.values().iterator();
    }

    @Override 
    public boolean equals(Object o) {
        if (this == o) return true;
        return o instanceof KeywordCollectionView other && map.equals(other.map)
                && getPublicStates().equals(other.getPublicStates());
    }
    @Override
    public int hashCode() { return 31 * map.hashCode() + getPublicStates().hashCode(); }

    @Override
    public String toString() { return getValues().toString(); }
}
