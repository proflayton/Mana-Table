package forge.api;

import com.google.gson.JsonObject;
import forge.LobbyPlayer;
import forge.card.CardStateName;
import forge.card.MagicColor;
import forge.deck.CardPool;
import forge.game.GameEntityView;
import forge.game.GameState;
import forge.game.GameView;
import forge.game.card.CardView;
import forge.game.keyword.Keyword;
import forge.game.player.IHasIcon;
import forge.game.player.PlayerView;
import forge.game.spellability.SpellAbilityView;
import forge.game.zone.ZoneType;
import forge.gamemodes.net.NetworkGuiGame;
import forge.gui.control.PlaybackSpeed;
import forge.gui.interfaces.IGuiGame;
import forge.interfaces.IGameController;
import forge.item.PaperCard;
import forge.localinstance.skin.FSkinProp;
import forge.trackable.TrackableCollection;
import forge.util.FSerializableFunction;
import forge.util.ITriggerEvent;

import java.util.ArrayList;
import java.util.Collection;
import java.util.Collections;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Objects;
import java.util.Set;
import java.util.UUID;
import java.util.concurrent.CancellationException;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.ExecutionException;

final class NetworkMatchSession extends NetworkGuiGame implements ManaTableSession {
    private final String id = UUID.randomUUID().toString();
    private final Object gate = new Object();
    private final List<String> notices = new ArrayList<>();
    private final HashSet<CardView> selectable = new HashSet<>();
    private final HashSet<CardView> actionable = new HashSet<>();
    private final HashSet<GameEntityView> highlighted = new HashSet<>();
    private PlayerView viewer;
    private Map<String, Object> latest = map("id", id, "revision", 0L, "status", "starting",
            "message", "Waiting for Forge to open the network match...", "format", "Multiplayer",
            "players", List.of(), "stack", List.of(), "activity", List.of(), "notices", List.of());
    private long revision;
    private String error;
    private boolean opened;
    private String message = "Waiting for the game...";
    private String ok = "Continue";
    private String cancel = "Cancel";
    private boolean okEnabled;
    private boolean cancelEnabled;
    private CardView promptSource;
    private Pending pending;
    private long inputSequence = -1;
    private int inputOwnerId = -1;
    private String authoritativeInputType = "InputLockUI";
    private boolean inputActive;

    private static final class Pending {
        final String id;
        final String kind;
        final Map<String, CardView> cards = new LinkedHashMap<>();
        final CompletableFuture<JsonObject> response = new CompletableFuture<>();
        Map<String, Object> prompt;
        int size;
        int min;
        int max;
        int amount;
        boolean atLeastOne;
        boolean numeric;
        boolean maySkip;
        List<Integer> limits = List.of();

        Pending(String kind) {
            this(kind, UUID.randomUUID().toString());
        }

        Pending(String kind, String id) {
            this.kind = kind;
            this.id = id;
        }
    }

    @Override
    public IGuiGame gui() {
        return this;
    }

    @Override
    public Object platformDialog(String name, Object[] args) {
        return switch (name) {
            case "getChoices" -> choose((String) args[0], (int) args[1], (int) args[2], new ArrayList<>((Collection<?>) args[3]), false,
                    (FSerializableFunction<Object, String>) args[5]);
            case "chooseCard" -> first(choose(args[0] + "\n" + args[1], 1, 1, (List<?>) args[2], false, null));
            case "order" -> {
                List<?> choices = combine(args[4], args[5]);
                yield new IGuiGame.OrderResult<>(choose(args[0] + " · " + args[1], choices.size() - (int) args[3],
                        choices.size() - (int) args[2], choices, true, null), false);
            }
            case "showOptionDialog" -> {
                List<?> options = (List<?>) args[3];
                yield options.indexOf(first(choose(args[1] + "\n" + args[0], 1, 1, options, false, null)));
            }
            case "showInputDialog" -> inputText(args);
            default -> throw new UnsupportedOperationException("Network prompt is not implemented yet: " + name);
        };
    }

