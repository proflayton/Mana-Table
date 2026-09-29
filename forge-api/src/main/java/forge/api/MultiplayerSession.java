package forge.api;

import forge.gamemodes.match.GameLobby;
import forge.gamemodes.match.LobbySlot;
import forge.gamemodes.match.LobbySlotType;
import forge.deck.Deck;
import forge.deck.DeckFormat;
import forge.game.GameType;
import forge.gamemodes.net.ChatMessage;
import forge.gamemodes.net.IOnlineChatInterface;
import forge.gamemodes.net.IOnlineLobby;
import forge.gamemodes.net.IRemote;
import forge.gamemodes.net.NetConnectUtil;
import forge.gamemodes.net.client.FGameClient;
import forge.gamemodes.net.client.ClientGameLobby;
import forge.gamemodes.net.event.UpdateLobbyPlayerEvent;
import forge.gamemodes.net.server.FServerManager;
import forge.gui.interfaces.ILobbyView;
import forge.interfaces.IPlayerChangeListener;
import forge.localinstance.properties.ForgeConstants;
import forge.localinstance.properties.ForgeNetPreferences;
import forge.model.FModel;

import java.nio.file.Path;
import java.nio.file.Files;
import java.io.IOException;
import java.net.InetSocketAddress;
import java.net.ServerSocket;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Objects;

final class MultiplayerSession {
    private final Path resources;
    private final Path profile;
    private final OnlineLobby onlineLobby = new OnlineLobby();
    private final ChatAdapter chat = new ChatAdapter();
    private GameLobby lobby;
    private FGameClient client;
    private NetworkMatchSession match;
    private String mode = "idle";
    private String status = "Not connected.";
    private String error;
    private boolean initialized;
    private int localSlot = 0;

    MultiplayerSession(Path resources, Path profile) {
        this.resources = resources;
        this.profile = profile;
    }

    synchronized Object host(String format, int playerCount) {
        initializePlatform();
        closeExistingConnection();
        mode = "hosting";
        status = "Starting server...";
        error = null;
        try {
            ForgeNetPreferences preferences = FModel.getNetPreferences();
            preferences.setPref(ForgeNetPreferences.FNetPref.NET_PORT, String.valueOf(availablePort()));
            preferences.save();
            ChatMessage result = NetConnectUtil.host(onlineLobby, chat);
            addMessage(result);
            status = result.getMessage();
            applyConfiguration(format, playerCount);
        } catch (Exception ex) {
            mode = "error";
            error = ex.getMessage() == null ? ex.getClass().getSimpleName() : ex.getMessage();
            status = error;
            closeExistingConnection();
        }
        return state();
    }

    synchronized Object join(String address) {
        initializePlatform();
        closeExistingConnection();
        match = new NetworkMatchSession();
        HeadlessPlatform.activate(match);
        mode = "joining";
        status = "Connecting to " + address + "...";
        error = null;
        ChatMessage result = NetConnectUtil.join(address, onlineLobby, chat);
        addMessage(result);
        String message = result.getMessage();
        if (Objects.equals(message, ForgeConstants.INVALID_HOST_COMMAND)
                || Objects.equals(message, ForgeConstants.CLOSE_CONN_COMMAND)
                || message != null && message.startsWith(ForgeConstants.CONN_ERROR_PREFIX)) {
            mode = "error";
            error = message != null && message.startsWith(ForgeConstants.CONN_ERROR_PREFIX)
                    ? message.substring(ForgeConstants.CONN_ERROR_PREFIX.length()) : message;
            status = error == null || error.isBlank() ? "Connection failed." : error;
        } else {
            mode = "joined";
            status = message;
        }
        return state();
    }

    synchronized Object configure(String format, int playerCount) {
        requireLobby();
        if (!FServerManager.getInstance().isHosting()) {
            throw new IllegalStateException("Only the host can change lobby settings.");
        }
        applyConfiguration(format, playerCount);
        status = lobbyFormat() + " lobby configured for " + lobby.getNumberOfSlots() + " players.";
        return state();
    }

