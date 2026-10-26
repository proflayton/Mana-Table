package forge.api;

import forge.StaticData;
import forge.ai.AiProfileUtil;
import forge.gamemodes.match.HostedMatch;
import forge.gui.GuiBase;
import forge.gui.interfaces.IGuiBase;
import forge.localinstance.properties.ForgePreferences.FPref;
import forge.model.FModel;
import forge.game.Game;
import forge.game.GameAction;

import java.io.File;
import java.lang.reflect.InvocationHandler;
import java.lang.reflect.Proxy;
import java.lang.ref.WeakReference;
import java.nio.file.Path;
import java.util.Map;
import java.util.WeakHashMap;
import java.util.concurrent.*;

/** Portable GUI services for the shared human controller, without Swing windows. */
final class HeadlessPlatform {
    private static final ExecutorService UI = Executors.newSingleThreadExecutor(task -> {
        Thread thread = new Thread(task, "Mana Table input");
        thread.setDaemon(true);
        return thread;
    });
    private static volatile Thread uiThread;
    private static volatile ManaTableSession active;
    private static final Map<Game, WeakReference<ManaTableSession>> owners = new WeakHashMap<>();
    private static boolean initialized;

    static synchronized void initialize(Path resources, Path profile) {
        if (initialized) return;
        System.setProperty("forge.resources", resources.toAbsolutePath().normalize() + File.separator);
        System.setProperty("forge.userDir", profile.resolve("engine-profile").toAbsolutePath().toString());
        System.setProperty("forge.cacheDir", profile.resolve("engine-cache").toAbsolutePath().toString());
        GuiBase.setUsingAppDirectory(true);
        var base = (IGuiBase) Proxy.newProxyInstance(IGuiBase.class.getClassLoader(), new Class<?>[]{IGuiBase.class}, (proxy, method, args) -> {
            if (method.isDefault()) return InvocationHandler.invokeDefault(proxy, method, args);
            ManaTableSession owner = owner();
            return switch (method.getName()) {
                case "isRunningOnDesktop" -> true;
                case "isLibgdxPort", "hasNetGame", "isSupportedAudioFormat" -> false;
                case "isGuiThread" -> Thread.currentThread() == uiThread;
                case "getAssetsDir" -> resources.toAbsolutePath().getParent() + File.separator;
                case "getCurrentVersion" -> "Mana Table beta";
                case "getScreenScale" -> 1f;
                case "getAvatarCount", "getSleevesCount" -> 1;
                case "encodeSymbols" -> args[0];
                case "invokeInEdtLater" -> { later((Runnable) args[0]); yield null; }
                case "invokeInEdtNow", "invokeInEdtAndWait" -> { andWait((Runnable) args[0]); yield null; }
                case "runBackgroundTask" -> {
                    Runnable task = (Runnable) args[1];
                    CompletableFuture.runAsync(() -> {
                        if (owner == null) task.run(); else owner.inState(task);
                    });
                    yield null;
                }
                case "getNewGuiGame" -> owner == null ? null : owner.gui();
                case "hostMatch" -> new HostedMatch();
                case "getUpnpPlatformService" -> new org.jupnp.DefaultUpnpServiceConfiguration();
                case "showBugReportDialog" -> { if (owner != null) owner.fail(new IllegalStateException(String.valueOf(args[1]))); yield null; }
                case "showOptionDialog", "showInputDialog", "getChoices", "order", "chooseCard" -> {
                    if (owner == null) throw new IllegalStateException("No active match");
                    yield owner.platformDialog(method.getName(), args);
                }
                case "getSkinIcon", "getUnskinnedIcon", "getCardArt", "createLayeredImage", "getImageFetcher",
                     "createAudioClip", "createAudioMusic" -> null;
                case "showImageDialog", "clearImageCache", "preventSystemSleep", "copyToClipboard", "startAltSoundSystem" -> null;
                case "toString" -> "Mana Table platform";
                case "hashCode" -> System.identityHashCode(proxy);
                case "equals" -> proxy == args[0];
                default -> throw new UnsupportedOperationException("Desktop service is not available: " + method.getName());
            };
        });
        GuiBase.setInterface(base);
        var prefs = FModel.getPreferences();
        prefs.setPref(FPref.PLAYER_NAME, "You");
        prefs.setPref(FPref.UI_SELECT_FROM_CARD_DISPLAYS, false);
        prefs.setPref(FPref.UI_ORDER_HAND, false);
        prefs.setPref(FPref.UI_SHOW_ACTIONABLE_HIGHLIGHTS, true);
        prefs.setPref(FPref.UI_SHOW_AUTOTAP_PREVIEW, true);
        // A land play must return to a visible decision, even with nothing else to cast.
        // Auto-passing here skips publication of the new board and can run into the AI's turn.
        prefs.setPref(FPref.YIELD_AUTO_PASS_NO_ACTIONS, false);
        prefs.setPref(FPref.YIELD_DECLINE_SCOPE_STACK_YIELD, "NEVER");
        prefs.setPref(FPref.YIELD_DECLINE_SCOPE_NO_ACTIONS, "NEVER");
        AiProfileUtil.loadAllProfiles(resources.resolve("ai").toString());
        StaticData.instance().setMulliganRule(forge.MulliganDefs.MulliganRule.London);
        initialized = true;
    }

    static void activate(ManaTableSession session) {
        if (session instanceof MatchTable table) synchronized (owners) {
            owners.put(table.game, new WeakReference<>(session));
        }
        active = session;
    }

    private static ManaTableSession owner() {
        Game current = GameAction.currentGame();
        if (current == null) return active;
        synchronized (owners) {
            var registered = owners.get(current);
            return registered == null ? null : registered.get();
        }
    }

    static void later(Runnable task) {
        ManaTableSession owner = owner();
        UI.execute(() -> run(task, owner));
    }

    /** Input-owned observation without the normal GUI callback publication. */
    static <T> T query(Callable<T> task) {
        return query(owner(), task);
    }

    static <T> T query(ManaTableSession owner, Callable<T> task) {
        try {
            if (Thread.currentThread() == uiThread) return owner == null ? task.call() : owner.callInState(task);
            var completion = UI.submit(() -> { uiThread = Thread.currentThread(); return owner == null ? task.call() : owner.callInState(task); });
            return owner == null ? completion.get(10, TimeUnit.SECONDS) : owner.awaitUi(completion, 10, TimeUnit.SECONDS);
        } catch (ExecutionException error) {
            if (error.getCause() instanceof RuntimeException failure) throw failure;
            throw new IllegalStateException("Could not inspect the current source actions", error.getCause());
        } catch (InterruptedException error) {
            Thread.currentThread().interrupt();
            throw new IllegalStateException("Inspecting source actions was interrupted", error);
        } catch (Exception error) {
            if (error instanceof RuntimeException failure) throw failure;
            throw new IllegalStateException("Could not inspect the current source actions", error);
        }
    }

    private static void run(Runnable task, ManaTableSession owner) {
        uiThread = Thread.currentThread();
        try {
            if (owner == null) task.run();
            else owner.inState(() -> { task.run(); owner.publishInput(); });
        } catch (Throwable error) {
            error.printStackTrace(System.err);
            if (owner != null) owner.fail(error);
        }
    }

    private static void andWait(Runnable task) throws Exception {
        if (Thread.currentThread() == uiThread) { task.run(); return; }
        ManaTableSession owner = owner();
        var completion = UI.submit(() -> run(task, owner));
        if (owner == null) completion.get();
        else owner.awaitUi(completion);
    }
}