    @Override
    public void publishInput() {
        synchronized (gate) {
            boolean owned = viewer != null && inputOwnerId == viewer.getId();
            if (error != null || pending != null && !pending.kind.equals("input") || !inputActive || !owned
                    || viewer == null || getGameController(viewer) == null) {
                if (pending != null && pending.kind.equals("input") && (!inputActive || !owned)) {
                    pending = null;
                }
                publish();
                return;
            }
            Pending next = pending;
            String promptId = "network-input-" + inputSequence;
            if (next == null || !next.id.equals(promptId)) {
                next = new Pending("input", promptId);
                pending = next;
            }
            next.prompt = map("id", next.id, "kind", "input", "inputType", authoritativeInputType,
                    "message", message, "ok", ok, "cancel", cancel, "okEnabled", okEnabled,
                    "cancelEnabled", cancelEnabled, "canAttackAll", "InputAttack".equals(authoritativeInputType),
                    "playerChoices", acceptsPlayerChoice(authoritativeInputType) ? visiblePlayerIds() : List.of(),
                    "ownerId", inputOwnerId, "sequence", inputSequence, "canAutoPass", canAutoPass());
            if (promptSource != null && promptSource.canBeShownTo(viewer)) {
                next.prompt.put("sourceCard", cardState(promptSource, viewer, null));
                next.prompt.put("sourceZone", promptSource.getZone() == null ? "" : promptSource.getZone().name());
            }
            publish();
        }
    }

    private static boolean acceptsPlayerChoice(String inputType) {
        return inputType.contains("Target") || inputType.equals("InputSelectEntitiesFromList");
    }

    private boolean canAutoPass() {
        GameView view = getGameView();
        return "InputPassPriority".equals(authoritativeInputType) && okEnabled && view != null
                && view.getTurn() > 0 && !viewer.hasAvailableActions();
    }

    @Override
    public void fail(Throwable failure) {
        synchronized (gate) {
            error = failure.getMessage() == null ? failure.getClass().getSimpleName() : failure.getMessage();
            latest = map("id", id, "revision", ++revision, "status", "error", "error", error,
                    "format", "Multiplayer", "players", List.of(), "stack", List.of(), "activity", List.of(),
                    "notices", List.copyOf(notices));
        }
    }

    boolean opened() {
        return opened;
    }

    boolean finished() {
        synchronized (gate) {
            GameView view = getGameView();
            return error != null || view != null && view.isGameOver();
        }
    }

    void resetForLobby() {
        synchronized (gate) {
            opened = false;
            viewer = null;
            pending = null;
            inputSequence = -1;
            inputOwnerId = -1;
            authoritativeInputType = "InputLockUI";
            inputActive = false;
            message = "Waiting for the game...";
            ok = "Continue";
            cancel = "Cancel";
            okEnabled = false;
            cancelEnabled = false;
            promptSource = null;
            selectable.clear();
            actionable.clear();
            highlighted.clear();
        }
    }

    Map<String, Object> state() {
        synchronized (gate) {
            publish();
            return latest;
        }
    }

    Object action(JsonObject request) {
        synchronized (gate) {
            requireSession(request);
            Pending next = pending;
            if (next == null || !next.id.equals(string(request, "promptId"))) {
                throw new IllegalArgumentException("That choice has changed. Use the current prompt.");
            }
            if (!next.kind.equals("input")) {
                validateDialog(next, request);
                pending = null;
                markBusy();
                next.response.complete(request.deepCopy());
                return latest;
            }
            IGameController controller = getGameController(viewer);
            if (controller == null) {
                throw new IllegalStateException("The network player controller is not ready.");
            }
            String action = string(request, "action");
            if (action.equals("passIfNoResponse") && !Boolean.TRUE.equals(next.prompt.get("canAutoPass"))) {
                throw new IllegalArgumentException("This response window needs your decision");
            }
            CardView card = null;
            CardView attacker = null;
            PlayerView player = null;
            switch (action) {
                case "ok", "passIfNoResponse" -> {
                    if (!okEnabled) throw new IllegalArgumentException("Continue is not available");
                }
                case "cancel" -> {
                    if (!cancelEnabled) throw new IllegalArgumentException("Cancel is not available");
                }
                case "attackAll" -> { }
                case "card" -> {
                    card = next.cards.get(string(request, "key"));
                    if (card == null) throw new IllegalArgumentException("Card is not visible in this prompt");
                }
                case "player" -> player = player(request, "playerId");
                case "attack" -> {
                    attacker = next.cards.get(string(request, "attackerKey"));
                    if (request.has("defenderPlayerId")) player = player(request, "defenderPlayerId");
                    else card = next.cards.get(string(request, "defenderKey"));
                    if (attacker == null || card == null && player == null) throw new IllegalArgumentException("Invalid attack choice");
                }
                case "block" -> {
                    attacker = next.cards.get(string(request, "attackerKey"));
                    card = next.cards.get(string(request, "blockerKey"));
                    if (attacker == null || card == null) throw new IllegalArgumentException("Invalid block choice");
                }
                default -> throw new IllegalArgumentException("Unknown match action");
            }
            CardView chosenCard = card;
            CardView chosenAttacker = attacker;
            PlayerView chosenPlayer = player;
            // Forge owns the input lifecycle. In particular, Cancel can reset a combat
            // assignment while leaving the same InputAttack active, so keep the prompt
            // until setInputState reports that it ended or moved to a new sequence.
            publish();
            switch (action) {
                case "ok" -> controller.selectButtonOk();
                case "passIfNoResponse" -> controller.passPriority();
                case "cancel" -> controller.selectButtonCancel();
                case "attackAll" -> controller.alphaStrike();
                case "card" -> controller.selectCard(chosenCard, null, null);
                case "player" -> controller.selectPlayer(chosenPlayer, null);
                case "attack" -> {
                    if (chosenPlayer != null) controller.selectPlayer(chosenPlayer, null);
                    else controller.selectCard(chosenCard, null, null);
                    controller.selectCard(chosenAttacker, null, null);
                }
                case "block" -> {
                    controller.selectCard(chosenAttacker, null, null);
                    controller.selectCard(chosenCard, null, null);
                }
            }
            return latest;
        }
    }

