package forge.api;

import forge.gui.interfaces.IGuiGame;
import java.util.concurrent.Callable;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.Future;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.TimeoutException;

interface ManaTableSession {
    IGuiGame gui();
    Object platformDialog(String name, Object[] args);
    void publishInput();
    void fail(Throwable error);
    default void inState(Runnable action) { action.run(); }
    default <T> T callInState(Callable<T> action) throws Exception { return action.call(); }
    default void awaitUi(Future<?> completion) throws InterruptedException, ExecutionException { completion.get(); }
    default <T> T awaitUi(Future<T> completion, long timeout, TimeUnit unit)
            throws InterruptedException, ExecutionException, TimeoutException { return completion.get(timeout, unit); }
}