    synchronized Object selectDeck(Deck deck, String deckFormat) {
        requireLobby();
        String required = lobbyFormat();
        if (!required.equals(deckFormat)) {
            throw new IllegalArgumentException(required.equals("Commander")
                    ? "Choose a saved Commander deck for this lobby."
                    : "Choose a saved Constructed deck for this lobby.");
        }
        String problem = (required.equals("Commander") ? DeckFormat.Commander : DeckFormat.Constructed)
                .getDeckConformanceProblem(deck);
        if (problem != null) {
            throw new IllegalArgumentException(deck.getName() + " is not legal for " + required + ": " + problem);
        }
        sendLocalUpdate(UpdateLobbyPlayerEvent.deckUpdate(deck));
        sendLocalUpdate(UpdateLobbyPlayerEvent.setDeckSchemePlaneVanguard(deck.getName(), null, null, null));
        status = "Selected " + deck.getName() + ".";
        return state();
    }

    synchronized Object ready(boolean ready) {
        requireLobby();
        sendLocalUpdate(UpdateLobbyPlayerEvent.isReadyUpdate(ready));
        status = ready ? "Ready." : "Not ready.";
        return state();
    }

    synchronized Object start() {
        requireLobby();
        if (!FServerManager.getInstance().isHosting()) {
            throw new IllegalStateException("Only the host can start the game.");
        }
        for (int i = 0; i < lobby.getNumberOfSlots(); i++) {
            if (lobby.getSlot(i).getType() == LobbySlotType.OPEN) {
                throw new IllegalStateException("Waiting for all " + lobby.getNumberOfSlots() + " players to join.");
            }
        }
        LobbySlot unready = lobby.findFirstUnreadySlot();
        if (unready != null) {
            throw new IllegalStateException((unready.getName() == null ? "A player" : unready.getName()) + " is not ready.");
        }
        try {
            match = new NetworkMatchSession();
            HeadlessPlatform.activate(match);
            Runnable start = lobby.startGame();
            if (start == null) {
                throw new IllegalStateException("Forge did not start the game. Check that every player chose a deck.");
            }
            start.run();
            mode = "starting";
            status = "Starting game...";
        } catch (Throwable ex) {
            mode = "error";
            error = ex.getMessage() == null ? ex.getClass().getSimpleName() : ex.getMessage();
            status = error;
        }
        return state();
    }

    synchronized Object state() {
        Map<String, Object> result = new LinkedHashMap<>();
        result.put("mode", mode);
        result.put("status", status);
        result.put("error", error);
        result.put("hosting", initialized && FServerManager.getInstance().isHosting());
        result.put("matchActive", match != null && match.opened());
        result.put("format", lobbyFormat());
        result.put("playerCount", lobby == null ? 2 : lobby.getNumberOfSlots());
        result.put("maxPlayers", lobbyFormat().equals("Commander") ? 6 : 2);
        result.put("addresses", addresses());
        result.put("slots", slots());
        result.put("messages", List.copyOf(chat.messages));
        return result;
    }

    synchronized Object close() {
        closeExistingConnection();
        HeadlessPlatform.activate(null);
        lobby = null;
        match = null;
        mode = "idle";
        status = "Not connected.";
        error = null;
        chat.messages.clear();
        return state();
    }

    synchronized Object returnToLobby() {
        requireLobby();
        if (match == null || !match.finished()) {
            throw new IllegalStateException("The network match is still active.");
        }
        boolean hosting = FServerManager.getInstance().isHosting();
        match.resetForLobby();
        if (hosting) {
            for (int i = 0; i < lobby.getNumberOfSlots(); i++) {
                LobbySlot slot = lobby.getSlot(i);
                if (slot.getType() != LobbySlotType.OPEN) slot.setIsReady(false);
            }
            FServerManager.getInstance().updateLobbyState();
            mode = "hosting";
            status = "Game finished. Choose decks and ready up for another game.";
        } else {
            sendLocalUpdate(UpdateLobbyPlayerEvent.isReadyUpdate(false));
            mode = "joined";
            status = "Back in the lobby. Ready up when you want to play again.";
        }
        HeadlessPlatform.activate(null);
        if (hosting) match = null;
        error = null;
        return state();
    }