    Object concede(JsonObject request) {
        synchronized (gate) {
            requireSession(request);
            IGameController controller = getGameController(viewer);
            if (controller == null) throw new IllegalStateException("The network player controller is not ready.");
            if (pending != null) pending.response.completeExceptionally(new CancellationException("Match conceded"));
            pending = null;
            controller.concede();
            markBusy();
            return latest;
        }
    }

    @Override
    public void setGameView(GameView gameView) {
        super.setGameView(gameView);
        publish();
    }

    @Override
    public void openView(TrackableCollection<PlayerView> myPlayers) {
        HeadlessPlatform.activate(this);
        setNetGame();
        if (myPlayers != null && !myPlayers.isEmpty()) {
            viewer = myPlayers.iterator().next();
        }
        opened = true;
        publish();
    }

    @Override
    public void setInputState(PlayerView owner, String inputType, long sequence, boolean active) {
        synchronized (gate) {
            if (sequence < inputSequence) return;
            boolean changed = sequence != inputSequence;
            inputSequence = sequence;
            inputOwnerId = owner == null ? -1 : owner.getId();
            authoritativeInputType = inputType == null || inputType.isBlank() ? "InputLockUI" : inputType;
            inputActive = active;
            if (changed) {
                message = active ? "Waiting for Forge's prompt..." : "Waiting for the next action...";
                ok = "";
                cancel = "";
                okEnabled = false;
                cancelEnabled = false;
                promptSource = null;
                selectable.clear();
                actionable.clear();
                highlighted.clear();
            }
            if (pending != null && pending.kind.equals("input")) pending = null;
        }
        publishInput();
    }

    @Override
    protected void updateCurrentPlayer(PlayerView player) {
        if (viewer == null && player != null) {
            viewer = player;
        }
        publish();
    }

    @Override
    public boolean isNetGame() {
        return true;
    }

    @Override
    public boolean isGamePaused() {
        return false;
    }

    @Override
    public PlaybackSpeed getGameSpeed() {
        return PlaybackSpeed.NORMAL;
    }

    @Override
    public boolean isUiSetToSkipPhase(PlayerView playerTurn, forge.game.phase.PhaseType phase) {
        return false;
    }

    @Override
    public void showPromptMessage(PlayerView playerView, String message, CardView card) {
        synchronized (gate) {
            if (viewer == null || playerView == null || playerView.equals(viewer)) {
                this.message = message;
                this.promptSource = card;
            }
        }
    }

    @Override
    public void updateButtons(PlayerView owner, String label1, String label2, boolean enable1, boolean enable2, boolean focus1) {
        synchronized (gate) {
            if (viewer == null || owner == null || owner.equals(viewer)) {
                ok = label1;
                cancel = label2;
                okEnabled = enable1;
                cancelEnabled = enable2;
            }
        }
    }

    @Override
    public void finishGame() {
        publish();
    }

    @Override
    public void showCombat() {
        publish();
    }

    @Override
    public void flashIncorrectAction() {
    }

    @Override
    public void alertUser() {
    }

    @Override
    public void handleGameEvent(forge.game.event.GameEvent event) {
        super.handleGameEvent(event);
        publish();
    }

    @Override
    public void updateRevealedCards(TrackableCollection<CardView> collection) {
        publish();
    }

    @Override
    public GameState getGamestate() {
        return null;
    }

    @Override
    public void setPanelSelection(CardView hostCard) {
    }

    @Override
    public SpellAbilityView getAbilityToPlay(CardView hostCard, List<SpellAbilityView> abilities, ITriggerEvent triggerEvent) {
        if (abilities == null || abilities.isEmpty()) return null;
        if (abilities.size() == 1 && (triggerEvent == null || !abilities.get(0).promptIfOnlyPossibleAbility())) return abilities.get(0);
        List<?> selected = choose("Choose an ability of " + label(hostCard), triggerEvent == null ? 0 : 1, 1,
                abilities, false, ability -> String.valueOf(ability));
        return selected.isEmpty() ? null : (SpellAbilityView) selected.get(0);
    }

