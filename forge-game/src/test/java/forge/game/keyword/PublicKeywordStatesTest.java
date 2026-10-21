package forge.game.keyword;

import org.testng.annotations.Test;

import java.io.ByteArrayInputStream;
import java.io.ByteArrayOutputStream;
import java.io.ObjectInputStream;
import java.io.ObjectOutputStream;
import java.util.List;

import static org.testng.Assert.*;

public class PublicKeywordStatesTest {
    @Test public void qualifiedAndRepeatedKeywordsProduceCopiedPublicPresentationWithoutAnAllowlist() {
        var keywords = new KeywordCollection();
        for (String keyword : List.of("Shroud", "Shadow", "Hexproof:Red", "Protection:Blue", "Ward:2", "Ward:2")) keywords.add(keyword);
        var view = keywords.getView();
        var states = view.getPublicStates();
        assertTrue(states.stream().anyMatch(state -> state.key().equals("shroud") && state.label().equals("Shroud")));
        assertTrue(states.stream().anyMatch(state -> state.key().equals("shadow")));
        assertTrue(states.stream().anyMatch(state -> state.key().equals("hexproof") && state.label().equals("Hexproof from red")));
        assertTrue(states.stream().anyMatch(state -> state.key().equals("protection") && state.label().equals("Protection from blue")));
        var ward = states.stream().filter(state -> state.key().equals("ward")).findFirst().orElseThrow();
        assertEquals(ward.value(), Integer.valueOf(2)); assertTrue(ward.label().contains("2")); assertTrue(ward.detail().contains("2"));
        expectThrows(UnsupportedOperationException.class, () -> states.clear());
        keywords.clear(); view.getValues().clear();
        assertEquals(states.size(), 5, "Captured public descriptors do not follow either mutable source collection");
    }

    @Test public void multiplicityParticipatesInViewEqualityAndNeverSerializesRawOriginals() {
        var one = new KeywordCollection(); one.add("Ward:2");
        var two = new KeywordCollection(); two.add("Ward:2"); two.add("Ward:2");
        assertNotEquals(one.getView(), two.getView(), "Trackable views must publish when only the instance count changes");
        assertNotEquals(one.getView().hashCode(), two.getView().hashCode());
        var future = new DefaultKeywordView("AB$ SecretHelper | Defined$ PrivateCard", Keyword.UNDEFINED,
                "Future public mechanic", "A human-readable public explanation.");
        var states = PublicKeywordStates.snapshot(List.of(future));
        assertEquals(states.get(0).label(), "Future public mechanic");
        assertEquals(states.get(0).key(), "custom");
        assertFalse(states.toString().contains("SecretHelper")); assertFalse(states.toString().contains("PrivateCard"));
        assertEquals(states, PublicKeywordStates.snapshot(List.of(future)), "Presentation ids are stable across copied observations");
    }

    @Test public void desktopViewSerializationRetainsImmutableQualifiedStatesAndRepeatedInstances() throws Exception {
        var keywords = new KeywordCollection();
        keywords.add("Protection:Blue"); keywords.add("Ward:2"); keywords.add("Ward:2");
        var original = keywords.getView();
        var bytes = new ByteArrayOutputStream();
        try (var output = new ObjectOutputStream(bytes)) { output.writeObject(original); }
        KeywordCollectionView restored;
        try (var input = new ObjectInputStream(new ByteArrayInputStream(bytes.toByteArray()))) {
            restored = (KeywordCollectionView) input.readObject();
        }
        assertEquals(restored.getPublicStates(), original.getPublicStates());
        assertEquals(restored.getPublicStates().stream().filter(state -> state.key().equals("ward")).findFirst().orElseThrow().value(), Integer.valueOf(2));
        expectThrows(UnsupportedOperationException.class, () -> restored.getPublicStates().clear());
    }
}
