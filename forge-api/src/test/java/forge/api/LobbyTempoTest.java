package forge.api;

import com.google.gson.*;
import forge.StaticData;
import forge.deck.Deck;
import forge.deck.DeckSection;
import org.testng.annotations.Test;
import java.nio.file.Path;
import java.util.Map;
import static org.testng.Assert.*;

public class LobbyTempoTest {
    private static final Gson JSON = new Gson();
    private static JsonObject object(Object value) { return JSON.toJsonTree(value).getAsJsonObject(); }
    private static JsonObject settings(JsonObject lobby, String preset, String expiry) {
        return object(Map.of("tableId", lobby.get("tableId").getAsString(), "preset", preset, "expiry", expiry));
    }
    private static JsonObject ready(JsonObject lobby, String revision) {
        return object(Map.of("tableId", lobby.get("tableId").getAsString(), "ready", true, "tempoRevision", revision));
    }
    @Test(timeOut = 45000) public void timingRequiresHostSetupAuthorityAndExplicitReadyConsentFromEveryHuman() {
        Path resources = Path.of("../forge-gui/res");
        if (StaticData.instance() == null) EngineResources.load(resources);
        var host = new SyncLobby(resources, ApiTestWorkspace.profile("tempo-lobby"));
        SyncLobby guest = null;
        try {
            var original = object(host.host("Commander", 2, false, "local")); guest = host.localGuest();
            var deck = new Deck("Tempo deck"); deck.getMain().add(StaticData.instance().getCommonCards().getCard("Forest"), 99);
            deck.getOrCreate(DeckSection.Commander).add(StaticData.instance().getCommonCards().getCard("Rhys the Redeemed"));
            host.selectDeck(deck, "Commander"); guest.selectDeck(deck, "Commander");
            host.ready(true); guest.ready(true); assertTrue(object(host.state()).get("canStart").getAsBoolean());
            SyncLobby visitor = guest;
            assertThrows(IllegalArgumentException.class, () -> visitor.configureTempo(settings(original, "fast", "pass")));
            assertThrows(IllegalArgumentException.class, () -> host.configureTempo(settings(original, "invalid", "pass")));
            assertTrue(object(host.state()).get("canStart").getAsBoolean(), "Rejected policy changes leave readiness intact");
            var timed = object(host.configureTempo(settings(original, "fast", "pass")));
            assertFalse(timed.get("canStart").getAsBoolean());
            for (var seat : timed.getAsJsonArray("slots")) assertFalse(seat.getAsJsonObject().get("ready").getAsBoolean());
            assertThrows(IllegalArgumentException.class, () -> host.ready(true));
            assertThrows(IllegalArgumentException.class, () -> visitor.ready(ready(timed, "old-policy")));
            String revision = timed.getAsJsonObject("tempo").get("revision").getAsString();
            host.ready(ready(timed, revision)); guest.ready(ready(timed, revision));
            assertTrue(object(host.state()).get("canStart").getAsBoolean());
            var replacement = object(host.configureTempo(settings(timed, "standard", "warn")));
            assertThrows(IllegalArgumentException.class, () -> visitor.ready(ready(replacement, revision)));
            String nextRevision = replacement.getAsJsonObject("tempo").get("revision").getAsString();
            host.ready(ready(replacement, nextRevision)); guest.ready(ready(replacement, nextRevision)); host.start();
            assertThrows(IllegalArgumentException.class, () -> host.configureTempo(settings(replacement, "off", "pass")));
            assertEquals(object(host.match().state()).getAsJsonObject("tempo").getAsJsonObject("settings").get("seconds").getAsInt(), 60);
        } finally { if (guest != null) guest.close(); host.close(); }
    }
}