    @Override
    public MagicColor.Color chooseColor(String message, CardView source, List<MagicColor.Color> colors) {
        List<?> selected = choose(message, 1, 1, colors, false,
                color -> ((MagicColor.Color) color).getTranslatedName());
        return (MagicColor.Color) selected.get(0);
    }

    @Override
    public Map<CardView, Integer> assignCombatDamage(CardView attacker, List<CardView> blockers, int damage, GameEntityView defender, boolean overrideOrder, boolean maySkip) {
        var choices = new ArrayList<Object>(blockers);
        if (defender != null) choices.add(defender);
        Pending next = allocation("Assign " + damage + " combat damage from " + label(attacker), choices, damage, false);
        next.maySkip = maySkip;
        next.prompt.put("maySkip", maySkip);
        JsonObject response = await(next);
        if (string(response, "action").equals("skip")) return null;
        var values = response.getAsJsonArray("values");
        var result = new LinkedHashMap<CardView, Integer>();
        for (int i = 0; i < blockers.size(); i++) if (values.get(i).getAsInt() > 0) result.put(blockers.get(i), values.get(i).getAsInt());
        if (choices.size() > blockers.size() && values.get(blockers.size()).getAsInt() > 0) result.put(null, values.get(blockers.size()).getAsInt());
        return result;
    }

    @Override
    public Map<Object, Integer> assignGenericAmount(CardView effectSource, Map<Object, Integer> target, int amount, boolean atLeastOne, String amountLabel) {
        var choices = new ArrayList<>(target.keySet());
        Pending next = allocation("Assign " + amount + " " + amountLabel, choices, amount, atLeastOne);
        next.limits = choices.stream().map(target::get).toList();
        next.prompt.put("limits", next.limits);
        var values = await(next).getAsJsonArray("values");
        var result = new LinkedHashMap<Object, Integer>();
        for (int i = 0; i < values.size(); i++) result.put(choices.get(i), values.get(i).getAsInt());
        return result;
    }

    @Override
    public void message(String message, String title) {
        synchronized (gate) {
            notices.add(message);
            while (notices.size() > 12) notices.remove(0);
            publish();
        }
    }

    @Override
    public void showErrorDialog(String message, String title) {
        showPromptMessage(viewer, message, null);
    }

    @Override
    public boolean showConfirmDialog(String message, String title, String yesButtonText, String noButtonText, boolean defaultYes) {
        return first(choose(title + "\n" + message, 1, 1, List.of(yesButtonText, noButtonText), false, null)).equals(yesButtonText);
    }

    @Override
    public int showOptionDialog(String message, String title, FSkinProp icon, List<String> options, int defaultOption) {
        return options.indexOf(first(choose(title + "\n" + message, 1, 1, options, false, null)));
    }

    @Override
    public String showInputDialog(String message, String title, FSkinProp icon, String initialInput, List<String> inputOptions, boolean isNumeric) {
        if (inputOptions != null) return String.valueOf(first(choose(title + "\n" + message, 1, 1, inputOptions, false, null)));
        Pending next = new Pending("text");
        next.numeric = isNumeric;
        next.prompt = map("id", next.id, "kind", "text", "message", message, "initial", initialInput, "numeric", isNumeric);
        return await(next).get("value").getAsString();
    }

    @Override
    public boolean confirm(CardView c, String question, boolean defaultIsYes, List<String> options) {
        return first(choose(question, 1, 1, options, false, null)).equals(options.get(0));
    }

    @Override
    public <T> List<T> getChoices(String message, int min, int max, List<T> choices, List<T> selected, FSerializableFunction<T, String> display) {
        return (List<T>) choose(message, min, max, choices, false, (FSerializableFunction<Object, String>) display);
    }

    @Override
    public Integer getInteger(String message, int min, int max, boolean sortDesc) {
        return number(message, min, max);
    }

    @Override
    public Integer getInteger(String message, int min, int max, int cutoff) {
        return number(message, min, max);
    }

    @Override
    public <T> T oneOrNone(String message, List<T> choices) {
        return (T) first(choose(message, 0, 1, choices, false, null));
    }

    @Override
    public <T> T one(String message, List<T> choices, FSerializableFunction<T, String> display) {
        return (T) first(choose(message, 1, 1, choices, false, (FSerializableFunction<Object, String>) display));
    }

    @Override
    public <T> void reveal(String message, List<T> items) {
        if (items != null && !items.isEmpty()) dialog("reveal", message, items, 0, 0, false, null);
    }

