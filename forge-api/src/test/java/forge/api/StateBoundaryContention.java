package forge.api;

import forge.game.GameAction;

import java.lang.management.ManagementFactory;
import java.util.Arrays;
import java.util.concurrent.TimeUnit;

/** Observes real lock contention without releasing the engine's mutation boundary. */
final class StateBoundaryContention {
    static void await(Thread contender, long ownerThread) throws InterruptedException {
        long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(5);
        while (System.nanoTime() < deadline) {
            var info = ManagementFactory.getThreadMXBean().getThreadInfo(contender.getId(), 32);
            if (info != null && info.getThreadState() == Thread.State.WAITING && info.getLockInfo() != null
                    && (ownerThread < 0 || info.getLockOwnerId() == ownerThread)
                    && Arrays.stream(info.getStackTrace()).anyMatch(frame -> frame.getClassName().equals(GameAction.class.getName())
                        && (frame.getMethodName().equals("readState") || frame.getMethodName().equals("withState")))) return;
            if (!contender.isAlive()) throw new AssertionError("The contender completed before observing the real game-state boundary");
            Thread.sleep(1);
        }
        throw new AssertionError("The contender did not wait at the actual game-state boundary within five seconds");
    }
    private StateBoundaryContention() { }
}