    NetworkMatchSession match() {
        return match;
    }

    boolean hasMatch() {
        return match != null && match.opened();
    }

    private void initializePlatform() {
        try {
            Files.createDirectories(profile.resolve("engine-profile").resolve("preferences"));
        } catch (IOException ex) {
            throw new IllegalStateException("Could not create Forge network preferences directory", ex);
        }
        HeadlessPlatform.initialize(resources, profile);
        FModel.getNetPreferences().setPref(ForgeNetPreferences.FNetPref.UPnP, "NEVER");
        FModel.getNetPreferences().save();
        initialized = true;
    }

    private static int availablePort() throws IOException {
        try (ServerSocket socket = new ServerSocket()) {
            socket.setReuseAddress(false);
            socket.bind(new InetSocketAddress(0));
            return socket.getLocalPort();
        }
    }

    private void closeExistingConnection() {
        if (client != null) {
            client.close();
            client = null;
        }
        if (!initialized) {
            return;
        }
        FServerManager server = FServerManager.getInstance();
        if (server.isHosting()) {
            server.stopServer();
        }
    }

    private List<Map<String, Object>> addresses() {
        if (!initialized || !FServerManager.getInstance().isHosting()) {
            return List.of();
        }
        NetConnectUtil.ServerAddressList list = NetConnectUtil.collectHostedServerAddresses();
        List<Map<String, Object>> result = new ArrayList<>();
        for (int i = 0; i < list.urls.size(); i++) {
            result.add(Map.of("label", list.labels.get(i), "url", list.urls.get(i), "preferred", i == list.starIndex));
        }
        return result;
    }

    private List<Map<String, Object>> slots() {
        if (lobby == null) {
            return List.of();
        }
        int currentLocalSlot = localSlot();
        List<Map<String, Object>> result = new ArrayList<>();
        for (int i = 0; i < lobby.getNumberOfSlots(); i++) {
            LobbySlot slot = lobby.getSlot(i);
            Map<String, Object> row = new LinkedHashMap<>();
            row.put("index", i);
            row.put("type", slot.getType().name());
            row.put("name", slot.getName());
            row.put("ready", slot.isReady());
            row.put("local", i == currentLocalSlot);
            row.put("team", slot.getTeam());
            row.put("deck", slot.getDeckName() != null ? slot.getDeckName() : slot.getDeck() == null ? null : slot.getDeck().getName());
            result.add(row);
        }
        return result;
    }

    private void requireLobby() {
        if (lobby == null) {
            throw new IllegalStateException("Join or host a lobby first.");
        }
    }

    private void applyConfiguration(String format, int playerCount) {
        requireLobby();
        if (!List.of("Constructed", "Commander").contains(format)) {
            throw new IllegalArgumentException("Multiplayer supports Standard and Commander.");
        }
        int target = format.equals("Commander") ? playerCount : 2;
        if (target < 2 || target > 6) {
            throw new IllegalArgumentException("Commander lobbies support 2 to 6 players.");
        }
        boolean formatChanged = !format.equals(lobbyFormat());
        while (lobby.getNumberOfSlots() > target) {
            int index = lobby.getNumberOfSlots() - 1;
            if (lobby.getSlot(index).getType() != LobbySlotType.OPEN) {
                throw new IllegalStateException("A connected player occupies a seat you are trying to remove.");
            }
            lobby.removeSlot(index);
        }
        while (lobby.getNumberOfSlots() < target) lobby.addSlot();
        if (format.equals("Commander")) {
            lobby.applyVariant(GameType.Commander);
        } else {
            lobby.removeVariant(GameType.Commander);
            lobby.setGameType(GameType.Constructed);
        }
        for (int i = 0; i < lobby.getNumberOfSlots(); i++) {
            LobbySlot slot = lobby.getSlot(i);
            if (slot.getType() != LobbySlotType.OPEN) slot.setIsReady(false);
            if (formatChanged) {
                slot.setDeck(null);
                slot.setDeckName(null);
            }
        }
        FServerManager.getInstance().updateLobbyState();
    }