    @Override
    public <T> List<T> many(String title, String topCaption, int min, int max, List<T> sourceChoices, List<T> destChoices, CardView c) {
        return (List<T>) choose(title + " · " + topCaption, min, max, combine(sourceChoices, destChoices), false, null);
    }

    @Override
    public <T> OrderResult<T> order(String title, String top, int remainingObjectsMin, int remainingObjectsMax, List<T> sourceChoices, List<T> destChoices, CardView referenceCard, boolean sideboardingMode, boolean showRememberCheckbox) {
        List<T> choices = (List<T>) combine(sourceChoices, destChoices);
        return new OrderResult<>((List<T>) choose(title + " · " + top, choices.size() - remainingObjectsMax,
                choices.size() - remainingObjectsMin, choices, true, null), false);
    }

    @Override
    public <T> List<T> insertInList(String title, T newItem, List<T> oldItems) {
        var result = new ArrayList<T>();
        result.add(newItem);
        if (oldItems != null) {
            result.addAll(oldItems);
        }
        return (List<T>) choose(title, result.size(), result.size(), result, true, null);
    }

    @Override
    public List<PaperCard> sideboard(CardPool sideboard, CardPool main, String message) {
        return List.of();
    }

    @Override
    public GameEntityView chooseSingleEntityForEffect(String title, List<? extends GameEntityView> optionList, forge.game.player.DelayedReveal delayedReveal, boolean isOptional) {
        return (GameEntityView) first(choose(title, isOptional ? 0 : 1, 1, optionList, false, null));
    }

    @Override
    public List<GameEntityView> chooseEntitiesForEffect(String title, List<? extends GameEntityView> optionList, int min, int max, forge.game.player.DelayedReveal delayedReveal) {
        return (List<GameEntityView>) choose(title, min, max, optionList, false, null);
    }

    @Override
    public List<CardView> manipulateCardList(String title, Iterable<CardView> cards, Iterable<CardView> manipulable, boolean toTop, boolean toBottom, boolean toAnywhere) {
        var result = new ArrayList<CardView>();
        if (cards != null) {
            cards.forEach(result::add);
        }
        return (List<CardView>) choose(title, result.size(), result.size(), result, true, null);
    }

    @Override
    public void setCard(CardView card) {
    }

    @Override
    public void setPlayerAvatar(LobbyPlayer player, IHasIcon avatar) {
    }

    @Override
    public void setHighlighted(Iterable<GameEntityView> entities, boolean value) {
        synchronized (gate) {
            for (GameEntityView entity : entities) {
                if (value) highlighted.add(entity);
                else highlighted.remove(entity);
            }
        }
    }

    @Override
    public void setSelectables(Iterable<CardView> cards, int min, int max) {
        synchronized (gate) {
            selectable.clear();
            cards.forEach(selectable::add);
        }
    }

    @Override
    public void clearSelectables() {
        synchronized (gate) { selectable.clear(); }
    }

    @Override
    public boolean isSelecting() {
        synchronized (gate) { return !selectable.isEmpty(); }
    }

    @Override
    public void setWeaklySelectable(Iterable<CardView> cards) {
        synchronized (gate) {
            actionable.clear();
            cards.forEach(actionable::add);
        }
    }

    @Override
    public void clearWeaklySelectable() {
        synchronized (gate) { actionable.clear(); }
    }

    @Override
    public void setGamePause(boolean pause) {
    }

    @Override
    public void setGameSpeed(PlaybackSpeed gameSpeed) {
    }

    @Override
    public String getDayTime() {
        return null;
    }

    @Override
    public void updateDayTime(String daytime) {
    }

    @Override
    public void showWaitingTimer(PlayerView forPlayer, String waitingForPlayerName) {
    }

    @Override
    public void applyYieldUpdate(forge.gamemodes.match.YieldUpdate update) {
    }

    private void requireSession(JsonObject request) {
        if (!id.equals(string(request, "sessionId"))) throw new IllegalArgumentException("This match is no longer active");
        if (error != null) throw new IllegalStateException(error);
    }

    private PlayerView player(JsonObject request, String field) {
        int id = exactInt(string(request, field));
        GameView view = getGameView();
        if (view == null || view.getPlayers() == null) throw new IllegalArgumentException("Player is not available");
        for (PlayerView candidate : view.getPlayers()) if (candidate.getId() == id) return candidate;
        throw new IllegalArgumentException("Player is not available");
    }

    private List<Integer> visiblePlayerIds() {
        GameView view = getGameView();
        if (view == null || view.getPlayers() == null) return List.of();
        var result = new ArrayList<Integer>();
        for (PlayerView player : view.getPlayers()) result.add(player.getId());
        return result;
    }

    private void markBusy() {
        var next = new LinkedHashMap<>(latest);
        next.put("revision", ++revision);
        next.put("prompt", null);
        next.put("status", "resolving");
        latest = Collections.unmodifiableMap(next);
    }

    private List<?> choose(String title, int min, int max, List<?> choices, boolean ordered, FSerializableFunction<Object, String> display) {
        if (choices == null || choices.isEmpty()) return List.of();
        int actualMin = Math.max(0, min);
        int actualMax = Math.min(choices.size(), max < 0 ? choices.size() : max);
        JsonObject answer = dialog("choice", title, choices, actualMin, actualMax, ordered, display);
        var selected = new ArrayList<Object>();
        for (var index : answer.getAsJsonArray("choices")) selected.add(choices.get(index.getAsInt()));
        return selected;
    }

    private JsonObject dialog(String kind, String title, List<?> choices, int min, int max, boolean ordered,
                              FSerializableFunction<Object, String> display) {
        Pending next = new Pending(kind);
        next.size = choices.size();
        next.min = min;
        next.max = max;
        var items = new ArrayList<Object>();
        for (int i = 0; i < choices.size(); i++) {
            Object choice = choices.get(i);
            String choiceLabel = display == null ? label(choice) : display.apply(choice);
            var item = map("index", i, "label", choiceLabel);
            if (choice instanceof CardView card) item.put("card", choiceCard(card));
            items.add(item);
        }
        next.prompt = map("id", next.id, "kind", kind, "message", title, "choices", items,
                "min", min, "max", max, "ordered", ordered);
        return await(next);
    }

    private JsonObject await(Pending next) {
        synchronized (gate) {
            if (error != null) throw new CancellationException(error);
            pending = next;
            publish();
        }
        try {
            return next.response.get();
        } catch (InterruptedException exception) {
            Thread.currentThread().interrupt();
            throw new CancellationException("Match interrupted");
        } catch (ExecutionException exception) {
            throw new CancellationException(exception.getCause() == null ? "Choice cancelled" : exception.getCause().getMessage());
        }
    }

    private Integer number(String title, int min, int max) {
        Pending next = new Pending("number");
        next.min = min;
        next.max = max;
        next.prompt = map("id", next.id, "kind", "number", "message", title, "min", min, "max", max);
        return await(next).get("value").getAsInt();
    }

    private String inputText(Object[] args) {
        if (args[4] != null) return String.valueOf(first(choose(args[1] + "\n" + args[0], 1, 1, (List<?>) args[4], false, null)));
        Pending next = new Pending("text");
        next.numeric = (boolean) args[5];
        next.prompt = map("id", next.id, "kind", "text", "message", args[0], "initial", args[3], "numeric", args[5]);
        return await(next).get("value").getAsString();
    }

    private Pending allocation(String title, List<?> choices, int amount, boolean atLeastOne) {
        Pending next = new Pending("allocate");
        next.size = choices.size();
        next.amount = amount;
        next.atLeastOne = atLeastOne;
        var items = new ArrayList<Object>();
        for (int i = 0; i < choices.size(); i++) items.add(map("index", i, "label", label(choices.get(i))));
        next.prompt = map("id", next.id, "kind", "allocate", "message", title, "choices", items,
                "amount", amount, "atLeastOne", atLeastOne);
        return next;
    }

    private String label(Object item) {
        if (item instanceof CardView card) return card.canBeShownTo(viewer) && !card.isFaceDown() ? card.getCurrentState().getName() : "Face-down or hidden card";
        if (item instanceof PlayerView player) return player.equals(viewer) ? "You" : player.getName();
        if (item instanceof PaperCard card) return card.getName();
        return String.valueOf(item);
    }

    private Map<String, Object> choiceCard(CardView card) {
        boolean hidden = card.isFaceDown() || !card.canBeShownTo(viewer);
        var face = card.getCurrentState();
        var result = map("name", hidden ? "Face-down or hidden card" : face.getName(), "faceDown", hidden,
                "type", hidden ? "" : face.getType().toString(), "manaCost", hidden ? "" : face.getManaCost().toString(),
                "text", hidden ? "" : card.getText(), "power", hidden ? null : face.getPower(),
                "toughness", hidden ? null : face.getToughness());
        addCardFaces(result, card, !hidden);
        return result;
    }

    private static Object first(List<?> choices) {
        return choices == null || choices.isEmpty() ? null : choices.get(0);
    }

    private static List<?> combine(Object source, Object destination) {
        var combined = new ArrayList<Object>();
        if (source != null) combined.addAll((Collection<?>) source);
        if (destination != null) combined.addAll((Collection<?>) destination);
        return combined;
    }