    private String lobbyFormat() {
        return lobby != null && (lobby.getGameType() == GameType.Commander || lobby.hasVariant(GameType.Commander))
                ? "Commander" : "Constructed";
    }

    private void sendLocalUpdate(UpdateLobbyPlayerEvent event) {
        int slot = localSlot();
        if (onlineLobby.view.playerChangeListener == null) {
            throw new IllegalStateException("Lobby is still connecting.");
        }
        onlineLobby.view.playerChangeListener.update(slot, event);
        if (FServerManager.getInstance().isHosting()) {
            FServerManager.getInstance().updateLobbyState();
        }
    }

    private int localSlot() {
        if (lobby == null) {
            return 0;
        }
        if (FServerManager.getInstance().isHosting()) {
            localSlot = 0;
            return localSlot;
        }
        if (lobby instanceof ClientGameLobby clientLobby && clientLobby.getLocalPlayer() >= 0) {
            localSlot = clientLobby.getLocalPlayer();
            return localSlot;
        }
        if (localSlot >= 0 && localSlot < lobby.getNumberOfSlots()) {
            LobbySlot slot = lobby.getSlot(localSlot);
            if (slot != null && slot.getType() == LobbySlotType.REMOTE) {
                return localSlot;
            }
        }
        for (int i = 0; i < lobby.getNumberOfSlots(); i++) {
            LobbySlot slot = lobby.getSlot(i);
            if (slot != null && slot.getType() == LobbySlotType.REMOTE) {
                localSlot = i;
                return localSlot;
            }
        }
        localSlot = 0;
        return localSlot;
    }

    private void addMessage(ChatMessage message) {
        chat.addMessage(message);
    }

    private final class OnlineLobby implements IOnlineLobby {
        private final LobbyView view = new LobbyView();

        @Override
        public ILobbyView setLobby(GameLobby nextLobby) {
            lobby = nextLobby;
            localSlot = FServerManager.getInstance().isHosting() ? 0 : -1;
            return view;
        }

        @Override
        public void setClient(FGameClient nextClient) {
            client = nextClient;
        }

        @Override
        public void closeConn(String message) {
            mode = "error";
            error = message;
            status = message;
            closeExistingConnection();
        }
    }

    private final class LobbyView implements ILobbyView {
        private IPlayerChangeListener playerChangeListener;

        @Override
        public void setPlayerChangeListener(IPlayerChangeListener listener) {
            playerChangeListener = listener;
        }

        @Override
        public void update(boolean fullUpdate) {
            if (lobby != null && !"error".equals(mode)) {
                status = FServerManager.getInstance().isHosting() ? "Hosting lobby." : "Connected to lobby.";
            }
        }

        @Override
        public void update(int slot, forge.gamemodes.match.LobbySlotType type) {
            if (playerChangeListener != null && lobby != null) {
                playerChangeListener.update(slot, forge.gamemodes.net.event.UpdateLobbyPlayerEvent.create(
                        type, lobby.getSlot(slot).getName(), lobby.getSlot(slot).getAvatarIndex(), lobby.getSlot(slot).getSleeveIndex(),
                        lobby.getSlot(slot).getTeam(), lobby.getSlot(slot).isArchenemy(), lobby.getSlot(slot).isDevMode(),
                        lobby.getSlot(slot).getAiOptions(), lobby.getSlot(slot).getAiProfile()));
            }
        }
    }

    private final class ChatAdapter implements IOnlineChatInterface {
        private final List<Map<String, Object>> messages = new ArrayList<>();
        private IRemote remote;

        @Override
        public void setGameClient(IRemote nextRemote) {
            remote = nextRemote;
        }

        @Override
        public void addMessage(ChatMessage message) {
            Map<String, Object> row = new LinkedHashMap<>();
            row.put("source", message.getSource());
            row.put("message", message.getMessage());
            row.put("type", message.getType().name());
            row.put("timestamp", message.getTimestamp());
            row.put("formatted", message.getFormattedMessage());
            messages.add(row);
            if (messages.size() > 100) {
                messages.remove(0);
            }
        }
    }
}