    private static void validateDialog(Pending next, JsonObject request) {
        switch (next.kind) {
            case "choice" -> {
                var choices = request.getAsJsonArray("choices");
                if (choices == null || choices.size() < next.min || choices.size() > next.max) throw new IllegalArgumentException("Choose the required number of items");
                Set<Integer> selected = new LinkedHashSet<>();
                for (var item : choices) {
                    int index = exactInt(item.getAsString());
                    if (index < 0 || index >= next.size || !selected.add(index)) throw new IllegalArgumentException("Invalid or duplicate choice");
                }
            }
            case "number" -> {
                int value = exactInt(string(request, "value"));
                if (value < next.min || value > next.max) throw new IllegalArgumentException("Number is out of range");
            }
            case "text" -> {
                String value = string(request, "value");
                if (value.length() > 500) throw new IllegalArgumentException("Input is too long");
                if (next.numeric) exactInt(value);
            }
            case "allocate" -> {
                if (next.maySkip && string(request, "action").equals("skip")) return;
                var values = request.getAsJsonArray("values");
                if (values == null || values.size() != next.size) throw new IllegalArgumentException("Assign every amount");
                long total = 0;
                for (var value : values) {
                    int amount = exactInt(value.getAsString());
                    if (amount < (next.atLeastOne ? 1 : 0) || amount > next.amount) throw new IllegalArgumentException("Invalid amount");
                    total += amount;
                }
                if (total != next.amount) throw new IllegalArgumentException("The assigned amounts must total " + next.amount);
                for (int i = 0; i < next.limits.size(); i++) if (values.get(i).getAsInt() > next.limits.get(i)) throw new IllegalArgumentException("An amount exceeds its target's limit");
            }
            case "reveal" -> { }
            default -> throw new IllegalArgumentException("Unknown prompt");
        }
    }

    private static int exactInt(String value) {
        if (!value.matches("-?\\d{1,10}")) throw new IllegalArgumentException("Expected an integer");
        return Integer.parseInt(value);
    }

    private static String string(JsonObject json, String key) {
        return json.has(key) ? json.get(key).getAsString() : "";
    }

    private void publish() {
        synchronized (gate) {
            GameView view = getGameView();
            if (view == null) {
                return;
            }
            PlayerView localViewer = viewer;
            if (localViewer == null && view.getPlayers() != null && !view.getPlayers().isEmpty()) {
                localViewer = view.getPlayers().iterator().next();
                viewer = localViewer;
            }
            if (localViewer == null) {
                return;
            }

            if (pending != null) pending.cards.clear();

            var players = new ArrayList<Object>();
            var allPlayers = new ArrayList<PlayerView>();
            if (view.getPlayers() != null) {
                for (PlayerView player : view.getPlayers()) {
                    allPlayers.add(player);
                }
            }
            for (PlayerView player : allPlayers) {
                var zones = new ArrayList<Object>();
                for (ZoneType zone : List.of(ZoneType.Battlefield, ZoneType.Hand, ZoneType.Library, ZoneType.Graveyard, ZoneType.Exile, ZoneType.Command)) {
                    var visible = new ArrayList<Object>();
                    var cards = player.getCards(zone);
                    if (cards != null) {
                        for (CardView card : cards) {
                            if (card.canBeShownTo(localViewer)) {
                                visible.add(cardState(card, localViewer, pending));
                            }
                        }
                    }
                    zones.add(map("name", zone.name(), "count", player.getZoneSize(zone), "cards", visible));
                }
                var mana = new LinkedHashMap<String, Integer>();
                byte[] colors = {MagicColor.WHITE, MagicColor.BLUE, MagicColor.BLACK, MagicColor.RED, MagicColor.GREEN, MagicColor.COLORLESS};
                String[] labels = {"W", "U", "B", "R", "G", "C"};
                for (int i = 0; i < colors.length; i++) {
                    mana.put(labels[i], player.getMana(colors[i]));
                }
                players.add(map("id", player.getId(), "name", player.equals(localViewer) ? "You" : player.getName(),
                        "human", player.equals(localViewer), "seat", allPlayers.indexOf(player) + 1,
                        "eliminated", player.getHasLost(), "life", player.getLife(),
                        "priority", player.getHasPriority(), "mana", mana, "zones", zones,
                        "commanderDamage", List.of()));
            }

            var stack = new ArrayList<Object>();
            if (view.getStack() != null) {
                for (var item : view.getStack()) {
                    CardView source = item.getSourceCard();
                    boolean visible = source != null && source.canBeShownTo(localViewer) && !source.isFaceDown();
                    stack.add(map("id", item.getId(), "name", visible ? source.getCurrentState().getName() : "Face-down spell",
                            "text", visible ? item.getText() : "",
                            "card", source != null && source.canBeShownTo(localViewer) ? cardState(source, localViewer, null) : null,
                            "ability", item.isAbility(),
                            "controller", item.getActivatingPlayer() == null ? "" : item.getActivatingPlayer().getName()));
                }
            }

            boolean finished = view.isGameOver();
            String result = null;
            if (finished) {
                String winner = view.getWinningPlayerName();
                result = winner == null || winner.isBlank() ? "Draw"
                        : winner.equals(localViewer.getName()) ? "Victory" : "Defeat";
                pending = null;
            }
            latest = map("id", id, "revision", ++revision, "boardRevision", revision,
                    "format", "Multiplayer", "status", error != null ? "error" : finished ? "finished" : "playing",
                    "error", error, "playerCount", allPlayers.size(), "viewerId", localViewer.getId(),
                    "turn", view.getTurn(), "phase", view.getPhase() == null ? "Pregame" : view.getPhase().nameForUi,
                    "phaseKey", view.getPhase() == null ? "PREGAME" : view.getPhase().name(),
                    "activePlayerId", view.getPlayerTurn() == null ? null : view.getPlayerTurn().getId(),
                    "players", players, "stack", stack, "combat", null, "prompt", pending == null ? null : pending.prompt, "result", result,
                    "notices", List.copyOf(notices), "activity", List.of());
        }
    }

    private Map<String, Object> cardState(CardView card, PlayerView localViewer, Pending prompt) {
        boolean hidden = card.isFaceDown() || !card.canBeShownTo(localViewer);
        var face = card.getCurrentState();
        var counters = new LinkedHashMap<String, Integer>();
        if (card.getCounters() != null) {
            for (var entry : card.getCounters().entrySet()) {
                counters.put(entry.getElement().getName(), entry.getCount());
            }
        }
        String key = prompt == null ? "" : prompt.id + ":c" + prompt.cards.size();
        if (prompt != null) prompt.cards.put(key, card);
        var result = map("key", key, "visualId", String.valueOf(card.getId()), "combatId", String.valueOf(card.getId()),
                "name", hidden ? "Face-down card" : face.getName(),
                "type", hidden ? "" : face.getType().toString(),
                "manaCost", hidden ? "" : face.getManaCost().toString(),
                "power", hidden ? null : face.getPower(), "toughness", hidden ? null : face.getToughness(),
                "text", hidden ? "" : card.getText(), "tapped", card.isTapped(), "sick", card.isSick(),
                "damage", card.getDamage(), "attacking", card.isAttacking(), "blocking", card.isBlocking(),
                "counters", counters, "defenderId", null, "defender", null,
                "selectable", selectable.contains(card) || actionable.contains(card),
                "highlighted", highlighted.contains(card), "faceDown", hidden,
                "combatKeywords", hidden ? List.of() : List.of(Keyword.FLYING, Keyword.REACH, Keyword.TRAMPLE,
                        Keyword.FIRST_STRIKE, Keyword.DOUBLE_STRIKE, Keyword.DEATHTOUCH, Keyword.LIFELINK,
                        Keyword.MENACE, Keyword.VIGILANCE, Keyword.INDESTRUCTIBLE)
                        .stream().filter(face::hasKeyword).map(keyword -> keyword.name().toLowerCase(Locale.ROOT).replace('_', ' ')).toList());
        addCardFaces(result, card, !hidden && card.canBeShownTo(localViewer));
        return result;
    }

    private static void addCardFaces(Map<String, Object> result, CardView card, boolean visible) {
        if (!visible || !card.isDoubleFacedCard() || !card.hasAlternateState()) {
            return;
        }
        var current = card.getCurrentState();
        var other = card.getAlternateState();
        boolean back = current.getState() == CardStateName.Backside || current.getState() == CardStateName.Meld;
        var front = back ? other : current;
        String artName = Objects.requireNonNullElse(front.getOracleName(), "");
        if (artName.isEmpty()) {
            artName = front.getName();
        }
        result.put("artName", artName);
        result.put("artFace", back ? "back" : "front");
        result.put("otherFace", map("name", other.getName(), "manaCost", other.getManaCost().toString(),
                "type", other.getType().toString(), "oracleText", other.getOracleText(),
                "power", other.getPower(), "toughness", other.getToughness(),
                "artName", artName, "artFace", back ? "front" : "back"));
    }

    private static LinkedHashMap<String, Object> map(Object... fields) {
        var result = new LinkedHashMap<String, Object>();
        for (int i = 0; i < fields.length; i += 2) {
            result.put((String) fields[i], fields[i + 1]);
        }
        return result;
    }
}
